using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin/audit")]
[Authorize(Roles = "Admin")]
public class AdminAuditController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? userId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.AuditLogs.AsQueryable();

        if (Guid.TryParse(userId, out var parsedUserId))
            query = query.Where(a => a.UserId == parsedUserId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(a =>
                EF.Functions.ILike(a.Action, pattern) ||
                (a.UserName != null && EF.Functions.ILike(a.UserName, pattern)) ||
                (a.ActorName != null && EF.Functions.ILike(a.ActorName, pattern)) ||
                (a.Field != null && EF.Functions.ILike(a.Field, pattern)) ||
                (a.PreviousValue != null && EF.Functions.ILike(a.PreviousValue, pattern)) ||
                (a.NewValue != null && EF.Functions.ILike(a.NewValue, pattern)) ||
                (a.Details != null && EF.Functions.ILike(a.Details, pattern)));
        }

        var totalCount = await query.CountAsync();

        var entries = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = entries.Select(a => new AuditLogEntryDto(
            a.Id.ToString(),
            a.UserId?.ToString(),
            a.UserName,
            a.ActorUserId?.ToString(),
            a.ActorName,
            a.Action,
            a.Field,
            a.PreviousValue,
            a.NewValue,
            a.Details,
            a.CreatedAt
        )).ToList();

        return Ok(new PagedAuditLogDto(items, totalCount, page, pageSize));
    }
}
