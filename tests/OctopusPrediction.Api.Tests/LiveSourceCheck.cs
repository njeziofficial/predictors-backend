using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Scraping;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Tests;

// A manual check, never part of a normal test run: reads a competition's pages once on every
// source configured for it (appsettings.json, LiveScoreScraper:Competitions) and reports what
// each parsed, and which prediction week each round would go in. Nothing is saved. Run it from
// a developer machine before switching a competition on, one page load per source. It uses the
// same Chrome headless shell as the scraper (downloaded on first use; an installed Chrome is
// usually newer than PuppeteerSharp speaks to):
//
//   LIVE_SOURCE_CHECK=1 \
//   LIVE_SOURCE_CHECK_OUT=<folder for the report> \
//   LIVE_SOURCE_CHECK_COMPETITION=champions-league \
//   dotnet test --filter LiveSourceCheck
public class LiveSourceCheck
{
    [Fact]
    public async Task ReadCompetitionPages()
    {
        if (Environment.GetEnvironmentVariable("LIVE_SOURCE_CHECK") != "1") return;
        var outDir = Environment.GetEnvironmentVariable("LIVE_SOURCE_CHECK_OUT") ?? ".";
        var competitionId = Environment.GetEnvironmentVariable("LIVE_SOURCE_CHECK_COMPETITION") ?? Competitions.ChampionsLeague;
        var only = Environment.GetEnvironmentVariable("LIVE_SOURCE_CHECK_SOURCE");

        var apiDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "OctopusPrediction.Api");
        var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(apiDir, "appsettings.json")).Build()
            .GetSection("LiveScoreScraper").Get<LiveScraperSettings>()!;
        var options = Options.Create(settings);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        // The registered (La Liga) sources, as Program.cs makes them.
        var laLiga = typeof(IMatchSource).Assembly.GetTypes()
            .Where(t => typeof(IMatchSource).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => (IMatchSource)ActivatorUtilities.CreateInstance(services, t, options))
            .ToList();
        var sources = new CompetitionSources(services, laLiga, options).For(competitionId)
            .Where(s => only is null || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var report = new StringBuilder($"{competitionId} — {DateTime.UtcNow:u}\n");
        var shell = await new BrowserFetcher(SupportedBrowser.ChromeHeadlessShell).DownloadAsync();
        await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = shell.GetExecutablePath(),
            HeadlessMode = HeadlessMode.Shell,
            Args = ["--no-first-run", "--mute-audio", "--disable-extensions", "--blink-settings=imagesEnabled=false"],
        });

        foreach (var source in sources)
        {
            report.AppendLine($"\n## {source.Name}");
            await using var page = await browser.NewPageAsync();
            // As the scraper presents itself (LiveScraperBackgroundService.ScrapeSourceAsync).
            await page.SetUserAgentAsync((await browser.GetUserAgentAsync()).Replace("HeadlessChrome", "Chrome"));
            await page.SetExtraHttpHeadersAsync(new Dictionary<string, string> { ["Accept-Language"] = "en-GB,en;q=0.9" });
            await page.SetViewportAsync(new ViewPortOptions { Width = 1366, Height = 768 });
            var statuses = new List<string>();
            page.Response += (_, e) =>
            {
                if (e.Response.Request.IsNavigationRequest && e.Response.Frame == page.MainFrame)
                    statuses.Add($"{(int)e.Response.Status} {e.Response.Url}");
            };

            try
            {
                var matches = await source.ScrapeAsync(page, CancellationToken.None);
                report.AppendLine($"pages: {string.Join(" | ", statuses)}");
                report.AppendLine($"title: {await page.GetTitleAsync()}");
                report.AppendLine($"{matches.Length} matches");
                foreach (var m in matches.Take(40))
                {
                    var kickoff = MatchReading.ParseKickoff(m.Time);
                    var round = CompetitionRounds.ChampionsLeague(m.Round, kickoff?.Utc);
                    report.AppendLine($"  {m.Home} v {m.Away} | {m.ScoreHome}-{m.ScoreAway} | stage={m.Stage} " +
                        $"| status={MatchReading.MapStatus(m)} | time={m.Time} | round={m.Round} -> {round?.Key ?? "(none)"} " +
                        $"| id={MatchReading.FixtureId("Champions League", m.Home, m.Away)}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"pages: {string.Join(" | ", statuses)}");
                report.AppendLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            }

            // Like the scraper: a pause between sites.
            await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.Next(10)));
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, $"live-source-check-{competitionId}.txt"), report.ToString());
    }
}
