using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Wallet;

// Amounts are in kobo (₦1 = 100). See WalletEntry for what the signs mean.
public record WalletEntryDto(
    string Id,
    string UserId,
    string Season,
    string Kind,
    long AmountKobo,
    string? Note,
    DateTime OccurredAt,
    DateTime CreatedAt,
    string CreatedByName,
    // Set on a reversal: the entry it cancels.
    string? ReversesEntryId,
    // Set on an entry that has been reversed.
    bool Reversed);

// A player's own wallet for one season. The pot is shown to everyone: it's a closed group.
public record MyWalletDto(
    string Season,
    string CurrentSeason,
    List<string> Seasons,
    long EntryFeeKobo,
    bool Paid,
    long PaidInKobo,
    long WonKobo,
    long PaidOutKobo,
    long BalanceKobo,
    long PotKobo,
    int PaidPlayers,
    List<WalletEntryDto> Entries);

public record WalletPlayerDto(
    string UserId,
    string Name,
    string? WhatsAppName,
    bool IsDisabled,
    bool Paid,
    long PaidInKobo,
    long WonKobo,
    long PaidOutKobo,
    long BalanceKobo);

public record AdminWalletDto(
    string Season,
    string CurrentSeason,
    List<string> Seasons,
    long EntryFeeKobo,
    long PotKobo,
    // Shared out as winnings so far, and what's left to share.
    long AllocatedKobo,
    long UnallocatedKobo,
    long PaidOutKobo,
    // Won but not yet paid: what the organiser still has to send.
    long OwedKobo,
    int PaidPlayers,
    List<WalletPlayerDto> Players);

public record PlayerLedgerDto(WalletPlayerDto Player, List<WalletEntryDto> Entries);

public record RecordEntryFeesResultDto(int Recorded, int AlreadyPaid, AdminWalletDto Wallet);

public class RecordEntryFeesRequest
{
    [Required, MinLength(1)]
    public List<Guid> UserIds { get; set; } = [];

    // Defaults to the current season.
    [MaxLength(16)]
    public string? Season { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    public DateTime? OccurredAt { get; set; }
}

public class RecordEntryRequest
{
    [Required]
    public Guid UserId { get; set; }

    // "winnings", "payout" or "adjustment" (entry fees go through RecordEntryFeesRequest).
    [Required, MaxLength(20)]
    public string Kind { get; set; } = string.Empty;

    // Winnings and payouts: the amount, more than nothing. Adjustment: plus adds to what the
    // player is owed, minus takes from it.
    public long AmountKobo { get; set; }

    [MaxLength(16)]
    public string? Season { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    public DateTime? OccurredAt { get; set; }
}

public class ReverseEntryRequest
{
    [Required, MaxLength(300)]
    public string Reason { get; set; } = string.Empty;
}

public class UpdateWalletSettingsRequest
{
    [Required, MaxLength(16)]
    public string Season { get; set; } = string.Empty;

    public long EntryFeeKobo { get; set; }
}
