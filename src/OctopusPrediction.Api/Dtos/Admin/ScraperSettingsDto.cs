namespace OctopusPrediction.Api.Dtos.Admin;

public record ScraperSettingsDto(
    bool Enabled,
    int PollIntervalSeconds,
    string Competition,
    string SourceName,
    IReadOnlyList<string> AvailableSources,
    bool PredictionsLocked,
    bool RegistrationClosed,
    bool ReminderEnabled,
    int ReminderHoursBeforeFirstGame,
    PredictionRulesDto PredictionRules
);
