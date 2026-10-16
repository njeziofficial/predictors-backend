using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Chat;

// Who's on the other end. Deliberately no email or phone number: any player can see this.
public record ChatUserDto(string Id, string Name, string? WhatsAppName, bool IsAdmin, bool IsOnline);

public record ChatMessageDto(
    string Id,
    string ConversationId,
    string SenderId,
    string SenderName,
    // Null once deleted.
    string? Body,
    bool IsAnnouncement,
    string? ClientId,
    DateTime CreatedAt,
    DateTime? DeletedAt,
    // Null for text only, and once deleted.
    ChatImageDto? Image = null);

// A signed link that works for a while (SupabaseStorageSettings.LinkHours); fetching the messages again
// gives a fresh one.
public record ChatImageDto(string Url, int? Width, int? Height);

public record ConversationDto(
    string Id,
    ChatUserDto Other,
    ChatMessageDto? LastMessage,
    int UnreadCount,
    DateTime? MyLastReadAt,
    // When the other person last read; every message of mine up to here shows as "Seen".
    DateTime? OtherLastReadAt,
    DateTime LastMessageAt);

public record MessagePageDto(List<ChatMessageDto> Items, bool HasMore);

public record ReadReceiptDto(string ConversationId, string UserId, DateTime ReadAt);

public record BroadcastResultDto(int Recipients);

public class SendMessageRequest
{
    [Required, MaxLength(ChatLimits.MaxBody)]
    public string Body { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ClientId { get; set; }
}

// An image, with an optional caption. Width and height are the image's own, as the sender's
// browser read them.
public class SendImageRequest
{
    [Required]
    public IFormFile File { get; set; } = null!;

    [MaxLength(ChatLimits.MaxBody)]
    public string? Body { get; set; }

    [MaxLength(64)]
    public string? ClientId { get; set; }

    [Range(1, Services.ChatImages.MaxSide)]
    public int? Width { get; set; }

    [Range(1, Services.ChatImages.MaxSide)]
    public int? Height { get; set; }
}

public class StartConversationRequest
{
    [Required]
    public Guid UserId { get; set; }
}

public class BroadcastRequest
{
    [Required, MaxLength(ChatLimits.MaxBody)]
    public string Body { get; set; } = string.Empty;

    // Leave empty to send to every active player except yourself.
    public List<Guid>? UserIds { get; set; }
}

public static class ChatLimits
{
    public const int MaxBody = 2000;
    public const int PageSize = 40;
}
