namespace OctopusPrediction.Api.Entities;

public class ScraperSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public string Competition { get; set; } = "La Liga";
    public string SourceName { get; set; } = "Flashscore";
    public bool PredictionsLocked { get; set; } = false;
    // When true, POST /api/auth/register is refused. Existing accounts can still log in, and the
    // system user can still create accounts from the admin CMS.
    public bool RegistrationClosed { get; set; } = false;
    // System-user-only kill switch: when false, AuditLogger.Log is a no-op for everyone —
    // see AuditLogSettings, which caches this in memory so every call site isn't a DB hit.
    public bool AuditLogEnabled { get; set; } = true;
    // Off by default: nothing actually sends a reminder yet (no messaging provider is wired
    // up), so this stays inert until that's built — enabling it just records intent.
    public bool ReminderEnabled { get; set; } = false;
    public int ReminderHoursBeforeFirstGame { get; set; } = 24;
}
