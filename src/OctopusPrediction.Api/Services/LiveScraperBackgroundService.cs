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
    private string? _executablePath;
    // Chrome's "headless shell" build: the old lightweight headless mode, far less memory than
    // full Chrome. If it can't be downloaded or launched, the scraper falls back to full Chrome.
    private bool _useHeadlessShell = true;
    private bool? _wasInMatchWindow;
    // Counts polls, so Rotate mode leads with a different source each time (SourcePlan.ForPoll).
    private int _poll;

    // Between match windows nothing changes but new fixtures appearing, so scrape rarely and
    // don't keep Chrome running in between.
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(30);
    // Chrome's memory creeps up over hours of reloading the same page; start a fresh one now and then.
    private const int RecycleBrowserAfterCycles = 60;

    private static readonly string[] BrowserArgs =
    [
        "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage", "--disable-gpu",
        "--no-first-run", "--mute-audio", "--disable-extensions",
        // One renderer process for the page and its (ad) iframes instead of one per site.
        "--disable-site-isolation-trials", "--renderer-process-limit=2",
        // Puppeteer's own --disable-features list repeated, since a second flag replaces the first.
        "--disable-features=site-per-process,IsolateOrigins,Translate,BackForwardCache,AcceptCHFrame,MediaRouter,OptimizationHints",
        "--blink-settings=imagesEnabled=false",
    ];

    // Nothing the scrapers read comes from these: they only cost memory and bandwidth.
    private static readonly HashSet<ResourceType> BlockedResourceTypes =
        [ResourceType.Image, ResourceType.Media, ResourceType.Font];

    private static readonly string[] BlockedHosts =
    [
        "doubleclick.net", "googlesyndication.com", "googletagservices.com", "googletagmanager.com",
        "google-analytics.com", "adservice.google", "amazon-adsystem.com", "criteo", "scorecardresearch.com",
        "facebook.net", "hotjar", "taboola", "outbrain", "adnxs.com", "rubiconproject", "pubmatic",
        "casalemedia", "teads", "quantserve", "moatads", "chartbeat",
    ];

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
                var cycles = 0;
                while (!ct.IsCancellationRequested)
                {
                    settings = await LoadSettingsAsync(ct);
                    if (!settings.Enabled)
                    {
                        logger.LogInformation("[LiveScraper] Disabled from admin settings — pausing");
                        break;
                    }

                    // Inside the try: a failed Chromium download or launch (e.g. a network blip at
                    // startup) must only skip this attempt. Escaping ExecuteAsync would stop the host.
                    browser ??= await LaunchBrowserAsync();

                    try
                    {
                        await ScrapeAndSyncAsync(browser, settings, ct);
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

                    var (inMatchWindow, nextWindowStart) = await GetMatchScheduleAsync(ct);
                    if (inMatchWindow != _wasInMatchWindow)
                    {
                        logger.LogInformation(inMatchWindow
                                ? "[LiveScraper] Match window: polling {Source} every {Interval}s"
                                : "[LiveScraper] No match on: polling {Source} every 30 minutes with Chrome closed in between",
                            SourcePlan.Describe(settings), settings.PollIntervalSeconds);
                        _wasInMatchWindow = inMatchWindow;
                    }

                    if (!inMatchWindow || ++cycles >= RecycleBrowserAfterCycles)
                    {
                        await browser.CloseAsync();
                        browser = null;
                        cycles = 0;
                    }

                    var delay = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
                    if (!inMatchWindow)
                    {
                        // Sleep until the idle poll or the next match window, whichever is sooner.
                        delay = IdlePollInterval;
                        if (nextWindowStart is { } next && next - DateTime.UtcNow < delay)
                            delay = next - DateTime.UtcNow;
                        if (delay < TimeSpan.FromSeconds(settings.PollIntervalSeconds))
                            delay = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
                    }
                    await Task.Delay(delay, ct);
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

    private async Task<IBrowser> LaunchBrowserAsync()
    {
        if (_useHeadlessShell)
        {
            try
            {
                if (_executablePath is null && File.Exists(_defaults.ChromeExecutablePath))
                {
                    _executablePath = _defaults.ChromeExecutablePath;
                    logger.LogInformation("[LiveScraper] Using the installed Chrome headless shell at {Path}", _executablePath);
                }
                return await LaunchAsync(SupportedBrowser.ChromeHeadlessShell, HeadlessMode.Shell);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[LiveScraper] Chrome headless shell unavailable — falling back to full Chrome");
                _useHeadlessShell = false;
                _executablePath = null;
            }
        }
        return await LaunchAsync(SupportedBrowser.Chrome, HeadlessMode.True);
    }

    private async Task<IBrowser> LaunchAsync(SupportedBrowser browser, HeadlessMode headless)
    {
        if (_executablePath is null)
        {
            logger.LogInformation("[LiveScraper] Downloading {Browser} (first run only)...", browser);
            var installed = await new BrowserFetcher(browser).DownloadAsync();
            _executablePath = installed.GetExecutablePath();
        }
        return await Puppeteer.LaunchAsync(new LaunchOptions
        {
            HeadlessMode = headless,
            ExecutablePath = _executablePath,
            Args = BrowserArgs,
        });
    }

    // Whether a match is on (or about to start), and when the next one's window opens.
    private async Task<(bool InWindow, DateTime? NextStart)> GetMatchScheduleAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var windows = await MatchWindows.ForMatchesAsync(db, now, ct);
            return (MatchWindows.Contains(windows, now), windows.FirstOrDefault(w => w.Start > now)?.Start);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Can't tell: keep polling at full speed rather than miss a match.
            logger.LogWarning(ex, "[LiveScraper] Couldn't read the match schedule");
            return (true, null);
        }
    }

    // Skips images, video, fonts and ad/analytics requests: none of it is read, all of it costs memory.
    // Stylesheets too where that's been checked not to change what the scraper finds (Flashscore:
    // same matches, ~40 MB less); other sources may lay out or lazy-load rows with CSS.
    private static async Task BlockUnneededRequestsAsync(IPage page, bool blockStylesheets)
    {
        await page.SetRequestInterceptionAsync(true);
        page.Request += async (_, e) =>
        {
            try
            {
                var host = Uri.TryCreate(e.Request.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
                if (BlockedResourceTypes.Contains(e.Request.ResourceType)
                    || (blockStylesheets && e.Request.ResourceType == ResourceType.StyleSheet)
                    || BlockedHosts.Any(host.Contains))
                    await e.Request.AbortAsync();
                else
                    await e.Request.ContinueAsync();
            }
            catch
            {
                // The page closed or navigated away mid-request; nothing to do.
            }
        };
    }

    private async Task<ScraperSettings> LoadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await ScraperSettingsStore.GetOrCreateAsync(db, _defaults, ct);
    }

    // Tries this poll's sources in turn (one in Single mode; see SourcePlan) and syncs the
    // first that returns matches. If none does, the poll is skipped and retried next interval.
    private async Task ScrapeAndSyncAsync(IBrowser browser, ScraperSettings settings, CancellationToken ct)
    {
        var plan = SourcePlan.ForPoll(_sources, settings, _poll++);
        if (plan.Count == 1 && !string.Equals(plan[0].Name, settings.SourceName, StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("[LiveScraper] Unknown source {SourceName} — falling back to {Fallback}",
                settings.SourceName, plan[0].Name);

        foreach (var source in plan)
        {
            var matches = await ScrapeSourceAsync(browser, source, ct);
            if (matches.Length == 0) continue;

            logger.LogDebug("[LiveScraper] Scraped {Count} matches from {Source}", matches.Length, source.Name);
            await SyncAsync(matches, settings.Competition, ct);
            return;
        }

        if (plan.Count > 1)
            logger.LogWarning("[LiveScraper] No source returned matches this cycle ({Sources})",
                string.Join(", ", plan.Select(s => s.Name)));
    }

    // A failed or empty scrape comes back as no matches, so the caller can move to the next source.
    private async Task<ScrapedMatchDto[]> ScrapeSourceAsync(IBrowser browser, IMatchSource source, CancellationToken ct)
    {
        await using var page = await browser.NewPageAsync();
        await BlockUnneededRequestsAsync(page, blockStylesheets: source.Name == "Flashscore");
        await page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        try
        {
            var matches = await source.ScrapeAsync(page, ct);
            if (matches.Length == 0)
                logger.LogWarning("[LiveScraper] {Source} returned no matches this cycle", source.Name);
            return matches;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[LiveScraper] {Source} failed this cycle", source.Name);
            return [];
        }
    }

    private async Task SyncAsync(ScrapedMatchDto[] matches, string competition, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scoring = scope.ServiceProvider.GetRequiredService<IScoringService>();

        var addedWeeks = new HashSet<string>();
        var toScore = new List<string>();

        foreach (var m in matches)
        {
            var changedId = await SyncMatchAsync(db, addedWeeks, m, competition, ct);
            if (changedId is not null) toScore.Add(changedId);
        }

        await db.SaveChangesAsync(ct);
        await RemoveEmptyUnknownWeekAsync(db, competition, ct);

        foreach (var fId in toScore)
        {
            await scoring.ScoreFixtureAsync(fId);
            logger.LogInformation("[LiveScraper] Predictions scored for fixture {Id}", fId);
        }
    }

    // A match can't be over sooner than this after kickoff (two halves and the break), so a
    // "finished" reading before then is a source glitch, not a result.
    private static readonly TimeSpan MinMatchDuration = TimeSpan.FromMinutes(100);

    // Returns the fixtureId when its result changed (it ended, its final score changed, or it
    // stopped being ended), so its predictions are scored again; null otherwise.
    private async Task<string?> SyncMatchAsync(
        AppDbContext db, HashSet<string> addedWeeks, ScrapedMatchDto m, string competition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Home) || string.IsNullOrWhiteSpace(m.Away)) return null;

        var fixtureId = BuildFixtureId(m.Home, m.Away);
        var hasRound = !string.IsNullOrWhiteSpace(m.Round);
        var weekId = BuildWeekId(competition, m.Round);
        var newStatus = MapStatus(m);

        var fixture = await db.Fixtures.FindAsync([fixtureId], ct);
        var wasEnded = fixture?.Status == FixtureStatus.Ended;
        var (oldFinalHome, oldFinalAway) = (fixture?.FinalScoreHome, fixture?.FinalScoreAway);

        // A bare "HH:mm" is only a guess at the date (see ParseKickoff), so it's the last resort.
        var scrapedKickoff = ParseKickoff(m.Time);
        var datedKickoff = scrapedKickoff is { HasDate: true } k ? k.Utc : (DateTime?)null;

        // Flashscore marks a row finished by the absence of its "live" marker, which can drop
        // out for a moment mid-match. Scoring that would award points for a half-time score.
        var kickoffForCheck = datedKickoff ?? fixture?.Kickoff ?? scrapedKickoff?.Utc;
        if (newStatus == FixtureStatus.Ended && kickoffForCheck is { } ko && DateTime.UtcNow < ko + MinMatchDuration)
        {
            var keep = DateTime.UtcNow < ko ? FixtureStatus.PreMatch
                : fixture?.Status is FixtureStatus.HalfTime ? FixtureStatus.HalfTime
                : FixtureStatus.Live;
            logger.LogWarning("[LiveScraper] {Home} vs {Away} reported finished {Minutes:0} min after kickoff — treating it as {Status}",
                m.Home, m.Away, (DateTime.UtcNow - ko).TotalMinutes, keep);
            newStatus = keep;
        }

        // Without a round there's no week to put a new fixture in: leave it until a poll finds
        // the round (FlashscoreSource looks it up), rather than invent a "Week ?".
        if (fixture is null && !hasRound)
        {
            logger.LogDebug("[LiveScraper] Skipping new fixture {Home} vs {Away} until its round is known", m.Home, m.Away);
            return null;
        }

        // A team plays once a round. If the week already has a match for either team, this is
        // that match under a spelling TeamNameNormalizer doesn't know yet: don't add it twice.
        if (fixture is null && await FindSameRoundMatchAsync(db, weekId, m, ct) is { } existing)
        {
            logger.LogWarning("[LiveScraper] Not adding {Home} vs {Away}: {Week} already has {ExistingHome} vs {ExistingAway}. " +
                "If they're the same match, add the spelling to TeamNameNormalizer.",
                m.Home, m.Away, weekId, existing.HomeTeam, existing.AwayTeam);
            return null;
        }

        // Once a match has started or finished it never goes back to "not started". Sources that
        // only mark "FT" (ESPN, WorldFootball) show a match in play as not started, which would
        // otherwise reopen it and take its points away.
        if (fixture is not null && newStatus == FixtureStatus.PreMatch
            && fixture.Status is FixtureStatus.Live or FixtureStatus.HalfTime or FixtureStatus.Ended)
        {
            logger.LogDebug("[LiveScraper] Ignoring 'not started' for {Home} vs {Away}, already {Status}",
                m.Home, m.Away, fixture.Status);
            return null;
        }

        if (hasRound && (fixture is null || fixture.WeekId == UnknownWeekId(competition)))
            await EnsureWeekAsync(db, addedWeeks, weekId, competition, m.Round, ct);

        if (fixture is not null && hasRound && fixture.WeekId == UnknownWeekId(competition))
            await MoveOutOfUnknownWeekAsync(db, fixture, weekId, ct);

        if (fixture is null)
        {
            fixture = new Fixture
            {
                Id = fixtureId,
                WeekId = weekId,
                HomeTeam = m.Home,
                AwayTeam = m.Away,
                Kickoff = scrapedKickoff?.Utc ?? DateTime.UtcNow,
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
            // Only a time that comes with its date may move a kickoff, which drives prediction
            // locks: a bare "HH:mm" may be another day's match, or in the site's own time zone.
            if (datedKickoff.HasValue) fixture.Kickoff = datedKickoff.Value;
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
            // Back in play after a wrong "finished" reading: that result no longer stands.
            fixture.FinalScoreHome = null;
            fixture.FinalScoreAway = null;
        }

        var isEnded = newStatus == FixtureStatus.Ended;
        var resultChanged = isEnded != wasEnded
            || (isEnded && (fixture.FinalScoreHome != oldFinalHome || fixture.FinalScoreAway != oldFinalAway));
        return resultChanged ? fixtureId : null;
    }

    // A fixture in the week (saved, or added earlier this poll) involving either of m's teams.
    private static async Task<Fixture?> FindSameRoundMatchAsync(
        AppDbContext db, string weekId, ScrapedMatchDto m, CancellationToken ct)
    {
        string[] teams = [TeamNameNormalizer.Slug(m.Home), TeamNameNormalizer.Slug(m.Away)];
        var inWeek = await db.Fixtures.Where(f => f.WeekId == weekId).ToListAsync(ct);
        return inWeek.Concat(db.Fixtures.Local.Where(f => f.WeekId == weekId))
            .FirstOrDefault(f => teams.Contains(TeamNameNormalizer.Slug(f.HomeTeam))
                || teams.Contains(TeamNameNormalizer.Slug(f.AwayTeam)));
    }

    private async Task EnsureWeekAsync(
        AppDbContext db, HashSet<string> addedWeeks, string weekId, string competition, string? round, CancellationToken ct)
    {
        if (addedWeeks.Contains(weekId) || await db.MatchWeeks.AnyAsync(w => w.Id == weekId, ct)) return;
        db.MatchWeeks.Add(new MatchWeek
        {
            Id = weekId,
            Name = BuildWeekName(competition, round),
            Competition = competition
        });
        addedWeeks.Add(weekId);
        logger.LogInformation("[LiveScraper] Created MatchWeek: {Id}", weekId);
    }

    // Older scrapes filed fixtures whose round wasn't on the page under "Week ?". Once the round
    // is known, move the fixture and its predictions to the right week. Tracked changes, not a
    // bulk update, so the save empties the cached weeks, predictions and leaderboard.
    private async Task MoveOutOfUnknownWeekAsync(AppDbContext db, Fixture fixture, string weekId, CancellationToken ct)
    {
        logger.LogInformation("[LiveScraper] Moving {Home} vs {Away} from {From} to {To}",
            fixture.HomeTeam, fixture.AwayTeam, fixture.WeekId, weekId);
        fixture.WeekId = weekId;
        foreach (var prediction in await db.Predictions.Where(p => p.FixtureId == fixture.Id).ToListAsync(ct))
            prediction.WeekId = weekId;
    }

    // Drops "Week ?" once every fixture has been moved out of it (see MoveOutOfUnknownWeekAsync).
    private async Task RemoveEmptyUnknownWeekAsync(AppDbContext db, string competition, CancellationToken ct)
    {
        var unknownWeekId = UnknownWeekId(competition);
        var week = await db.MatchWeeks.FindAsync([unknownWeekId], ct);
        if (week is null || await db.Fixtures.AnyAsync(f => f.WeekId == unknownWeekId, ct)) return;
        db.MatchWeeks.Remove(week);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("[LiveScraper] Removed empty MatchWeek {Id}", unknownWeekId);
    }

    private static string UnknownWeekId(string competition) => BuildWeekId(competition, null);

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

    // HasDate is false for a bare "HH:mm", which is assumed to be today.
    private readonly record struct ScrapedKickoff(DateTime Utc, bool HasDate);

    private static ScrapedKickoff? ParseKickoff(string? time)
    {
        if (string.IsNullOrWhiteSpace(time) || time.Contains("'") || time is "HT" or "FT") return null;

        // FotMob supplies a full ISO-8601 UTC instant instead of a bare time-of-day string —
        // trust it outright rather than running it through the "assume today" fallback below,
        // which exists only because the other sources never give us a real date to work with.
        // TryParseExact (not the general-purpose TryParse) so a bare "HH:mm" from another
        // source can never be misread as an ISO timestamp and take this branch by accident.
        if (DateTimeOffset.TryParseExact(time, "yyyy-MM-ddTHH:mm:ssZ", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var iso))
            return new ScrapedKickoff(iso.UtcDateTime, HasDate: true);

        // Day and month, no year: this year.
        if (DateTime.TryParseExact(time, ["d.MM. HH:mm", "dd.MM. HH:mm"],
            null, System.Globalization.DateTimeStyles.None, out var dated))
            return new ScrapedKickoff(DateTime.SpecifyKind(dated, DateTimeKind.Utc), HasDate: true);

        if (DateTime.TryParseExact(time, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var t))
        {
            var today = DateTime.UtcNow.Date;
            return new ScrapedKickoff(DateTime.SpecifyKind(today + t.TimeOfDay, DateTimeKind.Utc), HasDate: false);
        }
        return null;
    }
}
