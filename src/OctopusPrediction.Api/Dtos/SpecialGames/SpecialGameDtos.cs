using OctopusPrediction.Api.Dtos.Fixtures;

namespace OctopusPrediction.Api.Dtos.SpecialGames;

// Amounts are in kobo (₦1 = 100).
public record SpecialPrizeDto(
    string UserId,
    string Name,
    string? WhatsAppName,
    long AmountKobo,
    bool Paid,
    DateTime? PaidAt);

public record SpecialGameDto(
    string FixtureId,
    string WeekName,
    string Competition,
    string HomeTeam,
    string AwayTeam,
    DateTime Kickoff,
    string Status,
    ScoreDto? FinalScore,
    long PotKobo,
    int ScorePicks,
    // Picks made before the match was made special: they can't win the pot.
    int OutdatedPicks,
    // Why the wallets couldn't follow the latest result or pot, until the admin acts.
    string? Problem,
    List<SpecialPrizeDto> Prizes);

public class SetSpecialPotRequest
{
    public long PotKobo { get; set; }
}

public class SetSpecialGamesEnabledRequest
{
    public bool Enabled { get; set; }
}

public class SetSpecialPrizePaidRequest
{
    public bool Paid { get; set; }
}
