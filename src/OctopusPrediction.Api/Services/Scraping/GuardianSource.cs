using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// The Guardian's league pages embed their matches in the props of a FootballMatchesPageWrapper
/// element: days, each with competitions, each with matches of a kind ("Result", "Live",
/// "Fixture"). This reads /live and /results; a match on both counts as its result. It has no
/// round numbers, so it updates fixtures but doesn't create them.
/// </summary>
internal sealed partial class GuardianSource(ILogger<GuardianSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Guardian";
    public bool ProvidesRounds => false;

    private const string DefaultBaseUrl = "https://www.theguardian.com/football/laligafootball";
    private readonly string _baseUrl = options.Value.Sources.GetValueOrDefault("Guardian", DefaultBaseUrl).TrimEnd('/');

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        var tag = new Uri(_baseUrl).AbsolutePath.Trim('/');
        var merged = new Dictionary<string, ScrapedMatchDto>();
        var first = true;
        foreach (var part in new[] { "live", "results" })
        {
            if (!first) await Task.Delay(2_000, ct);
            first = false;
            if (await PageReading.HtmlAsync(page, $"{_baseUrl}/{part}", logger, Name) is not { } html) continue;
            foreach (var m in Parse(html, tag)) merged[m.Id ?? $"{m.Home}|{m.Away}"] = m;
        }

        if (merged.Count == 0)
            logger.LogWarning("[Guardian] Empty result — the page's embedded match data may have changed");
        return [.. merged.Values];
    }

    /// <summary>The page's matches in the competition with this tag ("football/laligafootball").</summary>
    internal static ScrapedMatchDto[] Parse(string html, string tag)
    {
        if (Props().Match(html) is not { Success: true } props) return [];
        using var data = JsonDocument.Parse(WebUtility.HtmlDecode(props.Groups[1].Value));
        if (!data.RootElement.TryGetProperty("initialDays", out var days) || days.ValueKind != JsonValueKind.Array) return [];

        var matches = new List<ScrapedMatchDto>();
        foreach (var day in days.EnumerateArray())
        foreach (var competition in day.GetProperty("competitions").EnumerateArray())
        {
            if (PageReading.Text(competition, "tag") != tag) continue;
            foreach (var match in competition.GetProperty("matches").EnumerateArray())
                if (ToDto(match) is { } m) matches.Add(m);
        }
        return [.. matches];
    }

    private static ScrapedMatchDto? ToDto(JsonElement match)
    {
        var home = match.GetProperty("homeTeam");
        var away = match.GetProperty("awayTeam");
        var m = new ScrapedMatchDto
        {
            Id = PageReading.Text(match, "paId") is { } id ? $"gu-{id}" : null,
            Home = PageReading.Text(home, "name") ?? "",
            Away = PageReading.Text(away, "name") ?? "",
            Time = PageReading.Text(match, "dateTimeISOString"),
        };
        return Apply(m, PageReading.Text(match, "kind"), PageReading.Text(match, "status"), PageReading.Text(match, "comment"),
            PageReading.Text(home, "score"), PageReading.Text(away, "score")) ? m : null;
    }

    /// <summary>The kind as the readings every source gives; false for a kind this doesn't know, which is left out.</summary>
    internal static bool Apply(ScrapedMatchDto m, string? kind, string? status, string? comment, string? scoreHome, string? scoreAway)
    {
        var calledOff = MatchReading.CalledOff(status) is not null ? status : MatchReading.CalledOff(comment) is not null ? comment : null;
        if (kind is "Result" or "Live" && calledOff is not null)
        {
            (m.Stage, m.ScoreHome, m.ScoreAway) = (calledOff, scoreHome, scoreAway);
            return true;
        }

        switch (kind)
        {
            case "Fixture":
                (m.IsScheduled, m.IsLive) = (true, false);
                return true;
            case "Result":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, false, scoreHome, scoreAway);
                return true;
            case "Live":
                (m.IsScheduled, m.IsLive, m.ScoreHome, m.ScoreAway) = (false, true, scoreHome, scoreAway);
                m.Stage = MatchReading.IsHalfTime(status) ? "HT" : null;
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"<gu-island\b[^>]*\bname=""FootballMatchesPageWrapper""[^>]*\bprops=""([^""]*)""")]
    private static partial Regex Props();
}
