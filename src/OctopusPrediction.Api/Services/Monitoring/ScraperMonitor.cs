using System.Threading.Channels;

namespace OctopusPrediction.Api.Services.Monitoring;

public record PollRecord(
    DateTime At,
    int DurationMs,
    // The sources this poll would try, in order (SourcePlan).
    IReadOnlyList<string> Plan,
    // The source whose matches were used; null when none returned any.
    string? Source,
    int Matches,
    // How many of them had changed and were queued (MatchUpdateQueue).
    int Queued,
    // Ok, NoMatches (every source tried came back empty or failed) or Resting (every source cooling down).
    string Outcome,
    string? Problem,
    // Which competition the poll was for.
    string Competition = "La Liga");

// Why the scraper's wait ended.
public enum WakeReason
{
    Elapsed,
    // "Poll now" on the Scraper Health page: every competition polls.
    PollNow,
    // A competition was switched on: only the competitions other than La Liga poll.
    CompetitionSwitchedOn,
}

public record SourceActivity(string Name, DateTime? LastAttemptAt, DateTime? LastSuccessAt, int LastMatches, int LastDurationMs, string? LastResult);

// What the live scraper and the points check are doing, kept in memory for the system admin's
// Scraper Health page. Reset by a restart: the page shows when this process started.
public sealed class ScraperMonitor
{
    private const int MaxPolls = 60;

    private readonly object _lock = new();
    private readonly LinkedList<PollRecord> _polls = new();
    private readonly Dictionary<string, SourceActivity> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _pollNow = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    // ── Scraper state ───────────────────────────────────────────────────────────────────────
    /// <summary>Starting, Polling, Waiting, Paused or Restarting.</summary>
    public string Phase { get; private set; } = "Starting";
    /// <summary>Why it's in that phase, in plain words.</summary>
    public string? PhaseDetail { get; private set; }
    public DateTime? NextPollAt { get; private set; }
    /// <summary>InPlay, MatchWindow (window open, nothing being played) or NoMatch; null until the first poll.</summary>
    public string? Schedule { get; private set; }

    public void SetPhase(string phase, string? detail = null, DateTime? nextPollAt = null)
    {
        lock (_lock)
        {
            Phase = phase;
            PhaseDetail = detail;
            NextPollAt = nextPollAt;
        }
    }

    public void SetSchedule(string schedule)
    {
        lock (_lock) Schedule = schedule;
    }

    // ── Polls and sources ───────────────────────────────────────────────────────────────────
    public void RecordPoll(PollRecord poll)
    {
        lock (_lock)
        {
            _polls.AddFirst(poll);
            while (_polls.Count > MaxPolls) _polls.RemoveLast();
        }
    }

    public void RecordSourceAttempt(string name, DateTime at, int matches, int durationMs, string result)
    {
        lock (_lock)
        {
            var last = _sources.GetValueOrDefault(name);
            _sources[name] = new SourceActivity(name, at,
                matches > 0 ? at : last?.LastSuccessAt, matches, durationMs, result);
        }
    }

    public IReadOnlyList<PollRecord> Polls()
    {
        lock (_lock) return [.. _polls];
    }

    public IReadOnlyList<SourceActivity> Sources()
    {
        lock (_lock) return [.. _sources.Values];
    }

    // ── Points check (PointsReconciliationBackgroundService) ────────────────────────────────
    public DateTime? ReconcileLastRunAt { get; private set; }
    public int ReconcileLastCorrections { get; private set; }
    public string? ReconcileLastError { get; private set; }

    public void RecordReconcile(DateTime at, int corrections, string? error)
    {
        lock (_lock)
        {
            ReconcileLastRunAt = at;
            ReconcileLastCorrections = corrections;
            ReconcileLastError = error;
        }
    }

    // ── Poll now ────────────────────────────────────────────────────────────────────────────
    /// <summary>Cuts the scraper's current wait short so it polls straight away.</summary>
    public void RequestPollNow() => _pollNow.Writer.TryWrite(true);

    /// <summary>Wakes the scraper to poll the competitions other than La Liga (one was just switched on).</summary>
    public void RequestCompetitionPoll() => _competitionPoll.Writer.TryWrite(true);

    private readonly Channel<bool> _competitionPoll = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// The scraper's wait between polls: returns early, saying why, when a poll is requested or
    /// a competition is switched on. Elapsed otherwise.
    /// </summary>
    public async Task<WakeReason> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return WakeReason.Elapsed;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(delay);
        var pollNow = _pollNow.Reader.WaitToReadAsync(cts.Token).AsTask();
        var switchedOn = _competitionPoll.Reader.WaitToReadAsync(cts.Token).AsTask();
        // Ends when either signal arrives or the delay runs out (both waits are then cancelled).
        await Task.WhenAny(pollNow, switchedOn);
        cts.Cancel(); // let go of the other wait
        ct.ThrowIfCancellationRequested();

        if (_pollNow.Reader.TryRead(out _)) return WakeReason.PollNow;
        if (_competitionPoll.Reader.TryRead(out _)) return WakeReason.CompetitionSwitchedOn;
        return WakeReason.Elapsed;
    }
}
