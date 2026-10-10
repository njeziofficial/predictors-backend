using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// One scrapeable site that can produce a round-up of La Liga matches. Which sources the
/// background service reads, and in what order, is the system user's choice in admin settings
/// (see SourcePlan).
/// </summary>
public interface IMatchSource
{
    string Name { get; }

    // Whether the source says which round a match belongs to. Only these can create new
    // fixtures (a fixture needs a week); the others only update fixtures that already exist.
    bool ProvidesRounds { get; }

    Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct);
}
