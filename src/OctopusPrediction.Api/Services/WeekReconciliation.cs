using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// The end-of-week check. Once every match in a week is over, after PointsReconciler has re-marked
// every prediction, confirms the week's stored points all agree with the scoring rule (WeekMarking)
// and signs the week off: ReconciledAt, plus a summary in the audit trail. A week that doesn't
// agree is left unsigned and logged as an error, and is tried again on the next run. Runs from
// PointsReconciliationBackgroundService; a result changing afterwards (ScoringService) reopens it.
public class WeekReconciliation(AppDbContext db, WeekMarking marking, ILogger<WeekReconciliation> logger)
{
    /// <param name="corrections">What the reconciler run just before this one put right.</param>
    public async Task<int> SignOffFinishedWeeksAsync(IReadOnlyList<PointsCorrection> corrections, CancellationToken ct = default)
    {
        var due = await db.MatchWeeks
            .Where(w => w.ReconciledAt == null && w.Fixtures.Any()
                && w.Fixtures.All(f => f.Status == FixtureStatus.Ended
                    || f.Status == FixtureStatus.Postponed || f.Status == FixtureStatus.Cancelled))
            .ToListAsync(ct);

        var signedOff = 0;
        foreach (var week in due)
        {
            var result = await marking.ForWeekAsync(week.Id, ct);
            if (result is null) continue;

            var wrong = result.Players.Where(p => p.Points != p.ExpectedPoints).ToList();
            if (wrong.Count > 0)
            {
                logger.LogError("[Points] {Week} not signed off: points disagree with the scoring rule for {Players}",
                    week.Name, string.Join(", ", wrong.Select(p => $"{p.Name} ({p.Points} vs {p.ExpectedPoints})")));
                continue;
            }

            var picks = result.Players.Sum(p => p.PicksMade);
            var players = result.Players.Count(p => p.PicksMade > 0);
            var corrected = corrections.Count(c => c.WeekId == week.Id);
            var top = string.Join(", ", result.Players.Where(p => p.PicksMade > 0).Take(5)
                .Select(p => $"{p.WhatsAppName ?? p.Name} {p.Points}"));

            week.ReconciledAt = DateTime.UtcNow;
            AuditLogger.Log(db, subject: null, actor: null, action: "WeekReconciled",
                field: week.Name.Truncate(50),
                newValue: $"{result.Players.Sum(p => p.Points)} pts".Truncate(500),
                details: ($"All matches over. {picks} picks by {players} players re-marked; " +
                    $"{corrected} corrected in this check. Top: {top}").Truncate(500));
            logger.LogInformation("[Points] Signed off {Week}: {Picks} picks by {Players} players, {Corrected} corrected",
                week.Name, picks, players, corrected);
            signedOff++;
        }

        if (signedOff > 0) await db.SaveChangesAsync(ct);
        return signedOff;
    }
}
