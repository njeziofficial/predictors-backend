using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Admin;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// Points users earned before the app existed. Any admin can view them; importing and deleting
// are the system user's call only, since they move the overall leaderboard directly.
[ApiController]
[Route("api/admin/previous-points")]
[Authorize(Roles = "Admin")]
public class AdminPreviousPointsController(AppDbContext db) : ControllerBase
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var entries = await db.PreviousPoints
            .Include(pp => pp.User)
            .OrderByDescending(pp => pp.Points)
            .ThenBy(pp => pp.User.Name)
            .ToListAsync();

        return Ok(entries.Select(pp => new PreviousPointsEntryDto(
            pp.Id.ToString(), pp.UserId.ToString(), pp.User.Name, pp.User.Email,
            pp.Points, pp.Label, pp.CreatedAt, pp.UpdatedAt)));
    }

    // All-or-nothing: if any row has an error, nothing is saved (even when DryRun is false), so a
    // half-imported file can't leave the leaderboard in a mixed state. Re-importing the same label
    // replaces each user's points for that label rather than adding to them, so it's safe to re-run
    // a corrected file.
    [HttpPost("import")]
    public async Task<IActionResult> Import(PreviousPointsImportRequest request)
    {
        var actor = await GetCurrentAdminAsync();
        if (actor?.IsSystemUser != true)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Only the system user can import previous points." });

        var label = request.Label.Trim();
        if (label.Length == 0)
            return BadRequest(new { message = "A label is required." });

        var emails = request.Rows
            .Select(r => (r.Email ?? "").Trim().ToLower())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();

        var usersByEmail = await db.Users
            .Where(u => emails.Contains(u.Email))
            .ToDictionaryAsync(u => u.Email);

        var userIds = usersByEmail.Values.Select(u => u.Id).ToList();
        var existingByUser = await db.PreviousPoints
            .Where(pp => pp.Label == label && userIds.Contains(pp.UserId))
            .ToDictionaryAsync(pp => pp.UserId);

        var results = new List<PreviousPointsImportRowResult>();
        var seen = new HashSet<string>();

        for (var i = 0; i < request.Rows.Count; i++)
        {
            var row = request.Rows[i];
            var email = (row.Email ?? "").Trim().ToLower();
            var name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim();

            PreviousPointsImportRowResult Result(string action, int? current = null, string? error = null) =>
                new(i + 1, email, name, row.Points, action, current, error);

            if (email.Length == 0 || !EmailValidator.IsValid(email))
            {
                results.Add(Result("error", error: "Invalid email address."));
                continue;
            }
            if (!seen.Add(email))
            {
                results.Add(Result("error", error: "This email appears more than once in the file."));
                continue;
            }

            if (usersByEmail.TryGetValue(email, out var user))
            {
                // Show the account's actual name, not whatever the file had.
                name = user.Name;
                results.Add(existingByUser.TryGetValue(user.Id, out var existing)
                    ? Result(existing.Points == row.Points ? "unchanged" : "update", existing.Points)
                    : Result("add"));
            }
            else if (name is null || name.Length < 2)
            {
                results.Add(Result("error", error: "No account with this email. Add a name (2+ characters) to create one."));
            }
            else
            {
                results.Add(Result("create_user"));
            }
        }

        var errorCount = results.Count(r => r.Action == "error");
        if (request.DryRun || errorCount > 0)
            return Ok(new PreviousPointsImportResult(request.DryRun, false, errorCount, results, []));

        var now = DateTime.UtcNow;
        var created = new List<CreatedAccountDto>();

        for (var i = 0; i < request.Rows.Count; i++)
        {
            var row = request.Rows[i];
            var result = results[i];
            if (result.Action == "unchanged") continue;

            if (result.Action == "create_user")
            {
                var password = AdminUsersController.GenerateTemporaryPassword();
                var newUser = new User
                {
                    Id = Guid.NewGuid(),
                    Name = result.Name!,
                    Email = result.Email,
                    PhoneNumber = string.IsNullOrWhiteSpace(row.PhoneNumber) ? null : row.PhoneNumber.Trim(),
                    WhatsAppName = string.IsNullOrWhiteSpace(row.WhatsAppName) ? null : row.WhatsAppName.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    Role = UserRole.User,
                    MustResetPassword = true,
                    CreatedAt = now
                };
                db.Users.Add(newUser);
                usersByEmail[newUser.Email] = newUser;
                created.Add(new CreatedAccountDto(newUser.Name, newUser.Email, password));
                AuditLogger.Log(db, subject: newUser, actor: actor, action: "UserCreated",
                    details: $"Email: {newUser.Email}, Role: User (previous points import)");
            }

            var user = usersByEmail[result.Email];
            if (existingByUser.TryGetValue(user.Id, out var existing))
            {
                existing.Points = row.Points;
                existing.UpdatedAt = now;
            }
            else
            {
                db.PreviousPoints.Add(new PreviousPoints
                {
                    UserId = user.Id,
                    Points = row.Points,
                    Label = label,
                    CreatedByUserId = actor.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            AuditLogger.Log(db, subject: user, actor: actor, action: "PreviousPointsImported",
                field: "Previous Points", previousValue: result.CurrentPoints?.ToString() ?? "—",
                newValue: row.Points.ToString(), details: $"Label: {label}");
        }

        await db.SaveChangesAsync();
        return Ok(new PreviousPointsImportResult(false, true, 0, results, created));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var actor = await GetCurrentAdminAsync();
        if (actor?.IsSystemUser != true)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Only the system user can remove previous points." });

        var entry = await db.PreviousPoints.Include(pp => pp.User).FirstOrDefaultAsync(pp => pp.Id == id);
        if (entry is null) return NotFound(new { message = "Entry not found." });

        AuditLogger.Log(db, subject: entry.User, actor: actor, action: "PreviousPointsRemoved",
            field: "Previous Points", previousValue: entry.Points.ToString(), newValue: "—",
            details: $"Label: {entry.Label}");

        db.PreviousPoints.Remove(entry);
        await db.SaveChangesAsync();
        return Ok(new { message = "Previous points removed." });
    }

    private Task<User?> GetCurrentAdminAsync()
    {
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
    }
}
