using Microsoft.AspNetCore.SignalR;
using OctopusPrediction.Api.Hubs;

namespace OctopusPrediction.Api.Services.Caching;

// Tells every open browser which shared data just changed (over the chat hub connection they
// already hold), so the frontend can keep its copies for a long time and refetch only when
// told, instead of polling. Bursts — a scrape scoring many predictions, players submitting
// near a deadline — are coalesced into one message per window.
public sealed class LiveUpdateNotifier(IHubContext<ChatHub, IChatClient> hub, ILogger<LiveUpdateNotifier> logger)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);

    // Only data every player sees; per-user data (access, the directory) isn't announced.
    private static readonly Dictionary<CacheRegion, string> Announced = new()
    {
        [CacheRegion.Fixtures] = "fixtures",
        [CacheRegion.Leaderboard] = "leaderboard",
        [CacheRegion.Settings] = "settings",
    };

    private readonly HashSet<string> _pending = [];
    private readonly object _lock = new();
    private bool _scheduled;

    public void Notify(IEnumerable<CacheRegion> regions)
    {
        lock (_lock)
        {
            foreach (var region in regions)
                if (Announced.TryGetValue(region, out var name)) _pending.Add(name);
            if (_pending.Count == 0 || _scheduled) return;
            _scheduled = true;
        }
        _ = SendAfterWindowAsync();
    }

    private async Task SendAfterWindowAsync()
    {
        await Task.Delay(Window);
        string[] areas;
        lock (_lock)
        {
            areas = [.. _pending];
            _pending.Clear();
            _scheduled = false;
        }

        try
        {
            await hub.Clients.All.DataChanged(areas);
        }
        catch (Exception ex)
        {
            // Clients fall back to their own refetch intervals.
            logger.LogWarning(ex, "Couldn't announce data changes ({Areas})", string.Join(", ", areas));
        }
    }
}
