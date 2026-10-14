using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Fallback source. livescore.com's pages are built from its own public JSON API, which is read
/// directly: the league's whole season in one response, each match with its status text ("NS",
/// "45'", "HT", "FT", "AET", "Postp.", "Aband.", "Canc."), its score, and its kickoff as
/// yyyyMMddHHmmss in UTC (the trailing "/0" in the URL is the time-zone offset). Reading the
/// pages instead broke whenever livescore.com rebuilt them; the last rebuild left it finding
/// nothing at all.
///
/// The configured URL stays the league's page on the site ("…/football/spain/laliga"); the API
/// address is made from its country and league ("spain/laliga").
/// </summary>
internal sealed partial class LivescoreSource(ILogger<LivescoreSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Livescore";
    public bool ProvidesRounds => false;

    private const string DefaultBaseUrl = "https://www.livescore.com/en/football/spain/laliga";
    private readonly string _apiUrl = ApiUrl(options.Value.Sources.GetValueOrDefault("Livescore", DefaultBaseUrl));

    // Matches further away than this aren't passed on: the season list has every match, and
    // nothing changes for one weeks away.
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        // Opened as a page, like FotMobSource, so the request looks like the browser's own and
        // the scraper's refusal check sees its status.
        var response = await page.GoToAsync(_apiUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });
        if (response is null || !response.Ok)
        {
            logger.LogWarning("[Livescore] {Url} answered {Status}", _apiUrl, response is null ? "nothing" : (int)response.Status);
            return [];
        }

        var matches = Parse(await response.TextAsync(), DateTime.UtcNow);
        if (matches.Length == 0)
            logger.LogWarning("[Livescore] Empty result — the API's shape may have changed");
        return matches;
    }

    /// <summary>The API address for a league page on the site, or the URL itself if it's already one.</summary>
    internal static string ApiUrl(string configured)
    {
        if (configured.Contains("public-api.livescore.com", StringComparison.OrdinalIgnoreCase)) return configured;
        var league = LeaguePath().Match(configured);
        var path = league.Success ? league.Groups[1].Value.Trim('/') : "spain/laliga";
        return $"https://prod-public-api.livescore.com/v1/api/app/stage/soccer/{path}/0?MD=1";
    }

    /// <summary>The matches within two weeks of now, from the API's answer.</summary>
    internal static ScrapedMatchDto[] Parse(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Stages", out var stages) || stages.ValueKind != JsonValueKind.Array) return [];

        var matches = new List<ScrapedMatchDto>();
        foreach (var stage in stages.EnumerateArray())
        {
            if (!stage.TryGetProperty("Events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in events.EnumerateArray())
            {
                var home = TeamName(e, "T1");
                var away = TeamName(e, "T2");
                if (home is null || away is null) continue;
                if (!DateTime.TryParseExact(Text(e, "Esd"), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var kickoff)) continue;
                if (kickoff < now - Window || kickoff > now + Window) continue;

                var status = Text(e, "Eps");
                var started = status is not (null or "NS");
                matches.Add(new ScrapedMatchDto
                {
                    Id = Text(e, "Eid") is { } id ? $"ls-{id}" : null,
                    Home = home,
                    Away = away,
                    ScoreHome = started ? Text(e, "Tr1") : null,
                    ScoreAway = started ? Text(e, "Tr2") : null,
                    // The status text is read as every text-only source's is (MatchReading.MapStatus).
                    Stage = status,
                    Time = kickoff.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                });
            }
        }
        return [.. matches];
    }

    private static string? TeamName(JsonElement e, string side) =>
        e.TryGetProperty(side, out var teams) && teams.ValueKind == JsonValueKind.Array && teams.GetArrayLength() > 0
            ? Text(teams[0], "Nm")
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    // "…/football/spain/laliga" or "…/football/spain/laliga/fixtures/": the country and league.
    [GeneratedRegex(@"/football/([a-z0-9-]+/[a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LeaguePath();
}
