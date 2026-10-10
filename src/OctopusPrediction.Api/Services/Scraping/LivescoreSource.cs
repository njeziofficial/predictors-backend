using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Fallback source. livescore.com's CSS classes are build-hashed and change on every
/// deploy, so this relies on the small set of stable `data-id` attribute suffixes it
/// ships instead (e.g. a data-id ending in "_mtc-r_hm-tm-nm" — each row prefixes these
/// with its own match id, e.g. "0-1810666_mtc-r_hm-tm-nm", hence the suffix match below).
/// Its status text ("FT" / "HT" / a live minute / "HH:mm" for not-yet-started) follows
/// the same convention MapStatus already parses.
/// The base league page only lists a handful of "key" fixtures/results, so this visits
/// the dedicated /fixtures/ and /results/ tabs and merges them.
/// </summary>
internal sealed class LivescoreSource(ILogger<LivescoreSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Livescore";
    public bool ProvidesRounds => false;

    private const string DefaultBaseUrl = "https://www.livescore.com/en/football/spain/laliga";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("Livescore", DefaultBaseUrl);

    private const string ExtractScript = """
        () => {
            const results = [];
            const homeEls = document.querySelectorAll('[data-id$="_mtc-r_hm-tm-nm"]');
            homeEls.forEach(homeEl => {
                const row = homeEl.closest('a');
                if (!row) return;
                const home = homeEl.textContent.trim();
                const away = row.querySelector('[data-id$="_mtc-r_aw-tm-nm"]')?.textContent?.trim() ?? '';
                if (!home || !away) return;

                const stage = row.querySelector('[data-id$="_mtc-r_st-tm"]')?.textContent?.trim() ?? null;
                const href = row.getAttribute('href') ?? '';
                const idMatch = href.match(/\/(\d+)\/?$/);

                results.push({
                    id: idMatch ? 'ls-' + idMatch[1] : null,
                    home,
                    away,
                    scoreHome: row.querySelector('[data-id$="_mtc-r_hm-sc"]')?.textContent?.trim() ?? null,
                    scoreAway: row.querySelector('[data-id$="_mtc-r_aw-sc"]')?.textContent?.trim() ?? null,
                    stage,
                    time: /^\d{1,2}:\d{2}$/.test(stage ?? '') ? stage : null,
                    round: null
                });
            });
            return JSON.stringify(results);
        }
        """;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var fixtures = await ScrapePageAsync(page, $"{_baseUrl}/fixtures/", ct);
        var results = await ScrapePageAsync(page, $"{_baseUrl}/results/", ct);

        var merged = new Dictionary<string, ScrapedMatchDto>();
        foreach (var m in fixtures.Concat(results))
        {
            var key = m.Id ?? $"{m.Home}|{m.Away}";
            merged[key] = m;
        }

        if (merged.Count == 0)
            logger.LogWarning("[Livescore] Empty result — CSS/data-id selectors may need updating");

        return merged.Values.ToArray();
    }

    private async Task<ScrapedMatchDto[]> ScrapePageAsync(IPage page, string url, CancellationToken ct)
    {
        await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Load],
            Timeout = 30_000
        });

        await Task.Delay(3_000, ct);

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        if (string.IsNullOrWhiteSpace(json)) return [];

        return JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options) ?? [];
    }
}
