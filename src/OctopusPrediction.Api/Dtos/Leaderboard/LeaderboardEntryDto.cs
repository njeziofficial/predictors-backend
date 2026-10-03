namespace OctopusPrediction.Api.Dtos.Leaderboard;

public record LeaderboardEntryDto(
    string UserId,
    string Name,
    int TotalPoints,
    int Position,
    DateTime? LastSubmittedAt
);
