namespace OctopusPrediction.Api.Dtos.Fixtures;

public record FixtureDto(
    string Id,
    string WeekId,
    string HomeTeam,
    string AwayTeam,
    DateTime Kickoff,
    string Status,
    ScoreDto? FinalScore,
    ScoreDto? LiveScore,
    int? LiveMinute
);

public record ScoreDto(int Home, int Away);
