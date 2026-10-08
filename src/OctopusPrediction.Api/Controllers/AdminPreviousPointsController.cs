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

    // A prefix match ("Anony" → "Anonymous") needs at least this many characters, so short
    // names can't land on the wrong account.
    private const int MinPrefixLength = 4;

    [HttpGet]
    [RequirePermission(Permissions.PreviousPointsView)]
    public async Task<IActionResult> GetAll()
    {
        var entries = await db.PreviousPoints
            .Include(pp => pp.User)
            .OrderByDescending(pp => pp.Points)
            .ThenByDescending(pp => pp.CorrectScores)
            .ThenBy(pp => pp.User.Name)
            .ToListAsync();

        return Ok(entries.Select(pp => new PreviousPointsEntryDto(
            pp.Id.ToString(), pp.UserId.ToString(), pp.User.Name, pp.User.Email,
            pp.Points, pp.CorrectScores, pp.Label, pp.CreatedAt, pp.UpdatedAt)));
    }

    // All-or-nothing: if any row has an error, nothing is saved (even when DryRun is false), so a
    // half-imported file can't leave the leaderboard in a mixed state. Re-importing the same label
    // replaces each user's points for that label rather than adding to them, so it's safe to re-run
    // a corrected file — e.g. each week's league table under one label.
    [HttpPost("import")]
    [RequirePermission(Permissions.PreviousPointsManage)]
    public async Task<IActionResult> Import(PreviousPointsImportRequest request)
    {
        var actor = await GetCurrentAdminAsync();
        // Rows with an unknown email create accounts, which is a separate permission.
        var canCreateUsers = await Permissions.HasAsync(db, actor!, Permissions.UsersCreate);

        var label = request.Label.Trim();
        if (label.Length == 0)
            return BadRequest(new { message = "A label is required." });

        // League tables are small; loading everyone keeps WhatsApp-name matching simple.
        var users = await db.Users.ToListAsync();
        var usersByEmail = users.ToDictionary(u => u.Email);
        var aliases = await db.PlayerAliases.ToDictionaryAsync(a => a.Key);

        var existingByUser = await db.PreviousPoints
            .Where(pp => pp.Label == label)
            .ToDictionaryAsync(pp => pp.UserId);

        var results = new List<PreviousPointsImportRowResult>();
        var matchedUsers = new List<User?>();
        var seenEmails = new HashSet<string>();
        var seenUsers = new HashSet<Guid>();
        var newWhatsAppKeys = new HashSet<string>();

        for (var i = 0; i < request.Rows.Count; i++)
        {
            var row = request.Rows[i];
            var email = (row.Email ?? "").Trim().ToLower();
            var name = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name.Trim();
            var whatsApp = string.IsNullOrWhiteSpace(row.WhatsAppName) ? null : row.WhatsAppName.Trim();
            User? user = null;
            string? matchedBy = null;

            PreviousPointsImportRowResult Result(string action, string? error = null)
            {
                existingByUser.TryGetValue(user?.Id ?? Guid.Empty, out var existing);
                return new(i + 1, user?.Email ?? email, user?.Name ?? name, whatsApp, row.Points,
                    row.CorrectScores ?? existing?.CorrectScores ?? 0, action,
                    existing?.Points, existing?.CorrectScores, matchedBy, error);
            }

            void Add(PreviousPointsImportRowResult result)
            {
                results.Add(result);
                matchedUsers.Add(result.Action is "error" or "skipped" ? null : user);
            }

            if (row.CorrectScores < 0)
            {
                Add(Result("error", "Correct scores can't be negative."));
                continue;
            }

            if (email.Length > 0)
            {
                if (!EmailValidator.IsValid(email))
                {
                    Add(Result("error", "Invalid email address."));
                    continue;
                }
                if (!seenEmails.Add(email))
                {
                    Add(Result("error", "This email appears more than once in the file."));
                    continue;
                }
                if (usersByEmail.TryGetValue(email, out user)) matchedBy = "email";
            }
            else if (whatsApp is not null)
            {
                var (match, by, error) = MatchByName(whatsApp, users, aliases);
                if (error is not null)
                {
                    Add(Result("error", error));
                    continue;
                }
                if (match is null)
                {
                    Add(Result("skipped", "No account with this WhatsApp name — add an email column to create one."));
                    continue;
                }
                (user, matchedBy) = (match, by);
            }
            else
            {
                Add(Result("error", "Each row needs an email or a WhatsApp name."));
                continue;
            }

            if (user is not null)
            {
                if (!seenUsers.Add(user.Id))
                {
                    Add(Result("error", $"{user.Name} is matched by more than one row in the file."));
                    continue;
                }
                Add(existingByUser.TryGetValue(user.Id, out var existing)
                    ? Result(existing.Points == row.Points
                             && existing.CorrectScores == (row.CorrectScores ?? existing.CorrectScores)
                        ? "unchanged" : "update")
                    : Result("add"));
            }
            else if (name is null || name.Length < 2)
            {
                Add(Result("error", "No account with this email. Add a name (2+ characters) to create one."));
            }
            else if (whatsApp is not null
                     && (users.Any(u => WhatsAppNames.Key(u.WhatsAppName) == WhatsAppNames.Key(whatsApp))
                         || !newWhatsAppKeys.Add(WhatsAppNames.Key(whatsApp))))
            {
                // WhatsApp names sign people in, so a new account can't reuse one.
                Add(Result("error", WhatsAppNames.TakenMessage));
            }
            else if (!canCreateUsers)
            {
                Add(Result("error", "No account with this email, and you don't have permission to create players."));
            }
            else
            {
                Add(Result("create_user"));
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
            var user = matchedUsers[i];

            // A row with both an email and a WhatsApp name links that name to the account, so
            // next time the name alone finds it (e.g. "FiskySo" → Big Fisky's email).
            if (result.MatchedBy == "email" && result.WhatsAppName is not null)
                RememberAlias(result.WhatsAppName, user!, aliases, now);

            if (result.Action is "unchanged" or "skipped") continue;

            if (result.Action == "create_user")
            {
                var password = AdminUsersController.GenerateTemporaryPassword();
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Name = result.Name!,
                    Email = result.Email,
                    PhoneNumber = string.IsNullOrWhiteSpace(row.PhoneNumber) ? null : row.PhoneNumber.Trim(),
                    WhatsAppName = result.WhatsAppName,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                    Role = UserRole.User,
                    MustResetPassword = true,
                    CreatedAt = now
                };
                db.Users.Add(user);
                created.Add(new CreatedAccountDto(user.Name, user.Email, password));
                AuditLogger.Log(db, subject: user, actor: actor, action: "UserCreated",
                    details: $"Email: {user.Email}, Role: User (previous points import)");
            }

            if (existingByUser.TryGetValue(user!.Id, out var existing))
            {
                existing.Points = row.Points;
                existing.CorrectScores = result.CorrectScores;
                existing.UpdatedAt = now;
            }
            else
            {
                db.PreviousPoints.Add(new PreviousPoints
                {
                    UserId = user.Id,
                    Points = row.Points,
                    CorrectScores = result.CorrectScores,
                    Label = label,
                    CreatedByUserId = actor.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            AuditLogger.Log(db, subject: user, actor: actor, action: "PreviousPointsImported",
                field: "Previous Points",
                previousValue: result.CurrentPoints is null ? "—" : $"{result.CurrentPoints} ({result.CurrentCorrectScores} CS)",
                newValue: $"{row.Points} ({result.CorrectScores} CS)", details: $"Label: {label}");
        }

        await db.SaveChangesAsync();
        return Ok(new PreviousPointsImportResult(false, true, 0, results, created));
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permissions.PreviousPointsManage)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var actor = await GetCurrentAdminAsync();

        var entry = await db.PreviousPoints.Include(pp => pp.User).FirstOrDefaultAsync(pp => pp.Id == id);
        if (entry is null) return NotFound(new { message = "Entry not found." });

        AuditLogger.Log(db, subject: entry.User, actor: actor, action: "PreviousPointsRemoved",
            field: "Previous Points", previousValue: entry.Points.ToString(), newValue: "—",
            details: $"Label: {entry.Label}");

        db.PreviousPoints.Remove(entry);
        await db.SaveChangesAsync();
        return Ok(new { message = "Previous points removed." });
    }

    // Finds the account a league-table name refers to. Names are compared ignoring case, spaces,
    // apostrophes and other punctuation ("God’s O" = "Gods O"). Tries, in order, stopping at the
    // first step with any match: a name an admin linked earlier (PlayerAlias), exact WhatsApp
    // name, exact account name, then a unique prefix either way round on either name
    // ("Anony" ↔ "Anonymous"). More than one match at a step is an error rather than a guess; no
    // match at all returns (null, null, null).
    private static (User? User, string? MatchedBy, string? Error) MatchByName(
        string name, List<User> users, Dictionary<string, PlayerAlias> aliases)
    {
        var key = NormalizeName(name);
        if (key.Length == 0) return (null, null, "WhatsApp name has no letters or digits.");

        if (aliases.TryGetValue(key, out var alias))
        {
            var linked = users.FirstOrDefault(u => u.Id == alias.UserId);
            if (linked is not null) return (linked, "alias", null);
        }

        var steps = new (string By, Func<User, bool> Matches)[]
        {
            ("whatsapp", u => NormalizeName(u.WhatsAppName) == key),
            ("name", u => NormalizeName(u.Name) == key),
            ("whatsapp", u => IsPrefixMatch(key, NormalizeName(u.WhatsAppName))),
            ("name", u => IsPrefixMatch(key, NormalizeName(u.Name))),
        };

        foreach (var (by, matches) in steps)
        {
            var found = users.Where(matches).ToList();
            if (found.Count == 1) return (found[0], by, null);
            if (found.Count > 1)
                return (null, null,
                    $"\"{name}\" matches several accounts ({string.Join(", ", found.Select(u => u.Name))}) — use an email.");
        }
        return (null, null, null);
    }

    // Saves (or repoints) name → user unless the name already finds that user on its own.
    private void RememberAlias(string name, User user, Dictionary<string, PlayerAlias> aliases, DateTime now)
    {
        var key = NormalizeName(name);
        if (key.Length == 0 || key == NormalizeName(user.WhatsAppName) || key == NormalizeName(user.Name)) return;

        if (aliases.TryGetValue(key, out var existing))
        {
            if (existing.UserId == user.Id) return;
            existing.UserId = user.Id;
            existing.Alias = name;
        }
        else
        {
            var alias = new PlayerAlias { Key = key, Alias = name, UserId = user.Id, CreatedAt = now };
            db.PlayerAliases.Add(alias);
            aliases[key] = alias;
        }
    }

    private static bool IsPrefixMatch(string a, string b) =>
        a.Length > 0 && b.Length > 0 && Math.Min(a.Length, b.Length) >= MinPrefixLength
        && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));

    private static string NormalizeName(string? name) => WhatsAppNames.Key(name);

    private Task<User?> GetCurrentAdminAsync()
    {
        var currentUserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
    }
}
