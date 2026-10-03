using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

public record WeekRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [Required, MinLength(1), MaxLength(100)] string Competition
);
