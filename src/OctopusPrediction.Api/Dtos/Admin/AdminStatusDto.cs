namespace OctopusPrediction.Api.Dtos.Admin;

public record AdminStatusDto(
    DateTime? LastScrapedAt,
    bool ScraperEnabled,
    // Whether Twilio is set up, so reminders also go out by WhatsApp/SMS.
    bool RemindersConfigured,
    bool PredictionsLocked,
    bool RegistrationClosed,
    // Sources that have failed or been refused lately (see SourceGuard); empty when all is well.
    IReadOnlyList<SourceHealthDto> SourceProblems,
    // Active players with push notifications on in at least one browser, so reminders reach them.
    int PlayersWithNotifications
);

// CoolingUntil is set while the scraper is leaving the source alone.
public record SourceHealthDto(string Name, int Failures, DateTime? CoolingUntil, string? LastProblem);
