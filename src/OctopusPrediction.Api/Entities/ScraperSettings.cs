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
    // Prediction reminders (ReminderBackgroundService), sent as push notifications and, when
    // Twilio is configured, WhatsApp/SMS. Both are the system user's alone (AdminController.SetReminders).
    public bool ReminderEnabled { get; set; } = false;
    // Comma-separated hours before each week's first match to send a reminder (72 = 3 days): one
    // reminder per entry, each only to players who haven't finished predicting. See ReminderSchedule.
    public string ReminderOffsetsHours { get; set; } = Services.ReminderSchedule.DefaultHoursText;
    // Which email provider sends ("Resend", "Brevo", "Gmail"), or "auto" for the first one set up.
    // The system user's choice (AdminEmailController); credentials stay in config.
    public string EmailProvider { get; set; } = Services.Email.EmailProviderNames.Auto;
    // Who emails come from, set on the admin page; null falls back to the sender in config. The
    // name is shared; each provider has its own address, since each only sends from addresses it
    // has verified (Brevo: a verified sender; Gmail: the signed-in account; Resend: its domain).
    public string? EmailSenderName { get; set; }
    public string? EmailResendFrom { get; set; }
    public string? EmailBrevoSender { get; set; }
    public string? EmailGmailSender { get; set; }
    // When the system admin last cleared the failed-email notice on the Settings page: only
    // failures after it are shown. The email log keeps them all.
    public DateTime? EmailErrorsClearedAt { get; set; }

    // Whether resetting a password (by an admin, or with a Forgot password email) also removes the
    // account's passkeys, so only the new password signs in until fingerprint/face is set up
    // again. Off by default, as passkeys don't depend on the password. The system admin's call.
    public bool RemovePasskeysOnPasswordReset { get; set; } = false;

    // The wallet ledger's current season and its entry fee (in kobo): new entries go to this
    // season unless another is named, and "mark as paid" records this fee. The system admin's
    // call (AdminWalletController.UpdateSettings).
    public string WalletSeason { get; set; } = Services.WalletLedger.DefaultSeason;
    public long WalletEntryFeeKobo { get; set; } = Services.WalletLedger.DefaultEntryFeeKobo;
}
