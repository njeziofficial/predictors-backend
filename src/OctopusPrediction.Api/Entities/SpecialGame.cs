namespace OctopusPrediction.Api.Entities;

// A player's exact score for a special game, on the Special games tab. Separate from their normal
// prediction on the match (Prediction), which it never affects, and it earns no league points.
public class SpecialGamePick
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FixtureId { get; set; } = string.Empty;
    public Fixture Fixture { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// One player's standing share of a special game's pot (Fixture.SpecialPotKobo). The money is in
// the wallet ledger; this ties it to the match and says whether the organiser has sent it. A
// share that stops being due has its entry reversed and this row removed.
public class SpecialGamePrize
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FixtureId { get; set; } = string.Empty;
    public Fixture Fixture { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public long AmountKobo { get; set; }
    // The ledger entry that put the prize in their wallet.
    public Guid EntryId { get; set; }
    // The ledger payout recorded when the organiser marked it as paid; null until then.
    public Guid? PayoutEntryId { get; set; }
    public DateTime? PaidAt { get; set; }
}
