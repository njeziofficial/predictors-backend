using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Dtos.MatchWeeks;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/weeks")]
[Authorize]
public class MatchWeeksController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var weeks = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .OrderBy(w => w.Fixtures.Min(f => (DateTime?)f.Kickoff))
            .ToListAsync();

        return Ok(weeks.Select(FixtureMappers.ToDto));
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent()
    {
        var week = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .Where(w => w.Fixtures.Any(f =>
                f.Status == FixtureStatus.PreMatch || f.Status == FixtureStatus.Live))
            .OrderBy(w => w.Fixtures.Min(f => (DateTime?)f.Kickoff))
            .FirstOrDefaultAsync();

        if (week is null) return NotFound(new { message = "No active week found." });
        return Ok(FixtureMappers.ToDto(week));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var week = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .FirstOrDefaultAsync(w => w.Id == id);

        if (week is null) return NotFound();
        return Ok(FixtureMappers.ToDto(week));
    }
}
