namespace OctopusPrediction.Api.Dtos.Admin;

public record AdminStatusDto(
    DateTime? LastScrapedAt,
    bool ScraperEnabled,
    bool RemindersConfigured,
    bool PredictionsLocked,
    bool RegistrationClosed,
    // Sources that have failed or been refused lately (see SourceGuard); empty when all is well.
    IReadOnlyList<SourceHealthDto> SourceProblems
);

// CoolingUntil is set while the scraper is leaving the source alone.
public record SourceHealthDto(string Name, int Failures, DateTime? CoolingUntil, string? LastProblem);
