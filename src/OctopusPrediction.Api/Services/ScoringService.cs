using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public class ScoringService(AppDbContext db) : IScoringService
{
    // Only an exact score earns this much (the best directional result, a draw, is 5), so
    // PointsEarned == CorrectScorePoints identifies a correct-score hit.
    public const int CorrectScorePoints = 10;

    private static readonly Dictionary<OutcomeType, int> Points = new()
    {
        [OutcomeType.HomeWin] = 2,
        [OutcomeType.AwayWin] = 3,
        [OutcomeType.Draw] = 5,
        [OutcomeType.CorrectScore] = CorrectScorePoints,
    };

    public int ScorePrediction(Prediction prediction, Fixture fixture, DateTime? previousPointsSince = null)
    {
        // A player's previous points are their league table as of the import, so they already
        // include every match played before it. In-app predictions on those matches (made while
        // the app was being tried out) would count them twice.
        if (previousPointsSince is { } since && fixture.Kickoff < since)
            return 0;

        if (fixture.Status != FixtureStatus.Ended
            || fixture.FinalScoreHome is null
            || fixture.FinalScoreAway is null)
            return 0;

        var home = fixture.FinalScoreHome.Value;
        var away = fixture.FinalScoreAway.Value;

        var actual = home > away ? OutcomeType.HomeWin
            : away > home ? OutcomeType.AwayWin
            : OutcomeType.Draw;

        // A correct-score pick is all or nothing: the exact score earns CorrectScorePoints, any
        // other score earns 0, even one that got the winner (or the draw) right. Only a home win,
        // away win or draw pick is marked on the result alone.
        if (prediction.Outcome == OutcomeType.CorrectScore)
            return prediction.HomeGoals == home && prediction.AwayGoals == away ? Points[OutcomeType.CorrectScore] : 0;

        return prediction.Outcome == actual ? Points[prediction.Outcome] : 0;
    }

    // Recalculates every prediction on the fixture from its current state, whatever that is: a
    // fixture that's no longer Ended (or never was) gives 0, so points awarded for a wrong
    // "finished" reading are taken back as soon as the fixture changes. Call it whenever a
    // fixture's status, final score or week changes.
    public async Task ScoreFixtureAsync(string fixtureId)
    {
        var fixture = await db.Fixtures.FindAsync(fixtureId);
        if (fixture is null) return;

        var predictions = await db.Predictions
            .Where(p => p.FixtureId == fixtureId)
            .ToListAsync();
        var since = await PreviousPointsSinceAsync(db);

        foreach (var p in predictions)
        {
            p.PointsEarned = ScorePrediction(p, fixture, since.GetValueOrDefault(p.UserId));
            // Weekly standings add up points by the prediction's week.
            p.WeekId = fixture.WeekId;
        }

        // A result changed after the week was signed off: check the week again once it's over.
        var week = await db.MatchWeeks.FindAsync(fixture.WeekId);
        if (week?.ReconciledAt is not null) week.ReconciledAt = null;

        await db.SaveChangesAsync();
    }

    // When each player's previous points were first imported. CreatedAt, not UpdatedAt: re-importing
    // the same table (say, to fix a name) must not move the cutoff and wipe later in-app points.
    public static async Task<Dictionary<Guid, DateTime?>> PreviousPointsSinceAsync(AppDbContext db, CancellationToken ct = default) =>
        await db.PreviousPoints.AsNoTracking()
            .GroupBy(pp => pp.UserId)
            .Select(g => new { UserId = g.Key, Since = g.Min(pp => pp.CreatedAt) })
            .ToDictionaryAsync(x => x.UserId, x => (DateTime?)x.Since, ct);
}
