using OctopusPrediction.Api.Dtos.Auth;

namespace OctopusPrediction.Api.Services;

public enum LoginFailureReason
{
    InvalidCredentials,
    AccountDisabled
}

public enum RefreshFailureReason
{
    InvalidToken,
    AccountDisabled
}

// Response goes in the body; RefreshToken goes in the httpOnly cookie. RefreshToken is null when
// a refresh was answered from the grace window (a concurrent refresh already rotated the cookie),
// in which case the cookie the browser already holds must be left alone.
public record AuthSession(AuthResponse Response, string? RefreshToken, DateTime RefreshTokenExpiresAt);

public record LoginResult(AuthSession? Session, LoginFailureReason? FailureReason);

public record RefreshResult(AuthSession? Session, RefreshFailureReason? FailureReason);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request);
    // Signs in a user already proven to be who they say (e.g. by a passkey); method goes in the audit log.
    Task<LoginResult> SignInAsync(Entities.User user, string? method = null);
    Task<AuthSession?> RegisterAsync(RegisterRequest request);
    Task<RefreshResult> RefreshAsync(string refreshToken);
    Task LogoutAsync(string refreshToken);
}
