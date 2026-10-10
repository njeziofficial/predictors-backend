using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
[RequirePermission(Permissions.FixturesManage)]
public class AdminFixturesController(AppDbContext db, IScoringService scoring) : ControllerBase
{
    [HttpPost("weeks")]
    public async Task<IActionResult> CreateWeek(WeekRequest request)
    {
        var week = new MatchWeek
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            Competition = request.Competition
        };
        db.MatchWeeks.Add(week);
        await db.SaveChangesAsync();
        return Ok(FixtureMappers.ToDto(week));
    }

    [HttpPut("weeks/{id}")]
    public async Task<IActionResult> UpdateWeek(string id, WeekRequest request)
    {
        var week = await db.MatchWeeks.Include(w => w.Fixtures).FirstOrDefaultAsync(w => w.Id == id);
        if (week is null) return NotFound(new { message = "Week not found." });

        week.Name = request.Name;
        week.Competition = request.Competition;
        await db.SaveChangesAsync();
        return Ok(FixtureMappers.ToDto(week));
    }

    [HttpDelete("weeks/{id}")]
    public async Task<IActionResult> DeleteWeek(string id)
    {
        var week = await db.MatchWeeks.FindAsync(id);
        if (week is null) return NotFound(new { message = "Week not found." });

        db.MatchWeeks.Remove(week);
        await db.SaveChangesAsync();
        return Ok(new { message = "Week deleted." });
    }

    [HttpPost("weeks/{weekId}/fixtures")]
    public async Task<IActionResult> CreateFixture(string weekId, CreateFixtureRequest request)
    {
        var week = await db.MatchWeeks.FindAsync(weekId);
        if (week is null) return NotFound(new { message = "Week not found." });

        var fixture = new Fixture
        {
            Id = Guid.NewGuid().ToString(),
            WeekId = weekId,
            HomeTeam = request.HomeTeam,
            AwayTeam = request.AwayTeam,
            Kickoff = request.Kickoff.Kind == DateTimeKind.Utc ? request.Kickoff : request.Kickoff.ToUniversalTime(),
            Status = FixtureStatus.PreMatch,
            UpdatedAt = DateTime.UtcNow
        };
        db.Fixtures.Add(fixture);
        await db.SaveChangesAsync();
        return Ok(FixtureMappers.ToDto(fixture));
    }

    [HttpPut("fixtures/{id}")]
    public async Task<IActionResult> UpdateFixture(string id, UpdateFixtureRequest request)
    {
        if (!Enum.TryParse<FixtureStatus>(request.Status, true, out var status) || !Enum.IsDefined(status))
            return BadRequest(new { message = $"Invalid status: {request.Status}" });

        if (request.FinalScoreHome.HasValue != request.FinalScoreAway.HasValue)
            return BadRequest(new { message = "Final score requires both home and away values." });

        var fixture = await db.Fixtures.FindAsync(id);
        if (fixture is null) return NotFound(new { message = "Fixture not found." });

        if (!string.IsNullOrWhiteSpace(request.WeekId) && request.WeekId != fixture.WeekId)
        {
            if (!await db.MatchWeeks.AnyAsync(w => w.Id == request.WeekId))
                return BadRequest(new { message = "Target week not found." });
            fixture.WeekId = request.WeekId;
        }

        fixture.HomeTeam = request.HomeTeam;
        fixture.AwayTeam = request.AwayTeam;
        fixture.Kickoff = request.Kickoff.Kind == DateTimeKind.Utc ? request.Kickoff : request.Kickoff.ToUniversalTime();
        fixture.Status = status;
        fixture.FinalScoreHome = request.FinalScoreHome;
        fixture.FinalScoreAway = request.FinalScoreAway;
        fixture.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Every edit: a status taken back from Ended must take its points back too, and a fixture
        // moved to another week must take its predictions with it.
        await scoring.ScoreFixtureAsync(id);

        return Ok(FixtureMappers.ToDto(fixture));
    }

    // Recalculates every prediction's points now and fixes any that are wrong (the same check
    // that runs every 15 minutes). Returns what it changed; each change is also in the audit trail.
    [HttpPost("points/recheck")]
    public async Task<IActionResult> RecheckPoints([FromServices] PointsReconciler reconciler, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        return Ok(await reconciler.ReconcileAsync(actor, ct));
    }

    [HttpDelete("fixtures/{id}")]
    public async Task<IActionResult> DeleteFixture(string id)
    {
        var fixture = await db.Fixtures.FindAsync(id);
        if (fixture is null) return NotFound(new { message = "Fixture not found." });

        db.Fixtures.Remove(fixture);
        await db.SaveChangesAsync();
        return Ok(new { message = "Fixture deleted." });
    }
}
