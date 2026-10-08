namespace OctopusPrediction.Api.Entities;

// What every admin may do unless their own UserPermission says otherwise. One row per
// permission key in Services/Permissions; the system user controls these.
public class RolePermission
{
    public string Permission { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

// A per-admin exception to RolePermission: Allowed grants or withholds that one permission
// for that admin. No row means the admin follows the role default.
public class UserPermission
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Permission { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
