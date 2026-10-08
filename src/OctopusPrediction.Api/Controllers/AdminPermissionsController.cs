using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

// Who may do what in the back office. Reading your own permissions is open to every admin;
// everything else here is the system user's.
[ApiController]
[Route("api/admin/permissions")]
[Authorize(Roles = "Admin")]
public class AdminPermissionsController(AppDbContext db, AppCache cache) : ControllerBase
{
    // Every back-office page asks this, so it comes from the access cache.
    [HttpGet("me")]
    public async Task<IActionResult> GetMine()
    {
        var me = await cache.UserAccessAsync(db, Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
        if (me is null) return Unauthorized();
        return Ok(new MyPermissionsDto(me.IsSystemUser, me.Permissions));
    }

    [HttpGet]
    [SystemUserOnly]
    public async Task<IActionResult> GetAll() => Ok(await OverviewAsync());

    // The admin-level defaults: what every admin may do unless they have an exception.
    [HttpPut("defaults")]
    [SystemUserOnly]
    public async Task<IActionResult> UpdateDefaults(UpdatePermissionDefaultsRequest request)
    {
        var unknown = request.Permissions.Keys.Where(k => !Permissions.IsKnown(k)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { message = $"Unknown permission: {string.Join(", ", unknown)}" });

        var actor = await CurrentUserAsync();
        var rows = await db.RolePermissions.ToDictionaryAsync(r => r.Permission);
        foreach (var (key, allowed) in request.Permissions)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                row = new RolePermission { Permission = key, Allowed = !allowed };
                db.RolePermissions.Add(row);
            }
            if (row.Allowed == allowed) continue;

            AuditLogger.Log(db, subject: null, actor: actor, action: "PermissionDefaultChanged",
                field: Permissions.Label(key), previousValue: Describe(row.Allowed), newValue: Describe(allowed),
                details: "All admins");
            row.Allowed = allowed;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedByUserId = actor?.Id;
        }
        await db.SaveChangesAsync();
        return Ok(await OverviewAsync());
    }

    // One admin's exceptions to the defaults.
    [HttpPut("users/{id}")]
    [SystemUserOnly]
    public async Task<IActionResult> UpdateAdmin(Guid id, UpdateAdminPermissionsRequest request)
    {
        var unknown = request.Overrides.Keys.Where(k => !Permissions.IsKnown(k)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { message = $"Unknown permission: {string.Join(", ", unknown)}" });

        var target = await db.Users.FindAsync(id);
        if (target is null) return NotFound(new { message = "User not found." });
        if (target.IsSystemUser)
            return BadRequest(new { message = "The system admin always has every permission." });
        if (target.Role != UserRole.Admin)
            return BadRequest(new { message = "Only admins have back-office permissions. Make them an admin first." });

        var actor = await CurrentUserAsync();
        var rows = await db.UserPermissions.Where(p => p.UserId == id).ToDictionaryAsync(p => p.Permission);
        foreach (var (key, allowed) in request.Overrides)
        {
            rows.TryGetValue(key, out var row);
            var previous = row?.Allowed;
            if (previous == allowed) continue;

            if (allowed is null) db.UserPermissions.Remove(row!);
            else if (row is null)
                db.UserPermissions.Add(new UserPermission
                {
                    UserId = id, Permission = key, Allowed = allowed.Value, UpdatedByUserId = actor?.Id
                });
            else
            {
                row.Allowed = allowed.Value;
                row.UpdatedAt = DateTime.UtcNow;
                row.UpdatedByUserId = actor?.Id;
            }

            AuditLogger.Log(db, subject: target, actor: actor, action: "PermissionChanged",
                field: Permissions.Label(key), previousValue: Describe(previous), newValue: Describe(allowed));
        }
        await db.SaveChangesAsync();
        return Ok(await OverviewAsync());
    }

    private async Task<PermissionsOverviewDto> OverviewAsync()
    {
        var defaults = await db.RolePermissions.ToDictionaryAsync(r => r.Permission, r => r.Allowed);
        var admins = await db.Users
            .Where(u => u.Role == UserRole.Admin && !u.IsSystemUser)
            .OrderBy(u => u.Name)
            .ToListAsync();
        var overrides = (await db.UserPermissions.ToListAsync())
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Permission, p => p.Allowed));

        var result = new List<AdminPermissionsDto>();
        foreach (var a in admins)
            result.Add(new AdminPermissionsDto(
                a.Id.ToString(), a.Name, a.Email, a.WhatsAppName, a.IsDisabled,
                overrides.GetValueOrDefault(a.Id) ?? [],
                await Permissions.EffectiveAsync(db, a)));

        return new PermissionsOverviewDto(
            Permissions.All.Select(p => new PermissionDefinitionDto(p.Key, p.Group, p.Label, p.Description)).ToList(),
            Permissions.All.ToDictionary(p => p.Key, p => defaults.GetValueOrDefault(p.Key, p.DefaultAllowed)),
            result);
    }

    private static string Describe(bool? allowed) => allowed switch
    {
        true => "Allowed",
        false => "Not allowed",
        null => "Default",
    };

    private Task<User?> CurrentUserAsync()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}
