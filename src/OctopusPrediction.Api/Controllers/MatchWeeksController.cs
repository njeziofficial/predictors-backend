using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// All three read the same cached snapshot of weeks and fixtures (see CachedReads), which empties
// when the scraper or an admin actually changes a fixture.
[ApiController]
[Route("api/weeks")]
[Authorize]
public class MatchWeeksController(AppDbContext db, AppCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok((await cache.WeeksAsync(db)).Weeks);

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent()
    {
        var week = (await cache.WeeksAsync(db)).Current;
        if (week is null) return NotFound(new { message = "No active week found." });
        return Ok(week);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var week = (await cache.WeeksAsync(db)).ById.GetValueOrDefault(id);
        if (week is null) return NotFound();
        return Ok(week);
    }
}
