using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Controllers;

// The signed-in user's in-app notifications: the latest ones with how many are unread, and
// marking them read (opening one, dismissing it, or all at once).
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(AppDbContext db) : ControllerBase
{
    public const int ListSize = 30;

    public record NotificationDto(string Id, string Kind, string Title, string Body, string Url, DateTime CreatedAt, bool Read);
    public record InboxDto(int Unread, IReadOnlyList<NotificationDto> Items);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var me = CurrentUserId();
        var mine = db.UserNotifications.Where(n => n.UserId == me);
        var unread = await mine.CountAsync(n => n.ReadAt == null);
        var items = await mine.OrderByDescending(n => n.CreatedAt).Take(ListSize)
            .Select(n => new NotificationDto(n.Id.ToString(), n.Kind, n.Title, n.Body, n.Url, n.CreatedAt, n.ReadAt != null))
            .ToListAsync();
        return Ok(new InboxDto(unread, items));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var me = CurrentUserId();
        var notification = await db.UserNotifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == me);
        if (notification is null) return NotFound();
        notification.ReadAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var me = CurrentUserId();
        var now = DateTime.UtcNow;
        foreach (var n in await db.UserNotifications.Where(n => n.UserId == me && n.ReadAt == null).ToListAsync())
            n.ReadAt = now;
        await db.SaveChangesAsync();
        return NoContent();
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
