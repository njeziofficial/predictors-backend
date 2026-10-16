using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Wallet;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// A player's own wallet: whether they've paid, what they've won and been paid, and the pot.
// Read only; admins record the money (AdminWalletController).
[ApiController]
[Route("api/wallet")]
[Authorize]
public class WalletController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? season)
    {
        var me = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var (current, fee) = await WalletLedger.CurrentAsync(db);
        season ??= current;
        if (!WalletLedger.IsSeason(season)) return BadRequest(new { message = "A season looks like 2026/27." });

        var totals = await WalletLedger.TotalsAsync(db, season);
        var mine = totals.GetValueOrDefault(me, WalletLedger.Totals.None);
        var pot = WalletLedger.Pot(totals.Values);
        return Ok(new MyWalletDto(
            season, current, await WalletLedger.SeasonsAsync(db, current), fee,
            mine.Paid, mine.PaidIn, mine.Won, mine.PaidOut, mine.Balance,
            pot.Pot, totals.Values.Count(t => t.Paid),
            await AdminWalletController.EntriesAsync(db, me, season)));
    }
}
