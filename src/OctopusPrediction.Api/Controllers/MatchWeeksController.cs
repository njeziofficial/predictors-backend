using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// All three read the same cached snapshot of weeks and fixtures (see CachedReads), which empties
// when the scraper or an admin actually changes a fixture. Weeks of a competition that's switched
// off are hidden from players (admins still see them).
[ApiController]
[Route("api/weeks")]
[Authorize]
public class MatchWeeksController(AppDbContext db, AppCache cache) : ControllerBase
{
    // Every week the caller can see, or only one competition's.
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? competition)
    {
        var competitions = await cache.CompetitionsAsync(db);
        var isAdmin = User.IsInRole("Admin");
        if (competition is not null && !competitions.IsVisible(competition, isAdmin))
            return NotFound(new { message = "Competition not found." });

        var weeks = (await cache.WeeksAsync(db)).Weeks
            .Where(w => competition is null ? competitions.IsVisible(w.CompetitionId, isAdmin) : w.CompetitionId == competition);
        return Ok(weeks);
    }

    // The week being predicted now in a competition (La Liga when none is named).
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] string? competition)
    {
        competition ??= Competitions.Default;
        var competitions = await cache.CompetitionsAsync(db);
        if (!competitions.IsVisible(competition, User.IsInRole("Admin")))
            return NotFound(new { message = "Competition not found." });

        var week = (await cache.WeeksAsync(db)).CurrentByCompetition.GetValueOrDefault(competition);
        if (week is null) return NotFound(new { message = "No active week found." });
        return Ok(week);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var week = (await cache.WeeksAsync(db)).ById.GetValueOrDefault(id);
        if (week is null || !(await cache.CompetitionsAsync(db)).IsVisible(week.CompetitionId, User.IsInRole("Admin")))
            return NotFound();
        return Ok(week);
    }
}
