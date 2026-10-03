using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Leaderboard;

namespace OctopusPrediction.Api.Controllers;

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
                TotalPoints = u.Predictions.Sum(p => p.PointsEarned),
                LastSubmittedAt = u.Predictions.Any()
                    ? (DateTime?)u.Predictions.Max(p => p.SubmittedAt)
                    : null
            })
            .OrderByDescending(x => x.TotalPoints)
            .ThenBy(x => x.LastSubmittedAt)
            .ToListAsync();

        return Ok(entries.Select((e, i) => new LeaderboardEntryDto(
            e.Id.ToString(), e.Name, e.TotalPoints, i + 1, e.LastSubmittedAt
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
                LastSubmittedAt = u.Predictions.Any(p => p.WeekId == weekId)
                    ? (DateTime?)u.Predictions.Where(p => p.WeekId == weekId).Max(p => p.SubmittedAt)
                    : null
            })
            .OrderByDescending(x => x.TotalPoints)
            .ThenBy(x => x.LastSubmittedAt)
            .ToListAsync();

        return Ok(entries.Select((e, i) => new LeaderboardEntryDto(
            e.Id.ToString(), e.Name, e.TotalPoints, i + 1, e.LastSubmittedAt
        )));
    }
}
