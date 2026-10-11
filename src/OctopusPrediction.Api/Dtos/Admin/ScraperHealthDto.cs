using OctopusPrediction.Api.Services.Monitoring;

namespace OctopusPrediction.Api.Dtos.Admin;

// Everything the system admin's Scraper Health page shows. In-memory parts (state, polls,
// sources, logs) describe this server process only and start again on a restart.
public record ScraperHealthDto(
    DateTime Now,
    HealthProcessDto Process,
    HealthSettingsDto Settings,
    HealthStateDto State,
    // Things that need attention, worst first.
    IReadOnlyList<HealthWarningDto> Warnings,
    IReadOnlyList<PollRecord> Polls,
    IReadOnlyList<HealthSourceDto> Sources,
    HealthQueueDto Queue,
    HealthPointsDto Points,
    IReadOnlyList<HealthMatchDto> Matches,
    IReadOnlyList<LogEntry> Logs
);

public record HealthProcessDto(DateTime StartedAt, string Environment, string? Version);

public record HealthSettingsDto(bool Enabled, string Mode, IReadOnlyList<string> Sources, int PollIntervalSeconds, string Competition);

public record HealthStateDto(
    string Phase,
    string? Detail,
    DateTime? NextPollAt,
    string? Schedule,
    DateTime? LastPollAt,
    // Last poll that got matches from a source.
    DateTime? LastSuccessAt);

// Severity: "error" (scores are likely wrong or not updating) or "warning".
public record HealthWarningDto(string Severity, string Title, string Detail);

public record HealthSourceDto(
    string Name,
    bool InUse,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt,
    int LastMatches,
    int LastDurationMs,
    string? LastResult,
    int Failures,
    DateTime? CoolingUntil,
    string? LastProblem);

public record HealthQueueDto(
    int Pending,
    DateTime? OldestPendingAt,
    int AppliedLastHour,
    int FailedLastDay,
    DateTime? LastAppliedAt,
    int RetentionHours,
    IReadOnlyList<HealthQueueRowDto> Recent);

public record HealthQueueRowDto(
    long Id, string FixtureId, string Source, DateTime ScrapedAt, DateTime? ProcessedAt,
    string? Outcome, int Attempts, string? Error,
    // What the row said, e.g. "Live 67' 1-0" or "Ended 2-2".
    string Reading);

public record HealthPointsDto(
    DateTime? LastRunAt,
    int LastCorrections,
    string? LastError,
    IReadOnlyList<HealthWeekDto> Weeks);

public record HealthWeekDto(string Id, string Name, int Fixtures, int Finished, DateTime? ReconciledAt);

public record HealthMatchDto(
    string Id, string HomeTeam, string AwayTeam, DateTime Kickoff, string Status,
    int? ScoreHome, int? ScoreAway, int? Minute, DateTime UpdatedAt,
    // Set when the match looks wrong (e.g. still live long after it must have ended).
    string? Problem);
