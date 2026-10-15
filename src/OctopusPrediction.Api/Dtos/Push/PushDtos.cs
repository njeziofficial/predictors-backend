using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Push;

// The shape a browser's PushSubscription.toJSON() produces.
public record PushSubscriptionKeys([Required, MaxLength(200)] string P256dh, [Required, MaxLength(100)] string Auth);

public record SavePushSubscriptionRequest([Required, MaxLength(1000)] string Endpoint, [Required] PushSubscriptionKeys Keys);

public record RemovePushSubscriptionRequest([Required, MaxLength(1000)] string Endpoint);

public record PushPublicKeyDto(string PublicKey);

// Reached is how many of the user's browsers took the test notification.
public record PushTestResultDto(int Reached);

// Which notifications the player wants (on every device they've turned them on in), and whether
// to get them by email when push is on in none. Email defaults to on for an older client that
// doesn't send it.
public record NotificationPreferencesDto(bool Reminders, bool Announcements, bool Email = true, bool Messages = true, bool MessagePreview = true);
