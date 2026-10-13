using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Authorize]
public class CompetitionsController(AppDbContext db, AppCache cache) : ControllerBase
{
    // The competitions the caller can see: the ones switched on, or every one for an admin.
    [HttpGet("api/competitions")]
    public async Task<IActionResult> GetAll()
    {
        var competitions = await cache.CompetitionsAsync(db);
        var isAdmin = User.IsInRole("Admin");
        return Ok(competitions.All.Where(c => competitions.IsVisible(c.Id, isAdmin)));
    }

    // Switching a competition off hides it from players, closes it to predictions and stops the
    // scraper reading it. Its weeks, predictions and points are kept for when it comes back.
    [HttpPut("api/admin/competitions/{id}")]
    [Authorize(Roles = "Admin")]
    [RequirePermission(Permissions.CompetitionsManage)]
    public async Task<IActionResult> SetEnabled(string id, SetCompetitionEnabledRequest request)
    {
        var competition = await db.Competitions.FindAsync(id);
        if (competition is null) return NotFound(new { message = "Competition not found." });

        if (competition.Enabled != request.Enabled)
        {
            var previous = competition.Enabled;
            competition.Enabled = request.Enabled;
            competition.UpdatedAt = DateTime.UtcNow;
            var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == actorId);
            AuditLogger.Log(db, subject: null, actor: actor, action: "CompetitionEnabledChanged",
                field: competition.Name, previousValue: previous.ToString(), newValue: request.Enabled.ToString());
            await db.SaveChangesAsync();
        }

        return Ok(new Dtos.Competitions.CompetitionDto(competition.Id, competition.Name, competition.Enabled, competition.SortOrder));
    }
}
