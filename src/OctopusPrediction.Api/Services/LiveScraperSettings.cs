namespace OctopusPrediction.Api.Services;

public class LiveScraperSettings
{
    public string Competition { get; set; } = "La Liga";
    public int PollIntervalSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    // How long applied rows stay in MatchUpdates (a history of what each source reported)
    // before MatchUpdateCleanupService deletes them.
    public int UpdateRetentionHours { get; set; } = 48;

    // A Chrome headless shell already on disk (the Docker image installs one), so the scraper
    // doesn't download it again on every start: Render's disk is wiped each time the service
    // sleeps or restarts. Unset, or missing on disk, means download it as before.
    public string? ChromeExecutablePath { get; set; }

    // Keyed by IMatchSource.Name (with spaces removed, e.g. "BbcSport" for "BBC Sport"). A
    // missing/empty entry falls back to that source's own compiled-in default URL, so this
    // section only needs to list overrides — add a new key here to point an existing source
    // at a different URL (e.g. a different competition), it does not register a new site.
    public Dictionary<string, string> Sources { get; set; } = new();

    // Competitions other than La Liga, keyed by competition id ("champions-league"). Each lists
    // its own page per source, keyed like Sources. Unlike Sources there's no fallback: a source
    // without a URL here isn't used for that competition (its defaults are La Liga's pages).
    public Dictionary<string, CompetitionScrapeSettings> Competitions { get; set; } = new();

    // How many pages, at most, the other competitions may load from one site in an hour,
    // counting La Liga's own: past it they move to their next source. La Liga is never held back.
    public int MaxPagesPerSitePerHour { get; set; } = 80;

    // Set only on the copies CompetitionSources gives another competition's sources; null for
    // La Liga's, so a source can tell which pages it's reading.
    public string? CompetitionId { get; set; }
}

public class CompetitionScrapeSettings
{
    public Dictionary<string, string> Sources { get; set; } = new();
}
