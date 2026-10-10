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
    IEnumerable<IMatchSource> sources,
    SourceGuard guard) : ControllerBase
{
    private readonly IReadOnlyList<string> _sourceNames = [.. sources.Select(s => s.Name)];
    private readonly IReadOnlyList<string> _roundSources = [.. sources.Where(s => s.ProvidesRounds).Select(s => s.Name)];

    [HttpGet("settings")]
    [RequirePermission(Permissions.SettingsView)]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        return Ok(ToDto(settings));
    }

    [HttpPut("settings")]
    [RequirePermission(Permissions.SettingsManage)]
    public async Task<IActionResult> UpdateSettings(UpdateScraperSettingsRequest request)
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        settings.Enabled = request.Enabled;
        settings.PollIntervalSeconds = request.PollIntervalSeconds;
        settings.Competition = request.Competition;
        settings.ReminderEnabled = request.ReminderEnabled;
        settings.ReminderHoursBeforeFirstGame = request.ReminderHoursBeforeFirstGame;
        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    // Which site(s) live scores come from. Kept with the system user: a bad choice can stop new
    // weeks appearing or feed every player the wrong scores.
    [HttpPut("scraper-source")]
    [SystemUserOnly]
    public async Task<IActionResult> SetScraperSource(SetScraperSourceRequest request)
    {
        var mode = SourceModes.Normalize(request.Mode);
        if (mode is null)
            return BadRequest(new { message = $"Unknown source mode: {request.Mode}" });

        string? Known(string name) =>
            _sourceNames.FirstOrDefault(s => string.Equals(s, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        var sourceName = Known(request.SourceName);
        if (sourceName is null)
            return BadRequest(new { message = $"Unknown source: {request.SourceName}" });

        var unknown = request.SourceOrder.Where(n => Known(n) is null).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { message = $"Unknown source: {string.Join(", ", unknown)}" });

        var order = request.SourceOrder.Select(n => Known(n)!).Distinct().ToList();
        if (mode != SourceModes.Single && order.Count < 2)
            return BadRequest(new { message = $"{mode} needs at least two sources." });

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var orderText = SourcePlan.FormatOrder(order);
        var changes = new List<(string Field, string Previous, string New)>
        {
            ("Source mode", settings.SourceMode, mode),
            ("Source", settings.SourceName, sourceName),
            ("Source order", settings.SourceOrder, orderText),
        }.Where(c => c.Previous != c.New).ToList();

        settings.SourceMode = mode;
        settings.SourceName = sourceName;
        settings.SourceOrder = orderText;

        if (changes.Count > 0)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            foreach (var (field, previous, value) in changes)
                AuditLogger.Log(db, subject: null, actor: actor, action: "ScraperSourceChanged",
                    field: field, previousValue: previous, newValue: value);
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    [HttpPut("predictions-lock")]
    [RequirePermission(Permissions.SettingsManage)]
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
    [RequirePermission(Permissions.SettingsManage)]
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

    // Only the system user by default; it can be granted to other admins (Permissions.PredictionRulesManage).
    [HttpPut("prediction-rules")]
    [RequirePermission(Permissions.PredictionRulesManage)]
    public async Task<IActionResult> SetPredictionRules(PredictionRulesDto request)
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var changes = new List<(string Field, bool Previous, bool New)>
        {
            ("Allow incomplete predictions", settings.AllowPartialPredictions, request.AllowPartialPredictions),
            ("Predictions are final", settings.PredictionsFinal, request.PredictionsFinal),
            ("Lock week at first kickoff", settings.LockWeekAtFirstKickoff, request.LockWeekAtFirstKickoff),
            ("Allow late predictions", settings.AllowLatePredictions, request.AllowLatePredictions),
        }.Where(c => c.Previous != c.New).ToList();

        settings.AllowPartialPredictions = request.AllowPartialPredictions;
        settings.PredictionsFinal = request.PredictionsFinal;
        settings.LockWeekAtFirstKickoff = request.LockWeekAtFirstKickoff;
        settings.AllowLatePredictions = request.AllowLatePredictions;

        if (changes.Count > 0)
        {
            var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            foreach (var (field, previous, value) in changes)
                AuditLogger.Log(db, subject: null, actor: actor, action: "PredictionRuleChanged",
                    field: field, previousValue: previous ? "On" : "Off", newValue: value ? "On" : "Off");
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(settings));
    }

    // Switching the audit log off would let an admin hide their own changes, so it stays with the system user.
    [HttpGet("audit-log-settings")]
    [SystemUserOnly]
    public async Task<IActionResult> GetAuditLogSettings()
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        return Ok(new AuditLogSettingsDto(settings.AuditLogEnabled));
    }

    [HttpPut("audit-log-settings")]
    [SystemUserOnly]
    public async Task<IActionResult> SetAuditLogSettings(SetAuditLogEnabledRequest request)
    {
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
        var sourceProblems = guard.Snapshot()
            .Select(h => new SourceHealthDto(h.Name, h.Failures, h.CoolingUntil > DateTime.UtcNow ? h.CoolingUntil : null, h.LastProblem))
            .ToList();
        return Ok(new AdminStatusDto(lastScrapedAt, settings.Enabled, remindersConfigured, settings.PredictionsLocked,
            settings.RegistrationClosed, sourceProblems));
    }

    private ScraperSettingsDto ToDto(ScraperSettings s) =>
        new(s.Enabled, s.PollIntervalSeconds, s.Competition, s.SourceName, _sourceNames,
            SourceModes.Normalize(s.SourceMode) ?? SourceModes.Single, SourcePlan.ParseOrder(s.SourceOrder), _roundSources,
            s.PredictionsLocked, s.RegistrationClosed,
            s.ReminderEnabled, s.ReminderHoursBeforeFirstGame,
            new PredictionRulesDto(s.AllowPartialPredictions, s.PredictionsFinal, s.LockWeekAtFirstKickoff,
                s.AllowLatePredictions));
}
