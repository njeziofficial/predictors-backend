using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.BallonDor;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// The Ballon d'Or side game for players: rank your top 5 before the lock, then see everyone's
// picks and, once the admin enters the result, who won what (BallonDorGame).
[ApiController]
[Route("api/ballon-dor")]
[Authorize]
public class BallonDorController(AppDbContext db) : ControllerBase
{
    // The latest event, or 204 when there's none or it's switched off.
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var ev = await db.BallonDorEvents.AsNoTracking().OrderByDescending(e => e.Year).FirstOrDefaultAsync();
        if (ev is not { Enabled: true }) return NoContent();
        return Ok(await ToDtoAsync(db, ev, Me(), board: DateTime.UtcNow >= ev.LocksAt));
    }

    [HttpPut("{year:int}/picks")]
    public async Task<IActionResult> Submit(int year, SubmitBallonDorPicksRequest request)
    {
        var ev = await db.BallonDorEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Year == year);
        if (ev is not { Enabled: true }) return NotFound(new { message = "There's no Ballon d'Or game for that year." });
        if (DateTime.UtcNow >= ev.LocksAt) return BadRequest(new { message = "Picks are locked: the ceremony has started." });

        var me = Me();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me);
        if (user is null) return Unauthorized();
        if (user.IsSystemUser) return BadRequest(new { message = "The system admin account doesn't play." });

        if (Canonical(request.Picks, ev.Nominees) is not { } picks)
            return BadRequest(new { message = $"Pick {BallonDorGame.Places} different players from the nominees." });

        var pick = await db.BallonDorPicks.FirstOrDefaultAsync(p => p.Year == year && p.UserId == me);
        if (pick is null)
            db.BallonDorPicks.Add(new BallonDorPick { Year = year, UserId = me, Picks = picks });
        else
            (pick.Picks, pick.UpdatedAt) = (picks, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return Ok(await ToDtoAsync(db, ev, me, board: false));
    }

    /// <summary>
    /// The names as the nominee list spells them, or null unless there are exactly five different
    /// nominees. Used for the admin's result too.
    /// </summary>
    internal static List<string>? Canonical(List<string>? names, List<string> nominees)
    {
        if (names is null || names.Count != BallonDorGame.Places) return null;
        var found = names.Select(n => nominees.FirstOrDefault(x => BallonDorGame.Same(x, n ?? ""))).ToList();
        if (found.Any(n => n is null)) return null;
        List<string> canonical = [.. found.Select(n => n!)];
        return canonical.Distinct(StringComparer.OrdinalIgnoreCase).Count() == BallonDorGame.Places ? canonical : null;
    }

    internal static async Task<BallonDorDto> ToDtoAsync(AppDbContext db, BallonDorEvent ev, Guid me, bool board)
    {
        var picks = await db.BallonDorPicks.AsNoTracking().Include(p => p.User)
            .Where(p => p.Year == ev.Year).ToListAsync();
        var prizes = BallonDorGame.Prizes(ev, picks);
        List<BallonDorEntryDto>? entries = null;
        if (board)
            entries = [.. picks
                .Select(p => new BallonDorEntryDto(
                    p.UserId.ToString(), p.User.Name, p.Picks, p.UpdatedAt,
                    BallonDorGame.CalledWinner(p.Picks, ev.Result), BallonDorGame.CalledTopFive(p.Picks, ev.Result),
                    BallonDorGame.CorrectPlaces(p.Picks, ev.Result), prizes[p.UserId].Winner + prizes[p.UserId].TopFive))
                .OrderByDescending(e => e.PrizeKobo)
                .ThenByDescending(e => e.CorrectPlaces)
                .ThenBy(e => e.Name)];
        return new BallonDorDto(
            ev.Year, ev.Nominees, ev.LocksAt, DateTime.UtcNow >= ev.LocksAt, ev.Result,
            ev.WinnerPotKobo, ev.TopFivePotKobo, picks.Count,
            picks.FirstOrDefault(p => p.UserId == me)?.Picks, entries, ev.Enabled);
    }

    private Guid Me() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
