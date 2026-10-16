using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// The figures the wallet shows, all plain sums of the ledger (see WalletEntry for the signs):
//   paid in  = a player's entry fees           pot       = everyone's entry fees
//   won      = their winnings                  allocated = everyone's winnings
//   paid out = their payouts (shown positive)  balance   = won + adjustments − paid out (owed to them)
// and the rules that keep them sensible: winnings never more than the pot has left, a payout
// never more than the player is owed.
public static partial class WalletLedger
{
    public const string DefaultSeason = "2026/27";
    public const long DefaultEntryFeeKobo = 3_000_00;
    // ₦10m: well above any real entry, to catch a slip of the keyboard.
    public const long MaxAmountKobo = 10_000_000_00;

    public sealed record Totals(long PaidIn, long Won, long PaidOut, long Adjusted)
    {
        public long Balance => Won + Adjusted - PaidOut;
        public bool Paid => PaidIn > 0;
        public static readonly Totals None = new(0, 0, 0, 0);
    }

    public sealed record PotTotals(long Pot, long Allocated, long PaidOut, long Owed)
    {
        public long Unallocated => Pot - Allocated;
    }

    [GeneratedRegex(@"^\d{4}/\d{2}$")]
    private static partial Regex SeasonPattern();

    public static bool IsSeason(string? season) => season is not null && SeasonPattern().IsMatch(season);

    /// <summary>"₦3,000" or "₦1,250.50".</summary>
    public static string Naira(long kobo) =>
        (kobo < 0 ? "-" : "") + "₦" + (Math.Abs(kobo) / 100m).ToString("#,0.##", CultureInfo.InvariantCulture);

    public static async Task<(string Season, long EntryFeeKobo)> CurrentAsync(AppDbContext db, CancellationToken ct = default)
    {
        var settings = await db.ScraperSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return (settings?.WalletSeason ?? DefaultSeason, settings?.WalletEntryFeeKobo ?? DefaultEntryFeeKobo);
    }

    /// <summary>Seasons with entries, and the current one, newest first.</summary>
    public static async Task<List<string>> SeasonsAsync(AppDbContext db, string current, CancellationToken ct = default)
    {
        var seasons = await db.WalletEntries.Select(e => e.Season).Distinct().ToListAsync(ct);
        return [.. seasons.Append(current).Distinct().OrderDescending()];
    }

    /// <summary>Each player's figures for the season.</summary>
    public static async Task<Dictionary<Guid, Totals>> TotalsAsync(AppDbContext db, string season, CancellationToken ct = default)
    {
        var sums = await db.WalletEntries
            .Where(e => e.Season == season)
            .GroupBy(e => new { e.UserId, e.Kind })
            .Select(g => new { g.Key.UserId, g.Key.Kind, Amount = g.Sum(e => e.AmountKobo) })
            .ToListAsync(ct);
        return sums.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => new Totals(
            PaidIn: g.Where(s => s.Kind == WalletKinds.EntryFee).Sum(s => s.Amount),
            Won: g.Where(s => s.Kind == WalletKinds.Winnings).Sum(s => s.Amount),
            PaidOut: -g.Where(s => s.Kind == WalletKinds.Payout).Sum(s => s.Amount),
            Adjusted: g.Where(s => s.Kind == WalletKinds.Adjustment).Sum(s => s.Amount)));
    }

    public static PotTotals Pot(IEnumerable<Totals> players)
    {
        var all = players.ToList();
        return new PotTotals(all.Sum(t => t.PaidIn), all.Sum(t => t.Won), all.Sum(t => t.PaidOut), all.Sum(t => t.Balance));
    }

    /// <summary>
    /// Why this entry can't be added to the season's ledger, or null if it can. `amount` is signed
    /// as it will be stored. Run it on the totals as they are just before saving.
    /// </summary>
    public static string? Check(string kind, long amount, Totals player, PotTotals pot) => kind switch
    {
        WalletKinds.EntryFee when amount > 0 && player.Paid => "This player has already paid for this season.",
        WalletKinds.EntryFee when player.PaidIn + amount < 0 => "That would take this player's entry fee below nothing.",
        // Taking money out of the pot that's already been shared out would leave winners unfunded.
        WalletKinds.EntryFee when pot.Unallocated + amount < 0 =>
            $"The pot would be smaller than the winnings already shared out ({Naira(pot.Allocated)}). Reverse some winnings first.",
        WalletKinds.Winnings when amount > pot.Unallocated =>
            $"Only {Naira(pot.Unallocated)} of the pot hasn't been shared out yet.",
        WalletKinds.Winnings or WalletKinds.Adjustment or WalletKinds.Payout when player.Balance + amount < 0 =>
            $"This player is only owed {Naira(player.Balance)}. Reverse their payout first if it was a mistake.",
        _ => null,
    };
}
