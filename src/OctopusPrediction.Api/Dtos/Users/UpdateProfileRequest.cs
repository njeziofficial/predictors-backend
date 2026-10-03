using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Users;

public record UpdateProfileRequest(
    [Required, MinLength(2)] string Name,
    [Required] string PhoneNumber,
    [Required] string WhatsAppName
);
