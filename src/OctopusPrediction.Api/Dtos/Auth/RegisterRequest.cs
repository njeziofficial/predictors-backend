using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Auth;

public record RegisterRequest(
    [Required, MinLength(2)] string Name,
    [Required, EmailAddress] string Email,
    [Required] string PhoneNumber,
    [Required] string WhatsAppName,
    [Required, MinLength(6)] string Password
);
