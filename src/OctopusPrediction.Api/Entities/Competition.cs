namespace OctopusPrediction.Api.Entities;

// A competition players predict, with its own weeks and its own tables (week and overall). The
// rows come from Services/Competitions.cs; the system admin switches each one on or off.
public class Competition
{
    // The slug of Name, which is also the prefix of its scraped week ids ("la-liga-week-8").
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // Off: players don't see it, can't predict it, and the scraper and match windows ignore it.
    public bool Enabled { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
