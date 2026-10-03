namespace OctopusPrediction.Api.Entities;

public class Fixture
{
    public string Id { get; set; } = string.Empty;
    public string WeekId { get; set; } = string.Empty;
    public MatchWeek Week { get; set; } = null!;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public DateTime Kickoff { get; set; }
    public FixtureStatus Status { get; set; } = FixtureStatus.PreMatch;
    public int? FinalScoreHome { get; set; }
    public int? FinalScoreAway { get; set; }
    public int? LiveScoreHome { get; set; }
    public int? LiveScoreAway { get; set; }
    public int? LiveMinute { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Prediction> Predictions { get; set; } = [];
}
