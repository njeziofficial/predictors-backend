using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Services.LiveScores;

// Keeps MatchUpdates from growing for ever: deletes rows once they've been applied and kept for
// LiveScoreScraper:UpdateRetentionHours. Rows still waiting are never deleted.
internal sealed class MatchUpdateCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<LiveScraperSettings> options,
    ILogger<MatchUpdateCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the HTTP server bind first, same as the other background services.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, options.Value.UpdateRetentionHours));
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var deleted = await db.MatchUpdates
                    .Where(u => u.ProcessedAt != null && u.ProcessedAt < cutoff)
                    .ExecuteDeleteAsync(ct);
                if (deleted > 0)
                    logger.LogInformation("[LiveScores] Deleted {Count} match updates processed before {Cutoff:u}", deleted, cutoff);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LiveScores] Cleaning up old match updates failed");
            }

            await Task.Delay(Interval, ct);
        }
    }
}
