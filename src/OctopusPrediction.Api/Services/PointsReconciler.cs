using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public record PointsCorrection(
    string PredictionId,
    string UserId,
    string UserName,
    string FixtureId,
    string Match,
    int PreviousPoints,
    int Points,
    string PreviousWeekId,
    string WeekId);

// The safety net under scoring: recalculates every prediction's points from its fixture as it
// stands now (ScoringService.ScorePrediction, the one scoring rule) and puts right any that
// disagree, along with any prediction filed under a different week than its fixture. Scoring
// normally happens as results change, so this should find nothing; when it does, each fix is
// written to the audit trail. Runs on a timer (PointsReconciliationBackgroundService) and from
// the admin "Recheck points" button.
public class PointsReconciler(AppDbContext db, IScoringService scoring, ILogger<PointsReconciler> logger)
{
    public async Task<IReadOnlyList<PointsCorrection>> ReconcileAsync(User? actor, CancellationToken ct = default)
    {
        var fixtures = await db.Fixtures.AsNoTracking().ToDictionaryAsync(f => f.Id, ct);
        var predictions = await db.Predictions.Include(p => p.User).ToListAsync(ct);

        var corrections = new List<PointsCorrection>();
        foreach (var p in predictions)
        {
            if (!fixtures.TryGetValue(p.FixtureId, out var fixture)) continue;
            var points = scoring.ScorePrediction(p, fixture);
            if (p.PointsEarned == points && p.WeekId == fixture.WeekId) continue;

            corrections.Add(new PointsCorrection(
                p.Id.ToString(), p.UserId.ToString(), p.User.Name, fixture.Id,
                $"{fixture.HomeTeam} v {fixture.AwayTeam}", p.PointsEarned, points, p.WeekId, fixture.WeekId));

            if (p.PointsEarned != points)
                AuditLogger.Log(db, subject: p.User, actor: actor, action: "PointsCorrected",
                    field: $"{fixture.HomeTeam} v {fixture.AwayTeam}".Truncate(50),
                    previousValue: p.PointsEarned.ToString(), newValue: points.ToString(),
                    details: Describe(p, fixture).Truncate(500));
            if (p.WeekId != fixture.WeekId)
                AuditLogger.Log(db, subject: p.User, actor: actor, action: "PredictionWeekCorrected",
                    field: $"{fixture.HomeTeam} v {fixture.AwayTeam}".Truncate(50),
                    previousValue: p.WeekId, newValue: fixture.WeekId);

            p.PointsEarned = points;
            p.WeekId = fixture.WeekId;
        }

        if (corrections.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var c in corrections)
                logger.LogWarning("[Points] Corrected {User} on {Match}: {Previous} -> {Points} pts, week {PreviousWeek} -> {Week}",
                    c.UserName, c.Match, c.PreviousPoints, c.Points, c.PreviousWeekId, c.WeekId);
        }
        return corrections;
    }

    private static string Describe(Prediction p, Fixture f)
    {
        var pick = p.Outcome == OutcomeType.CorrectScore ? $"score {p.HomeGoals}-{p.AwayGoals}" : p.Outcome.ToString();
        var result = f.Status == FixtureStatus.Ended && f.FinalScoreHome is not null
            ? $"ended {f.FinalScoreHome}-{f.FinalScoreAway}"
            : f.Status.ToString();
        return $"Predicted {pick}; match {result}";
    }
}

internal static class StringTruncation
{
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max];
}

// Runs the reconciler shortly after start-up and then every 15 minutes.
internal class PointsReconciliationBackgroundService(
    IServiceScopeFactory scopeFactory, ILogger<PointsReconciliationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let start-up (schema patches, the scraper's first sync) finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var corrections = await scope.ServiceProvider.GetRequiredService<PointsReconciler>().ReconcileAsync(actor: null, ct);
                if (corrections.Count > 0)
                    logger.LogWarning("[Points] Reconciliation corrected {Count} predictions", corrections.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Points] Reconciliation failed");
            }
            await Task.Delay(Interval, ct);
        }
    }
}
