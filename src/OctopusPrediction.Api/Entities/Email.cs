namespace OctopusPrediction.Api.Entities;

// A single-use link to choose a password: "reset" from Forgot password (an hour), or "welcome"
// for an account an admin created (a week). Only a hash of the token is kept, as for refresh
// tokens, so the table on its own can't be used to take over an account.
public class PasswordToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public string Purpose { get; set; } = PasswordTokenPurposes.Reset;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public static class PasswordTokenPurposes
{
    public const string Reset = "reset";
    public const string Welcome = "welcome";
}

// Every email the app tried to send, so it stays inside each provider's free limits (counted per
// UTC day and month) and the system admin can see what went out.
public class EmailLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    // Which provider sent it ("Resend", "Brevo", "Gmail"): each has its own limits.
    public string? Provider { get; set; }
    public string To { get; set; } = string.Empty;
    // "reset", "welcome", "reminder", "announcement" or "test"; see EmailKinds.
    public string Kind { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public bool Sent { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class EmailKinds
{
    public const string Reset = "reset";
    public const string Welcome = "welcome";
    public const string Reminder = "reminder";
    public const string Announcement = "announcement";
    public const string Test = "test";

    // Emails someone is waiting for, which the daily reserve keeps room for.
    public static bool IsAccount(string kind) => kind is Reset or Welcome or Test;
}
