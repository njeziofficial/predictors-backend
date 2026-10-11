using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.LiveScores;

// Applies the scraped readings MatchUpdateQueue wrote to MatchUpdates: updates the fixtures,
// scores predictions whose result changed, and marks the rows done. Woken as soon as anything
// is queued; also checks on a timer, so rows left by a restart or a failed attempt still go through.
internal sealed class MatchUpdateProcessor(
    IServiceScopeFactory scopeFactory,
    MatchUpdateQueue queue,
    ILogger<MatchUpdateProcessor> logger)
    : BackgroundService
{
    // Only a fallback: a poll that queues anything wakes the processor straight away.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int BatchSize = 200;
    // A batch that fails this often is given up on, so one bad row can't hold up every later one.
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Keep going while full batches come back: a backlog is cleared now, not one batch per wake.
                while (await ProcessBatchAsync(ct) == BatchSize) { }
                await queue.WaitAsync(CheckInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveScores] Applying queued match updates failed — retrying");
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    // Returns how many rows it took off the queue.
    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        List<MatchUpdate> rows;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rows = await db.MatchUpdates.AsNoTracking()
                .Where(u => u.ProcessedAt == null)
                .OrderBy(u => u.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
        }
        if (rows.Count == 0) return 0;

        // Only the newest reading of each match matters; older ones still waiting are superseded.
        var latest = rows.GroupBy(r => r.FixtureId).Select(g => g.MaxBy(r => r.Id)!).OrderBy(r => r.Id).ToList();
        var superseded = rows.Except(latest).Select(r => r.Id).ToList();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<FixtureSync>();
            foreach (var group in latest.GroupBy(r => r.Competition))
            {
                var matches = group.Select(r => JsonSerializer.Deserialize<ScrapedMatchDto>(r.Payload)!).ToList();
                foreach (var fixtureId in await sync.SyncAsync(matches, group.Key, ct))
                    queue.Forget(fixtureId);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(latest, ex, ct);
            throw;
        }

        var now = DateTime.UtcNow;
        await MarkAsync(latest.Select(r => r.Id), now, MatchUpdateOutcomes.Applied, ct);
        await MarkAsync(superseded, now, MatchUpdateOutcomes.Superseded, ct);
        queue.ReportApplied(rows.Max(r => r.Id));
        logger.LogDebug("[LiveScores] Applied {Applied} match updates ({Superseded} superseded)", latest.Count, superseded.Count);
        return rows.Count;
    }

    private async Task MarkAsync(IEnumerable<long> ids, DateTime now, string outcome, CancellationToken ct)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.MatchUpdates.Where(u => list.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ProcessedAt, now).SetProperty(u => u.Outcome, outcome), ct);
    }

    private async Task RecordFailureAsync(List<MatchUpdate> rows, Exception ex, CancellationToken ct)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";
        if (error.Length > 500) error = error[..500];
        var now = DateTime.UtcNow;
        var ids = rows.Select(r => r.Id).ToList();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.MatchUpdates.Where(u => ids.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Attempts, u => u.Attempts + 1).SetProperty(u => u.Error, error), ct);

        var givenUp = rows.Where(r => r.Attempts + 1 >= MaxAttempts).ToList();
        if (givenUp.Count == 0) return;
        var givenUpIds = givenUp.Select(r => r.Id).ToList();
        await db.MatchUpdates.Where(u => givenUpIds.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ProcessedAt, now).SetProperty(u => u.Outcome, MatchUpdateOutcomes.Failed), ct);
        // So the next poll queues these matches afresh instead of skipping them as unchanged.
        foreach (var row in givenUp) queue.Forget(row.FixtureId);
        if (givenUp.Count == rows.Count) queue.ReportApplied(rows.Max(r => r.Id));
        logger.LogError("[LiveScores] Gave up on {Count} match updates after {Attempts} attempts: {Error}",
            givenUp.Count, MaxAttempts, error);
    }
}
