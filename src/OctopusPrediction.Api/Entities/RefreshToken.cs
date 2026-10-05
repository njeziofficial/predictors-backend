namespace OctopusPrediction.Api.Entities;

// One refresh token in a login session. Every refresh rotates the token: the old row is revoked
// and points at its replacement, and all rows from one login share a FamilyId (also carried in
// the access token as the "session_id" claim). Presenting an already-rotated token outside the short
// grace window means it was copied, so the whole family is revoked. Only a SHA-256 hash of the
// token is stored — the raw value lives solely in the user's httpOnly cookie.
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    // "rotated", "logout", "reuse_detected", "password_changed", "password_reset", "disabled"
    public string? RevokedReason { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
