using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;
using OctopusPrediction.Api.Services.Monitoring;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Authorize]
public class CompetitionsController(AppDbContext db, AppCache cache, CompetitionSources sources, ScraperMonitor monitor) : ControllerBase
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
        // The main competition: switching it off would empty the game for every player.
        if (id == Competitions.LaLiga && !request.Enabled)
            return BadRequest(new { message = "La Liga can't be switched off." });

        if (competition.Enabled != request.Enabled)
        {
            var previous = competition.Enabled;
            competition.Enabled = request.Enabled;
            competition.UpdatedAt = DateTime.UtcNow;
            var actor = await CurrentUserAsync();
            AuditLogger.Log(db, subject: null, actor: actor, action: "CompetitionEnabledChanged",
                field: competition.Name, previousValue: previous.ToString(), newValue: request.Enabled.ToString());
            await db.SaveChangesAsync();

            // Read its fixtures now rather than at its next fixtures check, hours away. Only the
            // newly switched-on competition is polled; La Liga keeps its own timing.
            if (request.Enabled) monitor.RequestCompetitionPoll();
        }

        return Ok(new Dtos.Competitions.CompetitionDto(competition.Id, competition.Name, competition.Enabled, competition.SortOrder));
    }

    // Where each competition other than La Liga is scraped from (La Liga's is on the settings
    // page, as before). The system admin's alone, like La Liga's.
    [HttpGet("api/admin/competitions/scraping")]
    [Authorize(Roles = "Admin")]
    [SystemUserOnly]
    public async Task<IActionResult> GetScraping()
    {
        var rows = await db.Competitions.AsNoTracking()
            .Where(c => c.Id != Competitions.LaLiga)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();
        return Ok(rows.Select(c =>
        {
            var plan = Competitions.SourcePlanOf(c);
            var available = sources.NamesFor(c.Id);
            var (sourceName, order) = SourcePlan.Usable(plan.SourceName, plan.SourceOrder, available);
            return new CompetitionScrapingDto(c.Id, c.Name, c.Enabled, plan.SourceMode, sourceName, order, available);
        }));
    }

    [HttpPut("api/admin/competitions/{id}/scraper-source")]
    [Authorize(Roles = "Admin")]
    [SystemUserOnly]
    public async Task<IActionResult> SetScraperSource(string id, SetScraperSourceRequest request)
    {
        if (id == Competitions.LaLiga)
            return BadRequest(new { message = "La Liga's sources are set on the settings page." });
        var competition = await db.Competitions.FindAsync(id);
        if (competition is null) return NotFound(new { message = "Competition not found." });

        var mode = SourceModes.Normalize(request.Mode);
        if (mode is null) return BadRequest(new { message = $"Unknown source mode: {request.Mode}" });

        var available = sources.NamesFor(id);
        string? Known(string name) =>
            available.FirstOrDefault(s => string.Equals(s, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        var sourceName = Known(request.SourceName);
        if (sourceName is null)
            return BadRequest(new { message = $"{request.SourceName} has no page set for {competition.Name}." });
        var unknown = request.SourceOrder.Where(n => Known(n) is null).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { message = $"No page set for {competition.Name} on: {string.Join(", ", unknown)}" });

        var order = request.SourceOrder.Select(n => Known(n)!).Distinct().ToList();
        if (mode != SourceModes.Single && order.Count < 2)
            return BadRequest(new { message = $"{mode} needs at least two sources." });

        var before = Competitions.SourcePlanOf(competition);
        var orderText = SourcePlan.FormatOrder(order);
        competition.SourceMode = mode;
        competition.SourceName = sourceName;
        competition.SourceOrder = orderText;
        competition.UpdatedAt = DateTime.UtcNow;

        var actor = await CurrentUserAsync();
        foreach (var (field, previous, value) in new[]
                 {
                     ("Source mode", before.SourceMode, mode),
                     ("Source", before.SourceName, sourceName),
                     ("Source order", before.SourceOrder, orderText),
                 }.Where(c => c.Item2 != c.Item3))
            AuditLogger.Log(db, subject: null, actor: actor, action: "ScraperSourceChanged",
                field: $"{competition.Name}: {field}", previousValue: previous, newValue: value);
        await db.SaveChangesAsync();

        return Ok(new CompetitionScrapingDto(competition.Id, competition.Name, competition.Enabled, mode, sourceName, order, available));
    }

    private async Task<Entities.User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    }
}
