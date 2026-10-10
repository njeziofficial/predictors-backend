using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public interface IScoringService
{
    // previousPointsSince: when the player's carried-over points were first imported (see
    // ScoringService.PreviousPointsSinceAsync); matches that kicked off before then score 0.
    int ScorePrediction(Prediction prediction, Fixture fixture, DateTime? previousPointsSince = null);
    Task ScoreFixtureAsync(string fixtureId);
}
