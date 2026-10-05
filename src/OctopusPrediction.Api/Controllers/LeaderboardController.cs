using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Leaderboard;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Ranking: points, then correct scores called, then earliest submission.
[ApiController]
[Route("api/leaderboard")]
[Authorize]
public class LeaderboardController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetOverall()
    {
        var entries = await db.Users
            .Select(u => new
            {
                u.Id,
                u.Name,
                // Overall standings include points and correct scores carried over from before the app.
                PreviousPoints = u.PreviousPoints.Sum(pp => pp.Points),
                TotalPoints = u.Predictions.Sum(p => p.PointsEarned) + u.PreviousPoints.Sum(pp => pp.Points),
                CorrectScores = u.Predictions.Count(p => p.PointsEarned == ScoringService.CorrectScorePoints)
                    + u.PreviousPoints.Sum(pp => pp.CorrectScores),
                LastSubmittedAt = u.Predictions.Any()
                    ? (DateTime?)u.Predictions.Max(p => p.SubmittedAt)
                    : null
            })
            .OrderByDescending(x => x.TotalPoints)
            .ThenByDescending(x => x.CorrectScores)
            .ThenBy(x => x.LastSubmittedAt)
            .ToListAsync();

        return Ok(entries.Select((e, i) => new LeaderboardEntryDto(
            e.Id.ToString(), e.Name, e.TotalPoints, e.PreviousPoints, e.CorrectScores, i + 1, e.LastSubmittedAt
        )));
    }

    [HttpGet("{weekId}")]
    public async Task<IActionResult> GetByWeek(string weekId)
    {
        var entries = await db.Users
            .Select(u => new
            {
                u.Id,
                u.Name,
                TotalPoints = u.Predictions
                    .Where(p => p.WeekId == weekId)
                    .Sum(p => p.PointsEarned),
                CorrectScores = u.Predictions
                    .Count(p => p.WeekId == weekId && p.PointsEarned == ScoringService.CorrectScorePoints),
                LastSubmittedAt = u.Predictions.Any(p => p.WeekId == weekId)
                    ? (DateTime?)u.Predictions.Where(p => p.WeekId == weekId).Max(p => p.SubmittedAt)
                    : null
            })
            .OrderByDescending(x => x.TotalPoints)
            .ThenByDescending(x => x.CorrectScores)
            .ThenBy(x => x.LastSubmittedAt)
            .ToListAsync();

        return Ok(entries.Select((e, i) => new LeaderboardEntryDto(
            e.Id.ToString(), e.Name, e.TotalPoints, 0, e.CorrectScores, i + 1, e.LastSubmittedAt
        )));
    }
}
