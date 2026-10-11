using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

public class ScoringServiceTests
{
    // ScorePrediction doesn't touch the database.
    private readonly ScoringService _scoring = new(null!);

    private static Fixture Ended(int home, int away) => new()
    {
        Id = "f", Kickoff = new DateTime(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc),
        Status = FixtureStatus.Ended, FinalScoreHome = home, FinalScoreAway = away,
    };

    private static Prediction Pick(OutcomeType outcome) => new() { Outcome = outcome };
    private static Prediction Score(int home, int away) =>
        new() { Outcome = OutcomeType.CorrectScore, HomeGoals = home, AwayGoals = away };

    [Theory]
    [InlineData(OutcomeType.HomeWin, 2, 0, 2)]
    [InlineData(OutcomeType.AwayWin, 0, 1, 3)]
    [InlineData(OutcomeType.Draw, 1, 1, 5)]
    [InlineData(OutcomeType.HomeWin, 0, 1, 0)]
    [InlineData(OutcomeType.AwayWin, 1, 1, 0)]
    [InlineData(OutcomeType.Draw, 2, 1, 0)]
    public void ResultPicks_ScoreOnTheResult(OutcomeType pick, int home, int away, int expected) =>
        Assert.Equal(expected, _scoring.ScorePrediction(Pick(pick), Ended(home, away)));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(0, 4)]
    public void ExactScore_Earns10(int home, int away) =>
        Assert.Equal(10, _scoring.ScorePrediction(Score(home, away), Ended(home, away)));

    // The bug: a wrong exact score used to earn the win/draw points when it got the winner right.
    [Theory]
    [InlineData(2, 1, 1, 0)] // right winner, wrong score
    [InlineData(1, 1, 0, 0)] // right (draw) result, wrong score
    [InlineData(2, 1, 5, 2)]
    [InlineData(0, 2, 1, 3)]
    [InlineData(1, 1, 2, 1)] // wrong result too
    public void WrongExactScore_EarnsNothing(int pickHome, int pickAway, int home, int away) =>
        Assert.Equal(0, _scoring.ScorePrediction(Score(pickHome, pickAway), Ended(home, away)));

    // Oludele Tosin's week 8 as it was marked (21) and as it should be (12).
    [Fact]
    public void Week8_Regression()
    {
        (Prediction Pick, Fixture Fixture)[] week =
        [
            (Score(1, 1), Ended(1, 1)),          // Málaga v Espanyol: exact → 10
            (Score(1, 1), Ended(1, 2)),          // Alavés v Atlético: 0
            (Pick(OutcomeType.HomeWin), Ended(3, 0)), // Barcelona v Getafe: 2
            (Score(2, 1), Ended(1, 0)),          // Real Madrid v Villarreal: was 2, now 0
            (Score(1, 1), Ended(0, 0)),          // Elche v Celta: was 5, now 0
            (Score(1, 1), Ended(3, 2)),          // Real Sociedad v Deportivo: 0
            (Score(2, 1), Ended(5, 2)),          // Betis v Osasuna: was 2, now 0
        ];
        Assert.Equal(12, week.Sum(w => _scoring.ScorePrediction(w.Pick, w.Fixture)));
    }

    [Theory]
    [InlineData(FixtureStatus.PreMatch)]
    [InlineData(FixtureStatus.Live)]
    [InlineData(FixtureStatus.HalfTime)]
    [InlineData(FixtureStatus.Postponed)]
    [InlineData(FixtureStatus.Cancelled)]
    public void UnfinishedMatch_EarnsNothing(FixtureStatus status)
    {
        var fixture = Ended(1, 1);
        fixture.Status = status;
        Assert.Equal(0, _scoring.ScorePrediction(Score(1, 1), fixture));
        Assert.Equal(0, _scoring.ScorePrediction(Pick(OutcomeType.Draw), fixture));
    }

    [Fact]
    public void MatchBeforePreviousPointsImport_EarnsNothing()
    {
        var fixture = Ended(1, 1);
        Assert.Equal(0, _scoring.ScorePrediction(Score(1, 1), fixture, previousPointsSince: fixture.Kickoff.AddDays(1)));
        Assert.Equal(10, _scoring.ScorePrediction(Score(1, 1), fixture, previousPointsSince: fixture.Kickoff.AddDays(-1)));
    }
}
