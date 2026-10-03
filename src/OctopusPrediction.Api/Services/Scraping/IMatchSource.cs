using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// One scrapeable site that can produce a round-up of La Liga matches. The background
/// service round-robins which source leads each poll and falls through the rest in that
/// rotated order until one returns a non-empty result, so a DOM change or block on one
/// site doesn't stop syncing and no single site is hit on every cycle.
/// </summary>
public interface IMatchSource
{
    string Name { get; }

    Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct);
}
