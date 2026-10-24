using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Reads 365Scores' public JSON API (what its pages load). One request gives the league's last,
/// current and next rounds: each match with its round number, its status group (2 scheduled,
/// 3 live, 4 ended) and words ("2nd Half", "Halftime", "Postponed"), the minute ("65'"), the
/// score and the kickoff in UTC. So, like FotMob, it can create fixtures as well as score them.
///
/// The configured URL is the league's page on the site ("…/football/league/laliga-11"); only its
/// competition id (the trailing number) is used. 11 = LaLiga.
/// </summary>
internal sealed partial class Scores365Source(ILogger<Scores365Source> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "365Scores";
    public bool ProvidesRounds => true;

    private const int DefaultCompetitionId = 11;
    private readonly int _competitionId = CompetitionId(options.Value.Sources.GetValueOrDefault("365Scores"));

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var url = "https://webws.365scores.com/web/games/current/?appTypeId=5&langId=1&timezoneName=UTC"
            + $"&userCountryId=1&competitions={_competitionId}";
        // Opened as a page, like FotMobSource, so the request looks like the browser's own and
        // the scraper's refusal check sees its status.
        var response = await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });
        if (response is null || !response.Ok)
        {
            logger.LogWarning("[365Scores] {Url} answered {Status}", url, response is null ? "nothing" : (int)response.Status);
            return [];
        }

        var matches = Parse(await response.TextAsync(), _competitionId);
        if (matches.Length == 0)
            logger.LogWarning("[365Scores] Empty result — the API's shape may have changed");
        return matches;
    }

    /// <summary>The competition id in a league page URL ("…/laliga-11"), or LaLiga's.</summary>
    internal static int CompetitionId(string? configured) =>
        configured is not null && IdInUrl().Match(configured) is { Success: true } match ? int.Parse(match.Groups[1].Value) : DefaultCompetitionId;

    internal static ScrapedMatchDto[] Parse(string json, int competitionId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return [];

        var matches = new List<ScrapedMatchDto>();
        foreach (var g in games.EnumerateArray())
        {
            if (Text(g, "competitionId") is { } c && c != competitionId.ToString(CultureInfo.InvariantCulture)) continue;
            var home = g.TryGetProperty("homeCompetitor", out var h) ? Text(h, "name") : null;
            var away = g.TryGetProperty("awayCompetitor", out var a) ? Text(a, "name") : null;
            if (home is null || away is null) continue;

            var m = new ScrapedMatchDto
            {
                Id = Text(g, "id") is { } id ? $"365-{id}" : null,
                Home = home,
                Away = away,
                Time = Kickoff(Text(g, "startTime")),
                Round = Round(Text(g, "roundName"), Text(g, "roundNum")),
            };
            Apply(m, Text(g, "statusGroup"), Text(g, "statusText"), Text(g, "gameTimeDisplay"), Text(h, "score"), Text(a, "score"));
            matches.Add(m);
        }
        return [.. matches];
    }

    /// <summary>
    /// 365Scores' status as the readings every source gives. A called-off match goes on as words
    /// (MatchReading.CalledOff) whatever its group, as ESPN's does.
    /// </summary>
    internal static void Apply(ScrapedMatchDto m, string? group, string? status, string? minute, string? scoreHome, string? scoreAway)
    {
        // A match not yet started has the score -1.
        string? Score(string? s) => s is null || s.StartsWith('-') ? null : s;

        if (MatchReading.CalledOff(status) is not null)
        {
            (m.Stage, m.ScoreHome, m.ScoreAway) = (status, Score(scoreHome), Score(scoreAway));
            return;
        }

        switch (group)
        {
            case "2":
                (m.IsScheduled, m.IsLive) = (true, false);
                break;
            case "3":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, Score(scoreHome), Score(scoreAway));
                m.Stage = IsHalfTime(status) ? "HT" : string.IsNullOrEmpty(minute) ? null : minute;
                break;
            case "4":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, false, Score(scoreHome), Score(scoreAway));
                // A knockout match decided after 90 minutes (see MatchReading.IsExtraTime).
                var words = status?.ToLowerInvariant() ?? "";
                m.Stage = words.Contains("pen") ? "Pen"
                    : words == "aet" || words.Contains("after et") || words.Contains("extra") ? "AET"
                    : null;
                break;
            default:
                // An unknown group reads as not started: nothing is scored on it.
                m.Stage = status;
                break;
        }
    }

    // "Halftime", "Half Time", "HT".
    private static bool IsHalfTime(string? status) =>
        status is not null && (status.Replace(" ", "").Equals("Halftime", StringComparison.OrdinalIgnoreCase) || status == "HT");

    // A league round is named "Round" with its number apart ("Round 10"); a knockout round has
    // its own name ("Quarter Finals"), which CompetitionRounds understands.
    private static string? Round(string? name, string? number) =>
        string.IsNullOrWhiteSpace(name) || name.Equals("Round", StringComparison.OrdinalIgnoreCase)
            ? number is null ? null : $"Round {number}"
            : name;

    // 365Scores gives "2026-10-24T19:00:00+00:00"; the readings use whole-second ISO UTC.
    private static string? Kickoff(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    [GeneratedRegex(@"-(\d+)/?(?:[?#].*)?$")]
    private static partial Regex IdInUrl();
}
