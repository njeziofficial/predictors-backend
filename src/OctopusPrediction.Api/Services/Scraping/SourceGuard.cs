namespace OctopusPrediction.Api.Services.Scraping;

public record SourceHealth(string Name, int Failures, DateTime? CoolingUntil, string? LastProblem);

// Keeps the scraper from hammering a site that has started refusing it. A site that answers
// 403/429/503 or shows a bot check ("blocked") is left alone for a while, doubling each time it
// happens again; repeated timeouts or empty pages ("failed") get a shorter rest. Retrying a site
// that is already blocking only makes the block longer, and other sources (Fallback/Rotate) carry
// on meanwhile. Shared with AdminController, which shows it on the scraper status card.
public class SourceGuard
{
    private static readonly TimeSpan FirstBlockCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxBlockCooldown = TimeSpan.FromHours(3);
    private static readonly TimeSpan FirstFailureCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxFailureCooldown = TimeSpan.FromHours(1);
    // Failures in a row before resting the source: one timeout is usually just the network.
    private const int FailuresBeforeCooldown = 3;

    private readonly object _lock = new();
    private readonly Dictionary<string, SourceHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCooling(string name, DateTime now)
    {
        lock (_lock)
            return _health.TryGetValue(name, out var h) && h.CoolingUntil > now;
    }

    public DateTime? CoolingUntil(string name)
    {
        lock (_lock)
            return _health.GetValueOrDefault(name)?.CoolingUntil;
    }

    public void RecordSuccess(string name)
    {
        lock (_lock)
            _health.Remove(name);
    }

    // Returns when the source may be tried again.
    public DateTime RecordBlocked(string name, string problem, TimeSpan? retryAfter, DateTime now)
    {
        lock (_lock)
        {
            var failures = (_health.GetValueOrDefault(name)?.Failures ?? 0) + 1;
            var cooldown = Backoff(FirstBlockCooldown, MaxBlockCooldown, failures - 1);
            if (retryAfter > cooldown) cooldown = retryAfter.Value;
            var until = now + Jitter(cooldown);
            _health[name] = new SourceHealth(name, failures, until, problem);
            return until;
        }
    }

    // Returns when the source may be tried again, or null while it hasn't failed often enough to rest.
    public DateTime? RecordFailure(string name, string problem, DateTime now)
    {
        lock (_lock)
        {
            var failures = (_health.GetValueOrDefault(name)?.Failures ?? 0) + 1;
            DateTime? until = failures >= FailuresBeforeCooldown
                ? now + Jitter(Backoff(FirstFailureCooldown, MaxFailureCooldown, failures - FailuresBeforeCooldown))
                : null;
            _health[name] = new SourceHealth(name, failures, until, problem);
            return until;
        }
    }

    // Sources that have had trouble lately, for the admin status card.
    public IReadOnlyList<SourceHealth> Snapshot()
    {
        lock (_lock)
            return [.. _health.Values.OrderBy(h => h.Name)];
    }

    private static TimeSpan Backoff(TimeSpan first, TimeSpan max, int doublings)
    {
        var ticks = first.Ticks * Math.Pow(2, Math.Min(doublings, 16));
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    // ±15%, so retries don't land on a neat, recognisable schedule.
    public static TimeSpan Jitter(TimeSpan span) =>
        span * (0.85 + Random.Shared.NextDouble() * 0.3);
}
