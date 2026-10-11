using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Monitoring;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Controllers;

// The system admin's view of the live scraper: what it's doing, how each source and the update
// queue are behaving, matches that look wrong, the points checks and recent log lines.
[ApiController]
[Route("api/admin/system")]
[Authorize(Roles = "Admin")]
[SystemUserOnly]
public class AdminScraperHealthController(
    AppDbContext db,
    IOptions<LiveScraperSettings> defaults,
    IEnumerable<IMatchSource> sources,
    SourceGuard guard,
    ScraperMonitor monitor,
    MatchUpdateQueue queue,
    RecentLogs logs,
    IWebHostEnvironment env) : ControllerBase
{
    // A match is over well within this; one still live after it is stuck.
    private static readonly TimeSpan LongestMatch = TimeSpan.FromMinutes(150);
    private static readonly TimeSpan LateKickoff = TimeSpan.FromMinutes(15);

    [HttpGet("scraper-health")]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value, ct);
        var mode = SourceModes.Normalize(settings.SourceMode) ?? SourceModes.Single;
        IReadOnlyList<string> inUse = mode == SourceModes.Single ? [settings.SourceName] : SourcePlan.ParseOrder(settings.SourceOrder);

        var polls = monitor.Polls();
        var state = new HealthStateDto(monitor.Phase, monitor.PhaseDetail, monitor.NextPollAt, monitor.Schedule,
            polls.FirstOrDefault()?.At, polls.FirstOrDefault(p => p.Outcome == "Ok")?.At);

        // ── Sources ─────────────────────────────────────────────────────────────────────────
        var activity = monitor.Sources().ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var health = guard.Snapshot().ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
        var sourceDtos = sources.Select(s =>
        {
            var a = activity.GetValueOrDefault(s.Name);
            var h = health.GetValueOrDefault(s.Name);
            return new HealthSourceDto(s.Name, inUse.Contains(s.Name, StringComparer.OrdinalIgnoreCase),
                a?.LastAttemptAt, a?.LastSuccessAt, a?.LastMatches ?? 0, a?.LastDurationMs ?? 0, a?.LastResult,
                h?.Failures ?? 0, h?.CoolingUntil > now ? h.CoolingUntil : null, h?.LastProblem);
        })
        .OrderByDescending(s => s.InUse).ThenBy(s => s.Name)
        .ToList();

        // ── Queue ───────────────────────────────────────────────────────────────────────────
        var pending = await db.MatchUpdates.Where(u => u.ProcessedAt == null).CountAsync(ct);
        var oldestPending = await db.MatchUpdates.Where(u => u.ProcessedAt == null).MinAsync(u => (DateTime?)u.ScrapedAt, ct);
        var appliedLastHour = await db.MatchUpdates.CountAsync(u => u.ProcessedAt > now.AddHours(-1) && u.Outcome == MatchUpdateOutcomes.Applied, ct);
        var failedLastDay = await db.MatchUpdates.CountAsync(u => u.ProcessedAt > now.AddDays(-1) && u.Outcome == MatchUpdateOutcomes.Failed, ct);
        var lastApplied = await db.MatchUpdates.MaxAsync(u => (DateTime?)u.ProcessedAt, ct);
        var recentRows = await db.MatchUpdates.AsNoTracking().OrderByDescending(u => u.Id).Take(30).ToListAsync(ct);
        var queueDto = new HealthQueueDto(pending, oldestPending, appliedLastHour, failedLastDay, lastApplied,
            defaults.Value.UpdateRetentionHours,
            recentRows.Select(r => new HealthQueueRowDto(r.Id, r.FixtureId, r.Source, r.ScrapedAt, r.ProcessedAt,
                r.Outcome, r.Attempts, r.Error, Describe(r.Payload))).ToList());

        // ── Matches around now ──────────────────────────────────────────────────────────────
        var fixtures = await db.Fixtures.AsNoTracking()
            .Where(f => (f.Kickoff > now.AddHours(-12) && f.Kickoff < now.AddHours(12))
                || f.Status == FixtureStatus.Live || f.Status == FixtureStatus.HalfTime)
            .OrderBy(f => f.Kickoff)
            .ToListAsync(ct);
        var matchDtos = fixtures.Select(f =>
        {
            string? problem = null;
            if (f.Status is FixtureStatus.Live or FixtureStatus.HalfTime && now - f.Kickoff > LongestMatch)
                problem = $"Still showing live {Hours(now - f.Kickoff)} after kickoff. The match is surely over: the source " +
                    "may be serving an old page, or another backend may be writing to this database.";
            else if (f.Status == FixtureStatus.PreMatch && now - f.Kickoff > LateKickoff && now - f.Kickoff < TimeSpan.FromHours(6))
                problem = $"Should have kicked off {Hours(now - f.Kickoff)} ago but hasn't been seen to start.";
            var ended = f.Status == FixtureStatus.Ended;
            return new HealthMatchDto(f.Id, f.HomeTeam, f.AwayTeam, f.Kickoff, FixtureMappers.MapStatus(f.Status),
                ended ? f.FinalScoreHome : f.LiveScoreHome, ended ? f.FinalScoreAway : f.LiveScoreAway,
                f.LiveMinute, f.UpdatedAt, problem);
        }).ToList();

        // Fixtures changed lately after this server last applied anything to them: another backend on
        // this database. An admin edit touches one fixture at a time; an older backend rewrites every
        // fixture each poll. (FixtureSync stamps UpdatedAt just before the row is marked applied.)
        var touched = await db.Fixtures.AsNoTracking()
            .Where(f => f.UpdatedAt > now.AddMinutes(-10) && f.UpdatedAt > monitor.StartedAt)
            .Select(f => new { f.Id, f.UpdatedAt })
            .ToListAsync(ct);
        var touchedIds = touched.Select(t => t.Id).ToList();
        var appliedAt = await db.MatchUpdates
            .Where(u => touchedIds.Contains(u.FixtureId) && u.ProcessedAt != null)
            .GroupBy(u => u.FixtureId)
            .Select(g => new { FixtureId = g.Key, At = g.Max(u => u.ProcessedAt) })
            .ToDictionaryAsync(x => x.FixtureId, x => x.At, ct);
        var foreignWrites = touched.Count(t =>
            appliedAt.GetValueOrDefault(t.Id) is not { } applied || t.UpdatedAt > applied.AddSeconds(5));

        // ── Points ──────────────────────────────────────────────────────────────────────────
        var weeks = await db.MatchWeeks.AsNoTracking()
            .Select(w => new
            {
                w.Id, w.Name, w.ReconciledAt,
                Fixtures = w.Fixtures.Count,
                Finished = w.Fixtures.Count(f => f.Status == FixtureStatus.Ended || f.Status == FixtureStatus.Postponed || f.Status == FixtureStatus.Cancelled),
                FirstKickoff = w.Fixtures.Min(f => (DateTime?)f.Kickoff),
            })
            .ToListAsync(ct);
        var weekDtos = weeks.Where(w => w.FirstKickoff < now.AddDays(3))
            .OrderByDescending(w => w.FirstKickoff).Take(6)
            .Select(w => new HealthWeekDto(w.Id, w.Name, w.Fixtures, w.Finished, w.ReconciledAt)).ToList();
        var pointsDto = new HealthPointsDto(monitor.ReconcileLastRunAt, monitor.ReconcileLastCorrections, monitor.ReconcileLastError, weekDtos);

        // ── Warnings ────────────────────────────────────────────────────────────────────────
        var warnings = new List<HealthWarningDto>();
        if (foreignWrites >= 5)
            warnings.Add(new("error", "Another backend is writing to this database",
                $"{foreignWrites} fixtures changed in the last 10 minutes that this server didn't change. An older backend " +
                "(a local Docker API, say) pointed at this database overwrites scores and points. Stop it."));
        foreach (var m in matchDtos.Where(m => m.Problem is not null))
            warnings.Add(new(m.Status == "live" ? "error" : "warning", $"{m.HomeTeam} v {m.AwayTeam}", m.Problem!));
        if (!settings.Enabled)
            warnings.Add(new("warning", "The scraper is switched off", "No scores are being read. Switch it on in Settings."));
        else
        {
            var stale = TimeSpan.FromSeconds(Math.Max(180, settings.PollIntervalSeconds * 3));
            if (monitor.Schedule == "InPlay" && (state.LastSuccessAt is null || now - state.LastSuccessAt > stale))
                warnings.Add(new("error", "Scores aren't updating",
                    $"A match is in play but no source has returned matches since {Since(state.LastSuccessAt, now)}."));
            var resting = sourceDtos.Where(s => s.InUse && s.CoolingUntil is not null).ToList();
            if (resting.Count > 0 && resting.Count == inUse.Count)
                warnings.Add(new("error", "Every source in use is resting",
                    $"{string.Join(", ", resting.Select(s => s.Name))} refused or kept failing. Add another source (Fallback or Rotate) in Settings."));
            else
                foreach (var s in resting)
                    warnings.Add(new("warning", $"{s.Name} is resting", $"Until {s.CoolingUntil:HH:mm} UTC: {s.LastProblem}"));
        }
        if (oldestPending is { } oldest && now - oldest > TimeSpan.FromMinutes(2))
            warnings.Add(new("warning", "Updates are waiting", $"{pending} scraped updates have waited since {oldest:HH:mm:ss} UTC to be applied."));
        if (failedLastDay > 0)
            warnings.Add(new("error", "Updates failed", $"{failedLastDay} scraped updates couldn't be applied in the last 24 hours. See the queue below."));
        if (monitor.ReconcileLastError is { } error)
            warnings.Add(new("error", "The points check failed", error));

        warnings = [.. warnings.OrderBy(w => w.Severity == "error" ? 0 : 1)];

        return Ok(new ScraperHealthDto(
            now,
            new HealthProcessDto(monitor.StartedAt, env.EnvironmentName, Version()),
            new HealthSettingsDto(settings.Enabled, mode, inUse, settings.PollIntervalSeconds, settings.Competition),
            state, warnings, polls, sourceDtos, queueDto, pointsDto, matchDtos, logs.Snapshot()));
    }

    // Polls straight away and re-applies every match, not only the changed ones.
    [HttpPost("scraper/poll-now")]
    public async Task<IActionResult> PollNow(CancellationToken ct)
    {
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value, ct);
        if (!settings.Enabled)
            return Conflict(new { message = "The scraper is switched off. Switch it on in Settings first." });
        queue.ForgetAll();
        monitor.RequestPollNow();
        return Ok(new { message = "Polling now. Every match will be re-applied." });
    }

    private static string Describe(string payload)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ScrapedMatchDto>(payload);
            if (m is null) return "";
            var status = MatchReading.MapStatus(m);
            var score = m.ScoreHome is not null && m.ScoreAway is not null ? $" {m.ScoreHome}-{m.ScoreAway}" : "";
            var minute = status == FixtureStatus.Live && MatchReading.ParseMinute(m.Stage) is { } min ? $" {min}'" : "";
            return status switch
            {
                FixtureStatus.PreMatch => "Not started",
                FixtureStatus.Postponed or FixtureStatus.Cancelled => status.ToString(),
                FixtureStatus.HalfTime => $"Half time{score}",
                _ => $"{status}{minute}{score}",
            };
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Hours(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{(int)span.TotalMinutes} minutes";

    private static string Since(DateTime? at, DateTime now) =>
        at is { } t ? $"{t:HH:mm} UTC ({Hours(now - t)} ago)" : "this server started";

    // Render sets RENDER_GIT_COMMIT; otherwise the build's informational version (which carries the commit too).
    private static string? Version()
    {
        var commit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(commit)) return commit.Length > 7 ? commit[..7] : commit;
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = info?.IndexOf('+') ?? -1;
        return plus >= 0 && info!.Length > plus + 8 ? info[(plus + 1)..(plus + 8)] : info;
    }
}
