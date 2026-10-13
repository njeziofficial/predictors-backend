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
    // Knockout matches only (never La Liga): predictions are marked on the score after 90
    // minutes, so the scraper keeps the last score it saw in normal time, and once a match goes
    // to extra time the final score is that one. AfterExtraTime is the real end result, to show.
    public int? RegularTimeScoreHome { get; set; }
    public int? RegularTimeScoreAway { get; set; }
    public bool WentToExtraTime { get; set; }
    public int? AfterExtraTimeHome { get; set; }
    public int? AfterExtraTimeAway { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Prediction> Predictions { get; set; } = [];
}
