using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;

namespace OctopusPrediction.Api.Services;

// A week's marking: each player's picks, the points they were given and the points the scoring
// rule gives now, with the reason. Behind the admin Marking page and the end-of-week check
// (WeekReconciliation), so both read the marking the same way.
public class WeekMarking(AppDbContext db, IScoringService scoring)
{
    public async Task<WeekMarkingDto?> ForWeekAsync(string weekId, CancellationToken ct = default)
    {
        var week = await db.MatchWeeks.AsNoTracking()
            .Include(w => w.Fixtures)
            .FirstOrDefaultAsync(w => w.Id == weekId, ct);
        if (week is null) return null;

        var now = DateTime.UtcNow;
        var fixtures = week.Fixtures.OrderBy(f => f.Kickoff).ThenBy(f => f.HomeTeam).ToList();
        var byId = fixtures.ToDictionary(f => f.Id);
        bool Visible(Fixture f) => f.Status != FixtureStatus.PreMatch || f.Kickoff <= now;

        var predictions = await db.Predictions.AsNoTracking()
            .Where(p => p.WeekId == weekId || p.Fixture.WeekId == weekId)
            .ToListAsync(ct);
        var since = await ScoringService.PreviousPointsSinceAsync(db, ct);

        // Every active player (as on the Prediction Check page), plus anyone else with picks here.
        var withPicks = predictions.Select(p => p.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => (!u.IsSystemUser && !u.IsDisabled) || withPicks.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.WhatsAppName })
            .ToListAsync(ct);

        var players = users.Select(u =>
        {
            var picks = predictions
                .Where(p => p.UserId == u.Id && byId.ContainsKey(p.FixtureId))
                .OrderBy(p => fixtures.IndexOf(byId[p.FixtureId]))
                .Select(p =>
                {
                    var f = byId[p.FixtureId];
                    var userSince = since.GetValueOrDefault(p.UserId);
                    var expected = scoring.ScorePrediction(p, f, userSince);
                    if (!Visible(f))
                        return new MarkedPickDto(f.Id, null, null, null, p.PointsEarned, expected, "Hidden until kickoff");
                    return new MarkedPickDto(f.Id, OutcomeName(p.Outcome),
                        p.Outcome == OutcomeType.CorrectScore ? p.HomeGoals : null,
                        p.Outcome == OutcomeType.CorrectScore ? p.AwayGoals : null,
                        p.PointsEarned, expected, Reason(p, f, userSince, expected));
                })
                .ToList();
            return new PlayerMarkingDto(u.Id.ToString(), u.Name, u.WhatsAppName,
                picks.Sum(p => p.Points), picks.Sum(p => p.ExpectedPoints), picks.Count, picks);
        })
        .OrderByDescending(p => p.Points)
        .ThenBy(p => p.WhatsAppName ?? p.Name)
        .ToList();

        var fixtureDtos = fixtures.Select(f => new MarkingFixtureDto(
            f.Id, f.HomeTeam, f.AwayTeam, f.Kickoff, FixtureMappers.MapStatus(f.Status),
            f.FinalScoreHome, f.FinalScoreAway, Visible(f))).ToList();

        return new WeekMarkingDto(week.Id, week.Name, week.ReconciledAt, fixtureDtos, players);
    }

    public static string OutcomeName(OutcomeType o) => o switch
    {
        OutcomeType.HomeWin => "home_win",
        OutcomeType.AwayWin => "away_win",
        OutcomeType.Draw => "draw",
        OutcomeType.CorrectScore => "correct_score",
        _ => o.ToString().ToLowerInvariant()
    };

    // Plain words for why a pick earned what it did (the points themselves come from ScorePrediction).
    private static string Reason(Prediction p, Fixture f, DateTime? previousPointsSince, int points)
    {
        if (previousPointsSince is { } since && f.Kickoff < since)
            return "Counted in previous points";
        if (f.Status is FixtureStatus.Postponed or FixtureStatus.Cancelled)
            return $"Match {f.Status.ToString().ToLowerInvariant()}";
        if (f.Status != FixtureStatus.Ended || f.FinalScoreHome is null || f.FinalScoreAway is null)
            return "Not finished";

        if (p.Outcome == OutcomeType.CorrectScore)
            return points > 0
                ? $"Exact score {p.HomeGoals}-{p.AwayGoals}"
                : $"Wrong exact score ({p.HomeGoals}-{p.AwayGoals})";

        var label = p.Outcome switch
        {
            OutcomeType.HomeWin => "Home win",
            OutcomeType.AwayWin => "Away win",
            _ => "Draw",
        };
        return points > 0 ? $"{label} right" : $"{label} wrong";
    }
}
