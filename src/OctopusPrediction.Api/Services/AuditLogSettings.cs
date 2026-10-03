namespace OctopusPrediction.Api.Services;

// In-memory mirror of ScraperSettings.AuditLogEnabled. AuditLogger.Log is called from a couple
// dozen call sites on the hot path of nearly every mutation, so this exists purely to let it
// check a plain field instead of round-tripping the database on every single one — the database
// row is still the source of truth (set at startup in Program.cs, updated whenever the
// system-user-only setting changes in AdminController).
public static class AuditLogSettings
{
    public static volatile bool Enabled = true;
}
