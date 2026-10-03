using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Adds an entry to the change-tracked context; callers still call SaveChangesAsync
// themselves (usually alongside the change the entry is describing).
public static class AuditLogger
{
    public static void Log(
        AppDbContext db, User? subject, User? actor, string action,
        string? field = null, string? previousValue = null, string? newValue = null, string? details = null)
    {
        if (!AuditLogSettings.Enabled) return;
        LogAlways(db, subject, actor, action, field, previousValue, newValue, details);
    }

    // Bypasses the AuditLogSettings.Enabled gate — for the one event (toggling that setting
    // itself) that must always be recorded regardless of which direction it's toggling, since
    // it's what explains any gap in the log around it.
    public static void LogAlways(
        AppDbContext db, User? subject, User? actor, string action,
        string? field = null, string? previousValue = null, string? newValue = null, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = subject?.Id,
            UserName = subject?.Name,
            ActorUserId = actor?.Id,
            ActorName = actor?.Name,
            Action = action,
            Field = field,
            PreviousValue = previousValue,
            NewValue = newValue,
            Details = details
        });
    }
}
