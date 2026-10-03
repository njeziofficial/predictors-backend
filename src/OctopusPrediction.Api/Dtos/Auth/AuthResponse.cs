namespace OctopusPrediction.Api.Dtos.Auth;

public record AuthResponse(
    string Token,
    string UserId,
    string Name,
    string Email,
    string Role,
    DateTime ExpiresAt,
    bool MustResetPassword,
    bool IsSystemUser
);
