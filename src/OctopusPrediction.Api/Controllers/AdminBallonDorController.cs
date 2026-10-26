using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.BallonDor;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Running the Ballon d'Or side game: the nominees, the lock time and the prizes, then the result,
// which pays the prizes into the winners' wallets. Paying money needs "Record payments".
[ApiController]
[Route("api/admin/ballon-dor")]
[Authorize(Roles = "Admin")]
public class AdminBallonDorController(AppDbContext db) : ControllerBase
{
    private const int MaxNominees = 60;
    private const int MaxNameLength = 100;

    // The latest event with everyone's picks, or 204 when there's none.
    [HttpGet]
    [RequirePermission(Permissions.WalletView)]
    public async Task<IActionResult> Get()
    {
        var ev = await db.BallonDorEvents.AsNoTracking().OrderByDescending(e => e.Year).FirstOrDefaultAsync();
        if (ev is null) return NoContent();
        return Ok(await BallonDorController.ToDtoAsync(db, ev, Me(), board: true));
    }

    // Creates the year's event or changes it, until the result is in.
    [HttpPut]
    [RequirePermission(Permissions.WalletManage)]
    public async Task<IActionResult> Save(SaveBallonDorEventRequest request)
    {
        if (request.Year is < 2000 or > 2100) return BadRequest(new { message = "Give the year of the ceremony." });
        var nominees = request.Nominees.Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (nominees.Count < BallonDorGame.Places || nominees.Count > MaxNominees)
            return BadRequest(new { message = $"List between {BallonDorGame.Places} and {MaxNominees} nominees, one per line." });
        if (nominees.Any(n => n.Length > MaxNameLength))
            return BadRequest(new { message = $"A nominee's name can be at most {MaxNameLength} characters." });
        if (request.WinnerPotKobo < 0 || request.TopFivePotKobo < 0
            || request.WinnerPotKobo + request.TopFivePotKobo > WalletLedger.MaxAmountKobo)
            return BadRequest(new { message = "The prizes can't be below nothing." });

        var ev = await db.BallonDorEvents.FirstOrDefaultAsync(e => e.Year == request.Year);
        if (ev?.Result is not null)
            return BadRequest(new { message = "The result is in. Clear it first to change the game." });

        // A nominee someone has picked can't be dropped: their picks would no longer make sense.
        var picked = await db.BallonDorPicks.Where(p => p.Year == request.Year).Select(p => p.Picks).ToListAsync();
        var dropped = picked.SelectMany(p => p).Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(n => !nominees.Any(x => BallonDorGame.Same(x, n)));
        if (dropped is not null)
            return BadRequest(new { message = $"{dropped} has already been picked, so can't be taken off the list." });

        if (ev is null)
        {
            ev = new BallonDorEvent { Year = request.Year };
            db.BallonDorEvents.Add(ev);
        }
        ev.Nominees = nominees;
        ev.LocksAt = request.LocksAt.ToUniversalTime();
        ev.WinnerPotKobo = request.WinnerPotKobo;
        ev.TopFivePotKobo = request.TopFivePotKobo;
        ev.UpdatedAt = DateTime.UtcNow;
        AuditLogger.Log(db, subject: null, actor: await CurrentUserAsync(), action: "BallonDorSaved",
            details: $"Ballon d'Or {ev.Year}: {nominees.Count} nominees, locks {ev.LocksAt:yyyy-MM-dd HH:mm} UTC, " +
                     $"prizes {WalletLedger.Naira(ev.WinnerPotKobo)} + {WalletLedger.Naira(ev.TopFivePotKobo)}");
        await db.SaveChangesAsync();
        return Ok(await BallonDorController.ToDtoAsync(db, ev, Me(), board: true));
    }

    // Enters (or corrects, or clears) the real top 5 and pays the prizes into the wallets.
    [HttpPut("{year:int}/result")]
    [RequirePermission(Permissions.WalletManage)]
    public async Task<IActionResult> SetResult(int year, SetBallonDorResultRequest request)
    {
        var ev = await db.BallonDorEvents.FirstOrDefaultAsync(e => e.Year == year);
        if (ev is null) return NotFound(new { message = "There's no Ballon d'Or game for that year." });
        if (DateTime.UtcNow < ev.LocksAt)
            return BadRequest(new { message = "Picks are still open. Enter the result after they lock." });

        List<string>? result = null;
        if (request.Result is { Count: > 0 })
        {
            result = BallonDorController.Canonical(request.Result, ev.Nominees);
            if (result is null)
                return BadRequest(new { message = $"Enter {BallonDorGame.Places} different players from the nominees, winner first." });
        }

        var actor = await CurrentUserAsync();
        ev.Result = result;
        ev.UpdatedAt = DateTime.UtcNow;
        AuditLogger.Log(db, subject: null, actor: actor, action: "BallonDorResultSet",
            details: result is null ? $"Ballon d'Or {year} result cleared" : $"Ballon d'Or {year} result: {string.Join(", ", result)}");
        // Saves the result along with the prizes, or nothing at all.
        if (await BallonDorGame.SettleAsync(db, ev, actor) is { } problem)
            return BadRequest(new { message = problem });
        return Ok(await BallonDorController.ToDtoAsync(db, ev, Me(), board: true));
    }

    // Shows the game to players or hides it from them. Only the system user.
    [HttpPut("{year:int}/enabled")]
    [SystemUserOnly]
    public async Task<IActionResult> SetEnabled(int year, SetBallonDorEnabledRequest request)
    {
        var ev = await db.BallonDorEvents.FirstOrDefaultAsync(e => e.Year == year);
        if (ev is null) return NotFound(new { message = "There's no Ballon d'Or game for that year." });
        if (ev.Enabled != request.Enabled)
        {
            ev.Enabled = request.Enabled;
            ev.UpdatedAt = DateTime.UtcNow;
            AuditLogger.Log(db, subject: null, actor: await CurrentUserAsync(), action: "BallonDorSwitched",
                details: $"Ballon d'Or {year} {(request.Enabled ? "shown to" : "hidden from")} players");
            await db.SaveChangesAsync();
        }
        return Ok(await BallonDorController.ToDtoAsync(db, ev, Me(), board: true));
    }

    private Guid Me() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<User?> CurrentUserAsync() => db.Users.FirstOrDefaultAsync(u => u.Id == Me());
}
