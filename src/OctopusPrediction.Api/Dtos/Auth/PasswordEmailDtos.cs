using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Auth;

public record ForgotPasswordRequest([Required, MaxLength(256)] string Email);

public record PasswordTokenRequest([Required, MaxLength(100)] string Token);

public record SetPasswordRequest([Required, MaxLength(100)] string Token, [Required, MinLength(6), MaxLength(200)] string NewPassword);

// What the Set password page shows for a link: whether it still works, and for a welcome link
// the player's name. Purpose is "reset" or "welcome".
public record PasswordTokenDto(bool Valid, string? Purpose, string? Name);
