using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Last-resort fallback. ESPN's schedule table has a deceptive quirk confirmed by
/// cross-checking venues against each club's own ground across many rows: the cell
/// CSS-classed "away" is actually the HOME team, and the plain "colspan__col" cell holds
/// the real away/visiting team (the class name reflects a layout column, not team status —
/// don't trust it at face value). Finished matches drop the kickoff-time cell entirely and
/// show the score as "H - A" text inside the colspan__col link instead, with a separate
/// "FT" status cell replacing it. Only surfaces the current week's fixtures (no round
/// grouping), so this exists purely to backfill scores/status for fixtures another source
/// already created.
/// </summary>
internal sealed class EspnSource(ILogger<EspnSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "ESPN";

    private const string DefaultUrl = "https://www.espn.com/soccer/schedule/_/league/esp.1";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("Espn", DefaultUrl);

    private const string ExtractScript = """
        () => {
            const results = [];
            document.querySelectorAll('tbody tr').forEach(tr => {
                const home = tr.querySelector('.matchTeams .Table__Team.away a:last-child')?.textContent?.trim() ?? '';
                const away = tr.querySelector('.colspan__col .Table__Team a:last-child')?.textContent?.trim() ?? '';
                if (!home || !away) return;

                const scoreLink = tr.querySelector('.colspan__col a.at');
                const scoreMatch = (scoreLink?.textContent?.trim() ?? '').match(/(\d+)\s*-\s*(\d+)/);
                const idMatch = (scoreLink?.getAttribute('href') ?? '').match(/gameId\/(\d+)/);
                const time = tr.querySelector('.date__col')?.textContent?.trim() ?? null;
                const finished = /^\s*FT\s*$/i.test(tr.querySelector('.teams__col')?.textContent?.trim() ?? '');

                results.push({
                    id: idMatch ? 'espn-' + idMatch[1] : null,
                    home,
                    away,
                    scoreHome: scoreMatch ? scoreMatch[1] : null,
                    scoreAway: scoreMatch ? scoreMatch[2] : null,
                    stage: finished ? 'FT' : null,
                    time: finished ? null : time,
                    round: null
                });
            });
            return JSON.stringify(results);
        }
        """;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        await page.GoToAsync(_url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle0],
            Timeout = 45_000
        });

        try
        {
            await page.WaitForSelectorAsync("tbody tr", new WaitForSelectorOptions { Timeout = 15_000 });
        }
        catch (WaitTaskTimeoutException)
        {
            logger.LogWarning("[ESPN] Schedule table never appeared — CSS selectors may need updating");
            return [];
        }

        string json;
        try
        {
            json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        }
        catch (Exception ex) when (ex is NullReferenceException or PuppeteerException)
        {
            logger.LogWarning(ex, "[ESPN] Page navigated away mid-scrape — execution context was lost");
            return [];
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("[ESPN] Empty result — CSS selectors may need updating");
            return [];
        }

        var matches = JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options);
        return matches ?? [];
    }
}
