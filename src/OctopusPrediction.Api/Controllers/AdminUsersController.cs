using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Admin")]
public class AdminUsersController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
        return Ok(users.Select(ToDto));
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> SetRole(Guid id, UpdateUserRoleRequest request)
    {
        if (!Enum.TryParse<UserRole>(request.Role, true, out var role) || !Enum.IsDefined(role))
            return BadRequest(new { message = $"Invalid role: {request.Role}" });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound(new { message = "User not found." });

        if (user.IsSystemUser)
            return BadRequest(new { message = "The system user's role cannot be changed." });

        var actor = await GetCurrentAdminAsync();

        // Demoting an admin is the system user's call only — this also covers a regular admin
        // trying to demote themselves, since they're an admin target and aren't the system user.
        if (user.Role == UserRole.Admin && role != UserRole.Admin && actor?.IsSystemUser != true)
            return BadRequest(new { message = "Only the system user can demote another admin." });

        var previousRole = user.Role;
        user.Role = role;

        if (previousRole != role)
        {
            AuditLogger.Log(db, subject: user, actor: actor, action: "RoleChanged",
                field: "Role", previousValue: previousRole.ToString(), newValue: role.ToString());
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(Guid id, UpdateUserStatusRequest request)
    {
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (id == currentUserId && request.IsDisabled)
            return BadRequest(new { message = "You cannot disable your own account." });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound(new { message = "User not found." });

        if (user.IsSystemUser && request.IsDisabled)
            return BadRequest(new { message = "The system user can never be disabled." });

        var previousStatus = user.IsDisabled ? "Disabled" : "Enabled";
        var newStatus = request.IsDisabled ? "Disabled" : "Enabled";
        var changed = user.IsDisabled != request.IsDisabled;
        user.IsDisabled = request.IsDisabled;

        if (changed)
        {
            var actor = await GetCurrentAdminAsync();
            AuditLogger.Log(db, subject: user, actor: actor, action: "StatusChanged",
                field: "Status", previousValue: previousStatus, newValue: newStatus);
        }

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound(new { message = "User not found." });

        if (user.IsSystemUser)
            return BadRequest(new { message = "The system user's password cannot be reset this way." });

        var actor = await GetCurrentAdminAsync();

        // Admin-on-admin password resets are the system user's call only — this also covers a
        // regular admin trying to reset their own password through this endpoint, since they
        // themselves are an admin target and aren't the system user.
        if (user.Role == UserRole.Admin && actor?.IsSystemUser != true)
            return BadRequest(new { message = "Only the system user can reset another admin's password." });

        var temporaryPassword = GenerateTemporaryPassword();
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporaryPassword);
        // Only a User target can ever clear this themselves (via /api/users/me/password) — an
        // admin target can't, since admins are now blocked from changing their own password at
        // all, so forcing this on them would strand them at the reset screen with no way off it.
        // For an admin, this temp password just becomes their real password outright.
        user.MustResetPassword = user.Role != UserRole.Admin;

        AuditLogger.Log(db, subject: user, actor: actor, action: "PasswordReset");

        await db.SaveChangesAsync();
        return Ok(new { temporaryPassword });
    }

    // Mixed-case letters + digits, no ambiguous-looking characters (0/O, 1/l/I) since this
    // gets read aloud or typed by a human off a screen when the admin shares it.
    private const string PasswordChars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

    private static string GenerateTemporaryPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = PasswordChars[bytes[i] % PasswordChars.Length];
        return new string(chars);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound(new { message = "User not found." });

        if (user.IsSystemUser)
            return BadRequest(new { message = "The system user can never be deleted." });

        var actor = await GetCurrentAdminAsync();

        // Deleting an admin is the system user's call only — this also covers a regular admin
        // trying to delete themselves, since they're an admin target and aren't the system user.
        if (user.Role == UserRole.Admin && actor?.IsSystemUser != true)
            return BadRequest(new { message = "Only the system user can delete another admin." });

        AuditLogger.Log(db, subject: user, actor: actor, action: "UserDeleted", details: $"Email: {user.Email}");

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return Ok(new { message = "User deleted." });
    }

    private Task<User?> GetCurrentAdminAsync()
    {
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
    }

    private static UserSummaryDto ToDto(User u) => new(
        u.Id.ToString(), u.Name, u.Email, u.PhoneNumber, u.WhatsAppName,
        u.Role.ToString().ToLowerInvariant(), u.IsDisabled, u.IsSystemUser, u.CreatedAt, u.LastLoginAt
    );
}
