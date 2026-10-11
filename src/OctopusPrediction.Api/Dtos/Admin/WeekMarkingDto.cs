namespace OctopusPrediction.Api.Dtos.Admin;

// Every player's picks for a week next to the results and the points they earned, so an admin
// can check the marking. Picks on a match are only shown once it has kicked off: admins play too.
public record WeekMarkingDto(
    string WeekId,
    string WeekName,
    // When the end-of-week check ran (every match finished); null until then.
    DateTime? ReconciledAt,
    IReadOnlyList<MarkingFixtureDto> Fixtures,
    IReadOnlyList<PlayerMarkingDto> Players
);

public record MarkingFixtureDto(
    string Id,
    string HomeTeam,
    string AwayTeam,
    DateTime Kickoff,
    string Status,
    int? FinalScoreHome,
    int? FinalScoreAway,
    // False before kickoff: the picks on it are hidden.
    bool PicksVisible
);

public record PlayerMarkingDto(
    string UserId,
    string Name,
    string? WhatsAppName,
    // What the standings show (the stored points)…
    int Points,
    // …and what the scoring rule gives now. They differ only while a correction is pending.
    int ExpectedPoints,
    int PicksMade,
    IReadOnlyList<MarkedPickDto> Picks
);

public record MarkedPickDto(
    string FixtureId,
    // home_win, away_win, draw or correct_score; null while the pick is hidden.
    string? Outcome,
    int? HomeGoals,
    int? AwayGoals,
    int Points,
    int ExpectedPoints,
    // Why it earned what it did, e.g. "Exact score 1-1" or "Wrong exact score (2-1)".
    string Reason
);
