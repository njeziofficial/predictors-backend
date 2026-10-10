namespace OctopusPrediction.Api.Entities;

public class ScraperSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public string Competition { get; set; } = "La Liga";
    public string SourceName { get; set; } = "Flashscore";
    // Single (SourceName only), Fallback or Rotate (both over SourceOrder): see SourceModes.
    // The whole source choice is the system user's alone (AdminController.SetScraperSource).
    public string SourceMode { get; set; } = "Single";
    // Comma-separated source names for Fallback and Rotate, in order.
    public string SourceOrder { get; set; } = "Flashscore";
    public bool PredictionsLocked { get; set; } = false;
    // When true, POST /api/auth/register is refused. Existing accounts can still log in, and the
    // system user can still create accounts from the admin CMS.
    public bool RegistrationClosed { get; set; } = false;
    // Prediction rules, enforced in PredictionsController.Submit. Changing them needs the
    // "predictions.rules" permission, which only the system user has by default.
    // Off: every match still open must be predicted. On: any subset may be submitted.
    public bool AllowPartialPredictions { get; set; } = false;
    // On: a submitted prediction can never be changed. With AllowPartialPredictions, skipped open
    // matches can still be added later (without it, nothing can be skipped in the first place).
    public bool PredictionsFinal { get; set; } = false;
    // On: the whole week locks when its first match kicks off. Off: each match locks at its own kickoff.
    public bool LockWeekAtFirstKickoff { get; set; } = false;
    // With LockWeekAtFirstKickoff on: a player who had no prediction for the week when it locked
    // may still predict the matches yet to start.
    public bool AllowLatePredictions { get; set; } = false;
    // System-user-only kill switch: when false, AuditLogger.Log is a no-op for everyone —
    // see AuditLogSettings, which caches this in memory so every call site isn't a DB hit.
    public bool AuditLogEnabled { get; set; } = true;
    // Off by default: nothing actually sends a reminder yet (no messaging provider is wired
    // up), so this stays inert until that's built — enabling it just records intent.
    public bool ReminderEnabled { get; set; } = false;
    public int ReminderHoursBeforeFirstGame { get; set; } = 24;
}
