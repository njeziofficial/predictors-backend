using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Read by the Cloudflare Worker's cron (no user to sign in as) to know when to keep the backend
// awake. Anonymous: it only reveals when matches kick off, which is public anyway.
[ApiController]
[Route("api/public/wake-windows")]
[AllowAnonymous]
public class WakeWindowsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windows = await MatchWindows.ForBackendAsync(db, now, ct);
        return Ok(new { generatedAt = now, windows = windows.Select(w => new { start = w.Start, end = w.End }) });
    }
}
