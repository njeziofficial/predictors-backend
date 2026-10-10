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
    public bool ProvidesRounds => true;

    private const string DefaultUrl = "https://www.flashscore.com/football/spain/laliga/";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("Flashscore", DefaultUrl);

    // A match's round never changes, so rounds looked up elsewhere are remembered (by row id)
    // for as long as the backend is up. Lookups therefore happen about once per wake, not per poll.
    private readonly Dictionary<string, string> _roundById = new();
    // Match pages to open per poll for rounds the fixtures page didn't have; the rest wait.
    private const int MaxMatchPageLookups = 4;

    // Flashscore's 2026 frontend rewrite ("wcl-*" design system) renamed the old BEM
    // classes: event__participant--home/away -> event__homeParticipant/awayParticipant,
    // and event__time -> event__stageTime. It also stopped putting "FT"/"45'" text in a
    // single event__stage element for pre-match/finished rows (that div now only exists
    // for live matches, holding the live minute) — status is instead carried by a
    // modifier class on the row itself (event__match--scheduled / event__match--live;
    // no modifier means finished). See BuildFixtureId/MapStatus for how this is consumed.
    //
    // The league page also has a "Today's Matches" block (no round headers) above the
    // round-grouped "Scheduled" and "Latest Scores" blocks, and today's matches aren't repeated
    // in those. So the round resets at each block ("leagues--static"), leaving today's rows with
    // no round for ResolveMissingRoundsAsync to fill in, rather than inheriting another block's.
    private const string ExtractScript = """
        () => {
            const results = [];
            let currentRound = null;
            const elements = document.querySelectorAll('[class*="leagues--static"], [class*="event__round"], [id^="g_1_"]');
            elements.forEach(el => {
                if (Array.from(el.classList).some(c => c.includes('leagues--static'))) {
                    currentRound = null;
                    return;
                }
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

        var matches = JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options) ?? [];
        foreach (var m in matches.Where(m => m.Id is not null && m.Round is not null))
            _roundById[m.Id!] = m.Round!;

        try
        {
            await ResolveMissingRoundsAsync(page, matches, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The main page's results are still good; rows without a round just wait for next time.
            logger.LogWarning(ex, "[Flashscore] Couldn't look up missing rounds");
        }
        return matches;
    }

    // Fills in rounds for rows that had none (today's matches): from memory, else the league's
    // fixtures page (every upcoming match, grouped by round), else the match's own page, whose
    // breadcrumb reads "LaLiga - Round 8" (for one that has already kicked off).
    private async Task ResolveMissingRoundsAsync(IPage page, ScrapedMatchDto[] matches, CancellationToken ct)
    {
        List<ScrapedMatchDto> Missing() => matches
            .Where(m => m.Round is null && m.Id is not null)
            .Where(m => { if (_roundById.TryGetValue(m.Id!, out var r)) m.Round = r; return m.Round is null; })
            .ToList();

        if (Missing().Count == 0) return;

        await page.GoToAsync(new Uri(new Uri(_url.TrimEnd('/') + "/"), "fixtures/").ToString(),
            new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = 30_000 });
        await Task.Delay(3_000, ct);
        var fixturesJson = await page.EvaluateFunctionAsync<string>(ExtractScript);
        var fixtures = string.IsNullOrWhiteSpace(fixturesJson)
            ? []
            : JsonSerializer.Deserialize<ScrapedMatchDto[]>(fixturesJson, ScrapingJson.Options) ?? [];
        foreach (var f in fixtures.Where(f => f.Id is not null && f.Round is not null))
            _roundById[f.Id!] = f.Round!;

        foreach (var m in Missing().Take(MaxMatchPageLookups))
        {
            var matchId = m.Id!.StartsWith("g_1_") ? m.Id[4..] : m.Id;
            await page.GoToAsync($"https://www.flashscore.com/match/{matchId}/#/match-summary",
                new NavigationOptions { WaitUntil = [WaitUntilNavigation.Load], Timeout = 30_000 });
            await Task.Delay(2_000, ct);
            var round = await page.EvaluateFunctionAsync<string?>("""
                () => {
                    const crumbs = document.querySelector('[class*="detail__breadcrumbs"]')?.textContent ?? '';
                    const match = crumbs.match(/Round\s+\d+/i);
                    return match ? match[0] : null;
                }
                """);
            if (round is null) continue;
            m.Round = round;
            _roundById[m.Id] = round;
        }

        var stillMissing = Missing();
        if (stillMissing.Count > 0)
            logger.LogWarning("[Flashscore] No round yet for {Matches}; trying again next poll",
                string.Join(", ", stillMissing.Select(m => $"{m.Home} v {m.Away}")));
    }
}
