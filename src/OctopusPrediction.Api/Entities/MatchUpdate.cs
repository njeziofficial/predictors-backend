namespace OctopusPrediction.Api.Entities;

// One scraped reading of a match that differed from the last one seen (see MatchUpdateQueue),
// waiting for MatchUpdateProcessor to apply it to its fixture. Processed rows are kept for a
// while as a history of what each source reported, then deleted by MatchUpdateCleanupService.
public class MatchUpdate
{
    public long Id { get; set; }
    public string FixtureId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Competition { get; set; } = string.Empty;
    // MatchReading.Fingerprint of Payload.
    public string Fingerprint { get; set; } = string.Empty;
    // The ScrapedMatchDto as JSON.
    public string Payload { get; set; } = string.Empty;
    public DateTime ScrapedAt { get; set; }
    // Null while waiting to be applied.
    public DateTime? ProcessedAt { get; set; }
    // Applied, Superseded (a newer reading of the same match came in first) or Failed.
    public string? Outcome { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

public static class MatchUpdateOutcomes
{
    public const string Applied = "Applied";
    public const string Superseded = "Superseded";
    public const string Failed = "Failed";
}
