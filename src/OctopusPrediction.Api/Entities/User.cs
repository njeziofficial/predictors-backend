namespace OctopusPrediction.Api.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    // Nullable: existing users predate these fields. New registrations always provide them
    // (enforced by RegisterRequest's [Required]).
    public string? PhoneNumber { get; set; }
    public string? WhatsAppName { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsDisabled { get; set; } = false;
    // Marks the one permanent admin account (seeded from SeedAdmin:Email in config). Enforced
    // in AdminUsersController: this flag can never be deleted, disabled, or have its role
    // changed by anyone — including other admins — regardless of who's calling the API.
    public bool IsSystemUser { get; set; } = false;
    public bool MustResetPassword { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public ICollection<Prediction> Predictions { get; set; } = [];
}
