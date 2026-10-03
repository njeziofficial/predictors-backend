namespace OctopusPrediction.Api.Dtos.Users;

public record UserProfileDto(
    string Id,
    string Name,
    string Email,
    string? PhoneNumber,
    string? WhatsAppName,
    string Role,
    DateTime CreatedAt,
    DateTime? LastLoginAt
);
