using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Push;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Push notifications: each signed-in user registers the browsers they've turned them on in.
[ApiController]
[Route("api/push")]
[Authorize]
public class PushController(AppDbContext db, PushKeyProvider keys, PushSender sender) : ControllerBase
{
    // The browser needs the server's public key to subscribe.
    [HttpGet("public-key")]
    public async Task<IActionResult> GetPublicKey() => Ok(new PushPublicKeyDto((await keys.GetAsync()).PublicKey));

    // Saves this browser's subscription for the signed-in user. A browser has one subscription
    // whoever is signed in, so one last used by someone else moves to this user.
    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe(SavePushSubscriptionRequest request)
    {
        if (!PushSender.IsAllowedEndpoint(request.Endpoint))
            return BadRequest(new { message = "That isn't a push service this app can use." });

        var userId = CurrentUserId();
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint);
        if (existing is null)
        {
            db.PushSubscriptions.Add(new Entities.PushSubscription
            {
                UserId = userId,
                Endpoint = request.Endpoint,
                P256dh = request.Keys.P256dh,
                Auth = request.Keys.Auth,
            });
        }
        else
        {
            existing.UserId = userId;
            existing.P256dh = request.Keys.P256dh;
            existing.Auth = request.Keys.Auth;
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Forgets this browser's subscription: notifications switched off here, or signing out.
    [HttpPost("subscriptions/remove")]
    public async Task<IActionResult> Unsubscribe(RemovePushSubscriptionRequest request)
    {
        var userId = CurrentUserId();
        await db.PushSubscriptions
            .Where(s => s.Endpoint == request.Endpoint && s.UserId == userId)
            .ExecuteDeleteAsync();
        return NoContent();
    }

    // Sends a notification to the signed-in user's own browsers, to check it works.
    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        var message = new PushMessage("Notifications are on", "This device will get Octopus Prediction notifications.", "/predict", "test");
        var reached = await sender.SendAsync([CurrentUserId()], message, TimeSpan.FromMinutes(10));
        // In the app too, so the test shows how notifications appear there.
        NotificationInbox.Add(db, [CurrentUserId()], NotificationInbox.Test, message.Title, message.Body, message.Url);
        await db.SaveChangesAsync();
        return Ok(new PushTestResultDto(reached));
    }

    // Which notifications the signed-in user wants, on all their devices.
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var userId = CurrentUserId();
        var prefs = await db.Users.Where(u => u.Id == userId)
            .Select(u => new NotificationPreferencesDto(u.NotifyReminders, u.NotifyAnnouncements, u.NotifyByEmail, u.NotifyMessages, u.ShowMessagePreview))
            .FirstOrDefaultAsync();
        return prefs is null ? NotFound() : Ok(prefs);
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreferences(NotificationPreferencesDto request)
    {
        var user = await db.Users.FindAsync(CurrentUserId());
        if (user is null) return NotFound();
        user.NotifyReminders = request.Reminders;
        user.NotifyAnnouncements = request.Announcements;
        user.NotifyByEmail = request.Email;
        user.NotifyMessages = request.Messages;
        user.ShowMessagePreview = request.MessagePreview;
        await db.SaveChangesAsync();
        return Ok(new NotificationPreferencesDto(user.NotifyReminders, user.NotifyAnnouncements, user.NotifyByEmail,
            user.NotifyMessages, user.ShowMessagePreview));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
