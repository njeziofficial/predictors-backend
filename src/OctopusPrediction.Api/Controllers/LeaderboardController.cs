using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// Ranking: points, then correct scores called, then earliest submission. Served from the cache
// (see CachedReads), which empties whenever points, previous points or players change. Each
// competition has its own overall table; a week's table is that week's competition's.
[ApiController]
[Route("api/leaderboard")]
[Authorize]
public class LeaderboardController(AppDbContext db, AppCache cache) : ControllerBase
{
    // La Liga's table when no competition is named.
    [HttpGet]
    public async Task<IActionResult> GetOverall([FromQuery] string? competition)
    {
        competition ??= Competitions.Default;
        if (!(await cache.CompetitionsAsync(db)).IsVisible(competition, User.IsInRole("Admin")))
            return NotFound(new { message = "Competition not found." });
        return Ok(await cache.OverallLeaderboardAsync(db, competition));
    }

    [HttpGet("{weekId}")]
    public async Task<IActionResult> GetByWeek(string weekId)
    {
        var week = (await cache.WeeksAsync(db)).ById.GetValueOrDefault(weekId);
        if (week is not null && !(await cache.CompetitionsAsync(db)).IsVisible(week.CompetitionId, User.IsInRole("Admin")))
            return NotFound(new { message = "Week not found." });
        return Ok(await cache.WeekLeaderboardAsync(db, weekId));
    }
}
