namespace OctopusPrediction.Api.Entities;

public class Prediction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string FixtureId { get; set; } = string.Empty;
    public Fixture Fixture { get; set; } = null!;
    public string WeekId { get; set; } = string.Empty;
    public OutcomeType Outcome { get; set; }
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public int PointsEarned { get; set; } = 0;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}
