using System.ComponentModel.DataAnnotations;
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

// One player's score on a special game, for those who run them. Correct once the match has ended.
public record SpecialPickDto(
    string UserId,
    string Name,
    string? WhatsAppName,
    int HomeGoals,
    int AwayGoals,
    bool Correct);

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
    // Why the wallets couldn't follow the latest result or pot, until the admin acts.
    string? Problem,
    List<SpecialPrizeDto> Prizes);

// One game on a player's Special games tab. Other players' scores are never included.
public record PlayerSpecialGameDto(
    string FixtureId,
    string Competition,
    string WeekName,
    string HomeTeam,
    string AwayTeam,
    DateTime Kickoff,
    string Status,
    ScoreDto? FinalScore,
    long PotKobo,
    bool Open,
    int Entries,
    int? MyHomeGoals,
    int? MyAwayGoals,
    int Winners,
    long? MyPrizeKobo);

public class SubmitSpecialPickRequest
{
    [Range(0, 99)]
    public int HomeGoals { get; set; }

    [Range(0, 99)]
    public int AwayGoals { get; set; }
}

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
