namespace OctopusPrediction.Api.Dtos.Leaderboard;

public record LeaderboardEntryDto(
    string UserId,
    string Name,
    int TotalPoints,
    // Points carried over from before the app — already included in TotalPoints. Always 0 on weekly boards.
    int PreviousPoints,
    int Position,
    DateTime? LastSubmittedAt
);
