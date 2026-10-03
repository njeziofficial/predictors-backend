using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/fixtures")]
[Authorize]
public class FixturesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? weekId, [FromQuery] string? status)
    {
        var query = db.Fixtures.AsQueryable();

        if (!string.IsNullOrEmpty(weekId))
            query = query.Where(f => f.WeekId == weekId);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<FixtureStatus>(status, true, out var s))
            query = query.Where(f => f.Status == s);

        var fixtures = await query.OrderBy(f => f.Kickoff).ToListAsync();
        return Ok(fixtures.Select(FixtureMappers.ToDto));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var f = await db.Fixtures.FindAsync(id);
        if (f is null) return NotFound();
        return Ok(FixtureMappers.ToDto(f));
    }
}
