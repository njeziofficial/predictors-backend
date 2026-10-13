using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// Fixtures of a competition that's switched off are hidden from players, as its weeks are.
[ApiController]
[Route("api/fixtures")]
[Authorize]
public class FixturesController(AppDbContext db, AppCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? weekId, [FromQuery] string? status)
    {
        var weeks = await cache.WeeksAsync(db);
        var visible = await VisibleAsync(weeks);
        IEnumerable<(FixtureStatus Status, FixtureDto Fixture)> fixtures = weeks.Fixtures.Where(f => visible(f.Fixture));

        if (!string.IsNullOrEmpty(weekId))
            fixtures = fixtures.Where(f => f.Fixture.WeekId == weekId);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<FixtureStatus>(status, true, out var s))
            fixtures = fixtures.Where(f => f.Status == s);

        return Ok(fixtures.Select(f => f.Fixture));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var weeks = await cache.WeeksAsync(db);
        var fixture = weeks.FixturesById.GetValueOrDefault(id);
        if (fixture is null || !(await VisibleAsync(weeks))(fixture)) return NotFound();
        return Ok(fixture);
    }

    private async Task<Func<FixtureDto, bool>> VisibleAsync(WeeksSnapshot weeks)
    {
        var competitions = await cache.CompetitionsAsync(db);
        var isAdmin = User.IsInRole("Admin");
        return f => weeks.ById.TryGetValue(f.WeekId, out var w) && competitions.IsVisible(w.CompetitionId, isAdmin);
    }
}
