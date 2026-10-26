using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// The Ballon d'Or side game's rules (see BallonDorEvent):
//   winner pot   — shared equally by everyone whose 1st pick won
//   top-five pot — shared equally by everyone who called all five places in order
// A pot nobody wins isn't paid out. Shares are rounded down to the kobo.
//
// Prizes are paid into the wallet as adjustments, not winnings: the organiser puts them up, so
// they mustn't come out of the season pot (WalletLedger.Check caps winnings at what it holds).
public static class BallonDorGame
{
    public const int Places = 5;

    public static bool CalledWinner(IReadOnlyList<string> picks, IReadOnlyList<string>? result) =>
        result is { Count: > 0 } && picks.Count > 0 && Same(picks[0], result[0]);

    public static bool CalledTopFive(IReadOnlyList<string> picks, IReadOnlyList<string>? result) =>
        result is { Count: Places } && picks.Count == Places && picks.Zip(result).All(p => Same(p.First, p.Second));

    // How many places were exactly right, for the table (prizes don't depend on it).
    public static int CorrectPlaces(IReadOnlyList<string> picks, IReadOnlyList<string>? result) =>
        result is null ? 0 : picks.Zip(result).Count(p => Same(p.First, p.Second));

    public static long Share(long pot, int winners) => winners == 0 ? 0 : pot / winners;

    // The 2026 shortlist (UEFA / ballondor.com), so the game opens without an admin pasting it.
    // The ceremony starts 20:00 GMT on 26 October; picks lock 15 minutes before.
    public const int SeedYear = 2026;
    public static readonly DateTime SeedLocksAt = new(2026, 10, 26, 19, 45, 0, DateTimeKind.Utc);
    public static readonly IReadOnlyList<string> SeedNominees =
    [
        "Jude Bellingham", "Pau Cubarsí", "Marc Cucurella", "Ousmane Dembélé", "Luis Díaz",
        "Bruno Fernandes", "Gabriel Magalhães", "Erling Haaland", "Achraf Hakimi", "Harry Kane",
        "Khvicha Kvaratskhelia", "Lamine Yamal", "Sadio Mané", "Marquinhos", "Lautaro Martínez",
        "Kylian Mbappé", "Nuno Mendes", "Lionel Messi", "João Neves", "Michael Olise",
        "Willian Pacho", "Julián Quiñones", "Declan Rice", "Rodri", "Fabián Ruiz",
        "William Saliba", "Ferran Torres", "Dayot Upamecano", "Vinícius Júnior", "Vitinha",
    ];

    /// <summary>Opens the 2026 game with the shortlist, unless it exists or its picks have already locked.</summary>
    public static async Task SeedAsync(AppDbContext db, DateTime now)
    {
        if (now >= SeedLocksAt || await db.BallonDorEvents.AnyAsync(e => e.Year == SeedYear)) return;
        db.BallonDorEvents.Add(new BallonDorEvent { Year = SeedYear, Nominees = [.. SeedNominees], LocksAt = SeedLocksAt });
        await db.SaveChangesAsync();
    }

    public static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>What each player is due from each pot under the event's result (nothing without one).</summary>
    public static Dictionary<Guid, (long Winner, long TopFive)> Prizes(BallonDorEvent ev, IReadOnlyCollection<BallonDorPick> picks)
    {
        var winners = picks.Where(p => CalledWinner(p.Picks, ev.Result)).Select(p => p.UserId).ToHashSet();
        var topFive = picks.Where(p => CalledTopFive(p.Picks, ev.Result)).Select(p => p.UserId).ToHashSet();
        var winnerShare = Share(ev.WinnerPotKobo, winners.Count);
        var topFiveShare = Share(ev.TopFivePotKobo, topFive.Count);
        return picks.ToDictionary(p => p.UserId, p => (
            winners.Contains(p.UserId) ? winnerShare : 0L,
            topFive.Contains(p.UserId) ? topFiveShare : 0L));
    }

    /// <summary>
    /// Brings the wallet in line with the event's result: each player's prize entries are made to
    /// match what they're due now, reversing any earlier ones that no longer do (a corrected or
    /// cleared result). All or nothing: returns why it can't, changing nothing, when a reversal
    /// would take a player below nothing (they've already been paid that prize out).
    /// </summary>
    public static async Task<string?> SettleAsync(AppDbContext db, BallonDorEvent ev, User? actor)
    {
        await AdminWalletController.Writes.WaitAsync();
        try
        {
            var picks = await db.BallonDorPicks.Include(p => p.User).Where(p => p.Year == ev.Year).ToListAsync();
            var due = Prizes(ev, picks);
            var entryIds = picks.SelectMany(p => new[] { p.WinnerPrizeEntryId, p.TopFivePrizeEntryId })
                .OfType<Guid>().ToList();
            var entries = await db.WalletEntries.Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
            var (season, _) = await WalletLedger.CurrentAsync(db);
            var winnerCount = due.Values.Count(d => d.Winner > 0);
            var topFiveCount = due.Values.Count(d => d.TopFive > 0);

            // Plan every change first, so nothing is saved if any of it can't be.
            var reversals = new List<(BallonDorPick Pick, WalletEntry Entry)>();
            var payments = new List<(BallonDorPick Pick, bool Winner, long Amount)>();
            foreach (var pick in picks)
            {
                var (winner, topFive) = due[pick.UserId];
                Plan(pick, pick.WinnerPrizeEntryId, winner, isWinner: true);
                Plan(pick, pick.TopFivePrizeEntryId, topFive, isWinner: false);
            }

            void Plan(BallonDorPick pick, Guid? entryId, long amount, bool isWinner)
            {
                var existing = entryId is { } id ? entries.GetValueOrDefault(id) : null;
                if (existing?.AmountKobo == amount) return;
                if (existing is not null) reversals.Add((pick, existing));
                if (amount > 0) payments.Add((pick, isWinner, amount));
            }

            foreach (var group in reversals.GroupBy(r => (r.Entry.UserId, r.Entry.Season)))
            {
                var totals = await WalletLedger.TotalsAsync(db, group.Key.Season);
                var mine = totals.GetValueOrDefault(group.Key.UserId, WalletLedger.Totals.None);
                var amount = -group.Sum(r => r.Entry.AmountKobo);
                if (WalletLedger.Check(WalletKinds.Adjustment, amount, mine, WalletLedger.Pot(totals.Values)) is not null)
                    return $"{group.First().Pick.User.Name} has already been paid their Ballon d'Or prize, so it can't be taken back here. " +
                           "Reverse their payout in the wallet first.";
            }

            foreach (var (pick, entry) in reversals)
            {
                var reversal = NewEntry(entry.UserId, entry.Season, -entry.AmountKobo,
                    $"Ballon d'Or {ev.Year} result corrected", actor);
                reversal.ReversesEntryId = entry.Id;
                db.WalletEntries.Add(reversal);
                if (pick.WinnerPrizeEntryId == entry.Id) pick.WinnerPrizeEntryId = null;
                if (pick.TopFivePrizeEntryId == entry.Id) pick.TopFivePrizeEntryId = null;
                AuditLogger.Log(db, subject: pick.User, actor: actor, action: "WalletEntryReversed",
                    details: $"Ballon d'Or {ev.Year} prize {WalletLedger.Naira(entry.AmountKobo)} reversed: result corrected");
            }

            foreach (var (pick, isWinner, amount) in payments)
            {
                var note = isWinner
                    ? $"Ballon d'Or {ev.Year}: called the winner, {ev.Result![0]} ({WalletLedger.Naira(ev.WinnerPotKobo)} shared by {winnerCount})"
                    : $"Ballon d'Or {ev.Year}: called the top 5 in order ({WalletLedger.Naira(ev.TopFivePotKobo)} shared by {topFiveCount})";
                var entry = NewEntry(pick.UserId, season, amount, note, actor);
                db.WalletEntries.Add(entry);
                if (isWinner) pick.WinnerPrizeEntryId = entry.Id;
                else pick.TopFivePrizeEntryId = entry.Id;
                AuditLogger.Log(db, subject: pick.User, actor: actor, action: "WalletEntryRecorded",
                    details: $"{WalletKinds.Label(WalletKinds.Adjustment)} {WalletLedger.Naira(amount)} for {season} ({note})");
            }

            await db.SaveChangesAsync();
            return null;
        }
        finally
        {
            AdminWalletController.Writes.Release();
        }
    }

    private static WalletEntry NewEntry(Guid userId, string season, long amount, string note, User? actor) => new()
    {
        UserId = userId,
        Season = season,
        Kind = WalletKinds.Adjustment,
        AmountKobo = amount,
        Note = note,
        CreatedByUserId = actor?.Id,
        CreatedByName = actor?.Name ?? "",
    };
}
