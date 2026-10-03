using OctopusPrediction.Api.Dtos.Auth;

namespace OctopusPrediction.Api.Services;

public enum LoginFailureReason
{
    InvalidCredentials,
    AccountDisabled
}

public record LoginResult(AuthResponse? Response, LoginFailureReason? FailureReason);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(LoginRequest request);
    Task<AuthResponse?> RegisterAsync(RegisterRequest request);
}
