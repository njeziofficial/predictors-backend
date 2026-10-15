using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Players' in-app notifications (UserNotification): what's added whenever a reminder or admin
// notification goes out, so the app can list it on the player's next visit.
public static class NotificationInbox
{
    public const string Reminder = "reminder";
    public const string Announcement = "announcement";
    public const string Test = "test";

    // Older ones are deleted, read or not.
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);

    /// <summary>Adds the notification to each of these users' inboxes (staged: the caller saves).</summary>
    public static void Add(AppDbContext db, IEnumerable<Guid> userIds, string kind, string title, string body, string url)
    {
        var now = DateTime.UtcNow;
        foreach (var userId in userIds.Distinct())
            db.UserNotifications.Add(new UserNotification
            {
                UserId = userId,
                Kind = kind,
                Title = Clip(title, 100),
                Body = Clip(body, 300),
                Url = Clip(url, 200),
                CreatedAt = now,
            });
    }

    /// <summary>Deletes notifications older than KeepFor; how many went.</summary>
    public static Task<int> DeleteOldAsync(AppDbContext db, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        return db.UserNotifications.Where(n => n.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;
}
