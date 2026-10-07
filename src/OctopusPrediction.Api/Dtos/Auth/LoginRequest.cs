using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Auth;

public record LoginRequest(
    // Email address or WhatsApp name, as chosen by Method.
    string? Login,
    // "email" or "whatsapp". Left out by older clients: then anything with an "@" is an email.
    [RegularExpression("^(email|whatsapp)$", ErrorMessage = "Method must be \"email\" or \"whatsapp\".")]
    string? Method,
    // Older clients send the email here; used when Login is missing.
    string? Email,
    [Required, MinLength(6)] string Password
);
