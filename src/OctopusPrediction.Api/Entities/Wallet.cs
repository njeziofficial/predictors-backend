namespace OctopusPrediction.Api.Entities;

// The season's money, as a ledger. No money moves through the app: players pay the entry fee
// into the organiser's bank account, and winners are paid from it by bank transfer. The ledger
// records both, plus what each player won under the sharing formula, so everyone can see who
// has paid, the size of the pot, and what each player is owed.
//
// Entries are never changed or deleted. A mistake is put right by reversing the entry: a new
// entry with the opposite amount that points back at it (ReversesEntryId).
public class WalletEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    // "2026/27": each season has its own pot.
    public string Season { get; set; } = string.Empty;
    // WalletKinds.
    public string Kind { get; set; } = string.Empty;
    // In kobo (₦1 = 100), signed, so a player's figures are plain sums: an entry fee is plus
    // (into the pot), winnings plus (owed to them), a payout minus (paid to them), and an
    // adjustment either way. A reversal is the opposite of the entry it reverses.
    public long AmountKobo { get; set; }
    // Free text, like a bank transfer reference or why an adjustment was made.
    public string? Note { get; set; }
    // When the money moved (or the winnings were decided), which may be before it was recorded.
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public Guid? ReversesEntryId { get; set; }
}

public static class WalletKinds
{
    // The player paid the season's entry fee into the pot.
    public const string EntryFee = "entry_fee";
    // Their share of the pot, under the sharing formula.
    public const string Winnings = "winnings";
    // Sent to their bank account.
    public const string Payout = "payout";
    // Anything else, either way, with a note saying why.
    public const string Adjustment = "adjustment";

    public static readonly IReadOnlyList<string> All = [EntryFee, Winnings, Payout, Adjustment];

    // Those that make up what a player is owed (entry fees go into the pot instead).
    public static readonly IReadOnlyList<string> Balance = [Winnings, Payout, Adjustment];

    public static string Label(string kind) => kind switch
    {
        EntryFee => "Entry fee",
        Winnings => "Winnings",
        Payout => "Payout",
        Adjustment => "Adjustment",
        _ => kind,
    };
}
