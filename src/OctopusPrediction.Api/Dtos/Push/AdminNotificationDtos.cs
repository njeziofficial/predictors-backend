using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Push;

// A notification from the admin Notifications page. Audience is "everyone", "yet-to-predict"
// (with WeekId) or "selected" (with UserIds); see NotificationAudiences. Important sends it even
// to players who turned admin notifications off, and only the system admin may set it. Email also
// sends it by email to those in the audience with push on in no browser (who kept emails on).
public record SendNotificationRequest(
    [Required, MaxLength(100)] string Title,
    [Required, MaxLength(300)] string Body,
    [MaxLength(200)] string? Url,
    [Required, MaxLength(20)] string Audience,
    [MaxLength(150)] string? WeekId,
    [MaxLength(1000)] Guid[]? UserIds,
    bool Important,
    bool Email = false);

// The same audience, before sending: how many players are in it, and how many would get it
// (notifications on, and admin notifications not turned off unless Important).
public record NotificationAudienceRequest(
    [Required, MaxLength(20)] string Audience,
    [MaxLength(150)] string? WeekId,
    [MaxLength(1000)] Guid[]? UserIds,
    bool Important,
    bool Email = false);

// WillReceive: by push. WillEmail: by email instead (when Email was asked for). EmailsLeftToday:
// null when email isn't set up.
public record NotificationAudienceDto(int InAudience, int WillReceive, int WillEmail, int? EmailsLeftToday);

public record SendNotificationResultDto(int Targeted, int Reached, int Emailed);

// A player for the "choose players" list.
public record NotificationPlayerDto(
    string Id,
    string Name,
    string? WhatsAppName,
    bool IsAdmin,
    // Notifications on in at least one browser.
    bool HasNotifications,
    // Turned admin notifications off (they'd only get an Important one).
    bool MutedAnnouncements);

public record PushBroadcastDto(
    string Id,
    string Title,
    string Body,
    string Url,
    string Audience,
    string? WeekId,
    string? WeekName,
    bool Important,
    int Targeted,
    int Reached,
    int Emailed,
    string SentByName,
    DateTime CreatedAt);
