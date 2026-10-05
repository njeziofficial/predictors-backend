using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Services;

// Ends a user's sessions by revoking their live refresh tokens. Access tokens already issued
// stay valid until they expire (minutes), so this is for "log them out everywhere" events like
// a password change or reset. Like AuditLogger, it only stages changes — callers save.
public static class SessionRevoker
{
    public static async Task RevokeAllAsync(AppDbContext db, Guid userId, string reason, Guid? exceptFamilyId = null)
    {
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .Where(t => exceptFamilyId == null || t.FamilyId != exceptFamilyId)
            .ToListAsync();

        foreach (var token in active)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }
    }
}
