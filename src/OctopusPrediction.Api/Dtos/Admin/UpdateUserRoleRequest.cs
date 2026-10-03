using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

public record UpdateUserRoleRequest(
    [Required] string Role
);
