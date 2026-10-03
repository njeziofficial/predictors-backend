namespace OctopusPrediction.Api.Entities;

// Deliberately has no foreign key to User: an audit trail is independent history that
// must survive the subject/actor being deleted later, so names are captured as a
// point-in-time snapshot rather than looked up live.
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public string Action { get; set; } = string.Empty;
    // Field/PreviousValue/NewValue are populated for a single-field change (e.g. Role,
    // Status, Name); left null for events that aren't a before/after change of one field
    // (Login, Register, UserDeleted, PasswordChanged — never log password values).
    public string? Field { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
