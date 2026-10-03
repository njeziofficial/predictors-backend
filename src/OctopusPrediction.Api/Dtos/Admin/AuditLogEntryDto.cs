namespace OctopusPrediction.Api.Dtos.Admin;

public record AuditLogEntryDto(
    string Id,
    string? UserId,
    string? UserName,
    string? ActorUserId,
    string? ActorName,
    string Action,
    string? Field,
    string? PreviousValue,
    string? NewValue,
    string? Details,
    DateTime CreatedAt
);
