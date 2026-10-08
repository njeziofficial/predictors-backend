using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Services;

public record PermissionInfo(string Key, string Group, string Label, string Description, bool DefaultAllowed);

// Back-office actions an admin can be allowed or refused. The system user always has all of
// them, plus the powers that are never delegated: changing roles, managing permissions, acting
// on other admin accounts, switching the audit log off and the seed/clear-data endpoints.
//
// Defaults reproduce what ordinary admins could do before permissions existed; they apply only
// when a key is first added (see SeedDefaultsAsync), after which the system user's choice sticks.
public static class Permissions
{
    public const string FixturesManage = "fixtures.manage";
    public const string ParticipationView = "participation.view";
    public const string UsersView = "users.view";
    public const string UsersCreate = "users.create";
    public const string UsersEdit = "users.edit";
    public const string UsersStatus = "users.status";
    public const string UsersResetPassword = "users.reset_password";
    public const string UsersDelete = "users.delete";
    public const string PreviousPointsView = "previous_points.view";
    public const string PreviousPointsManage = "previous_points.manage";
    public const string AuditView = "audit.view";
    public const string SettingsView = "settings.view";
    public const string SettingsManage = "settings.manage";
    public const string MessagesBroadcast = "messages.broadcast";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(FixturesManage, "Fixtures", "Manage fixtures",
            "Create, edit and delete match weeks and fixtures, and correct scores.", true),
        new(ParticipationView, "Players", "Prediction check",
            "See who has or hasn't predicted a week, with their phone numbers.", true),
        new(UsersView, "Players", "View players",
            "See the player list with emails and phone numbers.", true),
        new(UsersCreate, "Players", "Create players",
            "Add player accounts and hand out their temporary passwords.", false),
        new(UsersEdit, "Players", "Edit player details",
            "Change a player's name, email, phone number or WhatsApp name.", false),
        new(UsersStatus, "Players", "Enable or disable players",
            "Block a player from signing in, or let them back in.", true),
        new(UsersResetPassword, "Players", "Reset player passwords",
            "Give a player a new temporary password.", true),
        new(UsersDelete, "Players", "Delete players",
            "Permanently remove a player and their predictions.", true),
        new(PreviousPointsView, "Previous points", "View previous points",
            "See points carried over from before the app.", true),
        new(PreviousPointsManage, "Previous points", "Import previous points",
            "Import league tables and remove previous-points entries.", false),
        new(AuditView, "Audit", "View audit trail",
            "Read the log of logins and changes.", true),
        new(SettingsView, "Settings", "View settings",
            "See scraper, reminder, lock and registration settings.", true),
        new(SettingsManage, "Settings", "Change settings",
            "Change the scraper and reminders, lock predictions and open or close registration.", true),
        new(MessagesBroadcast, "Messages", "Send announcements",
            "Message every player, or a chosen group, at once. Players can reply in their chat with you.", true),
    ];

    public static bool IsKnown(string key) => All.Any(p => p.Key == key);

    // Inserts any permission keys the table doesn't have yet with their default; existing
    // rows (the system user's choices) are left alone.
    public static async Task SeedDefaultsAsync(AppDbContext db)
    {
        var existing = await db.RolePermissions.Select(r => r.Permission).ToListAsync();
        foreach (var p in All.Where(p => !existing.Contains(p.Key)))
            db.RolePermissions.Add(new RolePermission { Permission = p.Key, Allowed = p.DefaultAllowed });
        await db.SaveChangesAsync();
    }

    // Every permission's effective value for this user: all on for the system user, all off
    // for non-admins, otherwise their own override or else the role default.
    public static async Task<Dictionary<string, bool>> EffectiveAsync(AppDbContext db, User user)
    {
        if (user.IsSystemUser) return All.ToDictionary(p => p.Key, _ => true);
        if (user.Role != UserRole.Admin) return All.ToDictionary(p => p.Key, _ => false);

        var defaults = await db.RolePermissions.ToDictionaryAsync(r => r.Permission, r => r.Allowed);
        var overrides = await db.UserPermissions
            .Where(u => u.UserId == user.Id)
            .ToDictionaryAsync(u => u.Permission, u => u.Allowed);
        return All.ToDictionary(
            p => p.Key,
            p => overrides.TryGetValue(p.Key, out var o) ? o
                : defaults.TryGetValue(p.Key, out var d) ? d
                : p.DefaultAllowed);
    }

    public static async Task<bool> HasAsync(AppDbContext db, User user, string permission) =>
        (await EffectiveAsync(db, user)).GetValueOrDefault(permission);

    public static string Label(string key) => All.FirstOrDefault(p => p.Key == key)?.Label ?? key;

    // The signed-in user's role and permissions, from the access cache (emptied whenever a role,
    // permission default or exception changes, so a revoked permission takes effect at once).
    internal static async Task<UserAccess?> CurrentAccessAsync(HttpContext http)
    {
        var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(id, out var userId)) return null;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var cache = http.RequestServices.GetRequiredService<AppCache>();
        return await cache.UserAccessAsync(db, userId);
    }

    internal static IActionResult Denied(string message) =>
        new ObjectResult(new { code = "permission_denied", message }) { StatusCode = StatusCodes.Status403Forbidden };
}

// Lets the request through only when the signed-in admin has the permission (the system user
// always does). Goes on admin endpoints alongside [Authorize(Roles = "Admin")]. An authorization
// filter, so it answers before the request body is even validated. Signed-out requests are left
// to [Authorize], which answers 401.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class RequirePermissionAttribute(string permission) : Attribute, IAsyncAuthorizationFilter
{
    public string Permission { get; } = permission;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) return;
        var access = await Permissions.CurrentAccessAsync(context.HttpContext);
        if (access?.Can(Permission) != true)
            context.Result = Permissions.Denied(
                $"You don't have permission to {Permissions.Label(Permission).ToLowerInvariant()}. Ask the system admin for access.");
    }
}

// For the powers that are never delegated (see Permissions).
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class SystemUserOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) return;
        var access = await Permissions.CurrentAccessAsync(context.HttpContext);
        if (access?.IsSystemUser != true)
            context.Result = Permissions.Denied("Only the system admin can do this.");
    }
}
