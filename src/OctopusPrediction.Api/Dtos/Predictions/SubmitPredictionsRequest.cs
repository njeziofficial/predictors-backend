using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Predictions;

public record SubmitPredictionsRequest(
    [Required] string WeekId,
    [Required] IEnumerable<PredictionItemRequest> Predictions
);

public record PredictionItemRequest(
    [Required] string FixtureId,
    [Required] string Outcome,
    int? HomeGoals,
    int? AwayGoals
);
