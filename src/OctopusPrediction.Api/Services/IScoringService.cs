using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public interface IScoringService
{
    int ScorePrediction(Prediction prediction, Fixture fixture);
    Task ScoreFixtureAsync(string fixtureId);
}
