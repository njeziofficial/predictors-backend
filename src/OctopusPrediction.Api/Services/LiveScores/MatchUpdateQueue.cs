using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.LiveScores;

// Between the scraper and the database. Each poll the scraper hands over everything it read;
// only matches whose fingerprint (MatchReading.Fingerprint) differs from the last one queued are
// written to MatchUpdates, and MatchUpdateProcessor is woken to apply them at once. An unchanged
// poll — most of them, outside the minutes when a match is being played — costs no database work.
public sealed class MatchUpdateQueue(IServiceScopeFactory scopeFactory, ILogger<MatchUpdateQueue> logger)
{
    // Even an unchanged match is queued again after this long. That puts back anything changed
    // behind the scraper's back (an admin edit, a deleted fixture) and covers a restart's empty
    // memory, without waiting for the source to report something new.
    private static readonly TimeSpan ResyncAfter = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (string Fingerprint, DateTime QueuedAt)> _last = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // The last row queued, and the last the processor has taken off the queue (see WaitUntilAppliedAsync).
    private readonly object _lock = new();
    private long _queuedThrough;
    private long _appliedThrough;
    private TaskCompletionSource _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When a source last returned matches, changed or not. Null until the first one since startup.</summary>
    public DateTime? LastScrapedAt { get; private set; }

    /// <summary>Queues the matches that changed since last time. Returns how many were queued.</summary>
    public async Task<int> EnqueueAsync(
        IReadOnlyList<ScrapedMatchDto> matches, string source, string competition, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        LastScrapedAt = now;

        var rows = new List<MatchUpdate>();
        foreach (var m in matches)
        {
            if (string.IsNullOrWhiteSpace(m.Home) || string.IsNullOrWhiteSpace(m.Away)) continue;
            var fixtureId = MatchReading.FixtureId(m.Home, m.Away);
            var fingerprint = MatchReading.Fingerprint(m);
            if (_last.TryGetValue(fixtureId, out var last) && last.Fingerprint == fingerprint && now - last.QueuedAt < ResyncAfter)
                continue;
            // A source may list the same match twice (e.g. FotMob's day and season lists): keep the last.
            rows.RemoveAll(r => r.FixtureId == fixtureId);
            rows.Add(new MatchUpdate
            {
                FixtureId = fixtureId,
                Source = source,
                Competition = competition,
                Fingerprint = fingerprint,
                Payload = JsonSerializer.Serialize(m),
                ScrapedAt = now,
            });
        }
        if (rows.Count == 0) return 0;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MatchUpdates.AddRange(rows);
            await db.SaveChangesAsync(ct);
        }

        // Only once saved: if the save failed, the next poll tries the same matches again.
        foreach (var row in rows) _last[row.FixtureId] = (row.Fingerprint, now);
        lock (_lock) _queuedThrough = Math.Max(_queuedThrough, rows.Max(r => r.Id));
        _wake.Writer.TryWrite(true);
        logger.LogDebug("[LiveScores] Queued {Count} changed of {Total} matches from {Source}", rows.Count, matches.Count, source);
        return rows.Count;
    }

    /// <summary>The next poll queues this match even if it reads the same as now.</summary>
    public void Forget(string fixtureId) => _last.TryRemove(fixtureId, out _);

    /// <summary>The processor has taken every row up to this id off the queue (applied or given up on).</summary>
    public void ReportApplied(long throughId)
    {
        TaskCompletionSource done;
        lock (_lock)
        {
            _appliedThrough = Math.Max(_appliedThrough, throughId);
            done = _applied;
            _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    /// <summary>
    /// Waits until everything queued so far has been applied, or the timeout passes. The scraper
    /// reads the match schedule from Fixtures right after a poll: without this it would see the
    /// fixtures as they were before the poll (none at all, the first time) and sleep too long.
    /// </summary>
    public async Task WaitUntilAppliedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task next;
            lock (_lock)
            {
                if (_appliedThrough >= _queuedThrough) return;
                next = _applied.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return;
            try
            {
                await next.WaitAsync(left, ct);
            }
            catch (TimeoutException)
            {
                return; // Still busy (or failing): read the schedule as it stands.
            }
        }
    }

    /// <summary>Waits until something is queued, or the timeout passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _wake.Reader.WaitToReadAsync(cts.Token);
            _wake.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out: the processor checks the table anyway.
        }
    }
}
