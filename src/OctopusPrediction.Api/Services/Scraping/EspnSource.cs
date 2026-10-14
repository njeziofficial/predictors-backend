using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Fallback source, read from ESPN's public JSON scoreboard rather than its schedule page. The
/// schedule page only listed upcoming matches, so it never gave a result; the scoreboard gives
/// each match's state ("pre", "in", "post"), its status name (STATUS_HALFTIME,
/// STATUS_FINAL_AET, STATUS_ABANDONED, STATUS_POSTPONED...), the clock, the score and the
/// kickoff in UTC. It answers one day at a time, so a poll asks for today and tomorrow, and for
/// yesterday too just after midnight while late matches finish. No rounds, so it only updates
/// fixtures another source created.
///
/// The configured URL stays the league's schedule page on espn.com; the scoreboard is asked for
/// the league code in it ("esp.1", "uefa.champions").
/// </summary>
internal sealed partial class EspnSource(ILogger<EspnSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "ESPN";
    public bool ProvidesRounds => false;

    private const string DefaultUrl = "https://www.espn.com/soccer/schedule/_/league/esp.1";
    private readonly string _league = LeagueCode(options.Value.Sources.GetValueOrDefault("Espn", DefaultUrl));

    // How long after midnight (UTC) yesterday's matches are still asked for.
    private static readonly TimeSpan LateMatchGrace = TimeSpan.FromHours(3);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var days = new List<DateTime> { now.Date, now.Date.AddDays(1) };
        if (now.TimeOfDay < LateMatchGrace) days.Insert(0, now.Date.AddDays(-1));

        var matches = new Dictionary<string, ScrapedMatchDto>();
        foreach (var day in days)
        {
            var url = $"https://site.api.espn.com/apis/site/v2/sports/soccer/{_league}/scoreboard?dates={day:yyyyMMdd}";
            // Opened as a page, like FotMobSource, so the scraper's refusal check sees its status.
            var response = await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = 30_000
            });
            if (response is null || !response.Ok)
            {
                logger.LogWarning("[ESPN] {Url} answered {Status}", url, response is null ? "nothing" : (int)response.Status);
                continue;
            }
            foreach (var m in Parse(await response.TextAsync()))
                matches[m.Id ?? $"{m.Home}|{m.Away}"] = m;
        }

        // Most days there's no La Liga match at all, so an empty answer isn't a fault.
        return [.. matches.Values];
    }

    /// <summary>The league code in a schedule page URL ("…/league/esp.1"), or the text itself if it's already one.</summary>
    internal static string LeagueCode(string configured)
    {
        var match = LeagueInUrl().Match(configured);
        if (match.Success) return match.Groups[1].Value;
        return configured.Contains('/') ? "esp.1" : configured;
    }

    internal static ScrapedMatchDto[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return [];

        var matches = new List<ScrapedMatchDto>();
        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("competitions", out var competitions) || competitions.GetArrayLength() == 0) continue;
            var competition = competitions[0];
            if (!competition.TryGetProperty("competitors", out var competitors)) continue;
            JsonElement? home = null, away = null;
            foreach (var c in competitors.EnumerateArray())
            {
                if (Text(c, "homeAway") == "home") home = c;
                else if (Text(c, "homeAway") == "away") away = c;
            }
            if (home is not { } h || away is not { } a) continue;
            var homeName = TeamName(h);
            var awayName = TeamName(a);
            if (homeName is null || awayName is null) continue;

            var status = e.TryGetProperty("status", out var s) ? s
                : competition.TryGetProperty("status", out var cs) ? cs : default;
            var type = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("type", out var t) ? t : default;

            var m = new ScrapedMatchDto
            {
                Id = Text(e, "id") is { } id ? $"espn-{id}" : null,
                Home = homeName,
                Away = awayName,
                Time = Kickoff(Text(e, "date")),
            };
            Apply(m, Text(type, "name"), Text(type, "state"), Text(status, "displayClock"), Text(h, "score"), Text(a, "score"));
            matches.Add(m);
        }
        return [.. matches];
    }

    /// <summary>
    /// ESPN's status as the readings every source gives. A called-off match goes on as words
    /// (MatchReading.CalledOff) whatever its state: an abandoned one is "post" with the score at
    /// the time, which would otherwise read as finished.
    /// </summary>
    internal static void Apply(ScrapedMatchDto m, string? name, string? state, string? clock, string? scoreHome, string? scoreAway)
    {
        var calledOff = name switch
        {
            "STATUS_ABANDONED" => "Abandoned",
            "STATUS_POSTPONED" => "Postponed",
            "STATUS_SUSPENDED" => "Suspended",
            "STATUS_CANCELED" or "STATUS_CANCELLED" => "Canceled",
            _ => null,
        };
        if (calledOff is not null)
        {
            (m.Stage, m.ScoreHome, m.ScoreAway) = (calledOff, scoreHome, scoreAway);
            return;
        }

        switch (state)
        {
            case "pre":
                (m.IsScheduled, m.IsLive) = (true, false);
                break;
            case "in":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, scoreHome, scoreAway);
                m.Stage = name == "STATUS_HALFTIME" ? "HT" : clock;
                break;
            case "post":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, false, scoreHome, scoreAway);
                // A knockout match decided after 90 minutes (see MatchReading.IsExtraTime).
                m.Stage = name switch { "STATUS_FINAL_AET" => "AET", "STATUS_FINAL_PEN" => "Pen", _ => null };
                break;
            default:
                // An unknown state reads as not started: nothing is scored on it.
                m.Stage = name;
                break;
        }
    }

    // ESPN gives "2026-10-17T16:30Z"; the readings use whole-second ISO UTC.
    private static string? Kickoff(string? date) =>
        DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : null;

    private static string? TeamName(JsonElement competitor) =>
        competitor.TryGetProperty("team", out var team) ? Text(team, "displayName") ?? Text(team, "name") : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    [GeneratedRegex(@"/league/([a-z0-9._]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LeagueInUrl();
}
