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
    int? LiveMinute,
    // Knockout matches that went to extra time: the score at the end of it. FinalScore stays the
    // score after 90 minutes, which is what predictions are marked on.
    ScoreDto? AfterExtraTime = null
);

public record ScoreDto(int Home, int Away);
