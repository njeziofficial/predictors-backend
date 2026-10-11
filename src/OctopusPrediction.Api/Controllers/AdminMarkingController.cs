using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminMarkingController(WeekMarking marking) : ControllerBase
{
    // Every player's picks for the week with the result and points of each (see WeekMarking).
    [HttpGet("weeks/{weekId}/marking")]
    [RequirePermission(Permissions.MarkingView)]
    public async Task<IActionResult> GetMarking(string weekId, CancellationToken ct)
    {
        var result = await marking.ForWeekAsync(weekId, ct);
        return result is null ? NotFound(new { message = "Week not found." }) : Ok(result);
    }
}
