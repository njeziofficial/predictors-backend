using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Last-resort fallback. The bare scores-fixtures URL defaults to a "today only" view,
/// which is empty on every day La Liga isn't playing (most days, since a round spans
/// Fri-Mon) — so this instead visits the current month's dedicated ?filter=fixtures and
/// ?filter=results views and merges them, the same way LivescoreSource splits across its
/// /fixtures/ and /results/ tabs. Score digits live in adjacent sibling divs inside a
/// [data-testid="score"] element (home, a divider, away) — read via .children so the
/// divider and any concatenated-text parent wrapper can't be mistaken for a score digit.
/// </summary>
internal sealed class BbcSportSource(ILogger<BbcSportSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "BBC Sport";

    private const string DefaultBaseUrl = "https://www.bbc.com/sport/football/spanish-la-liga/scores-fixtures";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("BbcSport", DefaultBaseUrl);

    private const string ExtractScript = """
        () => {
            const results = [];
            const items = document.querySelectorAll('li[data-tipo-topic-id]');
            items.forEach(li => {
                const home = li.querySelector('[class*="TeamHome"] [class*="DesktopValue"]')?.textContent?.trim() ?? '';
                const away = li.querySelector('[class*="TeamAway"] [class*="DesktopValue"]')?.textContent?.trim() ?? '';
                if (!home || !away) return;

                const time = li.querySelector('time')?.textContent?.trim() ?? null;
                const scoreEl = li.querySelector('[data-testid="score"]');
                let scoreHome = null, scoreAway = null, stage = null;
                if (scoreEl && scoreEl.children.length >= 2) {
                    const kids = Array.from(scoreEl.children).map(e => e.textContent.trim());
                    scoreHome = kids[0];
                    scoreAway = kids[kids.length - 1];
                    stage = 'FT';
                }

                results.push({
                    id: li.getAttribute('data-tipo-topic-id'),
                    home,
                    away,
                    scoreHome,
                    scoreAway,
                    stage,
                    time,
                    round: null
                });
            });
            return JSON.stringify(results);
        }
        """;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var fixtures = await ScrapePageAsync(page, $"{_baseUrl}/{month}?filter=fixtures", ct);
        var results = await ScrapePageAsync(page, $"{_baseUrl}/{month}?filter=results", ct);

        var merged = new Dictionary<string, ScrapedMatchDto>();
        foreach (var m in fixtures.Concat(results))
        {
            var key = m.Id ?? $"{m.Home}|{m.Away}";
            merged[key] = m;
        }

        if (merged.Count == 0)
            logger.LogWarning("[BBC Sport] Empty result — CSS selectors may need updating");

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
