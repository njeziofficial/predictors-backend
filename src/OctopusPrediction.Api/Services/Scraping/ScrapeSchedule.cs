namespace OctopusPrediction.Api.Services.Scraping;

// How long the scraper waits before polling a competition again (before jitter). La Liga's
// timing is the scraper's original one; ScrapeScheduleTests pins it.
public static class ScrapeSchedule
{
    // Between La Liga's match windows nothing changes but new fixtures appearing, so it's polled
    // rarely, with Chrome closed in between.
    public static readonly TimeSpan LaLigaIdle = TimeSpan.FromMinutes(30);
    // Other competitions look for new fixtures far less often outside their windows: every
    // request saved is one less reason for a site to notice us.
    public static readonly TimeSpan OtherIdle = TimeSpan.FromHours(3);
    // Inside a match window but before kickoff (or between two matches), scores can't change, so
    // there's no need to load the page every PollIntervalSeconds. Full speed resumes shortly
    // before the next kickoff (see LiveScraperBackgroundService.KickoffSoon).
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(5);

    public static TimeSpan NextDelay(
        TimeSpan pollInterval, bool inMatchWindow, bool matchActive, DateTime? nextWindowStart, DateTime now, TimeSpan idle)
    {
        var delay = pollInterval;
        if (!inMatchWindow)
        {
            // Sleep until the idle poll or the next match window, whichever is sooner.
            delay = idle;
            if (nextWindowStart is { } next && next - now < delay)
                delay = next - now;
        }
        else if (!matchActive && QuietWindow > delay)
        {
            delay = QuietWindow;
        }
        if (delay < pollInterval) delay = pollInterval;
        return delay;
    }
}
