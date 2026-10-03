namespace OctopusPrediction.Api.Dtos.Predictions;

public record PredictionDto(
    string Id,
    string FixtureId,
    string WeekId,
    string Outcome,
    int? HomeGoals,
    int? AwayGoals,
    int PointsEarned,
    DateTime SubmittedAt
);
