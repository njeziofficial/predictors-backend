namespace OctopusPrediction.Api.Services;

public sealed class ScrapedMatchDto
{
    public string? Id { get; set; }
    public string Home { get; set; } = string.Empty;
    public string Away { get; set; } = string.Empty;
    public string? ScoreHome { get; set; }
    public string? ScoreAway { get; set; }

    /// <summary>Raw match-state text as shown by the source (e.g. "FT", "HT", "45", "16:00").</summary>
    public string? Stage { get; set; }

    /// <summary>Set when the source exposes an explicit "not started yet" flag, bypassing Stage-text parsing.</summary>
    public bool? IsScheduled { get; set; }

    /// <summary>Set when the source exposes an explicit "in progress" flag, bypassing Stage-text parsing.</summary>
    public bool? IsLive { get; set; }

    public string? Time { get; set; }
    public string? Round { get; set; }
}
