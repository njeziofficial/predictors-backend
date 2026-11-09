using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// AS's season calendar. The page embeds every match as data ("const sportEvents = [...]"): the
/// kickoff as a Unix time, the state (SPORT_EVENT_STATE_*), period, minute and goals, with teams
/// by id in "const teams = {...}". The round isn't on the match: the calendar's day links say it
/// (data-day="07-11-2026" data-title="Jornada 12"), so a match gets its day's round.
/// </summary>
internal sealed partial class AsSource(ILogger<AsSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "AS";
    public bool ProvidesRounds => true;

    private const string DefaultUrl = "https://as.com/resultados/futbol/primera/calendario/";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("AS", DefaultUrl);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var html = await PageReading.HtmlAsync(page, _url, logger, Name);
        if (html is null) return [];

        var matches = Parse(html, DateTime.UtcNow);
        if (matches.Length == 0)
            logger.LogWarning("[AS] Empty result — the page's embedded match data may have changed");
        return matches;
    }

    internal static ScrapedMatchDto[] Parse(string html, DateTime now)
    {
        using var events = PageReading.JsonAfter(html, "const sportEvents =");
        using var teams = PageReading.JsonAfter(html, "const teams =");
        if (events is null || teams is null || events.RootElement.ValueKind != JsonValueKind.Array) return [];

        var rounds = new Dictionary<string, string>();
        foreach (Match day in CalendarDay().Matches(html)) rounds[day.Groups[1].Value] = day.Groups[2].Value;

        string? TeamName(string? id) =>
            id is not null && teams.RootElement.TryGetProperty(id, out var team) ? PageReading.Text(team, "name") : null;

        var matches = new List<ScrapedMatchDto>();
        foreach (var e in events.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("date", out var date) || !date.TryGetInt64(out var seconds)) continue;
            var kickoff = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            if (!PageReading.InSyncWindow(kickoff, now)) continue;

            var local = e.GetProperty("local");
            var visitor = e.GetProperty("visitor");
            var (home, away) = (TeamName(PageReading.Text(local, "team")), TeamName(PageReading.Text(visitor, "team")));
            if (home is null || away is null) continue;

            var m = new ScrapedMatchDto
            {
                Home = home,
                Away = away,
                Time = PageReading.Iso(kickoff),
                Round = PageReading.Text(e, "day") is { } day && rounds.TryGetValue(day, out var round) ? round : null,
            };
            var moment = e.GetProperty("moment");
            Apply(m, PageReading.Text(e.GetProperty("state"), "id"), PageReading.Text(moment, "period"), PageReading.Text(moment, "minute"),
                Goals(local), Goals(visitor));
            matches.Add(m);
        }
        return [.. matches];
    }

    private static string? Goals(JsonElement side) =>
        side.TryGetProperty("score", out var score) ? PageReading.Text(score, "goals") : null;

    /// <summary>
    /// AS's state as the readings every source gives. Only the finished state counts as finished,
    /// even when a live match's period already says so. A state not listed here reads as not
    /// started, so nothing is scored on it.
    /// </summary>
    internal static void Apply(ScrapedMatchDto m, string? state, string? period, string? minute, string? goalsHome, string? goalsAway)
    {
        switch (state)
        {
            case "SPORT_EVENT_STATE_PRE_SPORT_EVENT":
                (m.IsScheduled, m.IsLive) = (true, false);
                break;
            case "SPORT_EVENT_STATE_FINISHED":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, false, goalsHome, goalsAway);
                break;
            case "SPORT_EVENT_STATE_LIVE":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, goalsHome, goalsAway);
                m.Stage = IsHalfTime(period) ? "HT" : minute is not (null or "0") ? $"{minute}'" : null;
                break;
            default:
                (m.Stage, m.ScoreHome, m.ScoreAway) = (CalledOff(state) ?? state, goalsHome, goalsAway);
                break;
        }
    }

    private static bool IsHalfTime(string? period) =>
        period is not null && (period.Contains("HALF_TIME", StringComparison.Ordinal) || PageReading.IsHalfTimeInSpanish(period));

    private static string? CalledOff(string? state) => state switch
    {
        "SPORT_EVENT_STATE_ABANDONED" => "Abandoned",
        "SPORT_EVENT_STATE_SUSPENDED" => "Suspended",
        "SPORT_EVENT_STATE_POSTPONED" => "Postponed",
        "SPORT_EVENT_STATE_CANCELLED" or "SPORT_EVENT_STATE_CANCELED" => "Cancelled",
        _ => null,
    };

    [GeneratedRegex(@"data-day=""([\d-]+)""\s+data-title=""([^""]+)""")]
    private static partial Regex CalendarDay();
}
