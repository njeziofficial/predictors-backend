namespace OctopusPrediction.Api.Entities;

public class MatchWeek
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Competition { get; set; } = string.Empty;
    // Set once a prediction reminder has gone out for this week, so the background service
    // doesn't re-send on every poll — reminders are a one-shot-per-week thing.
    public DateTime? ReminderSentAt { get; set; }
    // Set by the end-of-week check (WeekReconciliation) once every match has finished and the
    // week's points have been re-marked. Cleared if a result in the week changes afterwards.
    public DateTime? ReconciledAt { get; set; }
    public ICollection<Fixture> Fixtures { get; set; } = [];
}
