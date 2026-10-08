using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminParticipationController(AppDbContext db) : ControllerBase
{
    // Every active player with how many of the week's fixtures they've predicted. Admins play
    // too, so only the system admin account and disabled accounts are left out.
    [HttpGet("weeks/{weekId}/participation")]
    [RequirePermission(Permissions.ParticipationView)]
    public async Task<IActionResult> GetParticipation(string weekId)
    {
        var week = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .FirstOrDefaultAsync(w => w.Id == weekId);
        if (week is null) return NotFound(new { message = "Week not found." });

        var playable = week.Fixtures
            .Where(f => f.Status is not (FixtureStatus.Postponed or FixtureStatus.Cancelled))
            .ToList();
        var nextKickoff = playable
            .Where(f => f.Status == FixtureStatus.PreMatch)
            .Select(f => (DateTime?)f.Kickoff)
            .Min();

        var players = await db.Users
            .Where(u => !u.IsSystemUser && !u.IsDisabled)
            .Select(u => new PlayerParticipationDto(
                u.Id.ToString(),
                u.Name,
                u.WhatsAppName,
                u.PhoneNumber,
                u.Email,
                u.Predictions.Count(p => p.WeekId == weekId),
                u.Predictions.Where(p => p.WeekId == weekId).Max(p => (DateTime?)p.SubmittedAt),
                u.LastLoginAt))
            .ToListAsync();

        return Ok(new WeekParticipationDto(week.Id, week.Name, playable.Count, nextKickoff, players));
    }
}
