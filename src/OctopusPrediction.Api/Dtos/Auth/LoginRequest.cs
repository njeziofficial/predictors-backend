using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Auth;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password
);
