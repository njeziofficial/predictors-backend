using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Push;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// The admin Notifications page: push notifications written by an admin, sent to everyone,
// players yet to predict a week, or chosen players, with a history of what was sent.
[ApiController]
[Route("api/admin/notifications")]
[Authorize(Roles = "Admin")]
[RequirePermission(Permissions.NotificationsSend)]
public class AdminNotificationsController(AppDbContext db, PushSender sender) : ControllerBase
{
    // Sends an admin who isn't the system admin may make in an hour, so nobody floods phones.
    public const int HourlyLimit = 5;
    public const int MaxTitle = 60;
    public const int MaxBody = 200;
    // Kept by the push service for a phone that's off, then dropped.
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

    [HttpGet("players")]
    public async Task<IActionResult> GetPlayers()
    {
        var subscribed = (await db.PushSubscriptions.Select(s => s.UserId).Distinct().ToListAsync()).ToHashSet();
        var players = await NotificationAudiences.Players(db)
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name, u.WhatsAppName, u.Role, u.NotifyAnnouncements })
            .ToListAsync();
        return Ok(players.Select(u => new NotificationPlayerDto(u.Id.ToString(), u.Name, u.WhatsAppName,
            u.Role == UserRole.Admin, subscribed.Contains(u.Id), !u.NotifyAnnouncements)));
    }

    [HttpPost("audience")]
    public async Task<IActionResult> Audience(NotificationAudienceRequest request)
    {
        var (audience, problem) = await NotificationAudiences.ResolveAsync(db, request.Audience, request.WeekId, request.UserIds);
        if (audience is null) return BadRequest(new { message = problem });
        var receiving = audience.Where(u => request.Important || u.NotifyAnnouncements).Select(u => u.Id).ToList();
        var withSubscriptions = await sender.WithSubscriptionsAsync(receiving);
        return Ok(new NotificationAudienceDto(audience.Count, withSubscriptions.Count));
    }

    [HttpPost]
    public async Task<IActionResult> Send(SendNotificationRequest request)
    {
        var title = request.Title.Trim();
        var body = request.Body.Trim();
        if (title.Length == 0 || title.Length > MaxTitle)
            return BadRequest(new { message = $"The title must be 1 to {MaxTitle} characters." });
        if (body.Length == 0 || body.Length > MaxBody)
            return BadRequest(new { message = $"The message must be 1 to {MaxBody} characters." });
        var url = string.IsNullOrWhiteSpace(request.Url) ? "/" : request.Url.Trim();
        if (!IsAppPath(url))
            return BadRequest(new { message = "The link must be a page in the app, like /predict." });

        var me = await db.Users.FirstAsync(u => u.Id == CurrentUserId());
        if (request.Important && !me.IsSystemUser)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Only the system admin can send to players who turned admin notifications off." });
        if (!me.IsSystemUser)
        {
            var since = DateTime.UtcNow.AddHours(-1);
            var recent = await db.PushBroadcasts.CountAsync(b => b.SentByUserId == me.Id && b.CreatedAt > since);
            if (recent >= HourlyLimit)
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new { message = $"You can send {HourlyLimit} notifications an hour. Try again later." });
        }

        var (audience, problem) = await NotificationAudiences.ResolveAsync(db, request.Audience, request.WeekId, request.UserIds);
        if (audience is null) return BadRequest(new { message = problem });
        var receiving = audience.Where(u => request.Important || u.NotifyAnnouncements).Select(u => u.Id).ToList();
        var targeted = (await sender.WithSubscriptionsAsync(receiving)).Count;
        if (targeted == 0)
            return BadRequest(new { message = request.Important
                ? "Nobody in that audience has notifications turned on, so there's no one to send to."
                : "Nobody in that audience will get it: they have notifications off, or turned off news from the admins." });

        var broadcast = new PushBroadcast
        {
            Title = title,
            Body = body,
            Url = url,
            Audience = NotificationAudiences.Normalize(request.Audience)!,
            WeekId = NotificationAudiences.Normalize(request.Audience) == NotificationAudiences.YetToPredict ? request.WeekId : null,
            Important = request.Important,
            Targeted = targeted,
            SentByUserId = me.Id,
            SentByName = me.Name,
        };
        // Each its own tag, so a second notification doesn't replace the first on the phone.
        broadcast.Reached = await sender.SendAsync(receiving, new PushMessage(title, body, url, $"notice-{broadcast.Id:N}"), TimeToLive);

        db.PushBroadcasts.Add(broadcast);
        AuditLogger.Log(db, subject: null, actor: me, action: "PushNotificationSent",
            details: $"\"{title}\" to {broadcast.Audience} ({broadcast.Reached} of {targeted} reached)"
                     + (request.Important ? ", marked important" : ""));
        await db.SaveChangesAsync();

        return Ok(new SendNotificationResultDto(targeted, broadcast.Reached));
    }

    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var sent = await db.PushBroadcasts.OrderByDescending(b => b.CreatedAt).Take(30).ToListAsync();
        var weekIds = sent.Select(b => b.WeekId).OfType<string>().Distinct().ToList();
        var weekNames = await db.MatchWeeks.Where(w => weekIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return Ok(sent.Select(b => new PushBroadcastDto(b.Id.ToString(), b.Title, b.Body, b.Url, b.Audience, b.WeekId,
            b.WeekId is { } w ? weekNames.GetValueOrDefault(w) : null, b.Important, b.Targeted, b.Reached, b.SentByName, b.CreatedAt)));
    }

    // A page in the app ("/predict", "/leaderboard?week=3"), never another site: "//evil.com" and
    // "/\evil.com" would leave the app when the notification is tapped.
    public static bool IsAppPath(string url) =>
        url.Length <= 200 && url.StartsWith('/') && !url.StartsWith("//") && !url.Contains('\\') && !url.Any(char.IsWhiteSpace);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

// Who an admin notification can go to: active players (admins play too; the system admin
// account doesn't), as everyone, those yet to predict a week, or chosen ones.
public static class NotificationAudiences
{
    public const string Everyone = "everyone";
    public const string YetToPredict = "yet-to-predict";
    public const string Selected = "selected";

    public static string? Normalize(string? audience) =>
        audience?.Trim().ToLowerInvariant() switch
        {
            Everyone => Everyone,
            YetToPredict => YetToPredict,
            Selected => Selected,
            _ => null,
        };

    public static IQueryable<User> Players(AppDbContext db) => db.Users.Where(u => !u.IsSystemUser && !u.IsDisabled);

    /// <summary>The players in the audience, or null and why it can't be used.</summary>
    public static async Task<(List<User>? Players, string? Problem)> ResolveAsync(
        AppDbContext db, string? audience, string? weekId, IReadOnlyCollection<Guid>? userIds)
    {
        switch (Normalize(audience))
        {
            case Everyone:
                return (await Players(db).ToListAsync(), null);
            case YetToPredict:
                var week = string.IsNullOrWhiteSpace(weekId)
                    ? null
                    : await db.MatchWeeks.Include(w => w.Fixtures).FirstOrDefaultAsync(w => w.Id == weekId);
                if (week is null) return (null, "Choose the week whose predictions to chase.");
                if (!week.Fixtures.Any(f => f.Status == FixtureStatus.PreMatch))
                    return (null, "That week has no matches left to predict.");
                return (await ReminderSchedule.StillToPredict(db.Users, week).ToListAsync(), null);
            case Selected:
                if (userIds is not { Count: > 0 }) return (null, "Choose at least one player.");
                var ids = userIds.Distinct().ToList();
                return (await Players(db).Where(u => ids.Contains(u.Id)).ToListAsync(), null);
            default:
                return (null, "Choose who to send it to.");
        }
    }
}
