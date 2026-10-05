namespace OctopusPrediction.Api.Dtos.Users;

public record PreviousPointsDto(string Label, int Points, int CorrectScores, DateTime UpdatedAt);
