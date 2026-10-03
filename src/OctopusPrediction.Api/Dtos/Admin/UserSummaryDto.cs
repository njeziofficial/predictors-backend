namespace OctopusPrediction.Api.Dtos.Admin;

public record UserSummaryDto(
    string Id,
    string Name,
    string Email,
    string? PhoneNumber,
    string? WhatsAppName,
    string Role,
    bool IsDisabled,
    bool IsSystemUser,
    DateTime CreatedAt,
    DateTime? LastLoginAt
);
