using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace OctopusPrediction.Api.Services.Scraping;

// Each competition's own copy of the sources. La Liga's are the ones registered at startup,
// exactly as before. Another competition gets fresh copies, made the first time it's scraped
// (so never while it's switched off), each pointed at that competition's pages
// (LiveScraperSettings.Competitions) and keeping its own memory: rounds looked up, season lists.
public sealed class CompetitionSources(
    IServiceProvider services, IEnumerable<IMatchSource> laLiga, IOptions<LiveScraperSettings> options)
{
    private readonly IReadOnlyList<IMatchSource> _laLiga = [.. laLiga];
    private readonly ConcurrentDictionary<string, IReadOnlyList<IMatchSource>> _others = new();

    /// <summary>The sources that can read this competition: for another competition, only those with a page set for it.</summary>
    public IReadOnlyList<IMatchSource> For(string competitionId) =>
        competitionId == Competitions.LaLiga ? _laLiga : _others.GetOrAdd(competitionId, Create);

    /// <summary>The names of the sources For would give, without making them.</summary>
    public IReadOnlyList<string> NamesFor(string competitionId)
    {
        if (competitionId == Competitions.LaLiga) return [.. _laLiga.Select(s => s.Name)];
        var pages = options.Value.Competitions.GetValueOrDefault(competitionId)?.Sources;
        if (pages is null) return [];
        var keys = new HashSet<string>(pages.Keys, StringComparer.OrdinalIgnoreCase);
        return [.. _laLiga.Where(s => keys.Contains(ConfigKey(s.Name))).Select(s => s.Name)];
    }

    // Sources' config keys are their names without spaces ("BbcSport" for "BBC Sport").
    public static string ConfigKey(string sourceName) => sourceName.Replace(" ", "");

    private IReadOnlyList<IMatchSource> Create(string competitionId)
    {
        var defaults = options.Value;
        if (defaults.Competitions.GetValueOrDefault(competitionId) is not { } config) return [];

        var pages = new Dictionary<string, string>(config.Sources, StringComparer.OrdinalIgnoreCase);
        var settings = Options.Create(new LiveScraperSettings
        {
            Competition = defaults.Competition,
            PollIntervalSeconds = defaults.PollIntervalSeconds,
            Enabled = defaults.Enabled,
            ChromeExecutablePath = defaults.ChromeExecutablePath,
            // The sources look their URL up by exact key, so the keys are given as they expect.
            Sources = _laLiga
                .Where(s => pages.ContainsKey(ConfigKey(s.Name)))
                .ToDictionary(s => SourceKey(s), s => pages[ConfigKey(s.Name)]),
            CompetitionId = competitionId,
        });

        return [.. _laLiga
            .Where(s => pages.ContainsKey(ConfigKey(s.Name)))
            .Select(s => (IMatchSource)ActivatorUtilities.CreateInstance(services, s.GetType(), settings))];
    }

    // The key each source reads its URL from (see each source's constructor).
    private static string SourceKey(IMatchSource source) => source.Name switch
    {
        "BBC Sport" => "BbcSport",
        "ESPN" => "Espn",
        "Fox Sports" => "FoxSports",
        var name => name,
    };
}
