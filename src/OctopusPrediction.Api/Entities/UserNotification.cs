namespace OctopusPrediction.Api.Entities;

// A notification in a player's in-app inbox: every prediction reminder and admin notification
// lands here as well as going out by push or email, so the app can show it when the player comes
// back, whether or not the push arrived. Reading or dismissing it sets ReadAt.
public class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    // "reminder", "announcement" or "test".
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    // A page in the app, opened when the notification is.
    public string Url { get; set; } = "/";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}
