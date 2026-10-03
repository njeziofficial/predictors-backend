using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Data;

// Single-row runtime config for the live scraper. Both the background service and
// AdminController go through here so the "first run" defaults only live in one place.
public static class ScraperSettingsStore
{
    public static async Task<ScraperSettings> GetOrCreateAsync(
        AppDbContext db, LiveScraperSettings defaults, CancellationToken ct = default)
    {
        var settings = await db.ScraperSettings.FindAsync([1], ct);
        if (settings is not null) return settings;

        settings = new ScraperSettings
        {
            Id = 1,
            Enabled = defaults.Enabled,
            PollIntervalSeconds = defaults.PollIntervalSeconds,
            Competition = defaults.Competition
        };
        db.ScraperSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }
}
