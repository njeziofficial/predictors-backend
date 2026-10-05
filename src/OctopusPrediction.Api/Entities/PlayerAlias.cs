namespace OctopusPrediction.Api.Entities;

// A league-table name (e.g. "FiskySo") linked by an admin to the account it belongs to (e.g.
// "Big Fisky"), so later previous-points imports match it without asking again. Key is the name
// normalized the way AdminPreviousPointsController compares names.
public class PlayerAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
