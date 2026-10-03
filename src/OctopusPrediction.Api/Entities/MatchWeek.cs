namespace OctopusPrediction.Api.Entities;

public class MatchWeek
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Competition { get; set; } = string.Empty;
    // Set once a prediction reminder has gone out for this week, so the background service
    // doesn't re-send on every poll — reminders are a one-shot-per-week thing.
    public DateTime? ReminderSentAt { get; set; }
    public ICollection<Fixture> Fixtures { get; set; } = [];
}
