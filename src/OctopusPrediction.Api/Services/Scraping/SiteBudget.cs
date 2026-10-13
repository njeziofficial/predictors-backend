namespace OctopusPrediction.Api.Services.Scraping;

// Pages loaded from each site in the last hour, by every competition. A site that gets many
// more page loads from one visitor than it used to is how a scraper gets noticed, so the
// competitions other than La Liga stay under LiveScraperSettings.MaxPagesPerSitePerHour and
// move to their next source past it. La Liga's pages are counted but never held back.
public sealed class SiteBudget
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTime>> _loads = new(StringComparer.OrdinalIgnoreCase);

    public void RecordPageLoad(string site, DateTime at)
    {
        lock (_lock)
        {
            if (!_loads.TryGetValue(site, out var loads)) _loads[site] = loads = new Queue<DateTime>();
            loads.Enqueue(at);
            Trim(loads, at);
        }
    }

    public int PagesInLastHour(string site, DateTime now)
    {
        lock (_lock)
        {
            if (!_loads.TryGetValue(site, out var loads)) return 0;
            Trim(loads, now);
            return loads.Count;
        }
    }

    public bool HasRoom(string site, DateTime now, int maxPerHour) => PagesInLastHour(site, now) < maxPerHour;

    private static void Trim(Queue<DateTime> loads, DateTime now)
    {
        while (loads.Count > 0 && now - loads.Peek() >= Window) loads.Dequeue();
    }
}
