using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Chat;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Hubs;

// What the server pushes to browsers. Method names are the event names the client listens for.
public interface IChatClient
{
    Task MessageReceived(ChatMessageDto message);
    Task MessageDeleted(ChatMessageDto message);
    Task ConversationRead(ReadReceiptDto receipt);
    Task Typing(string conversationId, string userId);
    Task PresenceChanged(string userId, bool online);
    // Shared data changed ("fixtures", "leaderboard", "settings"): refetch your copies of it.
    // Sent by LiveUpdateNotifier; rides this connection because every signed-in tab holds one.
    Task DataChanged(string[] areas);
}

// Real-time delivery only. Anything that changes data (sending, reading, deleting, announcing)
// goes through ChatController so it gets the same auth, validation and permission checks as the
// rest of the API; the controller then pushes the result out through IHubContext<ChatHub>.
// Messages reach a user's every open tab via Clients.User, keyed by the JWT's NameIdentifier.
[Authorize]
public class ChatHub(AppDbContext db, AppCache cache, PresenceTracker presence) : Hub<IChatClient>
{
    public const string Path = "/api/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        var userId = CurrentUserId();
        // DisabledUserMiddleware already turns them away at negotiate; this covers the
        // transports that skip it.
        var access = await cache.UserAccessAsync(db, userId);
        if (access is null || access.IsDisabled)
        {
            Context.Abort();
            return;
        }

        if (presence.Connect(userId))
            await Clients.Others.PresenceChanged(userId.ToString(), true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = CurrentUserId();
        if (presence.Disconnect(userId))
            await Clients.Others.PresenceChanged(userId.ToString(), false);
        await base.OnDisconnectedAsync(exception);
    }

    // "I'm typing in this conversation." The client throttles these; the other side shows the
    // indicator for a few seconds after each one. Membership comes from the cache, so a busy
    // chat doesn't cost a query per keystroke burst.
    public async Task Typing(Guid conversationId)
    {
        var userId = CurrentUserId();
        var participants = await cache.ChatParticipantsAsync(db, conversationId);
        if (!participants.Contains(userId)) return; // not their conversation

        await Clients.Users(participants.Where(id => id != userId).Select(id => id.ToString()))
            .Typing(conversationId.ToString(), userId.ToString());
    }

    private Guid CurrentUserId() =>
        Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
