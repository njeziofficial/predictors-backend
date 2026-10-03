using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

public record UpdateFixtureRequest(
    [Required, MinLength(1), MaxLength(150)] string HomeTeam,
    [Required, MinLength(1), MaxLength(150)] string AwayTeam,
    [Required] DateTime Kickoff,
    [Required] string Status,
    [Range(0, int.MaxValue)] int? FinalScoreHome,
    [Range(0, int.MaxValue)] int? FinalScoreAway,
    string? WeekId
);
