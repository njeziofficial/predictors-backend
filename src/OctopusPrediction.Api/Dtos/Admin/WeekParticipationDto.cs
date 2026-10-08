namespace OctopusPrediction.Api.Dtos.Admin;

// Who has and hasn't predicted a week, so an admin can chase the stragglers.
public record WeekParticipationDto(
    string WeekId,
    string WeekName,
    // Fixtures that can be predicted (postponed and cancelled ones left out).
    int FixtureCount,
    // Kickoff of the earliest fixture still to start; null once every fixture has started.
    DateTime? NextKickoff,
    IReadOnlyList<PlayerParticipationDto> Players
);

public record PlayerParticipationDto(
    string UserId,
    string Name,
    string? WhatsAppName,
    string? PhoneNumber,
    string Email,
    // Predictions made for this week; equal to FixtureCount when complete.
    int Predicted,
    DateTime? SubmittedAt,
    DateTime? LastLoginAt
);
