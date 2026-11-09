using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Mundo Deportivo's calendar shows the current month, one panel per match: the score, and a line
/// saying "Fin" when it's over or the kickoff when it's to come (with "match--future" on the
/// panel). True UTC kickoffs are in the page's SportsEvent data. The round appears only in links
/// to match reports, so this source updates fixtures but doesn't create them.
/// </summary>
internal sealed partial class MundoDeportivoSource(ILogger<MundoDeportivoSource> logger, IOptions<LiveScraperSettings> options) : IMatchSource
{
    public string Name => "Mundo Deportivo";
    public bool ProvidesRounds => false;

    private const string DefaultUrl = "https://www.mundodeportivo.com/resultados/futbol/laliga/calendario";
    private readonly string _url = options.Value.Sources.GetValueOrDefault("MundoDeportivo", DefaultUrl);

    private const string ExtractScript = """
        () => {
            const kickoffs = {};
            document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
                try {
                    [JSON.parse(script.textContent)].flat().forEach(e => {
                        if (e['@type'] === 'SportsEvent' && e.performer?.length === 2)
                            kickoffs[e.performer[0].name + '|' + e.performer[1].name] = e.startDate;
                    });
                } catch { }
            });

            const panels = [];
            document.querySelectorAll('.panel.match').forEach(panel => {
                const names = [...panel.querySelectorAll('.match__name')].map(n => n.textContent.trim());
                const scores = [...panel.querySelectorAll('.match__score')].map(n => n.textContent.trim());
                if (names.length !== 2 || scores.length !== 2) return;
                panels.push({
                    home: names[0],
                    away: names[1],
                    scoreHome: scores[0],
                    scoreAway: scores[1],
                    info: panel.querySelector('.match__info-item')?.textContent?.trim() ?? null,
                    future: panel.classList.contains('match--future'),
                    kickoff: kickoffs[names[0] + '|' + names[1]] ?? null
                });
            });
            return JSON.stringify(panels);
        }
        """;

    internal sealed record MundoPanel(string Home, string Away, string? ScoreHome, string? ScoreAway, string? Info, bool Future, string? Kickoff);

    public async Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct)
    {
        await page.GoToAsync(_url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });

        var json = await page.EvaluateFunctionAsync<string>(ExtractScript);
        var panels = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<MundoPanel[]>(json, ScrapingJson.Options) ?? [];
        if (panels.Length == 0)
            logger.LogWarning("[Mundo Deportivo] Empty result — the calendar's markup may have changed");
        return [.. panels.Select(ToDto)];
    }

    /// <summary>
    /// The panel as a reading. Only "Fin" with a score is finished. Anything else with a score,
    /// which is how a match in play has to look, reads as in play and scores nothing.
    /// </summary>
    internal static ScrapedMatchDto ToDto(MundoPanel p)
    {
        var m = new ScrapedMatchDto { Home = p.Home, Away = p.Away, Time = p.Kickoff };
        var hasScore = PageReading.IsScore(p.ScoreHome) && PageReading.IsScore(p.ScoreAway);

        if (PageReading.CalledOffInEnglish(p.Info) is { } calledOff)
        {
            m.Stage = calledOff;
            if (hasScore) (m.ScoreHome, m.ScoreAway) = (p.ScoreHome, p.ScoreAway);
        }
        else if (p.Future || !hasScore)
        {
            (m.IsScheduled, m.IsLive) = (true, false);
        }
        else
        {
            (m.ScoreHome, m.ScoreAway, m.IsScheduled) = (p.ScoreHome, p.ScoreAway, false);
            m.IsLive = !IsFinished(p.Info);
            if (m.IsLive == true)
                m.Stage = PageReading.IsHalfTimeInSpanish(p.Info) ? "HT" : p.Info is not null && Minute().IsMatch(p.Info) ? p.Info.TrimEnd('\'') + "'" : null;
        }
        return m;
    }

    private static bool IsFinished(string? info) =>
        info?.Trim().ToLowerInvariant() is "fin" or "final" or "finalizado";

    [GeneratedRegex(@"^\d{1,3}(\+\d{1,2})?'?$")]
    private static partial Regex Minute();
}
