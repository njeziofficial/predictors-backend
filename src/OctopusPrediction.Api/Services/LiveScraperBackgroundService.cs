using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Scraping;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services;

internal class LiveScraperBackgroundService(
    IOptions<LiveScraperSettings> options,
    IEnumerable<IMatchSource> sources,
    IServiceScopeFactory scopeFactory,
    ILogger<LiveScraperBackgroundService> logger)
    : BackgroundService
{
    private readonly LiveScraperSettings _defaults = options.Value;
    private readonly IReadOnlyList<IMatchSource> _sources = [.. sources];
    private bool _chromiumReady;

    private static readonly LaunchOptions _browserOpts = new()
    {
        Headless = true,
        Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_sources.Count == 0)
        {
            logger.LogWarning("[LiveScraper] No match sources registered — nothing to scrape");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            ScraperSettings settings;
            try
            {
                settings = await LoadSettingsAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveScraper] Failed to load scraper settings — retrying");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                continue;
            }

            if (!settings.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            IBrowser? browser = null;
            try
            {
                // Inside the try: a failed Chromium download (e.g. a network blip at startup) must
                // only skip this attempt. Escaping ExecuteAsync would stop the whole host.
                await EnsureChromiumReadyAsync();
                logger.LogInformation("[LiveScraper] Polling {Source} every {Interval}s",
                    settings.SourceName, settings.PollIntervalSeconds);

                browser = await Puppeteer.LaunchAsync(_browserOpts);

                while (!ct.IsCancellationRequested)
                {
                    settings = await LoadSettingsAsync(ct);
                    if (!settings.Enabled)
                    {
                        logger.LogInformation("[LiveScraper] Disabled from admin settings — pausing");
                        break;
                    }

                    try
                    {
                        await ScrapeAndSyncAsync(browser, settings.Competition, settings.SourceName, ct);
                    }
                    catch (PuppeteerException pex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(pex, "[LiveScraper] Browser error — restarting");
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogError(ex, "[LiveScraper] Scrape cycle failed");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveScraper] Browser download or launch failed — retrying");
            }
            finally
            {
                if (browser is not null)
                    try { await browser.CloseAsync(); } catch { /* ignore on shutdown */ }
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task EnsureChromiumReadyAsync()
    {
        if (_chromiumReady) return;
        logger.LogInformation("[LiveScraper] Downloading Chromium (first run only)...");
        await new BrowserFetcher().DownloadAsync();
        _chromiumReady = true;
    }

    private async Task<ScraperSettings> LoadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await ScraperSettingsStore.GetOrCreateAsync(db, _defaults, ct);
    }

    // Scrapes the single admin-selected source only — no rotation or fallthrough to the
    // other registered sources. If that source comes up empty or errors, the cycle is
    // skipped and retried at the next poll interval.
    private async Task ScrapeAndSyncAsync(IBrowser browser, string competition, string sourceName, CancellationToken ct)
    {
        var source = _sources.FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            source = _sources[0];
            logger.LogWarning("[LiveScraper] Unknown source {SourceName} — falling back to {Fallback}",
                sourceName, source.Name);
        }

        await using var page = await browser.NewPageAsync();
        await page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        ScrapedMatchDto[] matches;
        try
        {
            matches = await source.ScrapeAsync(page, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[LiveScraper] {Source} failed this cycle", source.Name);
            return;
        }

        if (matches.Length == 0)
        {
            logger.LogWarning("[LiveScraper] {Source} returned no matches this cycle", source.Name);
            return;
        }

        logger.LogDebug("[LiveScraper] Scraped {Count} matches from {Source}", matches.Length, source.Name);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scoring = scope.ServiceProvider.GetRequiredService<IScoringService>();

        var addedWeeks = new HashSet<string>();
        var newlyEnded = new List<string>();

        foreach (var m in matches)
        {
            var endedId = await SyncMatchAsync(db, addedWeeks, m, competition, ct);
            if (endedId is not null) newlyEnded.Add(endedId);
        }

        await db.SaveChangesAsync(ct);

        foreach (var fId in newlyEnded)
        {
            await scoring.ScoreFixtureAsync(fId);
            logger.LogInformation("[LiveScraper] Predictions scored for ended fixture {Id}", fId);
        }
    }

    // Returns the fixtureId if this poll is the first time the fixture reached Ended; null otherwise.
    private async Task<string?> SyncMatchAsync(
        AppDbContext db, HashSet<string> addedWeeks, ScrapedMatchDto m, string competition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Home) || string.IsNullOrWhiteSpace(m.Away)) return null;

        var fixtureId = BuildFixtureId(m.Home, m.Away);
        var weekId = BuildWeekId(competition, m.Round);
        var newStatus = MapStatus(m);

        if (!addedWeeks.Contains(weekId) && !await db.MatchWeeks.AnyAsync(w => w.Id == weekId, ct))
        {
            db.MatchWeeks.Add(new MatchWeek
            {
                Id = weekId,
                Name = BuildWeekName(competition, m.Round),
                Competition = competition
            });
            addedWeeks.Add(weekId);
            logger.LogInformation("[LiveScraper] Created MatchWeek: {Id}", weekId);
        }

        var fixture = await db.Fixtures.FindAsync([fixtureId], ct);
        var wasEnded = fixture?.Status == FixtureStatus.Ended;

        if (fixture is null)
        {
            fixture = new Fixture
            {
                Id = fixtureId,
                WeekId = weekId,
                HomeTeam = m.Home,
                AwayTeam = m.Away,
                Kickoff = ParseKickoff(m.Time) ?? DateTime.UtcNow,
                Status = newStatus,
                UpdatedAt = DateTime.UtcNow
            };
            db.Fixtures.Add(fixture);
            logger.LogDebug("[LiveScraper] New fixture: {Home} vs {Away} [{Id}]", m.Home, m.Away, fixtureId);
        }
        else
        {
            fixture.Status = newStatus;
            fixture.UpdatedAt = DateTime.UtcNow;
            var kickoff = ParseKickoff(m.Time);
            if (kickoff.HasValue) fixture.Kickoff = kickoff.Value;
        }

        var scoreHome = ParseScore(m.ScoreHome);
        var scoreAway = ParseScore(m.ScoreAway);

        if (newStatus == FixtureStatus.Ended)
        {
            fixture.FinalScoreHome = scoreHome;
            fixture.FinalScoreAway = scoreAway;
            fixture.LiveScoreHome = null;
            fixture.LiveScoreAway = null;
            fixture.LiveMinute = null;
        }
        else if (newStatus is FixtureStatus.Live or FixtureStatus.HalfTime)
        {
            fixture.LiveScoreHome = scoreHome;
            fixture.LiveScoreAway = scoreAway;
            fixture.LiveMinute = ParseMinute(m.Stage);
        }

        return newStatus == FixtureStatus.Ended && !wasEnded ? fixtureId : null;
    }

    // Team-name based (not source-specific-id based) so that whichever source syncs a
    // given match, it resolves to the same Fixture row instead of creating a duplicate.
    private static string BuildFixtureId(string home, string away) =>
        $"fs-{TeamNameNormalizer.Slug(home)}-vs-{TeamNameNormalizer.Slug(away)}";

    private static string BuildWeekId(string competition, string? round) =>
        $"{Slug(competition)}-week-{NormalizeRound(round)}";

    private static string BuildWeekName(string competition, string? round) =>
        $"{competition} — Week {NormalizeRound(round)}";

    private static string NormalizeRound(string? round)
    {
        if (string.IsNullOrWhiteSpace(round)) return "?";
        var parts = round.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : round.Trim();
    }

    private static string Slug(string name) =>
        new string(name.ToLower().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // Prefers a source's explicit IsScheduled/IsLive flags (Flashscore's status now lives
    // on a row modifier class, not in the stage text — see FlashscoreSource). Falls back
    // to parsing Stage text for sources that only expose that (Livescore, BBC Sport).
    private static FixtureStatus MapStatus(ScrapedMatchDto m)
    {
        // Flashscore always supplies both flags (true/false, never absent). When present,
        // trust them completely — a finished Flashscore match has both flags false and no
        // Stage text at all, which the text-parsing fallback below would misread as PreMatch.
        if (m.IsScheduled is not null || m.IsLive is not null)
        {
            if (m.IsScheduled == true) return FixtureStatus.PreMatch;
            if (m.IsLive == true) return IsHalfTime(m.Stage) ? FixtureStatus.HalfTime : FixtureStatus.Live;
            return FixtureStatus.Ended;
        }

        return m.Stage?.Trim() switch
        {
            "FT" or "AET" or "AP" or "Finished" => FixtureStatus.Ended,
            var s when IsHalfTime(s) => FixtureStatus.HalfTime,
            "Postp." or "Postponed" => FixtureStatus.Postponed,
            "Canc." or "Cancelled" or "Canceled" or "Aband." => FixtureStatus.Cancelled,
            var s when s is not null && (s.EndsWith("'") || int.TryParse(s.Split('+')[0], out _)) => FixtureStatus.Live,
            _ => FixtureStatus.PreMatch
        };
    }

    private static bool IsHalfTime(string? stage) =>
        stage is not null && stage.Trim().Equals("HT", StringComparison.OrdinalIgnoreCase);

    private static int? ParseMinute(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;
        var clean = stage.TrimEnd('\'').Split('+')[0];
        return int.TryParse(clean, out var m) ? m : null;
    }

    private static int? ParseScore(string? score) =>
        int.TryParse(score?.Trim(), out var v) ? v : null;

    private static DateTime? ParseKickoff(string? time)
    {
        if (string.IsNullOrWhiteSpace(time) || time.Contains("'") || time is "HT" or "FT") return null;

        // FotMob supplies a full ISO-8601 UTC instant instead of a bare time-of-day string —
        // trust it outright rather than running it through the "assume today" fallback below,
        // which exists only because the other sources never give us a real date to work with.
        // TryParseExact (not the general-purpose TryParse) so a bare "HH:mm" from another
        // source can never be misread as an ISO timestamp and take this branch by accident.
        if (DateTimeOffset.TryParseExact(time, "yyyy-MM-ddTHH:mm:ssZ", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
            return iso.UtcDateTime;

        if (DateTime.TryParseExact(time,
            ["HH:mm", "d.MM. HH:mm", "dd.MM. HH:mm"],
            null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            var today = DateTime.UtcNow;
            if (dt.Year == 1) dt = new DateTime(today.Year, today.Month, today.Day, dt.Hour, dt.Minute, 0);
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        return null;
    }
}
