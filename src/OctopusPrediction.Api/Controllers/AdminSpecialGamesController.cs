using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Dtos.SpecialGames;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Running special games (see SpecialGames): which matches have a pot, who won it, and whether the
// organiser has sent each winner their share. The system admin only.
[ApiController]
[Route("api/admin/special-games")]
[Authorize(Roles = "Admin")]
[SystemUserOnly]
public class AdminSpecialGamesController(AppDbContext db) : ControllerBase
{
    // A match stops taking predictions this long before kickoff, so it can't be made special later.
    private static readonly TimeSpan KickoffLockLead = TimeSpan.FromSeconds(30);

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await GamesAsync());

    // Makes a match special, or changes its pot. A pot changed after the result moves the prizes.
    [HttpPut("{fixtureId}")]
    public async Task<IActionResult> SetPot(string fixtureId, SetSpecialPotRequest request)
    {
        if (request.PotKobo <= 0 || request.PotKobo > WalletLedger.MaxAmountKobo)
            return BadRequest(new { message = $"The pot must be more than nothing and at most {WalletLedger.Naira(WalletLedger.MaxAmountKobo)}." });

        var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId);
        if (fixture is null) return NotFound(new { message = "Match not found." });
        if (fixture.SpecialPotKobo is null
            && (fixture.Status != FixtureStatus.PreMatch || fixture.Kickoff <= DateTime.UtcNow + KickoffLockLead))
            return BadRequest(new { message = "A match can only be made special before it kicks off." });

        if (await SpecialGames.SetPotAsync(db, fixture, request.PotKobo, await CurrentUserAsync()) is { } problem)
            return BadRequest(new { message = problem });
        return Ok(await GamesAsync());
    }

    // Makes a match ordinary again, taking back any prizes not yet paid.
    [HttpDelete("{fixtureId}")]
    public async Task<IActionResult> Remove(string fixtureId)
    {
        var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId);
        if (fixture is null) return NotFound(new { message = "Match not found." });

        if (await SpecialGames.SetPotAsync(db, fixture, null, await CurrentUserAsync()) is { } problem)
            return BadRequest(new { message = problem });
        return Ok(await GamesAsync());
    }

    // Records that a winner has (or hasn't, after all) been sent their share by bank transfer.
    [HttpPut("{fixtureId}/prizes/{userId:guid}/paid")]
    public async Task<IActionResult> SetPaid(string fixtureId, Guid userId, SetSpecialPrizePaidRequest request)
    {
        var actor = await CurrentUserAsync();
        await AdminWalletController.Writes.WaitAsync();
        try
        {
            var prize = await db.SpecialGamePrizes.Include(p => p.User).Include(p => p.Fixture)
                .FirstOrDefaultAsync(p => p.FixtureId == fixtureId && p.UserId == userId);
            if (prize is null) return NotFound(new { message = "That player has no prize for this game." });

            if (request.Paid != prize.PayoutEntryId.HasValue)
            {
                var match = SpecialGames.Match(prize.Fixture);
                if (request.Paid)
                {
                    var season = await db.WalletEntries.Where(e => e.Id == prize.EntryId).Select(e => e.Season).FirstAsync();
                    var totals = await WalletLedger.TotalsAsync(db, season);
                    var mine = totals.GetValueOrDefault(userId, WalletLedger.Totals.None);
                    if (WalletLedger.Check(WalletKinds.Payout, -prize.AmountKobo, mine, WalletLedger.Pot(totals.Values)) is { } problem)
                        return BadRequest(new { message = problem });

                    var payout = SpecialGames.NewEntry(userId, season, -prize.AmountKobo, $"Special game: {match}", actor, WalletKinds.Payout);
                    db.WalletEntries.Add(payout);
                    prize.PayoutEntryId = payout.Id;
                    prize.PaidAt = payout.OccurredAt;
                }
                else
                {
                    var payout = await db.WalletEntries.FirstAsync(e => e.Id == prize.PayoutEntryId);
                    var reversal = SpecialGames.NewEntry(userId, payout.Season, -payout.AmountKobo, $"Special game: {match}, marked as unpaid", actor, WalletKinds.Payout);
                    reversal.ReversesEntryId = payout.Id;
                    db.WalletEntries.Add(reversal);
                    prize.PayoutEntryId = null;
                    prize.PaidAt = null;
                }
                AuditLogger.Log(db, subject: prize.User, actor: actor, action: "SpecialGamePrizePaid",
                    details: $"{WalletLedger.Naira(prize.AmountKobo)} prize marked as {(request.Paid ? "paid" : "unpaid")}: {match}".Truncate(500));
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            AdminWalletController.Writes.Release();
        }

        // Unpaid, it may now be free to follow a correction that was waiting on it.
        if (!request.Paid) await SpecialGames.SettleAsync(db, fixtureId, actor);
        return Ok(await GamesAsync());
    }

    private async Task<List<SpecialGameDto>> GamesAsync()
    {
        var fixtures = await db.Fixtures.AsNoTracking().Include(f => f.Week)
            .Where(f => f.SpecialPotKobo != null)
            .OrderByDescending(f => f.Kickoff)
            .ToListAsync();
        var ids = fixtures.Select(f => f.Id).ToList();
        var picks = await db.Predictions.AsNoTracking()
            .Where(p => ids.Contains(p.FixtureId))
            .GroupBy(p => new { p.FixtureId, Score = p.Outcome == OutcomeType.CorrectScore })
            .Select(g => new { g.Key.FixtureId, g.Key.Score, Count = g.Count() })
            .ToListAsync();
        var prizes = (await db.SpecialGamePrizes.AsNoTracking().Include(p => p.User)
                .Where(p => ids.Contains(p.FixtureId))
                .OrderBy(p => p.User.Name)
                .ToListAsync())
            .ToLookup(p => p.FixtureId);

        int Count(string fixtureId, bool score) =>
            picks.FirstOrDefault(p => p.FixtureId == fixtureId && p.Score == score)?.Count ?? 0;

        return [.. fixtures.Select(f => new SpecialGameDto(
            f.Id, f.Week.Name, f.Week.Competition, f.HomeTeam, f.AwayTeam, f.Kickoff, FixtureMappers.MapStatus(f.Status),
            f.FinalScoreHome is { } home && f.FinalScoreAway is { } away ? new ScoreDto(home, away) : null,
            f.SpecialPotKobo!.Value, Count(f.Id, true), Count(f.Id, false), f.SpecialProblem,
            [.. prizes[f.Id].Select(p => new SpecialPrizeDto(
                p.UserId.ToString(), p.User.Name, p.User.WhatsAppName, p.AmountKobo, p.PayoutEntryId is not null, p.PaidAt))]))];
    }

    private Task<User?> CurrentUserAsync()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}
