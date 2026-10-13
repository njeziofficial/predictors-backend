using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Monitoring;
using OctopusPrediction.Api.Services.Scraping;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services;

internal class LiveScraperBackgroundService(
    IOptions<LiveScraperSettings> options,
    IEnumerable<IMatchSource> sources,
    IServiceScopeFactory scopeFactory,
    SourceGuard guard,
    MatchUpdateQueue queue,
    ScraperMonitor monitor,
    CompetitionSources competitionSources,
    SiteBudget budget,
    ILogger<LiveScraperBackgroundService> logger)
    : BackgroundService
{
    private readonly LiveScraperSettings _defaults = options.Value;
    // La Liga's sources, as registered.
    private readonly IReadOnlyList<IMatchSource> _sources = [.. sources];

    // One competition's poll: La Liga with ScraperSettings' source choice, or another switched
    // on, with its own sources (CompetitionSources) and choice (Competitions.SourcePlanOf).
    private sealed record PollTarget(string Id, string Name, IReadOnlyList<IMatchSource> Sources, ScraperSettings Plan)
    {
        public bool IsLaLiga => Id == Competitions.LaLiga;
    }

    // The other competitions' own schedules. La Liga's is the main loop in ExecuteAsync, as it
    // always was; these are polled while it waits (see WaitPollingOthersAsync), never instead of it.
    private readonly Dictionary<string, DateTime> _otherDueAt = new();
    private readonly Dictionary<string, bool> _otherInWindow = new();
    private readonly Dictionary<string, int> _otherPolls = new();
    // Polls of different competitions never run back to back: the next other one waits at
    // least a random 5-20 seconds (Gap) after any poll ends, and none starts in the last
    // 15 seconds before La Liga's (it waits until after it instead).
    private DateTime _othersNotBefore = DateTime.MinValue;
    private static readonly TimeSpan ClearOfLaLiga = TimeSpan.FromSeconds(15);
    private bool OthersInWindow => _otherInWindow.Values.Any(v => v);
    private string? _executablePath;
    // Chrome's "headless shell" build: the old lightweight headless mode, far less memory than
    // full Chrome. If it can't be downloaded or launched, the scraper falls back to full Chrome.
    private bool _useHeadlessShell = true;
    private bool? _wasInMatchWindow;
    // Counts polls, so Rotate mode leads with a different source each time (SourcePlan.ForPoll).
    private int _poll;

    // How long between polls, in and out of match windows: see ScrapeSchedule. Full speed
    // resumes this long before the next kickoff.
    private static readonly TimeSpan KickoffSoon = TimeSpan.FromMinutes(5);
    // How long to wait for MatchUpdateProcessor to apply a poll before reading the schedule.
    private static readonly TimeSpan ApplyWait = TimeSpan.FromSeconds(15);
    private bool _allSourcesCoolingLogged;
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
                monitor.SetPhase("Paused", "Switched off in admin settings.");
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
                    monitor.SetPhase("Polling", $"Reading {SourcePlan.Describe(settings)}.");
                    browser ??= await LaunchBrowserAsync();

                    try
                    {
                        await ScrapeAndSyncAsync(browser, LaLigaTarget(settings), ct);
                    }
                    catch (PuppeteerException pex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(pex, "[LiveScraper] Browser error — restarting");
                        monitor.SetPhase("Restarting", $"Chrome failed ({pex.Message}). Starting a new one in 10 seconds.",
                            DateTime.UtcNow.AddSeconds(10));
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogError(ex, "[LiveScraper] Scrape cycle failed");
                    }

                    var (inMatchWindow, matchActive, nextWindowStart) = await GetMatchScheduleAsync(Competitions.LaLiga, ct);
                    monitor.SetSchedule(matchActive && inMatchWindow ? "InPlay" : inMatchWindow ? "MatchWindow" : "NoMatch");
                    if (inMatchWindow != _wasInMatchWindow)
                    {
                        logger.LogInformation(inMatchWindow
                                ? "[LiveScraper] Match window: polling {Source} every {Interval}s"
                                : "[LiveScraper] No match on: polling {Source} every 30 minutes with Chrome closed in between",
                            SourcePlan.Describe(settings), settings.PollIntervalSeconds);
                        _wasInMatchWindow = inMatchWindow;
                    }

                    // Chrome stays open while another competition has a match on, too.
                    if ((!inMatchWindow && !OthersInWindow) || ++cycles >= RecycleBrowserAfterCycles)
                    {
                        await browser.CloseAsync();
                        browser = null;
                        cycles = 0;
                    }

                    var delay = ScrapeSchedule.NextDelay(TimeSpan.FromSeconds(settings.PollIntervalSeconds),
                        inMatchWindow, matchActive, nextWindowStart, DateTime.UtcNow, ScrapeSchedule.LaLigaIdle);
                    // A request every exactly-60s is an obvious bot; a varying gap looks like a visitor.
                    delay = SourceGuard.Jitter(delay);
                    var waiting = !inMatchWindow
                        ? $"No match on, so polling every {ScrapeSchedule.LaLigaIdle.TotalMinutes:0} minutes with Chrome closed" +
                          (nextWindowStart is { } w ? $" (next match window opens {w:ddd HH:mm} UTC)." : ".")
                        : matchActive
                            ? $"A match is in play (or about to start): polling every {settings.PollIntervalSeconds}s."
                            : $"Match window open but nothing in play yet: polling every {ScrapeSchedule.QuietWindow.TotalMinutes:0} minutes.";
                    monitor.SetPhase("Waiting", waiting, DateTime.UtcNow + delay);
                    // Cut short by "Poll now" on the Scraper Health page. Competitions other than
                    // La Liga that are switched on are polled during the wait; with none, it's
                    // just the wait.
                    _othersNotBefore = DateTime.UtcNow + Gap();
                    browser = await WaitPollingOthersAsync(browser, inMatchWindow, delay, settings, waiting, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveScraper] Browser download or launch failed — retrying");
                monitor.SetPhase("Restarting", $"Chrome couldn't start ({ex.GetType().Name}: {ex.Message}). Retrying in 10 seconds.",
                    DateTime.UtcNow.AddSeconds(10));
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

    // Whether we're in a match window; whether a match is actually in play (or due to kick off
    // within KickoffSoon, or past its kickoff without being seen to start); and when the next
    // window opens.
    // One competition's: La Liga's polls never speed up for another competition's matches.
    private async Task<(bool InWindow, bool MatchActive, DateTime? NextStart)> GetMatchScheduleAsync(
        string competitionId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var windows = await MatchWindows.ForMatchesAsync(db, now, ct, competitionId);
            var active = await db.Fixtures.AsNoTracking().AnyAsync(f => f.Week.CompetitionId == competitionId && (
                f.Status == FixtureStatus.Live || f.Status == FixtureStatus.HalfTime
                || (f.Status == FixtureStatus.PreMatch && f.Kickoff <= now + KickoffSoon
                    && f.Kickoff > now - MatchWindows.AfterKickoff)), ct);
            return (MatchWindows.Contains(windows, now), active, windows.FirstOrDefault(w => w.Start > now)?.Start);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Can't tell: keep polling at full speed rather than miss a match.
            logger.LogWarning(ex, "[LiveScraper] Couldn't read the match schedule");
            return (true, true, null);
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

    private PollTarget LaLigaTarget(ScraperSettings settings) =>
        new(Competitions.LaLiga, settings.Competition, _sources, settings);

    // 5-20 seconds: how long after one competition's poll another's may start.
    private static TimeSpan Gap() => TimeSpan.FromSeconds(5 + Random.Shared.NextDouble() * 15);

    private int NextOtherPoll(string competitionId)
    {
        var poll = _otherPolls.GetValueOrDefault(competitionId);
        _otherPolls[competitionId] = poll + 1;
        return poll;
    }

    /// <summary>
    /// Waits until La Liga's next poll is due, polling the other competitions that are switched
    /// on as each falls due meanwhile. With none switched on it's the plain wait it always was.
    /// Returns the browser, which an other competition's poll may have opened or replaced.
    /// </summary>
    private async Task<IBrowser?> WaitPollingOthersAsync(
        IBrowser? browser, bool laLigaInWindow, TimeSpan delay, ScraperSettings settings, string waiting, CancellationToken ct)
    {
        var laLigaAt = DateTime.UtcNow + delay;
        while (true)
        {
            var others = await LoadOtherTargetsAsync(ct);
            // Due, and clear of La Liga's next poll; any due later wait until after it.
            DateTime? DueAt(PollTarget t)
            {
                var due = _otherDueAt.GetValueOrDefault(t.Id, DateTime.MinValue);
                if (due < _othersNotBefore) due = _othersNotBefore;
                return due < laLigaAt - ClearOfLaLiga ? due : null;
            }
            var next = others.Select(DueAt).Where(d => d is not null).Min();

            var reason = await monitor.WaitAsync((next ?? laLigaAt) - DateTime.UtcNow, ct);
            if (reason == WakeReason.PollNow)
            {
                // La Liga polls now; the others right after it.
                foreach (var t in others) _otherDueAt[t.Id] = DateTime.MinValue;
                return browser;
            }
            if (reason == WakeReason.CompetitionSwitchedOn)
            {
                foreach (var t in others) _otherDueAt[t.Id] = DateTime.MinValue;
                continue;
            }
            if (next is null) return browser;

            var now = DateTime.UtcNow;
            foreach (var target in others.Where(t => DueAt(t) <= now).OrderBy(_ => Random.Shared.Next()))
            {
                if (DateTime.UtcNow < _othersNotBefore) await Task.Delay(_othersNotBefore - DateTime.UtcNow, ct);
                browser = await PollOtherAsync(browser, target, settings, ct);
            }

            if (browser is not null && !laLigaInWindow && !OthersInWindow)
            {
                try { await browser.CloseAsync(); } catch { /* it's being thrown away */ }
                browser = null;
            }
            monitor.SetPhase("Waiting", waiting, laLigaAt);
        }
    }

    // One poll of a competition other than La Liga, then its next one scheduled from its own
    // match windows. Its failures stay here: La Liga's loop carries on whatever happens.
    private async Task<IBrowser?> PollOtherAsync(IBrowser? browser, PollTarget target, ScraperSettings settings, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
        monitor.SetPhase("Polling", $"Reading {target.Name} from {SourcePlan.Describe(target.Plan)}.");
        try
        {
            browser ??= await LaunchBrowserAsync();
            await ScrapeAndSyncAsync(browser, target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PuppeteerException pex)
        {
            logger.LogWarning(pex, "[LiveScraper] Browser error polling {Competition} — starting a new one next time", target.Name);
            if (browser is not null)
                try { await browser.CloseAsync(); } catch { /* it's being thrown away */ }
            browser = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LiveScraper] {Competition} poll failed", target.Name);
        }

        var (inWindow, active, nextWindowStart) = await GetMatchScheduleAsync(target.Id, ct);
        if (inWindow != _otherInWindow.GetValueOrDefault(target.Id))
            logger.LogInformation(inWindow
                    ? "[LiveScraper] {Competition} match window: polling {Source} every {Interval}s"
                    : "[LiveScraper] No {Competition} match on: looking for fixtures on {Source} every 3 hours",
                target.Name, SourcePlan.Describe(target.Plan), settings.PollIntervalSeconds);
        _otherInWindow[target.Id] = inWindow;

        var now = DateTime.UtcNow;
        _otherDueAt[target.Id] = now + SourceGuard.Jitter(
            ScrapeSchedule.NextDelay(pollInterval, inWindow, active, nextWindowStart, now, ScrapeSchedule.OtherIdle));
        _othersNotBefore = now + Gap();
        return browser;
    }

    // Plan for the competitions that aren't La Liga and are switched on, with their sources.
    // A competition switched off is forgotten, so nothing of it lingers (its window can't keep
    // Chrome open). On any error, none: La Liga's polling must not depend on this.
    private async Task<IReadOnlyList<PollTarget>> LoadOtherTargetsAsync(CancellationToken ct)
    {
        List<Competition> rows;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rows = await db.Competitions.AsNoTracking()
                .Where(c => c.Enabled && c.Id != Competitions.LaLiga)
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[LiveScraper] Couldn't read which other competitions are on — skipping them this time");
            rows = [];
        }

        var targets = rows
            .Select(c => new PollTarget(c.Id, c.Name, competitionSources.For(c.Id), Competitions.SourcePlanOf(c)))
            .Where(t => t.Sources.Count > 0)
            .ToList();
        foreach (var id in _otherInWindow.Keys.Except(targets.Select(t => t.Id)).ToList())
        {
            _otherInWindow.Remove(id);
            _otherDueAt.Remove(id);
        }
        return targets;
    }

    // Another competition's poll: its sources in plan order, skipping any site that's resting
    // (refused us, whichever competition asked) or has had its pages for the hour.
    private async Task PollOtherSourcesAsync(IBrowser browser, PollTarget target, IReadOnlyList<IMatchSource> plan,
        Action<string, string?, int, int, string?> record, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var max = _defaults.MaxPagesPerSitePerHour;
        var usable = plan.Where(s => !guard.IsCooling(s.Name, now) && budget.HasRoom(s.Name, now, max)).ToList();
        if (usable.Count == 0)
        {
            logger.LogInformation("[LiveScraper] Skipping {Competition}: every source is resting or has had {Max} pages this hour",
                target.Name, max);
            record("Resting", null, 0, 0, $"Every source is resting or has had its {max} pages this hour.");
            return;
        }

        foreach (var source in usable)
        {
            var matches = await ScrapeSourceAsync(browser, source, target, ct);
            if (matches.Length == 0) continue;

            var queued = await queue.EnqueueAsync(matches, source.Name, target.Name, ct);
            if (queued > 0)
                await queue.WaitUntilAppliedAsync(ApplyWait, ct);
            record("Ok", source.Name, matches.Length, queued, null);
            return;
        }
        record("NoMatches", null, 0, 0, $"No matches from {string.Join(", ", usable.Select(s => s.Name))}.");
    }

    private async Task<ScraperSettings> LoadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await ScraperSettingsStore.GetOrCreateAsync(db, _defaults, ct);
    }

    // Tries this poll's sources in turn (one in Single mode; see SourcePlan), skipping any that
    // SourceGuard is resting, and hands what the first to return matches read to MatchUpdateQueue,
    // which queues only the matches that changed for MatchUpdateProcessor to apply. If no source
    // returns matches, the poll is skipped and retried next interval.
    private async Task ScrapeAndSyncAsync(IBrowser browser, PollTarget target, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var planNames = new List<string>();
        void Record(string outcome, string? source = null, int matches = 0, int queued = 0, string? problem = null) =>
            monitor.RecordPoll(new PollRecord(started, (int)(DateTime.UtcNow - started).TotalMilliseconds,
                planNames, source, matches, queued, outcome, problem, target.Name));
        try
        {
            await PollSourcesAsync(browser, target, planNames, Record, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Record("Failed", problem: $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task PollSourcesAsync(IBrowser browser, PollTarget target, List<string> planNames,
        Action<string, string?, int, int, string?> record, CancellationToken ct)
    {
        var settings = target.Plan;
        var plan = SourcePlan.ForPoll(target.Sources, settings, target.IsLaLiga ? _poll++ : NextOtherPoll(target.Id));
        planNames.AddRange(plan.Select(s => s.Name));
        if (plan.Count == 1 && !string.Equals(plan[0].Name, settings.SourceName, StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("[LiveScraper] Unknown source {SourceName} — falling back to {Fallback}",
                settings.SourceName, plan[0].Name);

        if (!target.IsLaLiga)
        {
            await PollOtherSourcesAsync(browser, target, plan, record, ct);
            return;
        }

        var usable = plan.Where(s => !guard.IsCooling(s.Name, DateTime.UtcNow)).ToList();
        if (usable.Count == 0)
        {
            if (!_allSourcesCoolingLogged)
                logger.LogWarning("[LiveScraper] Every source is resting after being refused ({Sources}); next try at {Until:u}. " +
                    "Fallback or Rotate with more sources keeps scores coming while one rests.",
                    string.Join(", ", plan.Select(s => s.Name)), plan.Min(s => guard.CoolingUntil(s.Name)));
            _allSourcesCoolingLogged = true;
            record("Resting", null, 0, 0, $"Every source is resting until {plan.Min(s => guard.CoolingUntil(s.Name)):HH:mm} UTC.");
            return;
        }
        _allSourcesCoolingLogged = false;

        foreach (var source in usable)
        {
            var matches = await ScrapeSourceAsync(browser, source, target, ct);
            if (matches.Length == 0) continue;

            logger.LogDebug("[LiveScraper] Scraped {Count} matches from {Source}", matches.Length, source.Name);
            var queued = await queue.EnqueueAsync(matches, source.Name, target.Name, ct);
            if (queued > 0)
                await queue.WaitUntilAppliedAsync(ApplyWait, ct);
            record("Ok", source.Name, matches.Length, queued, null);
            return;
        }

        if (usable.Count > 1)
            logger.LogWarning("[LiveScraper] No source returned matches this cycle ({Sources})",
                string.Join(", ", usable.Select(s => s.Name)));
        record("NoMatches", null, 0, 0, $"No matches from {string.Join(", ", usable.Select(s => s.Name))}.");
    }

    // Titles of the "checking your browser" / "access denied" pages bot protection serves
    // (Cloudflare, Akamai, PerimeterX, DataDome and the like) instead of the real page.
    private static readonly string[] BotCheckTitles =
    [
        "just a moment", "attention required", "access denied", "verify you are human", "are you a robot",
        "captcha", "pardon our interruption", "request blocked", "too many requests", "forbidden",
    ];

    // A failed or empty scrape comes back as no matches, so the caller can move to the next source.
    // The outcome goes to SourceGuard, which rests a source that is refusing us. A refusal rests
    // the site for every competition (it's our address being refused, whichever page asked); an
    // empty page or a success only counts for La Liga, so another competition's quiet page (no
    // matches this week) can't rest a site La Liga relies on.
    private async Task<ScrapedMatchDto[]> ScrapeSourceAsync(IBrowser browser, IMatchSource source, PollTarget target, CancellationToken ct)
    {
        var attemptStarted = DateTime.UtcNow;
        await using var page = await browser.NewPageAsync();
        await BlockUnneededRequestsAsync(page, blockStylesheets: source.Name == "Flashscore");
        // Look like the desktop Chrome this actually is: the browser's own version (a fixed,
        // ageing version string stands out more every month), without the "Headless" marker,
        // with the language and screen size an ordinary visitor sends.
        await page.SetUserAgentAsync((await browser.GetUserAgentAsync()).Replace("HeadlessChrome", "Chrome"));
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string> { ["Accept-Language"] = "en-GB,en;q=0.9" });
        await page.SetViewportAsync(new ViewPortOptions { Width = 1366, Height = 768 });

        // The site's answer to each page the source opens: a refusal shows up here even when the
        // source just sees an empty page.
        string? refusal = null;
        TimeSpan? retryAfter = null;
        page.Response += (_, e) =>
        {
            var response = e.Response;
            if (!response.Request.IsNavigationRequest || response.Frame != page.MainFrame) return;
            budget.RecordPageLoad(source.Name, DateTime.UtcNow);
            if ((int)response.Status is 403 or 429 or 503)
            {
                refusal = $"HTTP {(int)response.Status}";
                retryAfter = ParseRetryAfter(response.Headers);
            }
        };

        ScrapedMatchDto[] matches;
        Exception? error = null;
        try
        {
            matches = await source.ScrapeAsync(page, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            matches = [];
            error = ex;
        }

        if (matches.Length == 0 && refusal is null)
            refusal = await BotCheckTitleAsync(page);

        var now = DateTime.UtcNow;
        if (refusal is not null)
        {
            var until = guard.RecordBlocked(source.Name, refusal, retryAfter, now);
            logger.LogWarning("[LiveScraper] {Source} refused the scraper ({Reason}) — leaving it alone until {Until:u}",
                source.Name, refusal, until);
        }
        else if (!target.IsLaLiga)
        {
            if (matches.Length == 0)
                logger.LogInformation(error, "[LiveScraper] {Source} had no {Competition} matches this cycle", source.Name, target.Name);
        }
        else if (matches.Length == 0)
        {
            var problem = error is null ? "No matches on the page" : error.GetType().Name;
            if (error is null)
                logger.LogWarning("[LiveScraper] {Source} returned no matches this cycle", source.Name);
            else
                logger.LogWarning(error, "[LiveScraper] {Source} failed this cycle", source.Name);
            if (guard.RecordFailure(source.Name, problem, now) is { } until)
                logger.LogWarning("[LiveScraper] {Source} keeps failing — resting it until {Until:u}", source.Name, until);
        }
        else
        {
            guard.RecordSuccess(source.Name);
        }
        monitor.RecordSourceAttempt(target.IsLaLiga ? source.Name : $"{source.Name} ({target.Name})", attemptStarted, matches.Length, (int)(now - attemptStarted).TotalMilliseconds,
            refusal is not null ? $"Refused: {refusal}"
            : error is not null ? $"Failed: {error.GetType().Name}: {error.Message}"
            : matches.Length == 0 ? "No matches on the page"
            : $"{matches.Length} matches");
        return matches;
    }

    private static async Task<string?> BotCheckTitleAsync(IPage page)
    {
        try
        {
            var title = (await page.GetTitleAsync())?.Trim() ?? "";
            return BotCheckTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase))
                ? $"Bot check: \"{title}\""
                : null;
        }
        catch
        {
            return null; // The page closed or crashed; the failure is already recorded.
        }
    }

    // Retry-After is either a number of seconds or an HTTP date.
    private static TimeSpan? ParseRetryAfter(IDictionary<string, string>? headers)
    {
        var value = headers?.FirstOrDefault(h => h.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var seconds)) return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, out var at)) return at - DateTimeOffset.UtcNow;
        return null;
    }
}
