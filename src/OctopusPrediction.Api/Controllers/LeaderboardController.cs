using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// Ranking: points, then correct scores called, then earliest submission. Served from the cache
// (see CachedReads), which empties whenever points, previous points or players change.
[ApiController]
[Route("api/leaderboard")]
[Authorize]
public class LeaderboardController(AppDbContext db, AppCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetOverall() => Ok(await cache.OverallLeaderboardAsync(db));

    [HttpGet("{weekId}")]
    public async Task<IActionResult> GetByWeek(string weekId) => Ok(await cache.WeekLeaderboardAsync(db, weekId));
}
