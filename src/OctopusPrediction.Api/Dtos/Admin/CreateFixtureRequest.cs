using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

public record CreateFixtureRequest(
    [Required, MinLength(1), MaxLength(150)] string HomeTeam,
    [Required, MinLength(1), MaxLength(150)] string AwayTeam,
    [Required] DateTime Kickoff
);
