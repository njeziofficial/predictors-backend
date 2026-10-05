using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(
    AppDbContext db,
    IOptions<LiveScraperSettings> defaults,
    IOptions<TwilioSettings> twilio,
    IEnumerable<IMatchSource> sources) : ControllerBase
{
    private readonly IReadOnlyList<string> _sourceNames = [.. sources.Select(s => s.Name)];

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        return Ok(ToDto(settings));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(UpdateScraperSettingsRequest request)
    {
        if (!_sourceNames.Contains(request.SourceName, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = $"Unknown source: {request.SourceName}" });

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        settings.Enabled = request.Enabled;
        settings.PollIntervalSeconds = request.PollIntervalSeconds;
        settings.Competition = request.Competition;
        settings.SourceName = request.SourceName;
        settings.ReminderEnabled = request.ReminderEnabled;
        settings.ReminderHoursBeforeFirstGame = request.ReminderHoursBeforeFirstGame;
        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    [HttpPut("predictions-lock")]
    public async Task<IActionResult> SetPredictionsLock(SetPredictionsLockRequest request)
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var changed = settings.PredictionsLocked != request.Locked;
        settings.PredictionsLocked = request.Locked;

        if (changed)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            AuditLogger.Log(db, subject: null, actor: actor, action: "PredictionsLockChanged",
                newValue: request.Locked.ToString());
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    // Stops new sign-ups through POST /api/auth/register. Existing users are unaffected.
    [HttpPut("registration")]
    public async Task<IActionResult> SetRegistrationClosed(SetRegistrationClosedRequest request)
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var changed = settings.RegistrationClosed != request.Closed;
        settings.RegistrationClosed = request.Closed;

        if (changed)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            AuditLogger.Log(db, subject: null, actor: actor, action: "RegistrationClosedChanged",
                newValue: request.Closed.ToString());
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    [HttpGet("audit-log-settings")]
    public async Task<IActionResult> GetAuditLogSettings()
    {
        if (!await IsSystemUserAsync()) return Forbid();

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        return Ok(new AuditLogSettingsDto(settings.AuditLogEnabled));
    }

    [HttpPut("audit-log-settings")]
    public async Task<IActionResult> SetAuditLogSettings(SetAuditLogEnabledRequest request)
    {
        if (!await IsSystemUserAsync()) return Forbid();

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        if (settings.AuditLogEnabled != request.Enabled)
        {
            // LogAlways, not Log — this must be recorded on both transitions (off *and* back
            // on) regardless of which state the gate itself is in at the moment, since it's
            // the one entry that explains any gap in the log around it.
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            AuditLogger.LogAlways(db, subject: null, actor: actor, action: "AuditLoggingChanged",
                newValue: request.Enabled.ToString());
        }

        settings.AuditLogEnabled = request.Enabled;
        await db.SaveChangesAsync();
        AuditLogSettings.Enabled = request.Enabled;

        return Ok(new AuditLogSettingsDto(settings.AuditLogEnabled));
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var lastScrapedAt = await db.Fixtures.MaxAsync(f => (DateTime?)f.UpdatedAt);
        var remindersConfigured = !string.IsNullOrWhiteSpace(twilio.Value.AccountSid)
            && !string.IsNullOrWhiteSpace(twilio.Value.AuthToken);
        return Ok(new AdminStatusDto(lastScrapedAt, settings.Enabled, remindersConfigured, settings.PredictionsLocked,
            settings.RegistrationClosed));
    }

    private async Task<bool> IsSystemUserAsync()
    {
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return await db.Users.AnyAsync(u => u.Id == currentUserId && u.IsSystemUser);
    }

    private ScraperSettingsDto ToDto(ScraperSettings s) =>
        new(s.Enabled, s.PollIntervalSeconds, s.Competition, s.SourceName, _sourceNames, s.PredictionsLocked, s.RegistrationClosed,
            s.ReminderEnabled, s.ReminderHoursBeforeFirstGame);
}
