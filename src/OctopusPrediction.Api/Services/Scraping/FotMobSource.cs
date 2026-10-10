using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Fallback source. Unlike the others, FotMob's league page is a Next.js app that embeds the
/// entire season's fixture list as structured JSON in a `__NEXT_DATA__` script tag rather than
/// rendering scores into ad-hoc DOM markup — so this reads that JSON directly instead of
/// guessing at CSS selectors. It's also the only source besides Flashscore that supplies a real
/// round number and an exact ISO-8601 kickoff instant rather than a bare "HH:mm" that has to be
/// assumed to mean "today".
/// </summary>
internal sealed class FotMobSource(ILogger<FotMobSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "FotMob";
    public bool ProvidesRounds => true;

    // League id 87 = LaLiga.
    private const string DefaultUrl = "https://www.fotmob.com/leagues/87/matches/laliga";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("FotMob", DefaultUrl);

    // The full season is ~380 matches; only a near-term window is relevant to this app (recent
    // results still worth scoring, plus upcoming fixtures) — matching the scale of what the
    // other sources' pages show, rather than upserting the entire season every poll.
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        // The __NEXT_DATA__ blob is server-rendered into the raw HTML, so it's available as
        // soon as the document is parsed — no need to wait for the full "Load" event (images,
        // fonts, analytics), which on FotMob's heavier SPA was tripping the 30s timeout.
        await page.GoToAsync(_url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 45_000
        });

        var json = await page.EvaluateFunctionAsync<string?>(
            "() => document.getElementById('__NEXT_DATA__')?.textContent ?? null");

        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("[FotMob] __NEXT_DATA__ not found — page layout may have changed");
            return [];
        }

        try
        {
            return ExtractMatches(json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FotMob] Failed to parse __NEXT_DATA__");
            return [];
        }
    }

    // "allMatches" isn't at a fixed path under pageProps — FotMob nests it inside an
    // SWR prefetch cache keyed by an internal API path (pageProps.fallback["/api/..."]) that
    // isn't stable across requests, so rather than chase that key, this walks the whole
    // document looking for the one object that has both "firstUnplayedMatch" and "allMatches"
    // — a signature specific enough that it can't be confused with anything else on the page.
    private static JsonElement? FindAllMatches(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("firstUnplayedMatch", out _) && el.TryGetProperty("allMatches", out var found))
                return found;
            foreach (var prop in el.EnumerateObject())
            {
                var result = FindAllMatches(prop.Value);
                if (result is not null) return result;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var result = FindAllMatches(item);
                if (result is not null) return result;
            }
        }
        return null;
    }

    private static ScrapedMatchDto[] ExtractMatches(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var allMatches = FindAllMatches(doc.RootElement)
            ?? throw new InvalidOperationException("'allMatches' not found anywhere in __NEXT_DATA__");

        var now = DateTime.UtcNow;
        var results = new List<ScrapedMatchDto>();

        foreach (var m in allMatches.EnumerateArray())
        {
            var status = m.GetProperty("status");
            if (!status.TryGetProperty("utcTime", out var utcTimeEl)) continue;
            if (!DateTime.TryParse(utcTimeEl.GetString(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var utcTime)) continue;
            if (utcTime < now - Window || utcTime > now + Window) continue;

            var started = status.TryGetProperty("started", out var s) && s.GetBoolean();
            var finished = status.TryGetProperty("finished", out var f) && f.GetBoolean();
            var cancelled = status.TryGetProperty("cancelled", out var c) && c.GetBoolean();

            string? scoreHome = null, scoreAway = null;
            if (status.TryGetProperty("scoreStr", out var scoreStrEl))
            {
                var parts = scoreStrEl.GetString()?.Split(" - ");
                if (parts?.Length == 2) (scoreHome, scoreAway) = (parts[0].Trim(), parts[1].Trim());
            }

            string? stage = null;
            if (cancelled)
            {
                // MapStatus only recognizes Postponed/Cancelled via Stage text, not the
                // IsScheduled/IsLive flags — leaving both flags null routes this through that
                // text-based fallback instead of being misread as a finished 0-0.
                stage = "Postponed";
            }
            else if (started && !finished
                && status.TryGetProperty("liveTime", out var liveTime)
                && liveTime.TryGetProperty("short", out var shortEl))
            {
                stage = shortEl.GetString();
            }

            results.Add(new ScrapedMatchDto
            {
                Id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                Home = m.GetProperty("home").GetProperty("name").GetString() ?? "",
                Away = m.GetProperty("away").GetProperty("name").GetString() ?? "",
                ScoreHome = scoreHome,
                ScoreAway = scoreAway,
                Stage = stage,
                IsScheduled = cancelled ? null : !started,
                IsLive = cancelled ? null : started && !finished,
                Time = utcTimeEl.GetString(),
                Round = m.TryGetProperty("roundName", out var rn)
                    ? rn.ValueKind == JsonValueKind.Number ? rn.GetInt32().ToString() : rn.GetString()
                    : null
            });
        }

        return [.. results];
    }
}
