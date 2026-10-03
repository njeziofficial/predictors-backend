using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Fallback source. FoxSports' scores page only ever shows a single day at a time (navigated
/// via a "?date=yyyy-MM-dd" query string and a set of date-pill links), unlike every other
/// source here which lists a whole round/page of matches at once — so this visits yesterday,
/// today and tomorrow and merges them, the same way LivescoreSource merges its /fixtures/ and
/// /results/ tabs. It also never carries a round number, and its kickoff-time text for
/// not-yet-started matches is a 12-hour US/Eastern clock ("7:00 PM") rather than the 24-hour
/// "HH:mm" MapStatus/ParseKickoff already understand — rather than mis-parse that into the
/// wrong instant (exactly the kind of bug that corrupted several fixtures' dates already),
/// this source only ever reports scores/status for matches that already exist, and always
/// leaves kickoff time null so ParseKickoff safely no-ops instead of guessing.
/// </summary>
internal sealed class FoxSportsSource(ILogger<FoxSportsSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Fox Sports";

    private const string DefaultUrl = "https://www.foxsports.com/soccer/la-liga/scores";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("FoxSports", DefaultUrl);

    private const string ExtractScript = """
        () => {
            const results = [];
            // A page-wide "a.score-chip" also picks up a client-side-injected cross-sport
            // ticker elsewhere on the page (Liga MX, Bundesliga, Serie A, even EFL matches have
            // shown up under that same class — container scoping alone didn't keep it out).
            // Every genuine match card here links to "/soccer/la-liga-<slug>-...", so filtering
            // on that href prefix is what actually isolates real La Liga fixtures.
            document.querySelectorAll('a.score-chip[href*="/soccer/la-liga-"]').forEach(el => {
                const rows = el.querySelectorAll('.score-team-row');
                if (rows.length < 2) return;

                const nameOf = (row) => row.querySelector('.score-team-name.team span[title]')?.textContent?.trim()
                    ?? row.querySelector('.score-team-name.team span')?.textContent?.trim() ?? '';
                const scoreOf = (row) => row.querySelector('.score-team-score span')?.textContent?.trim() || null;

                const home = nameOf(rows[0]);
                const away = nameOf(rows[1]);
                if (!home || !away) return;

                // The only status text worth reading — a sibling ".status-text" block on the
                // same card carries betting odds ("DRAW +167"), not match state, and must not
                // be confused for it.
                const stage = el.querySelector('.score-game-status .score-game-info span')?.textContent?.trim() || null;

                results.push({ home, away, scoreHome: scoreOf(rows[0]), scoreAway: scoreOf(rows[1]), stage });
            });
            return JSON.stringify(results);
        }
        """;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var merged = new Dictionary<string, ScrapedMatchDto>();

        foreach (var day in new[] { today.AddDays(-1), today, today.AddDays(1) })
        {
            var matches = await ScrapeDayAsync(page, day, ct);
            foreach (var m in matches)
                merged[$"{m.Home}|{m.Away}"] = m;
        }

        if (merged.Count == 0)
            logger.LogWarning("[Fox Sports] Empty result — CSS selectors may need updating");

        return [.. merged.Values];
    }

    private async Task<ScrapedMatchDto[]> ScrapeDayAsync(IPage page, DateTime day, CancellationToken ct)
    {
        // The score cards are server-rendered (data-ssr="true"), so DOMContentLoaded is enough —
        // waiting for the full "Load" event here was timing out against the page's ad/video
        // scripts (Freewheel, Taboola, a Bitmovin player) that don't affect the score markup.
        var url = $"{_baseUrl}?date={day:yyyy-MM-dd}";
        await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });

        await Task.Delay(3_000, ct);

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        if (string.IsNullOrWhiteSpace(json)) return [];

        var matches = JsonSerializer.Deserialize<ScrapedMatchDto[]>(json, ScrapingJson.Options) ?? [];
        foreach (var m in matches)
            m.Stage = NormalizeStage(m.Stage);
        return matches;
    }

    // Translates FoxSports' own vocabulary onto the tokens MapStatus already recognizes,
    // rather than teaching MapStatus a second dialect for one fallback source.
    private static string? NormalizeStage(string? stage) => stage?.Trim().ToUpperInvariant() switch
    {
        "FINAL" => "FT",
        "PPD" or "POSTPONED" => "Postponed",
        "CANCELLED" or "CANCELED" => "Cancelled",
        "HT" or "HALFTIME" => "HT",
        _ => stage
    };
}
