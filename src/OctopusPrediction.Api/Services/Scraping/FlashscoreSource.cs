using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Primary source. Flashscore groups matches under "Round N" headers, which is what the
/// rest of the app (MatchWeek/Fixture) expects, so this is the only source that reliably
/// creates weeks — the others are score/status fallbacks for fixtures Flashscore already made.
/// </summary>
internal sealed class FlashscoreSource(ILogger<FlashscoreSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Flashscore";

    private const string DefaultUrl = "https://www.flashscore.com/football/spain/laliga/";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("Flashscore", DefaultUrl);

    // Flashscore's 2026 frontend rewrite ("wcl-*" design system) renamed the old BEM
    // classes: event__participant--home/away -> event__homeParticipant/awayParticipant,
    // and event__time -> event__stageTime. It also stopped putting "FT"/"45'" text in a
    // single event__stage element for pre-match/finished rows (that div now only exists
    // for live matches, holding the live minute) — status is instead carried by a
    // modifier class on the row itself (event__match--scheduled / event__match--live;
    // no modifier means finished). See BuildFixtureId/MapStatus for how this is consumed.
    private const string ExtractScript = """
        () => {
            const results = [];
            let currentRound = null;
            const elements = document.querySelectorAll('[class*="event__round"], [id^="g_1_"]');
            elements.forEach(el => {
                if (Array.from(el.classList).some(c => c.includes('event__round'))) {
                    currentRound = el.textContent.trim();
                    return;
                }
                if (!el.id || !el.id.startsWith('g_1_')) return;
                const home = el.querySelector('[class*="event__homeParticipant"]')?.textContent?.trim() ?? '';
                const away = el.querySelector('[class*="event__awayParticipant"]')?.textContent?.trim() ?? '';
                if (!home || !away) return;

                const classes = el.className.split(/\s+/);
                const isScheduled = classes.includes('event__match--scheduled');
                const isLive = classes.includes('event__match--live');
                const liveStage = isLive ? el.querySelector('.event__stage')?.textContent?.trim() ?? null : null;
                const stageTime = el.querySelector('[class*="event__stageTime"]')?.textContent?.trim() ?? null;

                results.push({
                    id: el.id,
                    home,
                    away,
                    scoreHome: el.querySelector('[class*="event__score--home"]')?.textContent?.trim() ?? null,
                    scoreAway: el.querySelector('[class*="event__score--away"]')?.textContent?.trim() ?? null,
                    stage: liveStage,
                    isScheduled,
                    isLive,
                    time: stageTime,
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
            WaitUntil = [WaitUntilNavigation.Load],
            Timeout = 30_000
        });

        await Task.Delay(3_000, ct);

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("[Flashscore] Empty result — CSS selectors may need updating");
            return [];
        }

        var matches = JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options);
        return matches ?? [];
    }
}
