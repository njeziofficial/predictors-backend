namespace OctopusPrediction.Api.Dtos.BallonDor;

// Amounts are in kobo (₦1 = 100).
public record BallonDorDto(
    int Year,
    List<string> Nominees,
    DateTime LocksAt,
    bool Locked,
    // The real top 5, winner first, once entered.
    List<string>? Result,
    long WinnerPotKobo,
    long TopFivePotKobo,
    // How many players have picked.
    int Entries,
    // The signed-in player's own picks (null if they haven't picked).
    List<string>? MyPicks,
    // Everyone's picks: for players only once picks are locked, for admins always.
    List<BallonDorEntryDto>? Board,
    // Shown to players. Always true on the players' endpoint, which hides a game that's off.
    bool Enabled);

public record BallonDorEntryDto(
    string UserId,
    string Name,
    List<string> Picks,
    DateTime PickedAt,
    bool CalledWinner,
    bool CalledTopFive,
    int CorrectPlaces,
    // Their share of the prizes under the result (nothing until there is one).
    long PrizeKobo);

public record SubmitBallonDorPicksRequest(List<string> Picks);

public record SaveBallonDorEventRequest(
    int Year,
    List<string> Nominees,
    DateTime LocksAt,
    long WinnerPotKobo,
    long TopFivePotKobo);

// Null or empty clears the result (and takes back any prizes it paid).
public record SetBallonDorResultRequest(List<string>? Result);

public record SetBallonDorEnabledRequest(bool Enabled);
