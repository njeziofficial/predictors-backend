using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.Email;

// Single-use links to choose a password, and the emails that carry them. A link is 32 random
// bytes; only its SHA-256 is stored. A new link replaces any unused one of the same user, so
// only the latest email works.
public static class PasswordTokens
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan WelcomeLifetime = TimeSpan.FromDays(7);
    // Forgot password emails one account may be sent an hour, so the form can't flood an inbox.
    public const int ResetsPerHour = 3;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Makes a link for this user (staged; the caller saves) and returns its token.</summary>
    public static async Task<string> CreateAsync(AppDbContext db, User user, string purpose, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await foreach (var old in db.PasswordTokens.Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > now).AsAsyncEnumerable().WithCancellation(ct))
            old.ExpiresAt = now;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.PasswordTokens.Add(new PasswordToken
        {
            UserId = user.Id,
            TokenHash = Hash(token),
            Purpose = purpose,
            ExpiresAt = now + (purpose == PasswordTokenPurposes.Welcome ? WelcomeLifetime : ResetLifetime),
        });
        return token;
    }

    /// <summary>The unused, unexpired link for this token, with its user, or null.</summary>
    public static Task<PasswordToken?> FindValidAsync(AppDbContext db, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return Task.FromResult<PasswordToken?>(null);
        var hash = Hash(token.Trim());
        var now = DateTime.UtcNow;
        return db.PasswordTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now && !t.User.IsDisabled, ct);
    }

    /// <summary>Whether this user may be sent another Forgot password email this hour.</summary>
    public static async Task<bool> CanSendResetAsync(AppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        return await db.PasswordTokens.CountAsync(t => t.UserId == userId && t.Purpose == PasswordTokenPurposes.Reset && t.CreatedAt > since, ct) < ResetsPerHour;
    }

    /// <summary>Makes a link and emails it: Forgot password, or welcome for a new account. False if it wasn't sent.</summary>
    public static async Task<bool> SendAsync(AppDbContext db, EmailSender email, User user, string purpose, CancellationToken ct = default)
    {
        if (!email.IsConfigured) return false;
        var token = await CreateAsync(db, user, purpose, ct);
        await db.SaveChangesAsync(ct);

        var link = $"{email.AppUrl}/set-password?token={token}";
        var message = purpose == PasswordTokenPurposes.Welcome
            ? EmailTemplates.Welcome(user.Name, user.Email, user.WhatsAppName, link)
            : EmailTemplates.PasswordReset(user.Name, link);
        return await email.SendAsync(new EmailMessage(user.Email, message.Subject, message.Html, message.Text,
            purpose == PasswordTokenPurposes.Welcome ? EmailKinds.Welcome : EmailKinds.Reset, user.Id), ct);
    }
}
