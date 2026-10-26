namespace OctopusPrediction.Api.Entities;

// A free side game on Ballon d'Or night: players rank their top 5 from the nominees before the
// ceremony. The organiser puts up two prizes from their own pocket (not the season pot): one
// shared equally by everyone who called the winner, one by everyone who called the whole top 5
// in order. Prizes land in the winners' wallets as adjustments (BallonDorGame).
public class BallonDorEvent
{
    // The year is the key: one event a year.
    public int Year { get; set; }
    // The shortlist, as the admin pasted it. Picks must come from it.
    public List<string> Nominees { get; set; } = [];
    // Picks can be made and changed until then, and are hidden from other players until then.
    public DateTime LocksAt { get; set; }
    // The real top 5, winner first, once the admin enters it. Null until then.
    public List<string>? Result { get; set; }
    // In kobo. Shared equally by those who called the winner / the top 5 in order.
    public long WinnerPotKobo { get; set; } = 2_500_00;
    public long TopFivePotKobo { get; set; } = 2_500_00;
    // Off: players don't see the game at all (no menu link, no page, no picks). Only the system
    // user switches it; admins still see and run it in the back office.
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class BallonDorPick
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Year { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    // Five nominees, 1st first.
    public List<string> Picks { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // The wallet entries paying this player's share of each prize, so entering the result again
    // (a correction) reverses exactly those and never pays anyone twice.
    public Guid? WinnerPrizeEntryId { get; set; }
    public Guid? TopFivePrizeEntryId { get; set; }
}
