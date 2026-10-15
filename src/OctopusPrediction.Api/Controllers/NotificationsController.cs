using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Controllers;

// The signed-in user's in-app notifications: a page of them (newest first, optionally searched or
// only unread) with how many are unread, marking them read (opening one, or all at once), and
// deleting them (one, several, or every read one).
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(AppDbContext db) : ControllerBase
{
    public const int ListSize = 30;
    public const int MaxPageSize = 50;
    public const int MaxDeleteAtOnce = 100;

    public record NotificationDto(string Id, string Kind, string Title, string Body, string Url, DateTime CreatedAt, bool Read);
    // Unread: all of the user's unread ones, whatever the search. Total: how many match it.
    public record InboxDto(int Unread, IReadOnlyList<NotificationDto> Items, int Total, int Page, int PageSize);
    public record DeleteNotificationsRequest(Guid[] Ids);
    public record DeletedDto(int Deleted);

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? q = null, [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = ListSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var me = CurrentUserId();
        var mine = db.UserNotifications.Where(n => n.UserId == me);
        var unread = await mine.CountAsync(n => n.ReadAt == null);

        var matching = mine;
        if (unreadOnly) matching = matching.Where(n => n.ReadAt == null);
        var search = q?.Trim().ToLower();
        if (!string.IsNullOrEmpty(search))
            matching = matching.Where(n => n.Title.ToLower().Contains(search) || n.Body.ToLower().Contains(search));

        var total = await matching.CountAsync();
        var items = await matching.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new NotificationDto(n.Id.ToString(), n.Kind, n.Title, n.Body, n.Url, n.CreatedAt, n.ReadAt != null))
            .ToListAsync();
        return Ok(new InboxDto(unread, items, total, page, pageSize));
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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var me = CurrentUserId();
        var notification = await db.UserNotifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == me);
        if (notification is null) return NotFound();
        db.UserNotifications.Remove(notification);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Several chosen on the Notifications page. Ids that aren't the user's are ignored.
    [HttpPost("delete")]
    public async Task<IActionResult> DeleteMany(DeleteNotificationsRequest request)
    {
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0) return BadRequest(new { message = "Choose at least one notification." });
        if (ids.Count > MaxDeleteAtOnce)
            return BadRequest(new { message = $"You can delete up to {MaxDeleteAtOnce} at a time." });
        var me = CurrentUserId();
        var doomed = await db.UserNotifications.Where(n => n.UserId == me && ids.Contains(n.Id)).ToListAsync();
        db.UserNotifications.RemoveRange(doomed);
        await db.SaveChangesAsync();
        return Ok(new DeletedDto(doomed.Count));
    }

    [HttpPost("delete-read")]
    public async Task<IActionResult> DeleteRead()
    {
        var me = CurrentUserId();
        var doomed = await db.UserNotifications.Where(n => n.UserId == me && n.ReadAt != null).ToListAsync();
        db.UserNotifications.RemoveRange(doomed);
        await db.SaveChangesAsync();
        return Ok(new DeletedDto(doomed.Count));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
