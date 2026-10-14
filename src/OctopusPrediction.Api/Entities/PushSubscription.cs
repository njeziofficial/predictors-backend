namespace OctopusPrediction.Api.Entities;

// One browser or installed app that has agreed to receive push notifications for a user. A
// player with a phone and a laptop has two. Endpoint is the push service's address for that
// browser (Google's FCM, Apple's or Mozilla's); P256dh and Auth are the keys its messages are
// encrypted with. Deleted when the push service says the subscription has gone (404/410).
public class PushSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A notification an admin sent from the Notifications page, kept for its history list.
public class PushBroadcast
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Url { get; set; } = "/";
    // "everyone", "yet-to-predict" (with WeekId) or "selected"; see NotificationAudiences.
    public string Audience { get; set; } = string.Empty;
    public string? WeekId { get; set; }
    // Sent even to players who turned admin notifications off (system admin only).
    public bool Important { get; set; }
    // Players in the audience who could get it (notifications on, not muted), and how many of
    // them at least one device actually took it on.
    public int Targeted { get; set; }
    public int Reached { get; set; }
    // Players without push who were sent it by email instead.
    public int Emailed { get; set; }
    public Guid? SentByUserId { get; set; }
    public string SentByName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// The server's VAPID key pair, which proves to the push services that notifications come from
// this backend. Generated once on first use and kept here so it survives redeploys: a new pair
// would orphan every existing subscription. Push:PublicKey/Push:PrivateKey in config override it.
public class PushKeys
{
    public int Id { get; set; } = 1;
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
