using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController(AppDbContext db, ILogger<HealthController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetHealth()
    {
        try
        {
            // Test database connection
            var canConnect = await db.Database.CanConnectAsync();
            if (!canConnect)
                return StatusCode(503, new { status = "unhealthy", reason = "Cannot connect to database" });

            // Get statistics
            var matchWeekCount = await db.MatchWeeks.CountAsync();
            var fixtureCount = await db.Fixtures.CountAsync();
            var userCount = await db.Users.CountAsync();

            logger.LogInformation("Health check: {Weeks} weeks, {Fixtures} fixtures, {Users} users",
                matchWeekCount, fixtureCount, userCount);

            return Ok(new
            {
                status = "healthy",
                database = "connected",
                timestamp = DateTime.UtcNow,
                data = new
                {
                    matchWeeks = matchWeekCount,
                    fixtures = fixtureCount,
                    users = userCount
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check failed");
            return StatusCode(503, new
            {
                status = "unhealthy",
                error = ex.Message
            });
        }
    }

    [HttpGet("db")]
    public async Task<IActionResult> CheckDatabase()
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect
                ? Ok(new { database = "connected", connectionString = db.Database.GetConnectionString() })
                : StatusCode(503, new { database = "disconnected" });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { database = "error", error = ex.Message });
        }
    }
}
