using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// The league's own site. laliga.com's results page is built server-side, and carries its data
/// as JSON in the page (__NEXT_DATA__): the current matchday's number and its matches, each with
/// its status ("PreMatch", "FirstHalf", "HalfTime", "SecondHalf", "FullTime", "Postponed"...),
/// score and kickoff in UTC. That JSON is read, not the markup. The page gives no minute, and is
/// cached for up to 10 minutes, so it's a dependable fallback rather than the fastest scores.
///
/// Every poll reads the current matchday; the next one, for the fixtures still to come, is read
/// at most every <see cref="NextMatchdayRefresh"/>. Only LaLiga's competitions are on the site.
/// </summary>
internal sealed partial class LaLigaSource(ILogger<LaLigaSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "LaLiga";
    public bool ProvidesRounds => true;

    private const string DefaultUrl = "https://www.laliga.com/en-GB/laliga-easports/results";
    private readonly string _resultsUrl = options.Value.Sources.GetValueOrDefault("LaLiga", DefaultUrl).TrimEnd('/');

    private static readonly TimeSpan NextMatchdayRefresh = TimeSpan.FromMinutes(30);
    private ScrapedMatchDto[] _next = [];
    private int? _nextWeek;
    private DateTime _nextReadAt = DateTime.MinValue;

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        if (await ReadAsync(page, _resultsUrl) is not { } current) return [];

        var now = DateTime.UtcNow;
        var nextWeek = current.Week + 1;
        if (current.Season is { } season && current.Week is not null
            && (nextWeek != _nextWeek || now - _nextReadAt >= NextMatchdayRefresh))
        {
            // "2026" → ".../results/2026-27/gameweek-11"
            var url = $"{_resultsUrl}/{season}-{(season + 1) % 100:D2}/gameweek-{nextWeek}";
            if (await ReadAsync(page, url) is { } next)
            {
                (_next, _nextWeek, _nextReadAt) = (next.Matches, nextWeek, now);
            }
        }

        var matches = current.Matches.ToDictionary(m => m.Id!);
        if (_nextWeek == nextWeek)
            foreach (var m in _next) matches.TryAdd(m.Id!, m);
        return [.. matches.Values];
    }

    // Null when the page couldn't be read, or has no match data; the scraper records it.
    private async Task<Matchday?> ReadAsync(IPage page, string url)
    {
        var response = await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });
        if (response is null || !response.Ok)
        {
            logger.LogWarning("[LaLiga] {Url} answered {Status}", url, response is null ? "nothing" : (int)response.Status);
            return null;
        }
        var matchday = Parse(await response.TextAsync());
        if (matchday is null || matchday.Matches.Length == 0)
            logger.LogWarning("[LaLiga] No matches in {Url} — the page's embedded data may have changed", url);
        return matchday;
    }

    internal sealed record Matchday(int? Season, int? Week, ScrapedMatchDto[] Matches);

    /// <summary>The matchday in a results page's embedded data, or null if it has none.</summary>
    internal static Matchday? Parse(string html)
    {
        var data = NextData().Match(html);
        if (!data.Success) return null;
        using var doc = JsonDocument.Parse(data.Groups[1].Value);
        if (!doc.RootElement.TryGetProperty("props", out var props) || !props.TryGetProperty("pageProps", out var pp)) return null;

        var week = pp.TryGetProperty("gameweek", out var gw) ? Number(gw, "week") : null;
        var season = Number(pp, "season");
        var round = week is null ? null : $"Round {week}";

        var matches = new List<ScrapedMatchDto>();
        if (pp.TryGetProperty("matches", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                var home = TeamName(e, "home_team");
                var away = TeamName(e, "away_team");
                if (Text(e, "id") is not { } id || home is null || away is null) continue;
                var m = new ScrapedMatchDto
                {
                    Id = $"laliga-{id}",
                    Home = home,
                    Away = away,
                    Time = Kickoff(Text(e, "date")),
                    Round = round,
                };
                Apply(m, Text(e, "status"), Text(e, "home_score"), Text(e, "away_score"));
                matches.Add(m);
            }
        return new Matchday(season, week, [.. matches]);
    }

    /// <summary>
    /// The site's status as the readings every source gives. It has no minute, so a match in play
    /// is only Live or HT (or ET/Pen past 90 minutes, see MatchReading.IsExtraTime). A called-off
    /// match ("Postponed", "Abandoned", "Canceled") goes on as its words, with no flags.
    /// </summary>
    internal static void Apply(ScrapedMatchDto m, string? status, string? scoreHome, string? scoreAway)
    {
        switch (status)
        {
            case "PreMatch":
                (m.IsScheduled, m.IsLive) = (true, false);
                break;
            case "FirstHalf" or "SecondHalf" or "HalfTime"
                or "ExtraFirstHalf" or "ExtraHalfTime" or "ExtraSecondHalf" or "ShootOut":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, scoreHome, scoreAway);
                m.Stage = status switch
                {
                    "HalfTime" => "HT",
                    "ShootOut" => "Pen",
                    "ExtraFirstHalf" or "ExtraHalfTime" or "ExtraSecondHalf" => "ET",
                    _ => null,
                };
                break;
            case "FullTime" or "Penalties":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, false, scoreHome, scoreAway);
                m.Stage = status == "Penalties" ? "Pen" : null;
                break;
            default:
                // Called off (MatchReading.CalledOff), or a status not seen yet, which reads as
                // not started: nothing is scored on it.
                (m.Stage, m.ScoreHome, m.ScoreAway) = (status, scoreHome, scoreAway);
                break;
        }
    }

    // The club's everyday name ("Atlético de Madrid", "R. Racing Club"), not its legal one.
    private static string? TeamName(JsonElement e, string side) =>
        e.TryGetProperty(side, out var team) ? Text(team, "nickname") ?? Text(team, "name") : null;

    // The site gives "2026-10-24T19:00:00+00:00"; the readings use whole-second ISO UTC.
    private static string? Kickoff(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : null;

    private static int? Number(JsonElement e, string name) =>
        int.TryParse(Text(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    [GeneratedRegex(@"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextData();
}
