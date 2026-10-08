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
    DateTime? DeletedAt);

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
