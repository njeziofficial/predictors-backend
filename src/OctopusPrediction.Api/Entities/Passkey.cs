namespace OctopusPrediction.Api.Entities;

// A passkey: a player's phone or computer can sign them in with its fingerprint, face or screen
// lock instead of their password (WebAuthn). The private key never leaves the device; the server
// keeps only the public key to check its signatures. Players add and remove their own in Profile.
public class Passkey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    // The authenticator's id for this credential, which the device sends when signing in.
    public byte[] CredentialId { get; set; } = [];
    // COSE-encoded public key.
    public byte[] PublicKey { get; set; } = [];
    // Goes up with each use on authenticators that count; a lower one means a cloned key.
    public long SignCount { get; set; }
    // What the player calls it, e.g. "Pixel 8" or "iPhone".
    public string Name { get; set; } = string.Empty;
    // How the browser can reach it ("internal", "hybrid"…), comma-separated.
    public string? Transports { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
