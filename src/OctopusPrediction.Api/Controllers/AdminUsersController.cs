using System.ComponentModel.DataAnnotations;
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
    private static readonly EmailAddressAttribute EmailValidator = new();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
        return Ok(users.Select(ToDto));
    }

    // Creating accounts (of either role) is the system user's call only.
    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        var actor = await GetCurrentAdminAsync();
        if (actor?.IsSystemUser != true)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Only the system user can create users." });

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role) || !Enum.IsDefined(role))
            return BadRequest(new { message = $"Invalid role: {request.Role}" });

        var email = request.Email.Trim().ToLower();
        if (await db.Users.AnyAsync(u => u.Email == email))
            return Conflict(new { message = "A user with this email already exists." });

        var generated = string.IsNullOrWhiteSpace(request.Password);
        var password = generated ? GenerateTemporaryPassword() : request.Password!;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Email = email,
            PhoneNumber = request.PhoneNumber,
            WhatsAppName = request.WhatsAppName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            // Same rule as ResetPassword: admins can't change their own password, so only a
            // User is forced through the reset screen on first login.
            MustResetPassword = role != UserRole.Admin,
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        AuditLogger.Log(db, subject: user, actor: actor, action: "UserCreated",
            details: $"Email: {user.Email}, Role: {role}");
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), null, new
        {
            user = ToDto(user),
            temporaryPassword = generated ? password : null
        });
    }

    // Editing someone's details is the system user's call only. Only the fields sent are changed.
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDetails(Guid id, UpdateUserDetailsRequest request)
    {
        var actor = await GetCurrentAdminAsync();
        if (actor?.IsSystemUser != true)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Only the system user can edit user details." });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound(new { message = "User not found." });

        var name = request.Name?.Trim();
        if (name is not null && name.Length < 2)
            return BadRequest(new { message = "Name must be at least 2 characters." });
        if (name?.Length > 200)
            return BadRequest(new { message = "Name must be at most 200 characters." });

        var email = request.Email?.Trim().ToLower();
        if (email is not null && email != user.Email)
        {
            if (!EmailValidator.IsValid(email) || email.Length > 256)
                return BadRequest(new { message = "Invalid email address." });
            // DbSeeder finds the system user by its configured email, so changing it would get a
            // second system user seeded on the next restart. Change SeedAdmin:Email instead.
            if (user.IsSystemUser)
                return BadRequest(new { message = "The system user's email can only be changed in the server configuration." });
            if (await db.Users.AnyAsync(u => u.Email == email && u.Id != id))
                return Conflict(new { message = "Another user already has this email." });
        }

        // Empty means "clear it"; null means "leave it".
        string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var phone = request.PhoneNumber is null ? user.PhoneNumber : Optional(request.PhoneNumber);
        var whatsApp = request.WhatsAppName is null ? user.WhatsAppName : Optional(request.WhatsAppName);
        if (phone?.Length > 30)
            return BadRequest(new { message = "Phone number must be at most 30 characters." });
        if (whatsApp?.Length > 200)
            return BadRequest(new { message = "WhatsApp name must be at most 200 characters." });

        void Change(string field, string? previous, string? next, Action apply)
        {
            if (previous == next) return;
            AuditLogger.Log(db, subject: user, actor: actor, action: "UserDetailsUpdated",
                field: field, previousValue: previous ?? "—", newValue: next ?? "—");
            apply();
        }

        Change("Name", user.Name, name ?? user.Name, () => user.Name = name!);
        Change("Email", user.Email, email ?? user.Email, () => user.Email = email!);
        Change("Phone Number", user.PhoneNumber, phone, () => user.PhoneNumber = phone);
        Change("WhatsApp Name", user.WhatsAppName, whatsApp, () => user.WhatsAppName = whatsApp);

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
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

        // DisabledUserMiddleware already blocks their requests; this also ends their sessions so
        // re-enabling them later doesn't silently revive an old login.
        if (request.IsDisabled)
            await SessionRevoker.RevokeAllAsync(db, user.Id, "disabled");

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
        // Whoever was logged in with the old password gets signed out everywhere.
        await SessionRevoker.RevokeAllAsync(db, user.Id, "password_reset");

        await db.SaveChangesAsync();
        return Ok(new { temporaryPassword });
    }

    // Mixed-case letters + digits, no ambiguous-looking characters (0/O, 1/l/I) since this
    // gets read aloud or typed by a human off a screen when the admin shares it.
    private const string PasswordChars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

    internal static string GenerateTemporaryPassword()
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
