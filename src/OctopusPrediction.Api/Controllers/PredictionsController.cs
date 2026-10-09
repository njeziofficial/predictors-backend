using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Dtos.Predictions;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/predictions")]
[Authorize]
public class PredictionsController(AppDbContext db, IOptions<LiveScraperSettings> defaults, AppCache cache) : ControllerBase
{
    [HttpGet("lock-status")]
    public async Task<IActionResult> GetLockStatus()
    {
        var settings = await cache.SettingsAsync(db, defaults.Value);
        return Ok(new
        {
            locked = settings.PredictionsLocked,
            rules = new PredictionRulesDto(settings.AllowPartialPredictions, settings.PredictionsFinal,
                settings.LockWeekAtFirstKickoff, settings.AllowLatePredictions)
        });
    }

    // A match stops taking predictions this long before its kickoff.
    private static readonly TimeSpan KickoffLockLead = TimeSpan.FromSeconds(30);

    [HttpPost]
    public async Task<IActionResult> Submit(SubmitPredictionsRequest request)
    {
        // Admins play too: they pick the main application after signing in.
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var settings = await cache.SettingsAsync(db, defaults.Value);
        if (settings.PredictionsLocked)
            return StatusCode(403, new { message = "Predictions are currently locked by an admin." });

        var user = await db.Users.FindAsync(userId);
        if (user?.MustResetPassword == true)
            return StatusCode(403, new
            {
                code = "must_reset_password",
                message = "You must reset your password before submitting predictions."
            });

        var week = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .FirstOrDefaultAsync(w => w.Id == request.WeekId);

        if (week is null) return NotFound(new { message = "Week not found." });

        // A match is open until KickoffLockLead before its kickoff, or until the scraper sees it
        // start, whichever comes first. Postponed and cancelled matches can't be predicted.
        var lockAt = DateTime.UtcNow + KickoffLockLead;
        var playable = week.Fixtures
            .Where(f => f.Status is not (FixtureStatus.Postponed or FixtureStatus.Cancelled))
            .ToList();
        var open = playable
            .Where(f => f.Status == FixtureStatus.PreMatch && f.Kickoff > lockAt)
            .ToDictionary(f => f.Id);
        var weekStarted = playable.Any(f => f.Status != FixtureStatus.PreMatch || f.Kickoff <= lockAt);

        var weekFixtureIds = week.Fixtures.Select(f => f.Id).ToList();
        var mine = await db.Predictions
            .Where(p => p.UserId == userId && weekFixtureIds.Contains(p.FixtureId))
            .ToDictionaryAsync(p => p.FixtureId);

        // Late predictions: only for players who had predicted nothing when the week locked.
        if (settings.LockWeekAtFirstKickoff && weekStarted && !(settings.AllowLatePredictions && mine.Count == 0))
            return BadRequest(new { message = "Predictions for this week locked when its first match kicked off." });

        if (open.Count == 0)
            return BadRequest(new { message = "Predictions are locked for this week." });

        var submitted = new Dictionary<string, (Fixture Fixture, OutcomeType Outcome, int? Home, int? Away)>();
        foreach (var item in request.Predictions)
        {
            var outcome = ParseOutcome(item.Outcome);
            if (outcome is null)
                return BadRequest(new { message = $"Invalid outcome: {item.Outcome}" });

            // Matches that have started (or aren't in this week) are skipped, not refused, so a
            // page left open across a kickoff can still save the rest.
            if (!open.TryGetValue(item.FixtureId, out var fixture)) continue;

            if (outcome == OutcomeType.CorrectScore && (item.HomeGoals is not >= 0 || item.AwayGoals is not >= 0))
                return BadRequest(new { message = $"Enter the score for {fixture.HomeTeam} vs {fixture.AwayTeam}." });

            submitted[item.FixtureId] = (fixture, outcome.Value, item.HomeGoals, item.AwayGoals);
        }

        if (settings.PredictionsFinal)
        {
            var changed = submitted.Values.FirstOrDefault(s =>
                mine.TryGetValue(s.Fixture.Id, out var p)
                && (p.Outcome != s.Outcome
                    // Goals only count for a score pick (other picks may carry stale ones).
                    || (s.Outcome == OutcomeType.CorrectScore && (p.HomeGoals != s.Home || p.AwayGoals != s.Away))));
            if (changed.Fixture is not null)
                return BadRequest(new
                {
                    message = $"Your prediction for {changed.Fixture.HomeTeam} vs {changed.Fixture.AwayTeam} " +
                              "is final and can't be changed."
                });
        }

        if (!settings.AllowPartialPredictions)
        {
            var missing = open.Values
                .Where(f => !mine.ContainsKey(f.Id) && !submitted.ContainsKey(f.Id))
                .OrderBy(f => f.Kickoff)
                .ToList();
            if (missing.Count > 0)
                return BadRequest(new
                {
                    message = $"Predict every match before submitting. Still to predict: " +
                              string.Join(", ", missing.Select(f => $"{f.HomeTeam} vs {f.AwayTeam}")) + "."
                });
        }

        if (submitted.Count == 0)
            return BadRequest(new { message = "Pick at least one match to predict." });

        var now = DateTime.UtcNow;
        foreach (var (fixtureId, s) in submitted)
        {
            if (mine.TryGetValue(fixtureId, out var existing))
            {
                // Under PredictionsFinal only unchanged resubmissions get here; leave them be.
                if (settings.PredictionsFinal) continue;
                existing.Outcome = s.Outcome;
                existing.HomeGoals = s.Home;
                existing.AwayGoals = s.Away;
                existing.SubmittedAt = now;
            }
            else
            {
                db.Predictions.Add(new Prediction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    FixtureId = fixtureId,
                    WeekId = request.WeekId,
                    Outcome = s.Outcome,
                    HomeGoals = s.Home,
                    AwayGoals = s.Away,
                    SubmittedAt = now
                });
            }
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "Predictions submitted." });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMine([FromQuery] string? weekId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var query = db.Predictions.Where(p => p.UserId == userId);

        if (!string.IsNullOrEmpty(weekId))
            query = query.Where(p => p.WeekId == weekId);

        var preds = await query.OrderBy(p => p.SubmittedAt).ToListAsync();
        return Ok(preds.Select(p => new PredictionDto(
            p.Id.ToString(),
            p.FixtureId,
            p.WeekId,
            OutcomeToString(p.Outcome),
            p.HomeGoals,
            p.AwayGoals,
            p.PointsEarned,
            p.SubmittedAt
        )));
    }

    private static OutcomeType? ParseOutcome(string? s) => s?.ToLower() switch
    {
        "home_win" => OutcomeType.HomeWin,
        "away_win" => OutcomeType.AwayWin,
        "draw" => OutcomeType.Draw,
        "correct_score" => OutcomeType.CorrectScore,
        _ => null
    };

    private static string OutcomeToString(OutcomeType o) => o switch
    {
        OutcomeType.HomeWin => "home_win",
        OutcomeType.AwayWin => "away_win",
        OutcomeType.Draw => "draw",
        OutcomeType.CorrectScore => "correct_score",
        _ => o.ToString().ToLower()
    };
}
