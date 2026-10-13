using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Monitoring;
using OctopusPrediction.Api.Services.Scraping;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Tests;

// Scraping more than one competition without changing how La Liga is scraped.
public class CompetitionScrapingTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static TimeSpan LaLiga(bool inWindow, bool active, DateTime? nextWindow = null) =>
        ScrapeSchedule.NextDelay(TimeSpan.FromSeconds(60), inWindow, active, nextWindow, Now, ScrapeSchedule.LaLigaIdle);

    // La Liga's timing as it was before other competitions: 60s with a match in play, 5 minutes
    // in a window with none yet, 30 minutes (or until the next window) otherwise, never under 60s.
    [Fact]
    public void LaLigaTimingIsUnchanged()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), LaLiga(inWindow: true, active: true));
        Assert.Equal(5 * Minute, LaLiga(inWindow: true, active: false));
        Assert.Equal(30 * Minute, LaLiga(inWindow: false, active: false));
        Assert.Equal(30 * Minute, LaLiga(inWindow: false, active: false, nextWindow: Now + 2 * TimeSpan.FromHours(1)));
        Assert.Equal(10 * Minute, LaLiga(inWindow: false, active: false, nextWindow: Now + 10 * Minute));
        Assert.Equal(TimeSpan.FromSeconds(60), LaLiga(inWindow: false, active: false, nextWindow: Now + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void OtherCompetitions_LookForFixturesEveryThreeHours_ButFollowMatchesAsClosely()
    {
        TimeSpan Other(bool inWindow, bool active) =>
            ScrapeSchedule.NextDelay(TimeSpan.FromSeconds(60), inWindow, active, null, Now, ScrapeSchedule.OtherIdle);

        Assert.Equal(TimeSpan.FromHours(3), Other(inWindow: false, active: false));
        Assert.Equal(5 * Minute, Other(inWindow: true, active: false));
        Assert.Equal(TimeSpan.FromSeconds(60), Other(inWindow: true, active: true));
    }

    // Stands in for a real source: what matters is which copies CompetitionSources makes.
    public sealed class FakeSource(IOptions<LiveScraperSettings> options) : IMatchSource
    {
        public LiveScraperSettings Settings { get; } = options.Value;
        public string Name => "FotMob";
        public bool ProvidesRounds => true;
        public Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct) => Task.FromResult<ScrapedMatchDto[]>([]);
    }

    public sealed class OtherFakeSource(IOptions<LiveScraperSettings> options) : IMatchSource
    {
        public LiveScraperSettings Settings { get; } = options.Value;
        public string Name => "BBC Sport";
        public bool ProvidesRounds => false;
        public Task<ScrapedMatchDto[]> ScrapeAsync(IPage page, CancellationToken ct) => Task.FromResult<ScrapedMatchDto[]>([]);
    }

    private static (CompetitionSources Sources, IMatchSource[] LaLiga) Sources(Dictionary<string, string> uclPages)
    {
        var settings = Options.Create(new LiveScraperSettings
        {
            Sources = new() { ["FotMob"] = "https://www.fotmob.com/leagues/87/matches/laliga" },
            Competitions = new() { ["champions-league"] = new CompetitionScrapeSettings { Sources = uclPages } },
        });
        IMatchSource[] laLiga = [new FakeSource(settings), new OtherFakeSource(settings)];
        var services = new ServiceCollection().BuildServiceProvider();
        return (new CompetitionSources(services, laLiga, settings), laLiga);
    }

    [Fact]
    public void LaLiga_KeepsTheRegisteredSources()
    {
        var (sources, laLiga) = Sources(new() { ["FotMob"] = "https://www.fotmob.com/leagues/42/matches/champions-league" });

        Assert.Equal(laLiga, sources.For(Competitions.LaLiga));
        Assert.Null(((FakeSource)laLiga[0]).Settings.CompetitionId);
    }

    [Fact]
    public void OtherCompetitions_GetTheirOwnCopies_OnlyOfSourcesWithAPageForThem()
    {
        var (sources, laLiga) = Sources(new() { ["FotMob"] = "https://www.fotmob.com/leagues/42/matches/champions-league" });

        var ucl = Assert.Single(sources.For(Competitions.ChampionsLeague));
        var copy = Assert.IsType<FakeSource>(ucl);
        Assert.NotSame(laLiga[0], copy);
        Assert.Equal("champions-league", copy.Settings.CompetitionId);
        Assert.Equal("https://www.fotmob.com/leagues/42/matches/champions-league", copy.Settings.Sources["FotMob"]);
        Assert.Same(ucl, sources.For(Competitions.ChampionsLeague)[0]); // made once, then kept
        Assert.Equal(["FotMob"], sources.NamesFor(Competitions.ChampionsLeague));
    }

    [Fact]
    public void ACompetitionWithNoPagesHasNoSources()
    {
        var (sources, _) = Sources([]);
        Assert.Empty(sources.For("premier-league"));
        Assert.Empty(sources.NamesFor("premier-league"));
    }

    [Fact]
    public void SiteBudget_CountsTheLastHourOnly()
    {
        var budget = new SiteBudget();
        for (var i = 0; i < 3; i++) budget.RecordPageLoad("Flashscore", Now.AddMinutes(-70 + i * 30));

        Assert.Equal(2, budget.PagesInLastHour("flashscore", Now));
        Assert.True(budget.HasRoom("Flashscore", Now, 3));
        Assert.False(budget.HasRoom("Flashscore", Now, 2));
        Assert.True(budget.HasRoom("FotMob", Now, 1));
    }

    [Fact]
    public async Task Wait_SaysWhyItEnded()
    {
        var monitor = new ScraperMonitor();

        Assert.Equal(WakeReason.Elapsed, await monitor.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));

        monitor.RequestCompetitionPoll();
        Assert.Equal(WakeReason.CompetitionSwitchedOn, await monitor.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));

        monitor.RequestPollNow();
        Assert.Equal(WakeReason.PollNow, await monitor.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));

        // Each signal is used up by the wait it ended.
        Assert.Equal(WakeReason.Elapsed, await monitor.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }
}
