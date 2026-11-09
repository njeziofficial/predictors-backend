using System.Text.Json;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// OneFootball's competition pages embed their match cards in __NEXT_DATA__, grouped into lists
/// headed by the round ("Matchday 12"). Each card has its kickoff in UTC, its score and a
/// tracking block saying its state ("PreMatch", "FullTime") and minute. Upcoming and finished
/// matches are separate pages, so both are read and merged.
/// </summary>
internal sealed class OneFootballSource(ILogger<OneFootballSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "OneFootball";
    public bool ProvidesRounds => true;

    private const string DefaultBaseUrl = "https://onefootball.com/en/competition/laliga-10";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("OneFootball", DefaultBaseUrl).TrimEnd('/');

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var merged = new Dictionary<string, ScrapedMatchDto>();
        var first = true;
        foreach (var part in new[] { "fixtures", "results" })
        {
            if (!first) await Task.Delay(2_000, ct);
            first = false;
            if (await PageReading.HtmlAsync(page, $"{_baseUrl}/{part}", logger, Name) is not { } html) continue;
            foreach (var m in Parse(html)) merged[m.Id ?? $"{m.Home}|{m.Away}"] = m;
        }

        if (merged.Count == 0)
            logger.LogWarning("[OneFootball] Empty result — the page's embedded match data may have changed");
        return [.. merged.Values];
    }

    internal static ScrapedMatchDto[] Parse(string html)
    {
        var tag = html.IndexOf("id=\"__NEXT_DATA__\"", StringComparison.Ordinal);
        var end = tag < 0 ? -1 : html.IndexOf('>', tag);
        if (end < 0) return [];

        using var data = PageReading.JsonAfter(html[(end + 1)..], "");
        if (data is null) return [];
        var matches = new List<ScrapedMatchDto>();
        Collect(data.RootElement, matches);
        return [.. matches];
    }

    private static void Collect(JsonElement e, List<ScrapedMatchDto> into)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray()) Collect(item, into);
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("matchCards", out var cards) && cards.ValueKind == JsonValueKind.Array)
            {
                var round = e.TryGetProperty("sectionHeader", out var header) ? PageReading.Text(header, "subtitle") : null;
                foreach (var card in cards.EnumerateArray())
                    if (ToDto(card, round) is { } m) into.Add(m);
                return;
            }
            foreach (var property in e.EnumerateObject()) Collect(property.Value, into);
        }
    }

    private static ScrapedMatchDto? ToDto(JsonElement card, string? round)
    {
        if (!card.TryGetProperty("homeTeam", out var homeTeam) || !card.TryGetProperty("awayTeam", out var awayTeam)) return null;
        var (home, away) = (PageReading.Text(homeTeam, "name"), PageReading.Text(awayTeam, "name"));
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away)) return null;

        var m = new ScrapedMatchDto
        {
            Id = PageReading.Text(card, "matchId") is { } id ? $"of-{id}" : null,
            Home = home,
            Away = away,
            Time = PageReading.Text(card, "kickoff"),
            Round = string.IsNullOrWhiteSpace(round) ? null : round,
        };
        Apply(m, Tracked(card, "match_state"), PageReading.Text(card, "timePeriod"), Tracked(card, "match_minute"),
            PageReading.Text(homeTeam, "score"), PageReading.Text(awayTeam, "score"));
        return m;
    }

    // The card's state and minute are only in its tracking parameters.
    private static string? Tracked(JsonElement card, string parameter)
    {
        if (!card.TryGetProperty("trackingEvents", out var events) || events.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in events.EnumerateArray())
            if (e.TryGetProperty("typedServerParameter", out var parameters) && parameters.TryGetProperty(parameter, out var value)
                && PageReading.Text(value, "value") is { } text)
                return text;
        return null;
    }

    /// <summary>
    /// OneFootball's state as the readings every source gives. Only full time counts as finished;
    /// any other state with a score reads as in play, and without one as not started.
    /// </summary>
    internal static void Apply(ScrapedMatchDto m, string? state, string? period, string? minute, string? scoreHome, string? scoreAway)
    {
        var hasScore = PageReading.IsScore(scoreHome) && PageReading.IsScore(scoreAway);
        var calledOff = MatchReading.CalledOff(period) is not null ? period : MatchReading.CalledOff(state) is not null ? state : null;

        if (calledOff is not null)
        {
            m.Stage = calledOff;
            if (hasScore) (m.ScoreHome, m.ScoreAway) = (scoreHome, scoreAway);
        }
        else if (state == "FullTime" || string.Equals(period, "Full time", StringComparison.OrdinalIgnoreCase))
        {
            (m.IsScheduled, m.IsLive) = (false, false);
            if (hasScore) (m.ScoreHome, m.ScoreAway) = (scoreHome, scoreAway);
        }
        else if (state == "PreMatch" || !hasScore)
        {
            (m.IsScheduled, m.IsLive) = (true, false);
        }
        else
        {
            (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, scoreHome, scoreAway);
            m.Stage = IsHalfTime(period) ? "HT" : minute is not (null or "" or "0") ? $"{minute}'" : null;
        }
    }

    private static bool IsHalfTime(string? period) =>
        period?.Replace(" ", "").Equals("Halftime", StringComparison.OrdinalIgnoreCase) == true;
}
