using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Users;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db) : ControllerBase
{
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return NotFound();
        return Ok(ToDto(user));
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe(UpdateProfileRequest request)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return NotFound();

        if (user.WhatsAppName != request.WhatsAppName
            && await WhatsAppNames.IsTakenAsync(db, request.WhatsAppName, user.Id))
            return Conflict(new { message = WhatsAppNames.TakenMessage });

        if (user.Name != request.Name)
            AuditLogger.Log(db, subject: user, actor: user, action: "ProfileUpdated",
                field: "Name", previousValue: user.Name, newValue: request.Name);
        if (user.PhoneNumber != request.PhoneNumber)
            AuditLogger.Log(db, subject: user, actor: user, action: "ProfileUpdated",
                field: "Phone Number", previousValue: user.PhoneNumber ?? "—", newValue: request.PhoneNumber);
        if (user.WhatsAppName != request.WhatsAppName)
            AuditLogger.Log(db, subject: user, actor: user, action: "ProfileUpdated",
                field: "WhatsApp Name", previousValue: user.WhatsAppName ?? "—", newValue: request.WhatsAppName);

        user.Name = request.Name;
        user.PhoneNumber = request.PhoneNumber;
        user.WhatsAppName = request.WhatsAppName;

        await db.SaveChangesAsync();
        return Ok(ToDto(user));
    }

    // Points carried over from before the app (see PreviousPoints). Usually one row per user.
    [HttpGet("me/previous-points")]
    public async Task<IActionResult> GetMyPreviousPoints()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var entries = await db.PreviousPoints
            .Where(pp => pp.UserId == id)
            .OrderBy(pp => pp.CreatedAt)
            .Select(pp => new PreviousPointsDto(pp.Label, pp.Points, pp.CorrectScores, pp.UpdatedAt))
            .ToListAsync();
        return Ok(entries);
    }

    [HttpPut("me/password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return NotFound();

        if (user.IsSystemUser)
            return BadRequest(new { message = "The system user's password can never be changed." });

        if (user.Role == UserRole.Admin)
            return BadRequest(new { message = "Admins cannot change their own password — only the system user can reset it." });

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.MustResetPassword = false;
        AuditLogger.Log(db, subject: user, actor: user, action: "PasswordChanged");
        // Sign out every other device, but keep the session making this request alive.
        Guid? currentSession = Guid.TryParse(User.FindFirstValue(AuthService.SessionIdClaim), out var sid) ? sid : null;
        await SessionRevoker.RevokeAllAsync(db, user.Id, "password_changed", exceptFamilyId: currentSession);
        await db.SaveChangesAsync();

        return Ok(new { message = "Password updated." });
    }

    private Task<User?> GetCurrentUserAsync()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }

    private static UserProfileDto ToDto(User u) => new(
        u.Id.ToString(), u.Name, u.Email, u.PhoneNumber, u.WhatsAppName,
        u.Role.ToString().ToLowerInvariant(), u.CreatedAt, u.LastLoginAt
    );
}
