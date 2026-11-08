using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Special games: an admin with "Run special games" puts a pot on a match (Fixture.SpecialPotKobo),
// players predict its exact score on the Special games tab (SpecialGamePick), and everyone who
// gets it right shares the pot equally, rounded down to the kobo. A pot nobody wins isn't paid.
// None of this touches the match's normal predictions or league points.
//
// Prizes follow the match's result automatically, including a wrong "finished" reading being put
// right. They're adjustments, not winnings, like the Ballon d'Or prizes: the organiser puts them
// up, so they mustn't come out of the season pot. Marking one as paid records a ledger payout.
public static class SpecialGames
{
    public static long Share(long pot, int winners) => winners == 0 ? 0 : pot / winners;

    // The switch for the whole feature. Off, prizes already in wallets stay, but none are added.
    public static async Task<bool> EnabledAsync(AppDbContext db, CancellationToken ct = default) =>
        await db.ScraperSettings.AsNoTracking().Select(s => (bool?)s.SpecialGamesEnabled).FirstOrDefaultAsync(ct) ?? true;

    // Scores close this long before kickoff, as normal predictions do.
    public static readonly TimeSpan KickoffLockLead = TimeSpan.FromSeconds(30);

    public static bool IsOpen(Fixture fixture) =>
        fixture.Status == FixtureStatus.PreMatch && fixture.Kickoff > DateTime.UtcNow + KickoffLockLead;

    public static bool Called(Fixture fixture, SpecialGamePick pick) =>
        fixture.Status == FixtureStatus.Ended
        && fixture.FinalScoreHome is not null
        && fixture.FinalScoreAway is not null
        && pick.HomeGoals == fixture.FinalScoreHome
        && pick.AwayGoals == fixture.FinalScoreAway;

    /// <summary>
    /// Brings the wallets in line with the match as it stands. Safe to run at any time. If it
    /// can't (someone already paid would lose out), nothing moves and the reason is kept on the
    /// fixture for the admin page.
    /// </summary>
    public static async Task SettleAsync(AppDbContext db, string fixtureId, User? actor, CancellationToken ct = default)
    {
        await AdminWalletController.Writes.WaitAsync(ct);
        try
        {
            var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId, ct);
            if (fixture is null) return;
            fixture.SpecialProblem = await ApplyAsync(db, fixture, actor, ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            AdminWalletController.Writes.Release();
        }
    }

    /// <summary>Sets or (null) removes a match's pot, moving any prizes to match. Returns why it can't, changing nothing.</summary>
    public static async Task<string?> SetPotAsync(AppDbContext db, Fixture fixture, long? pot, User? actor)
    {
        await AdminWalletController.Writes.WaitAsync();
        try
        {
            var previous = fixture.SpecialPotKobo;
            fixture.SpecialPotKobo = pot;
            if (await ApplyAsync(db, fixture, actor, default) is { } problem)
            {
                fixture.SpecialPotKobo = previous;
                return problem;
            }
            fixture.SpecialProblem = null;
            AuditLogger.Log(db, subject: null, actor: actor, action: "SpecialGameSaved",
                details: $"{Match(fixture)}: pot {(previous is { } p ? WalletLedger.Naira(p) : "none")} -> {(pot is { } n ? WalletLedger.Naira(n) : "none")}");
            await db.SaveChangesAsync();
            return null;
        }
        finally
        {
            AdminWalletController.Writes.Release();
        }
    }

    // Plans every change first, and touches nothing unless all of it can happen. The caller saves.
    private static async Task<string?> ApplyAsync(AppDbContext db, Fixture fixture, User? actor, CancellationToken ct)
    {
        // Paused while switched off (it catches up when switched back on). Removing a game still works.
        if (fixture.SpecialPotKobo is not null && !await EnabledAsync(db, ct)) return null;

        var prizes = await db.SpecialGamePrizes.Include(p => p.User).Where(p => p.FixtureId == fixture.Id).ToListAsync(ct);
        var picks = fixture.SpecialPotKobo is null
            ? []
            : await db.SpecialGamePicks.Include(p => p.User)
                .Where(p => p.FixtureId == fixture.Id && !p.User.IsSystemUser)
                .ToListAsync(ct);
        var winners = picks.Where(p => Called(fixture, p)).Select(p => p.User).ToList();
        var share = Share(fixture.SpecialPotKobo ?? 0, winners.Count);
        if (share == 0) winners = [];

        var winnerIds = winners.Select(u => u.Id).ToHashSet();
        var outdated = prizes.Where(p => !winnerIds.Contains(p.UserId) || p.AmountKobo != share).ToList();
        var kept = prizes.Except(outdated).Select(p => p.UserId).ToHashSet();
        var newWinners = winners.Where(u => !kept.Contains(u.Id)).ToList();
        if (outdated.Count == 0 && newWinners.Count == 0) return null;

        if (outdated.FirstOrDefault(p => p.PayoutEntryId is not null) is { } paid)
            return $"{paid.User.Name} is marked as paid but their prize no longer matches this game. Mark them as unpaid to apply the change.";

        var outdatedIds = outdated.Select(p => p.EntryId).ToList();
        var entries = await db.WalletEntries.Where(e => outdatedIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var group in outdated.GroupBy(p => (p.UserId, entries[p.EntryId].Season)))
        {
            var totals = await WalletLedger.TotalsAsync(db, group.Key.Season, ct);
            var mine = totals.GetValueOrDefault(group.Key.UserId, WalletLedger.Totals.None);
            if (WalletLedger.Check(WalletKinds.Adjustment, -group.Sum(p => p.AmountKobo), mine, WalletLedger.Pot(totals.Values)) is not null)
                return $"{group.First().User.Name} has already been paid out of their wallet, so this prize can't be taken back. Reverse that payout first.";
        }

        foreach (var prize in outdated)
        {
            var entry = entries[prize.EntryId];
            var reversal = NewEntry(entry.UserId, entry.Season, -entry.AmountKobo, $"Special game: {Match(fixture)}, result or pot changed", actor);
            reversal.ReversesEntryId = entry.Id;
            db.WalletEntries.Add(reversal);
            db.SpecialGamePrizes.Remove(prize);
            AuditLogger.Log(db, subject: prize.User, actor: actor, action: "WalletEntryReversed",
                details: $"Special game prize {WalletLedger.Naira(entry.AmountKobo)} reversed: {Match(fixture)}".Truncate(500));
        }

        if (newWinners.Count > 0)
        {
            var (season, _) = await WalletLedger.CurrentAsync(db, ct);
            var note = $"Special game: {Match(fixture)} ended {fixture.FinalScoreHome}-{fixture.FinalScoreAway} " +
                       $"({WalletLedger.Naira(fixture.SpecialPotKobo!.Value)} shared by {winners.Count})";
            foreach (var user in newWinners)
            {
                var entry = NewEntry(user.Id, season, share, note, actor);
                db.WalletEntries.Add(entry);
                db.SpecialGamePrizes.Add(new SpecialGamePrize { FixtureId = fixture.Id, UserId = user.Id, AmountKobo = share, EntryId = entry.Id });
                AuditLogger.Log(db, subject: user, actor: actor, action: "WalletEntryRecorded",
                    details: $"Special game prize {WalletLedger.Naira(share)} for {season}: {Match(fixture)}".Truncate(500));
            }
        }
        return null;
    }

    public static string Match(Fixture f) => $"{f.HomeTeam} vs {f.AwayTeam}";

    public static WalletEntry NewEntry(Guid userId, string season, long amount, string note, User? actor, string kind = WalletKinds.Adjustment) => new()
    {
        UserId = userId,
        Season = season,
        Kind = kind,
        AmountKobo = amount,
        Note = note.Truncate(300),
        CreatedByUserId = actor?.Id,
        // Most prizes are written when the scraper sees the result, with nobody at the keyboard.
        CreatedByName = actor?.Name ?? "Automatic",
    };
}
