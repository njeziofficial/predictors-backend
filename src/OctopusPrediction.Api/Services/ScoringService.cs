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

    public int ScorePrediction(Prediction prediction, Fixture fixture)
    {
        if (fixture.Status != FixtureStatus.Ended
            || fixture.FinalScoreHome is null
            || fixture.FinalScoreAway is null)
            return 0;

        var home = fixture.FinalScoreHome.Value;
        var away = fixture.FinalScoreAway.Value;

        var actual = home > away ? OutcomeType.HomeWin
            : away > home ? OutcomeType.AwayWin
            : OutcomeType.Draw;

        if (prediction.Outcome == OutcomeType.CorrectScore)
        {
            if (prediction.HomeGoals == home && prediction.AwayGoals == away)
                return Points[OutcomeType.CorrectScore];

            // Direction right but exact score wrong → directional points
            var predictedDir = (prediction.HomeGoals ?? 0) > (prediction.AwayGoals ?? 0) ? OutcomeType.HomeWin
                : (prediction.AwayGoals ?? 0) > (prediction.HomeGoals ?? 0) ? OutcomeType.AwayWin
                : OutcomeType.Draw;

            return predictedDir == actual ? Points[actual] : 0;
        }

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

        foreach (var p in predictions)
        {
            p.PointsEarned = ScorePrediction(p, fixture);
            // Weekly standings add up points by the prediction's week.
            p.WeekId = fixture.WeekId;
        }

        await db.SaveChangesAsync();
    }
}
