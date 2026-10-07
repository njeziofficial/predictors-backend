using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// WhatsApp names are typed by hand on phones, so "God's own", "God’s Own" and "godsown" must all
// be the same name: comparison ignores case, spaces and punctuation. Used to sign in by WhatsApp
// name and to keep two accounts from sharing one.
public static class WhatsAppNames
{
    public static string Key(string? name) =>
        new((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Accounts whose WhatsApp name has the same key, optionally leaving one account out.
    public static async Task<List<User>> FindAsync(AppDbContext db, string name, Guid? exceptUserId = null)
    {
        var key = Key(name);
        if (key.Length == 0) return [];
        // The key can't be computed in SQL, and there are only a few dozen users.
        var users = await db.Users
            .Where(u => u.WhatsAppName != null && (exceptUserId == null || u.Id != exceptUserId))
            .ToListAsync();
        return users.Where(u => Key(u.WhatsAppName) == key).ToList();
    }

    public static async Task<bool> IsTakenAsync(AppDbContext db, string? name, Guid? exceptUserId = null) =>
        !string.IsNullOrWhiteSpace(name) && (await FindAsync(db, name, exceptUserId)).Count > 0;

    public const string TakenMessage = "Another account already uses this WhatsApp name.";
}
