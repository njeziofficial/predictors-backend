using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
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
        return Ok(new { locked = settings.PredictionsLocked });
    }

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

        var earliest = week.Fixtures
            .Where(f => f.Status == FixtureStatus.PreMatch)
            .MinBy(f => f.Kickoff);

        if (earliest is null)
            return BadRequest(new { message = "No open fixtures in this week." });

        if (DateTime.UtcNow >= earliest.Kickoff.AddSeconds(-30))
            return BadRequest(new { message = "Predictions are locked for this week." });

        foreach (var item in request.Predictions)
        {
            var outcome = ParseOutcome(item.Outcome);
            if (outcome is null)
                return BadRequest(new { message = $"Invalid outcome: {item.Outcome}" });

            var fixture = week.Fixtures.FirstOrDefault(f => f.Id == item.FixtureId);
            if (fixture is null || fixture.Status != FixtureStatus.PreMatch) continue;

            var existing = await db.Predictions
                .FirstOrDefaultAsync(p => p.UserId == userId && p.FixtureId == item.FixtureId);

            if (existing is null)
            {
                db.Predictions.Add(new Prediction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    FixtureId = item.FixtureId,
                    WeekId = request.WeekId,
                    Outcome = outcome.Value,
                    HomeGoals = item.HomeGoals,
                    AwayGoals = item.AwayGoals,
                    SubmittedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.Outcome = outcome.Value;
                existing.HomeGoals = item.HomeGoals;
                existing.AwayGoals = item.AwayGoals;
                existing.SubmittedAt = DateTime.UtcNow;
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
