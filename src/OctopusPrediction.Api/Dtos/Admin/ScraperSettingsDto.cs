namespace OctopusPrediction.Api.Dtos.Admin;

public record ScraperSettingsDto(
    bool Enabled,
    int PollIntervalSeconds,
    string Competition,
    string SourceName,
    IReadOnlyList<string> AvailableSources,
    // Single, Fallback or Rotate (see SourceModes), and the ordered list the last two use.
    string SourceMode,
    IReadOnlyList<string> SourceOrder,
    // The available sources that can create new fixtures (IMatchSource.ProvidesRounds).
    IReadOnlyList<string> RoundSources,
    bool PredictionsLocked,
    bool RegistrationClosed,
    bool ReminderEnabled,
    int ReminderHoursBeforeFirstGame,
    PredictionRulesDto PredictionRules
);
