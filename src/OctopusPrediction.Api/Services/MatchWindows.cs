using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public record TimeWindow(DateTime Start, DateTime End);

// When the backend has work to do on its own: matches to follow and reminders to send. The
// Cloudflare Worker's "match windows" mode keeps the backend awake only during these (Render's
// free tier otherwise sleeps it), and the live scraper polls fast only during the match ones.
public static class MatchWindows
{
    // Awake before kickoff so the scraper is running (and Chrome launched) when the match starts.
    public static readonly TimeSpan BeforeKickoff = TimeSpan.FromMinutes(30);
    // A match normally ends ~2h after kickoff; the window closes as soon as the scraper sees it
    // end, so this is only the limit for one it hasn't.
    public static readonly TimeSpan AfterKickoff = TimeSpan.FromHours(3);
    // A match still showing live after AfterKickoff keeps the window open (extra time, delays),
    // but never past this, so a fixture stuck "live" can't keep the backend awake for good.
    public static readonly TimeSpan MaxLiveAfterKickoff = TimeSpan.FromHours(6);
    private static readonly TimeSpan LiveGrace = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Lookahead = TimeSpan.FromDays(21);

    // Upcoming and current match windows, merged where they overlap, in order.
    public static async Task<IReadOnlyList<TimeWindow>> ForMatchesAsync(AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        var fixtures = await db.Fixtures.AsNoTracking()
            .Where(f => f.Status == FixtureStatus.PreMatch || f.Status == FixtureStatus.Live || f.Status == FixtureStatus.HalfTime)
            .Where(f => f.Kickoff > now - MaxLiveAfterKickoff && f.Kickoff < now + Lookahead)
            .Select(f => new { f.Kickoff, f.Status })
            .ToListAsync(ct);

        var windows = fixtures.Select(f =>
        {
            var end = f.Kickoff + AfterKickoff;
            if (f.Status != FixtureStatus.PreMatch && end < now + LiveGrace) end = now + LiveGrace;
            return new TimeWindow(f.Kickoff - BeforeKickoff, end);
        });
        return Merge(windows.Where(w => w.End > now));
    }

    // Match windows plus a short window for each prediction reminder still to send
    // (ReminderBackgroundService sends one PollInterval-ish after its time comes).
    public static async Task<IReadOnlyList<TimeWindow>> ForBackendAsync(AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        var windows = new List<TimeWindow>(await ForMatchesAsync(db, now, ct));

        var settings = await db.ScraperSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.ReminderEnabled == true)
        {
            var firstKickoffs = await db.MatchWeeks.AsNoTracking()
                .Where(w => w.ReminderSentAt == null)
                .Select(w => w.Fixtures.Where(f => f.Status == FixtureStatus.PreMatch).Min(f => (DateTime?)f.Kickoff))
                .ToListAsync(ct);
            foreach (var kickoff in firstKickoffs.OfType<DateTime>().Where(k => k > now))
            {
                var sendAt = kickoff.AddHours(-settings.ReminderHoursBeforeFirstGame);
                if (sendAt + TimeSpan.FromMinutes(20) > now && sendAt < now + Lookahead)
                    windows.Add(new TimeWindow(sendAt - TimeSpan.FromMinutes(5), sendAt + TimeSpan.FromMinutes(20)));
            }
        }

        return Merge(windows);
    }

    public static bool Contains(IEnumerable<TimeWindow> windows, DateTime at) =>
        windows.Any(w => w.Start <= at && at < w.End);

    private static List<TimeWindow> Merge(IEnumerable<TimeWindow> windows)
    {
        var merged = new List<TimeWindow>();
        foreach (var w in windows.OrderBy(w => w.Start))
        {
            if (merged.Count > 0 && w.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = w.End > merged[^1].End ? w.End : merged[^1].End };
            else
                merged.Add(w);
        }
        return merged;
    }
}
