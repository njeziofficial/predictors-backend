using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Reads FotMob's own JSON API (what its pages load) rather than any page markup, so there are
/// no CSS selectors to break. Two lists:
/// <list type="bullet">
/// <item>The day's matches — what FotMob's live page shows — read every poll. FotMob caches
/// this for 10 seconds, so the scores are as fresh as the poll.</item>
/// <item>The league's season list, for rounds and the fixtures still to come, read at most every
/// <see cref="SeasonListRefresh"/>. (The league page carries the same list, but is cached for an
/// hour: reading scores from it is why FotMob scores used to lag.)</item>
/// </list>
/// Like Flashscore, it supplies a real round and an exact ISO-8601 kickoff, not a bare "HH:mm".
/// </summary>
internal sealed partial class FotMobSource(ILogger<FotMobSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "FotMob";
    public bool ProvidesRounds => true;

    private const string ApiBase = "https://www.fotmob.com/api/data";
    // The configured URL is the league page (LiveScoreScraper:Sources:FotMob); only its league id
    // is used. 87 = LaLiga.
    private const int DefaultLeagueId = 87;
    private readonly int _leagueId = LeagueIdFrom(options.Value.Sources.GetValueOrDefault("FotMob"));

    // The full season is ~380 matches; only a near-term window is relevant to this app (recent
    // results still worth scoring, plus upcoming fixtures).
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);
    // New rounds are only added now and then; the day list carries everything that changes in a match.
    private static readonly TimeSpan SeasonListRefresh = TimeSpan.FromMinutes(30);
    // A match listed under yesterday's date can still be finishing for this long after midnight UTC.
    private static readonly TimeSpan LateMatchGrace = TimeSpan.FromHours(3);

    private Dictionary<string, ScrapedMatchDto> _season = [];
    private DateTime _seasonReadAt = DateTime.MinValue;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // The day lists first: they're what the poll is for. If they can't be read, report no
        // matches rather than fall back to the older season list, which could take a score back.
        var today = new Dictionary<string, ScrapedMatchDto>();
        var dates = now.TimeOfDay < LateMatchGrace ? new[] { now.Date.AddDays(-1), now.Date } : [now.Date];
        foreach (var date in dates)
        {
            var json = await GetJsonAsync(page, $"{ApiBase}/matches?date={date:yyyyMMdd}&timezone=UTC&ccode3=GBR");
            if (json is null) return [];
            foreach (var (id, match) in ParseDay(json)) today[id] = match;
        }

        if (now - _seasonReadAt >= SeasonListRefresh && await ReadSeasonAsync(page) is { } season)
        {
            _season = season;
            _seasonReadAt = now;
        }

        var matches = new Dictionary<string, ScrapedMatchDto>();
        foreach (var (id, match) in _season)
            if (InWindow(match, now)) matches[id] = match;
        foreach (var (id, match) in today)
        {
            // The season list's round is the one the other code paths have always used.
            if (_season.TryGetValue(id, out var listed) && listed.Round is not null) match.Round = listed.Round;
            matches[id] = match;
        }
        return [.. matches.Values];
    }

    // Null when the list couldn't be read; the caller keeps the copy it has.
    private async Task<Dictionary<string, ScrapedMatchDto>?> ReadSeasonAsync(IPage page)
    {
        var json = await GetJsonAsync(page, $"{ApiBase}/leagues?id={_leagueId}");
        if (json is null) return null;
        try
        {
            return ParseSeason(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "[FotMob] Couldn't read the season list — keeping the previous copy");
            return null;
        }
    }

    // Opening the API URL as a page (not a fetch from script) keeps the request looking like the
    // browser's own, and lets the scraper's refusal check see its HTTP status. Null on any refusal
    // or error, which the scraper records against the source.
    private async Task<string?> GetJsonAsync(IPage page, string url)
    {
        var response = await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });
        if (response is null || !response.Ok)
        {
            logger.LogWarning("[FotMob] {Url} answered {Status}", url, response is null ? "nothing" : (int)response.Status);
            return null;
        }
        return await response.TextAsync();
    }

    private IEnumerable<(string Id, ScrapedMatchDto Match)> ParseDay(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("leagues", out var leagues)) yield break;
        foreach (var league in leagues.EnumerateArray())
        {
            // primaryId: a league split into groups lists each group under its own id.
            var isOurs = (league.TryGetProperty("primaryId", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == _leagueId)
                || (league.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == _leagueId);
            if (!isOurs || !league.TryGetProperty("matches", out var list)) continue;
            foreach (var m in list.EnumerateArray())
                if (ToDto(m, Text(m, "tournamentStage")) is { } dto) yield return (dto.Id!, dto);
        }
    }

    private static Dictionary<string, ScrapedMatchDto> ParseSeason(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var allMatches = FindAllMatches(doc.RootElement)
            ?? throw new InvalidOperationException("'allMatches' not found in the league data");

        var result = new Dictionary<string, ScrapedMatchDto>();
        foreach (var m in allMatches.EnumerateArray())
            if (ToDto(m, Text(m, "roundName") ?? Text(m, "round")) is { } dto) result[dto.Id!] = dto;
        return result;
    }

    // "allMatches" isn't at a fixed path in the league data (FotMob has moved it before), so
    // rather than chase it, this walks the whole document looking for the one object that has
    // both "firstUnplayedMatch" and "allMatches" — a signature specific enough that it can't be
    // confused with anything else.
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

    // The day and season lists describe a match the same way (status, home/away names); only
    // where the round is kept differs. Null for a row without an id or kickoff.
    private static ScrapedMatchDto? ToDto(JsonElement m, string? round)
    {
        var id = Text(m, "id");
        if (id is null || !m.TryGetProperty("status", out var status)) return null;
        var utcTime = Text(status, "utcTime");
        if (utcTime is null) return null;

        var started = Flag(status, "started");
        var finished = Flag(status, "finished");
        var cancelled = Flag(status, "cancelled");

        string? scoreHome = null, scoreAway = null;
        var parts = Text(status, "scoreStr")?.Split(" - ");
        if (parts?.Length == 2) (scoreHome, scoreAway) = (parts[0].Trim(), parts[1].Trim());

        string? stage = null;
        if (cancelled)
        {
            // MapStatus only recognizes Postponed/Cancelled via Stage text, not the
            // IsScheduled/IsLive flags — leaving both flags null routes this through that
            // text-based fallback instead of being misread as a finished 0-0.
            stage = "Postponed";
        }
        else if (started && !finished && status.TryGetProperty("liveTime", out var liveTime))
        {
            // "HT", or the minute as "67’" (see MatchReading.ParseMinute).
            stage = Text(liveTime, "short");
        }
        else if (finished && status.TryGetProperty("reason", out var reason)
            && Text(reason, "short") is ("AET" or "Pen" or "AP") and var decided)
        {
            // A knockout match decided after 90 minutes; its scoreStr is the score after extra
            // time. Only these are passed on (see MatchReading.IsExtraTime), so a match that
            // ended in normal time reads exactly as it always has.
            stage = decided;
        }

        return new ScrapedMatchDto
        {
            Id = id,
            Home = TeamName(m, "home"),
            Away = TeamName(m, "away"),
            ScoreHome = scoreHome,
            ScoreAway = scoreAway,
            Stage = stage,
            IsScheduled = cancelled ? null : !started,
            IsLive = cancelled ? null : started && !finished,
            Time = utcTime,
            Round = round
        };
    }

    private static bool InWindow(ScrapedMatchDto m, DateTime now) =>
        DateTime.TryParse(m.Time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var kickoff)
        && kickoff >= now - Window && kickoff <= now + Window;

    private static string TeamName(JsonElement m, string side) =>
        m.TryGetProperty(side, out var team) ? Text(team, "name") ?? "" : "";

    private static bool Flag(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // A string or number property as text (FotMob sends ids and rounds as either).
    private static string? Text(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static int LeagueIdFrom(string? url) =>
        url is not null && LeagueIdPattern().Match(url) is { Success: true } match ? int.Parse(match.Groups[1].Value) : DefaultLeagueId;

    [GeneratedRegex(@"/leagues/(\d+)")]
    private static partial Regex LeagueIdPattern();
}
