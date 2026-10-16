namespace OctopusPrediction.Api.Entities;

// A one-to-one chat between two players (or a player and an admin). Admin announcements are
// delivered into the same direct conversations, so a player can simply reply to one.
public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // "<smaller user id>:<larger user id>" — unique, so a pair of users only ever has one
    // conversation however both of them start it.
    public string DirectKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Bumped on every message so the inbox can sort without scanning messages.
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    public ICollection<ConversationParticipant> Participants { get; set; } = [];
    public ICollection<ChatMessage> Messages { get; set; } = [];

    public static string KeyFor(Guid a, Guid b) =>
        a.CompareTo(b) < 0 ? $"{a}:{b}" : $"{b}:{a}";
}

public class ConversationParticipant
{
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    // Everything up to here has been seen: drives unread counts and the other side's "Seen".
    public DateTime? LastReadAt { get; set; }
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public Guid SenderId { get; set; }
    public User Sender { get; set; } = null!;
    // Empty for an image sent without a caption.
    public string Body { get; set; } = string.Empty;
    // An attached image, kept in Supabase Storage under this key (ChatImages.KeyFor); null for text only, and
    // cleared when the message is deleted. Width and height (from the sender's browser) let the
    // app hold the image's space before it loads.
    public string? ImageKey { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    // Sent by an admin to many players at once; shown with an announcement style.
    public bool IsAnnouncement { get; set; }
    // The sender's own id for this message, so a retried send can't post it twice.
    public string? ClientId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Soft delete: the body is cleared and the bubble reads "Message deleted".
    public DateTime? DeletedAt { get; set; }
}
