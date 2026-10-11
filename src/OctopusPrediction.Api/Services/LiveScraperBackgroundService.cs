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
    // Inside a match window but before kickoff (or between two matches), scores can't change, so
    // there's no need to load the page every PollIntervalSeconds: every request saved is one less
    // reason for a site to notice us. Full speed resumes this long before the next kickoff.
    private static readonly TimeSpan QuietWindowPollInterval = TimeSpan.FromMinutes(5);
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
                        await ScrapeAndSyncAsync(browser, settings, ct);
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

                    var (inMatchWindow, matchActive, nextWindowStart) = await GetMatchScheduleAsync(ct);
                    monitor.SetSchedule(matchActive && inMatchWindow ? "InPlay" : inMatchWindow ? "MatchWindow" : "NoMatch");
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

                    var pollInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
                    var delay = pollInterval;
                    if (!inMatchWindow)
                    {
                        // Sleep until the idle poll or the next match window, whichever is sooner.
                        delay = IdlePollInterval;
                        if (nextWindowStart is { } next && next - DateTime.UtcNow < delay)
                            delay = next - DateTime.UtcNow;
                    }
                    else if (!matchActive && QuietWindowPollInterval > delay)
                    {
                        delay = QuietWindowPollInterval;
                    }
                    if (delay < pollInterval) delay = pollInterval;
                    // A request every exactly-60s is an obvious bot; a varying gap looks like a visitor.
                    delay = SourceGuard.Jitter(delay);
                    monitor.SetPhase("Waiting",
                        !inMatchWindow
                            ? $"No match on, so polling every {IdlePollInterval.TotalMinutes:0} minutes with Chrome closed" +
                              (nextWindowStart is { } w ? $" (next match window opens {w:ddd HH:mm} UTC)." : ".")
                            : matchActive
                                ? $"A match is in play (or about to start): polling every {settings.PollIntervalSeconds}s."
                                : $"Match window open but nothing in play yet: polling every {QuietWindowPollInterval.TotalMinutes:0} minutes.",
                        DateTime.UtcNow + delay);
                    // Cut short by "Poll now" on the Scraper Health page.
                    await monitor.WaitForNextPollAsync(delay, ct);
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
    private async Task<(bool InWindow, bool MatchActive, DateTime? NextStart)> GetMatchScheduleAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var windows = await MatchWindows.ForMatchesAsync(db, now, ct);
            var active = await db.Fixtures.AsNoTracking().AnyAsync(f =>
                f.Status == FixtureStatus.Live || f.Status == FixtureStatus.HalfTime
                || (f.Status == FixtureStatus.PreMatch && f.Kickoff <= now + KickoffSoon
                    && f.Kickoff > now - MatchWindows.AfterKickoff), ct);
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
    private async Task ScrapeAndSyncAsync(IBrowser browser, ScraperSettings settings, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var planNames = new List<string>();
        void Record(string outcome, string? source = null, int matches = 0, int queued = 0, string? problem = null) =>
            monitor.RecordPoll(new PollRecord(started, (int)(DateTime.UtcNow - started).TotalMilliseconds,
                planNames, source, matches, queued, outcome, problem));
        try
        {
            await PollSourcesAsync(browser, settings, planNames, Record, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Record("Failed", problem: $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task PollSourcesAsync(IBrowser browser, ScraperSettings settings, List<string> planNames,
        Action<string, string?, int, int, string?> record, CancellationToken ct)
    {
        var plan = SourcePlan.ForPoll(_sources, settings, _poll++);
        planNames.AddRange(plan.Select(s => s.Name));
        if (plan.Count == 1 && !string.Equals(plan[0].Name, settings.SourceName, StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("[LiveScraper] Unknown source {SourceName} — falling back to {Fallback}",
                settings.SourceName, plan[0].Name);

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
            var matches = await ScrapeSourceAsync(browser, source, ct);
            if (matches.Length == 0) continue;

            logger.LogDebug("[LiveScraper] Scraped {Count} matches from {Source}", matches.Length, source.Name);
            var queued = await queue.EnqueueAsync(matches, source.Name, settings.Competition, ct);
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
    // The outcome goes to SourceGuard, which rests a source that is refusing us.
    private async Task<ScrapedMatchDto[]> ScrapeSourceAsync(IBrowser browser, IMatchSource source, CancellationToken ct)
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
        monitor.RecordSourceAttempt(source.Name, attemptStarted, matches.Length, (int)(now - attemptStarted).TotalMilliseconds,
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
