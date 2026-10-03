namespace OctopusPrediction.Api.Dtos.Admin;

public record AdminStatusDto(
    DateTime? LastScrapedAt,
    bool ScraperEnabled,
    bool RemindersConfigured,
    bool PredictionsLocked
);
