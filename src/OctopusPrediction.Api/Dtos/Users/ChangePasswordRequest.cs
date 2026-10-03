using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Users;

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(6)] string NewPassword
);
