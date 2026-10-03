using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/seed")]
public class SeedDataController(AppDbContext db, ILogger<SeedDataController> logger) : ControllerBase
{
    [HttpPost("test-data")]
    public async Task<IActionResult> SeedTestData()
    {
        try
        {
            // Check if data already exists
            if (await db.MatchWeeks.AnyAsync())
                return BadRequest(new { message = "Database already contains MatchWeeks. Delete them first to seed test data." });

            logger.LogInformation("[SeedDataController] Starting test data seeding...");

            // Create test match weeks
            var week1 = new MatchWeek
            {
                Id = "premier-league-week-1",
                Name = "Premier League — Week 1",
                Competition = "Premier League"
            };

            var week2 = new MatchWeek
            {
                Id = "premier-league-week-2",
                Name = "Premier League — Week 2",
                Competition = "Premier League"
            };

            db.MatchWeeks.AddRange(week1, week2);
            await db.SaveChangesAsync();

            logger.LogInformation("[SeedDataController] Created {Count} MatchWeeks", 2);

            // Create test fixtures
            var now = DateTime.UtcNow;
            var fixtures = new List<Fixture>
            {
                new()
                {
                    Id = "fixture-1",
                    WeekId = "premier-league-week-1",
                    HomeTeam = "Manchester United",
                    AwayTeam = "Liverpool",
                    Kickoff = now.AddDays(1).Date.AddHours(15),
                    Status = FixtureStatus.PreMatch,
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "fixture-2",
                    WeekId = "premier-league-week-1",
                    HomeTeam = "Arsenal",
                    AwayTeam = "Chelsea",
                    Kickoff = now.AddDays(1).Date.AddHours(17).AddMinutes(30),
                    Status = FixtureStatus.PreMatch,
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "fixture-3",
                    WeekId = "premier-league-week-1",
                    HomeTeam = "Manchester City",
                    AwayTeam = "Tottenham",
                    Kickoff = now.AddDays(2).Date.AddHours(15),
                    Status = FixtureStatus.PreMatch,
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "fixture-4",
                    WeekId = "premier-league-week-2",
                    HomeTeam = "Liverpool",
                    AwayTeam = "Arsenal",
                    Kickoff = now.AddDays(8).Date.AddHours(15),
                    Status = FixtureStatus.PreMatch,
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "fixture-5",
                    WeekId = "premier-league-week-2",
                    HomeTeam = "Chelsea",
                    AwayTeam = "Manchester City",
                    Kickoff = now.AddDays(8).Date.AddHours(17).AddMinutes(30),
                    Status = FixtureStatus.PreMatch,
                    UpdatedAt = DateTime.UtcNow
                }
            };

            db.Fixtures.AddRange(fixtures);
            await db.SaveChangesAsync();

            logger.LogInformation("[SeedDataController] Created {Count} Fixtures", fixtures.Count);

            return Ok(new
            {
                message = "Test data seeded successfully",
                matchWeeks = await db.MatchWeeks.CountAsync(),
                fixtures = await db.Fixtures.CountAsync()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SeedDataController] Error seeding test data");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("clear-all")]
    public async Task<IActionResult> ClearAllData()
    {
        try
        {
            logger.LogWarning("[SeedDataController] Clearing all data from database...");

            // Delete in correct order due to foreign keys
            await db.Predictions.ExecuteDeleteAsync();
            await db.Fixtures.ExecuteDeleteAsync();
            await db.MatchWeeks.ExecuteDeleteAsync();

            logger.LogInformation("[SeedDataController] Database cleared successfully");

            return Ok(new { message = "All data cleared successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SeedDataController] Error clearing data");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
