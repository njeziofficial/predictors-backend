namespace OctopusPrediction.Api.Services;

public class LiveScraperSettings
{
    public string Competition { get; set; } = "La Liga";
    public int PollIntervalSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    // Keyed by IMatchSource.Name (with spaces removed, e.g. "BbcSport" for "BBC Sport"). A
    // missing/empty entry falls back to that source's own compiled-in default URL, so this
    // section only needs to list overrides — add a new key here to point an existing source
    // at a different URL (e.g. a different competition), it does not register a new site.
    public Dictionary<string, string> Sources { get; set; } = new();
}
