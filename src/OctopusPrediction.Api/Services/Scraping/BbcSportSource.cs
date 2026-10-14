using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Last-resort fallback. The bare scores-fixtures URL defaults to a "today only" view,
/// which is empty on every day La Liga isn't playing (most days, since a round spans
/// Fri-Mon) — so this instead visits the current month's dedicated ?filter=fixtures and
/// ?filter=results views and merges them.
///
/// Each page embeds the matches it shows as data (window.__INITIAL_DATA__), which is read
/// instead of the markup: every match there carries BBC's own status ("PreEvent", "MidEvent",
/// "PostEvent", or words like "Suspended" and "Postponed"), its kickoff in UTC and its score.
/// Reading the markup, a match with a score could only be taken as finished, so an abandoned
/// match read as a finished one and was marked on the score when play stopped.
/// </summary>
internal sealed class BbcSportSource(ILogger<BbcSportSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "BBC Sport";
    public bool ProvidesRounds => false;

    private const string DefaultBaseUrl = "https://www.bbc.com/sport/football/spanish-la-liga/scores-fixtures";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("BbcSport", DefaultBaseUrl);

    // Finds every match record in the page's data: an object with home and away teams (each
    // with a fullName) and a status. Returns them raw; ToDto turns them into readings. The page's
    // own script clears window.__INITIAL_DATA__ once it has used it, so it's read from the
    // script tag that set it (window.__INITIAL_DATA__="…";) when it's already gone.
    private const string ExtractScript = """
        () => {
            let data = window.__INITIAL_DATA__;
            if (!data) {
                const text = Array.from(document.scripts).map(s => s.textContent).find(t => t.includes('__INITIAL_DATA__'));
                if (text) {
                    let literal = text.slice(text.indexOf('=', text.indexOf('__INITIAL_DATA__')) + 1).trim();
                    if (literal.endsWith(';')) literal = literal.slice(0, -1);
                    try { data = JSON.parse(literal); } catch { return '[]'; }
                }
            }
            if (typeof data === 'string') { try { data = JSON.parse(data); } catch { return '[]'; } }
            const events = [];
            const walk = (o) => {
                if (Array.isArray(o)) { o.forEach(walk); return; }
                if (!o || typeof o !== 'object') return;
                if (o.home?.fullName && o.away?.fullName && typeof o.status === 'string') {
                    events.push({
                        id: o.tipoTopicId ?? o.id ?? null,
                        home: o.home.fullName,
                        away: o.away.fullName,
                        scoreHome: o.home.score ?? null,
                        scoreAway: o.away.score ?? null,
                        status: o.status,
                        period: o.periodLabel?.value ?? null,
                        kickoff: o.startDateTime ?? o.date?.iso ?? null
                    });
                    return;
                }
                Object.values(o).forEach(walk);
            };
            walk(data);
            return JSON.stringify(events);
        }
        """;

    // One match as the page's data describes it.
    internal sealed record BbcEvent(
        string? Id, string Home, string Away, string? ScoreHome, string? ScoreAway,
        string Status, string? Period, string? Kickoff);

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
            logger.LogWarning("[BBC Sport] Empty result — the page's embedded match data may have changed");

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

        var events = JsonSerializer.Deserialize<BbcEvent[]>(json, ScrapingJson.Options) ?? [];
        return [.. events.Select(ToDto)];
    }

    /// <summary>
    /// BBC's status, as the readings every source gives: "PreEvent" not started, "MidEvent" in
    /// play (its period label is the minute or "HT"), "PostEvent" finished (passing on a label
    /// like "AET" that isn't plain full time). Any other status ("Suspended", "Postponed",
    /// "Abandoned", "Cancelled") goes on as the stage with no flags, for MapStatus to read.
    /// </summary>
    internal static ScrapedMatchDto ToDto(BbcEvent e)
    {
        var m = new ScrapedMatchDto
        {
            Id = e.Id,
            Home = e.Home,
            Away = e.Away,
            ScoreHome = e.ScoreHome,
            ScoreAway = e.ScoreAway,
            Time = e.Kickoff,
        };
        switch (e.Status)
        {
            case "PreEvent":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (true, false, null, null);
                break;
            case "MidEvent":
                (m.IsScheduled, m.IsLive, m.Stage) = (false, true, e.Period);
                break;
            case "PostEvent":
                (m.IsScheduled, m.IsLive) = (false, false);
                m.Stage = e.Period is null or "FT" ? null : e.Period;
                break;
            default:
                m.Stage = e.Status;
                break;
        }
        return m;
    }
}
