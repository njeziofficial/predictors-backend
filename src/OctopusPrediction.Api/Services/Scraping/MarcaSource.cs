using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Marca's season calendar: one table per round (id="jornada12", caption "Jornada 12"), each row
/// showing the result, the date and time, or a status word ("Suspendido"). Kickoffs come from the
/// page's SportsEvent data, whose times end in "Z" but are really Madrid's clock.
///
/// A row with a result says nothing about whether the match is over, so a result only counts as
/// final once the match must have ended (FinishedAfter); until then it's read as in play.
/// </summary>
internal sealed partial class MarcaSource(ILogger<MarcaSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Marca";
    public bool ProvidesRounds => true;

    private const string DefaultUrl = "https://www.marca.com/futbol/primera-division/calendario.html";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("Marca", DefaultUrl);

    // Well past the longest league match (two halves, the break, a long stoppage).
    private static readonly TimeSpan FinishedAfter = TimeSpan.FromHours(3);

    private const string ExtractScript = """
        () => {
            const rows = [];
            document.querySelectorAll('table[id^="jornada"]').forEach(table => {
                const round = table.querySelector('caption')?.textContent?.trim() ?? null;
                table.querySelectorAll('tbody tr').forEach(tr => {
                    const cell = tr.querySelector('td.resultado');
                    const home = tr.querySelector('td.local span')?.textContent?.trim();
                    const away = tr.querySelector('td.visitante span')?.textContent?.trim();
                    if (!cell || !home || !away) return;
                    rows.push({
                        round, home, away,
                        score: cell.querySelector('.resultado-partido')?.textContent?.trim() ?? null,
                        status: cell.querySelector('.estado-partido')?.textContent?.trim() ?? null
                    });
                });
            });

            const kickoffs = {};
            document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
                try {
                    [JSON.parse(script.textContent)].flat().forEach(e => {
                        if (e['@type'] === 'SportsEvent' && e.homeTeam && e.awayTeam)
                            kickoffs[e.homeTeam.name + '|' + e.awayTeam.name] = e.startDate;
                    });
                } catch { }
            });
            rows.forEach(r => r.kickoff = kickoffs[r.home + '|' + r.away] ?? null);
            return JSON.stringify(rows);
        }
        """;

    internal sealed record MarcaRow(string? Round, string Home, string Away, string? Score, string? Status, string? Kickoff);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        await page.GoToAsync(_url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        var rows = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<MarcaRow[]>(json, ScrapingJson.Options) ?? [];
        var now = DateTime.UtcNow;
        var matches = rows.Select(r => ToDto(r, now)).OfType<ScrapedMatchDto>().ToArray();
        if (matches.Length == 0)
            logger.LogWarning("[Marca] Empty result — the calendar's markup may have changed");
        return matches;
    }

    /// <summary>The match as a reading; null when it has no readable kickoff or is outside the sync window.</summary>
    internal static ScrapedMatchDto? ToDto(MarcaRow row, DateTime now)
    {
        if (!DateTime.TryParseExact(row.Kickoff, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var kickoff = PageReading.MadridToUtc(local);
        if (!PageReading.InSyncWindow(kickoff, now)) return null;

        var m = new ScrapedMatchDto { Home = row.Home, Away = row.Away, Round = row.Round, Time = PageReading.Iso(kickoff) };
        var score = row.Score is null ? null : Score().Match(row.Score);

        if (PageReading.CalledOffInEnglish(row.Status) is { } calledOff)
        {
            m.Stage = calledOff;
            if (score is { Success: true }) (m.ScoreHome, m.ScoreAway) = (score.Groups[1].Value, score.Groups[2].Value);
        }
        else if (score is not { Success: true } || now < kickoff)
        {
            (m.IsScheduled, m.IsLive) = (true, false);
        }
        else
        {
            (m.ScoreHome, m.ScoreAway, m.IsScheduled) = (score.Groups[1].Value, score.Groups[2].Value, false);
            m.IsLive = row.Status is not null || now < kickoff + FinishedAfter;
            if (m.IsLive == true && PageReading.IsHalfTimeInSpanish(row.Status)) m.Stage = "HT";
        }
        return m;
    }

    [GeneratedRegex(@"^(\d+)\s*-\s*(\d+)$")]
    private static partial Regex Score();
}
