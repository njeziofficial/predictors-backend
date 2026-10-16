using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Chat;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Hubs;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// In-app messaging. Every change happens here and is then pushed to the people concerned
// through ChatHub, so the browser gets the result of its own request from the response and
// everyone else's from the hub.
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(
    AppDbContext db, AppCache cache, IHubContext<ChatHub, IChatClient> hub, PresenceTracker presence, ChatPushNotifier pushes,
    IChatImageStore images, ILogger<ChatController> logger)
    : ControllerBase
{
    // The inbox: conversations with at least one message, most recent first.
    [HttpGet("conversations")]
    public async Task<IActionResult> List()
    {
        var me = CurrentUserId();
        return Ok(await ConversationsAsync(me, conversationId: null));
    }

    // One conversation, even before its first message (for a link straight to a new chat).
    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var me = CurrentUserId();
        var dto = (await ConversationsAsync(me, id, includeEmpty: true)).SingleOrDefault();
        return dto is null ? NotFound(new { message = "Conversation not found." }) : Ok(dto);
    }

    // Opens the conversation with another player, starting it if they've never spoken.
    [HttpPost("conversations")]
    public async Task<IActionResult> Start(StartConversationRequest request)
    {
        var me = CurrentUserId();
        if (request.UserId == me)
            return BadRequest(new { message = "You can't start a chat with yourself." });

        var other = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (other is null) return NotFound(new { message = "Player not found." });
        if (other.IsDisabled) return BadRequest(new { message = "This player's account is disabled." });

        var conversation = await GetOrCreateDirectAsync(me, other.Id);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // They started it with us at the same moment; theirs won, so use that one.
            db.ChangeTracker.Clear();
            var key = Conversation.KeyFor(me, other.Id);
            conversation = await db.Conversations.FirstAsync(c => c.DirectKey == key);
        }

        var dto = (await ConversationsAsync(me, conversation.Id, includeEmpty: true)).Single();
        return Ok(dto);
    }

    // A page of history, oldest first. Pass the oldest message id you have as `before` to
    // load the page before it.
    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id, [FromQuery] Guid? before)
    {
        var me = CurrentUserId();
        if (!await IsParticipantAsync(id, me)) return NotFound(new { message = "Conversation not found." });

        var query = db.ChatMessages.Where(m => m.ConversationId == id);
        if (before is not null)
        {
            var cursor = await db.ChatMessages
                .Where(m => m.Id == before && m.ConversationId == id)
                .Select(m => (DateTime?)m.CreatedAt)
                .FirstOrDefaultAsync();
            if (cursor is null) return BadRequest(new { message = "Unknown message cursor." });
            query = query.Where(m => m.CreatedAt < cursor);
        }

        // One extra row tells us whether there's anything older.
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(ChatLimits.PageSize + 1)
            .Select(m => new { Message = m, SenderName = m.Sender.Name })
            .ToListAsync();

        var hasMore = rows.Count > ChatLimits.PageSize;
        await LoadImageUrlsAsync(rows.Select(r => r.Message));
        var items = rows.Take(ChatLimits.PageSize)
            .Reverse()
            .Select(r => ToDto(r.Message, r.SenderName))
            .ToList();
        return Ok(new MessagePageDto(items, hasMore));
    }

    [HttpPost("conversations/{id:guid}/messages")]
    public async Task<IActionResult> Send(Guid id, SendMessageRequest request)
    {
        var body = request.Body.Trim();
        if (body.Length == 0) return BadRequest(new { message = "Message can't be empty." });
        return await PostAsync(id, body, request.ClientId, image: null, HttpContext.RequestAborted);
    }

    // An image (JPEG, PNG, GIF or WebP, up to ChatImages.MaxBytes), with an optional caption.
    [HttpPost("conversations/{id:guid}/images")]
    [RequestSizeLimit(ChatImages.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ChatImages.MaxBytes + 64 * 1024)]
    public async Task<IActionResult> SendImage(Guid id, [FromForm] SendImageRequest request)
    {
        if (!images.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Sending images isn't set up yet." });
        if (request.File.Length == 0) return BadRequest(new { message = "That image is empty." });
        if (request.File.Length > ChatImages.MaxBytes)
            return BadRequest(new { message = $"Images can be up to {ChatImages.MaxBytes / (1024 * 1024)} MB." });

        var ct = HttpContext.RequestAborted;
        using var buffer = new MemoryStream((int)request.File.Length);
        await request.File.CopyToAsync(buffer, ct);
        var data = buffer.ToArray();
        if (ChatImages.Detect(data) is not { } kind)
            return BadRequest(new { message = "Only JPEG, PNG, GIF and WebP images can be sent." });

        var image = new ImageUpload(data, kind, request.Width, request.Height);
        return await PostAsync(id, (request.Body ?? "").Trim(), request.ClientId, image, ct);
    }

    private sealed record ImageUpload(byte[] Data, ChatImages.Kind Kind, int? Width, int? Height);

    private async Task<IActionResult> PostAsync(Guid id, string body, string? clientId, ImageUpload? image, CancellationToken ct)
    {
        var me = CurrentUserId();
        var participants = await db.ConversationParticipants
            .Where(p => p.ConversationId == id)
            .Include(p => p.User)
            .ToListAsync();
        var mine = participants.FirstOrDefault(p => p.UserId == me);
        if (mine is null) return NotFound(new { message = "Conversation not found." });
        if (participants.Any(p => p.UserId != me && p.User.IsDisabled))
            return BadRequest(new { message = "This player's account is disabled, so they can't receive messages." });

        // A retry of a send that already went through: hand back the original.
        if (!string.IsNullOrEmpty(clientId))
        {
            var existing = await db.ChatMessages
                .FirstOrDefaultAsync(m => m.SenderId == me && m.ClientId == clientId, ct);
            if (existing is not null)
            {
                await LoadImageUrlsAsync([existing]);
                return Ok(ToDto(existing, mine.User.Name));
            }
        }

        var message = new ChatMessage
        {
            ConversationId = id,
            SenderId = me,
            Body = body,
            ClientId = string.IsNullOrEmpty(clientId) ? null : clientId,
        };
        if (image is not null)
        {
            message.ImageKey = ChatImages.KeyFor(id, message.Id, image.Kind);
            (message.ImageWidth, message.ImageHeight) = (image.Width, image.Height);
            try
            {
                await images.PutAsync(message.ImageKey, image.Data, image.Kind.ContentType, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[Chat] Couldn't store an image in Supabase Storage");
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Couldn't upload the image. Try again." });
            }
        }

        db.ChatMessages.Add(message);
        var conversation = await db.Conversations.FirstAsync(c => c.Id == id, ct);
        conversation.LastMessageAt = message.CreatedAt;
        // Sending a message means you've read everything before it.
        mine.LastReadAt = message.CreatedAt;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch when (message.ImageKey is not null)
        {
            // Don't leave the image behind with no message pointing at it.
            await TryDeleteImageAsync(message.ImageKey);
            throw;
        }

        await LoadImageUrlsAsync([message]);
        var dto = ToDto(message, mine.User.Name);
        await hub.Clients.Users(participants.Select(p => p.UserId.ToString())).MessageReceived(dto);
        // And to the phones of those who don't have the app in front.
        pushes.Notify([.. participants.Where(p => p.UserId != me).Select(p => new ChatRecipient(p.UserId, id))],
            mine.User.Name, PushText(message), announcement: false);
        return Ok(dto);
    }

    // What the phone notification says: the text, marked as a photo when there is one.
    private static string PushText(ChatMessage m) =>
        m.ImageKey is null ? m.Body : m.Body.Length == 0 ? "📷 Photo" : $"📷 {m.Body}";

    private async Task TryDeleteImageAsync(string key)
    {
        try
        {
            await images.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Chat] Couldn't delete image {Key} from Supabase Storage", key);
        }
    }

    [HttpPost("conversations/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var me = CurrentUserId();
        var participant = await db.ConversationParticipants
            .FirstOrDefaultAsync(p => p.ConversationId == id && p.UserId == me);
        if (participant is null) return NotFound(new { message = "Conversation not found." });

        var latest = await db.Conversations.Where(c => c.Id == id).Select(c => c.LastMessageAt).FirstAsync();
        if (participant.LastReadAt is null || participant.LastReadAt < latest)
        {
            participant.LastReadAt = latest;
            await db.SaveChangesAsync();

            var receipt = new ReadReceiptDto(id.ToString(), me.ToString(), latest);
            // Both sides: the other person's "Seen", and the reader's own other tabs' badges.
            var everyone = await cache.ChatParticipantsAsync(db, id);
            await hub.Clients.Users(everyone.Select(u => u.ToString())).ConversationRead(receipt);
        }

        return Ok(new ReadReceiptDto(id.ToString(), me.ToString(), participant.LastReadAt!.Value));
    }

    // Only the sender can delete, and only their own message.
    [HttpDelete("messages/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var me = CurrentUserId();
        var message = await db.ChatMessages.Include(m => m.Sender).FirstOrDefaultAsync(m => m.Id == id);
        if (message is null) return NotFound(new { message = "Message not found." });
        if (message.SenderId != me)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "You can only delete your own messages." });

        if (message.DeletedAt is null)
        {
            var imageKey = message.ImageKey;
            message.DeletedAt = DateTime.UtcNow;
            message.Body = string.Empty;
            (message.ImageKey, message.ImageWidth, message.ImageHeight) = (null, null, null);
            await db.SaveChangesAsync();
            // Gone for good: a link handed out earlier stops working once the file is removed.
            if (imageKey is not null) await TryDeleteImageAsync(imageKey);

            var everyone = await cache.ChatParticipantsAsync(db, message.ConversationId);
            await hub.Clients.Users(everyone.Select(u => u.ToString())).MessageDeleted(ToDto(message, message.Sender.Name));
        }

        return Ok(ToDto(message, message.Sender.Name));
    }

    // Everyone you could start a chat with: active players other than yourself.
    [HttpGet("users")]
    public async Task<IActionResult> Directory()
    {
        var me = CurrentUserId();
        var online = presence.Online();
        var users = await cache.ChatDirectoryAsync(db);
        return Ok(users
            .Where(u => u.Id != me)
            .Select(u => new ChatUserDto(u.Id.ToString(), u.Name, u.WhatsAppName, u.IsAdmin, online.Contains(u.Id))));
    }

    // An admin message to many players at once. It lands in each player's direct conversation
    // with the admin, so a player who wants to answer just replies there.
    [HttpPost("broadcast")]
    [Authorize(Roles = "Admin")]
    [RequirePermission(Permissions.MessagesBroadcast)]
    public async Task<IActionResult> Broadcast(BroadcastRequest request)
    {
        var me = CurrentUserId();
        var body = request.Body.Trim();
        if (body.Length == 0) return BadRequest(new { message = "Announcement can't be empty." });

        var sender = await db.Users.FirstAsync(u => u.Id == me);
        // Players only, like the admin notifications: the system admin account doesn't play.
        var recipientsQuery = db.Users.Where(u => u.Id != me && !u.IsSystemUser && !u.IsDisabled);
        if (request.UserIds is { Count: > 0 })
            recipientsQuery = recipientsQuery.Where(u => request.UserIds.Contains(u.Id));
        var recipients = await recipientsQuery.Select(u => u.Id).ToListAsync();
        if (recipients.Count == 0) return BadRequest(new { message = "No active players to send to." });

        var keys = recipients.ToDictionary(r => Conversation.KeyFor(me, r));
        var keyList = keys.Keys.ToList();
        var now = DateTime.UtcNow;
        var sent = new List<(Guid Recipient, ChatMessage Message)>();

        // Two tries: if another request creates one of these conversations between our lookup
        // and our insert (e.g. that player messaging us right now), start over and reuse it.
        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.Conversations
                .Where(c => keyList.Contains(c.DirectKey))
                .ToDictionaryAsync(c => c.DirectKey);

            foreach (var (key, recipient) in keys)
            {
                if (!existing.TryGetValue(key, out var conversation))
                {
                    conversation = NewDirect(me, recipient);
                    db.Conversations.Add(conversation);
                }
                conversation.LastMessageAt = now;

                var message = new ChatMessage
                {
                    ConversationId = conversation.Id,
                    SenderId = me,
                    Body = body,
                    IsAnnouncement = true,
                    CreatedAt = now,
                };
                db.ChatMessages.Add(message);
                sent.Add((recipient, message));
            }
            AuditLogger.Log(db, subject: null, actor: sender, action: "MessageBroadcast",
                details: $"Announcement to {recipients.Count} player{(recipients.Count == 1 ? "" : "s")}: "
                         + (body.Length > 120 ? body[..120] + "…" : body));

            try
            {
                await db.SaveChangesAsync();
                break;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                db.ChangeTracker.Clear();
                sent.Clear();
            }
        }

        // The admin has obviously read their own announcement.
        await db.ConversationParticipants
            .Where(p => p.UserId == me && keyList.Contains(p.Conversation.DirectKey))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastReadAt, now));

        foreach (var (recipient, message) in sent)
            await hub.Clients.User(recipient.ToString()).MessageReceived(ToDto(message, sender.Name));
        pushes.Notify([.. sent.Select(s => new ChatRecipient(s.Recipient, s.Message.ConversationId))],
            sender.Name, body, announcement: true);

        return Ok(new BroadcastResultDto(recipients.Count));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<List<ConversationDto>> ConversationsAsync(Guid me, Guid? conversationId, bool includeEmpty = false)
    {
        var query = db.ConversationParticipants.Where(p => p.UserId == me);
        if (conversationId is not null) query = query.Where(p => p.ConversationId == conversationId);

        var rows = await query
            .Select(p => new
            {
                p.ConversationId,
                p.LastReadAt,
                p.Conversation.LastMessageAt,
                Other = p.Conversation.Participants
                    .Where(o => o.UserId != me)
                    .Select(o => new { o.UserId, o.LastReadAt, o.User.Name, o.User.WhatsAppName, o.User.Role })
                    .FirstOrDefault(),
                Last = p.Conversation.Messages
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new { Message = m, SenderName = m.Sender.Name })
                    .FirstOrDefault(),
                Unread = db.ChatMessages.Count(m =>
                    m.ConversationId == p.ConversationId
                    && m.SenderId != me
                    && m.DeletedAt == null
                    && (p.LastReadAt == null || m.CreatedAt > p.LastReadAt)),
            })
            .OrderByDescending(r => r.LastMessageAt)
            .ToListAsync();

        var online = presence.Online();
        // The inbox shows a photo as "📷 Photo", which needs the image there.
        await LoadImageUrlsAsync(rows.Where(r => r.Last is not null).Select(r => r.Last!.Message));
        return rows
            .Where(r => r.Other is not null && (includeEmpty || r.Last is not null))
            .Select(r => new ConversationDto(
                r.ConversationId.ToString(),
                new ChatUserDto(r.Other!.UserId.ToString(), r.Other.Name, r.Other.WhatsAppName,
                    r.Other.Role == UserRole.Admin, online.Contains(r.Other.UserId)),
                r.Last is null ? null : ToDto(r.Last.Message, r.Last.SenderName),
                r.Unread,
                r.LastReadAt,
                r.Other.LastReadAt,
                r.LastMessageAt))
            .ToList();
    }

    private async Task<Conversation> GetOrCreateDirectAsync(Guid a, Guid b)
    {
        var key = Conversation.KeyFor(a, b);
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == key);
        if (conversation is not null) return conversation;

        conversation = NewDirect(a, b);
        db.Conversations.Add(conversation);
        return conversation;
    }

    private static Conversation NewDirect(Guid a, Guid b)
    {
        var conversation = new Conversation { DirectKey = Conversation.KeyFor(a, b) };
        conversation.Participants.Add(new ConversationParticipant { ConversationId = conversation.Id, UserId = a });
        conversation.Participants.Add(new ConversationParticipant { ConversationId = conversation.Id, UserId = b });
        return conversation;
    }

    private async Task<bool> IsParticipantAsync(Guid conversationId, Guid userId) =>
        (await cache.ChatParticipantsAsync(db, conversationId)).Contains(userId);

    // Signed links for the images among these messages, fetched together before ToDto uses them.
    private IReadOnlyDictionary<string, string> _imageUrls = new Dictionary<string, string>();

    private async Task LoadImageUrlsAsync(IEnumerable<ChatMessage> messages)
    {
        var keys = messages.Where(m => m.DeletedAt is null && m.ImageKey is not null).Select(m => m.ImageKey!).ToList();
        if (keys.Count == 0 || !images.IsConfigured) return;
        try
        {
            _imageUrls = await images.UrlsAsync(keys, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The messages still load; their images come back on the next fetch.
            logger.LogWarning(ex, "[Chat] Couldn't sign image links");
        }
    }

    private ChatMessageDto ToDto(ChatMessage m, string senderName) => new(
        m.Id.ToString(), m.ConversationId.ToString(), m.SenderId.ToString(), senderName,
        m.DeletedAt is null ? m.Body : null, m.IsAnnouncement, m.ClientId, m.CreatedAt, m.DeletedAt,
        m.DeletedAt is null && m.ImageKey is not null && _imageUrls.TryGetValue(m.ImageKey, out var url)
            ? new ChatImageDto(url, m.ImageWidth, m.ImageHeight)
            : null);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
