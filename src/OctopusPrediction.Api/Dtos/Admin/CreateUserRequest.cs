using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// Password is optional: when omitted, a temporary one is generated and returned in the response.
public record CreateUserRequest(
    [Required, MinLength(2)] string Name,
    [Required, EmailAddress] string Email,
    [Required] string Role,
    string? PhoneNumber,
    string? WhatsAppName,
    [MinLength(6)] string? Password
);
