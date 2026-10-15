using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Hubs;

namespace OctopusPrediction.Api.Services;

// One recipient of a chat message, and their conversation with the sender.
public record ChatRecipient(Guid UserId, Guid ConversationId);

// Pushes a chat message (or announcement) to the phones of recipients who don't have the app in
// front (PresenceTracker.IsActive): those looking at it already got it live. The notification
// shows who it's from and the start of the message (or only who, for players who hid previews),
// opens the conversation when tapped, and replaces the previous one from the same conversation.
// Runs after the request has answered, so sending stays instant.
public sealed class ChatPushNotifier(IServiceScopeFactory scopes, PresenceTracker presence, ILogger<ChatPushNotifier> logger)
{
    public const int PreviewLength = 140;
    // Kept by the push service for a phone that's off for up to a day.
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);

    /// <summary>Starts pushing in the background and returns at once.</summary>
    public void Notify(IReadOnlyList<ChatRecipient> recipients, string senderName, string body, bool announcement) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await NotifyAsync(recipients, senderName, body, announcement);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Push] Couldn't push a chat message from {Sender}", senderName);
            }
        });

    /// <summary>Pushes now; how many recipients a device took it on.</summary>
    public async Task<int> NotifyAsync(IReadOnlyList<ChatRecipient> recipients, string senderName, string body, bool announcement,
        CancellationToken ct = default)
    {
        var away = recipients.Where(r => !presence.IsActive(r.UserId)).ToList();
        if (away.Count == 0) return 0;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<PushSender>();

        var ids = away.Select(r => r.UserId).ToList();
        var wanted = await db.Users
            .Where(u => ids.Contains(u.Id) && u.NotifyMessages && !u.IsDisabled)
            .Select(u => new { u.Id, u.Role, u.ShowMessagePreview })
            .ToDictionaryAsync(u => u.Id, ct);

        var messages = away
            .Where(r => wanted.ContainsKey(r.UserId))
            .Select(r =>
            {
                var user = wanted[r.UserId];
                return (r.UserId, Message(senderName, body, announcement, user.ShowMessagePreview,
                    UrlFor(user.Role, r.ConversationId), r.ConversationId));
            })
            .ToList();
        return await push.SendEachAsync(messages, TimeToLive, ct, PushUrgency.High);
    }

    public static PushMessage Message(string senderName, string body, bool announcement, bool preview, string url, Guid conversationId) =>
        new(
            Title: announcement ? $"Announcement from {senderName}" : senderName,
            Body: preview ? Preview(body) : announcement ? "Open the app to read it." : $"New message from {senderName}",
            Url: url,
            // A newer message in the same conversation replaces the older one on the phone.
            Tag: $"chat-{conversationId:N}");

    /// <summary>Where tapping opens the conversation: admins read their messages in the back office.</summary>
    public static string UrlFor(UserRole role, Guid conversationId) =>
        $"{(role == UserRole.Admin ? "/admin/messages" : "/messages")}/{conversationId}";

    /// <summary>The start of a message: on one line, at most PreviewLength characters.</summary>
    public static string Preview(string body)
    {
        var oneLine = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= PreviewLength ? oneLine : oneLine[..(PreviewLength - 1)].TrimEnd() + "…";
    }
}
