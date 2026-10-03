using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// worldfootball.net's "all-matches" page lists the entire season as flat sibling divs
/// under one container: a "Matchday N" round-head div, then a date-head, then one or more
/// match divs, repeating. Score/status/kickoff come from stable classes and a data-datetime
/// attribute rather than build-hashed CSS, so this tends to survive redesigns better than
/// the others — but it lists all 380 matches for the season, so results are filtered down
/// to a +/-7 day window around now (client-side, via data-datetime) to avoid syncing the
/// whole season every poll.
/// </summary>
internal sealed class WorldFootballSource(ILogger<WorldFootballSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "WorldFootball";

    private const string DefaultUrl = "https://www.worldfootball.net/competition/co97/spain-primera-division/all-matches/";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("WorldFootball", DefaultUrl);

    private const string ExtractScript = """
        () => {
            const results = [];
            let currentRound = null;
            const now = Date.now();
            const windowMs = 7 * 24 * 3600 * 1000;

            document.querySelectorAll('[data-match_id]').forEach(el => {
                let sib = el.previousElementSibling;
                while (sib) {
                    if (sib.classList.contains('round-head')) { currentRound = sib.textContent.trim(); break; }
                    sib = sib.previousElementSibling;
                }

                const dt = new Date(el.getAttribute('data-datetime'));
                if (isNaN(dt.getTime()) || Math.abs(dt.getTime() - now) > windowMs) return;

                const home = el.querySelector('.team-name-home')?.textContent?.trim() ?? '';
                const away = el.querySelector('.team-name-away')?.textContent?.trim() ?? '';
                if (!home || !away) return;

                // Unplayed matches show "-:-" here instead of digits.
                const resultParts = (el.querySelector('.match-result a')?.textContent?.trim() ?? '').split(':').map(s => s.trim());
                const played = resultParts.length === 2 && /^\d+$/.test(resultParts[0]) && /^\d+$/.test(resultParts[1]);
                const isFinished = el.classList.contains('finished');
                const time = el.querySelector('.match-time')?.textContent?.trim() ?? null;

                results.push({
                    id: 'wf-' + el.getAttribute('data-match_id'),
                    home,
                    away,
                    scoreHome: played ? resultParts[0] : null,
                    scoreAway: played ? resultParts[1] : null,
                    stage: isFinished ? 'FT' : null,
                    time: (!isFinished && /^\d{1,2}:\d{2}$/.test(time ?? '')) ? time : null,
                    round: currentRound
                });
            });
            return JSON.stringify(results);
        }
        """;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        await page.GoToAsync(_url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 45_000
        });

        await Task.Delay(2_000, ct);

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("[WorldFootball] Empty result — CSS selectors may need updating");
            return [];
        }

        var matches = JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options);
        return matches ?? [];
    }
}
