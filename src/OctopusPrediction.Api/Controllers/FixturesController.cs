using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/fixtures")]
[Authorize]
public class FixturesController(AppDbContext db, AppCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? weekId, [FromQuery] string? status)
    {
        IEnumerable<(FixtureStatus Status, FixtureDto Fixture)> fixtures = (await cache.WeeksAsync(db)).Fixtures;

        if (!string.IsNullOrEmpty(weekId))
            fixtures = fixtures.Where(f => f.Fixture.WeekId == weekId);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<FixtureStatus>(status, true, out var s))
            fixtures = fixtures.Where(f => f.Status == s);

        return Ok(fixtures.Select(f => f.Fixture));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var fixture = (await cache.WeeksAsync(db)).FixturesById.GetValueOrDefault(id);
        if (fixture is null) return NotFound();
        return Ok(fixture);
    }
}
