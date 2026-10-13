using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public record CompetitionInfo(string Id, string Name, bool EnabledByDefault);

// The competitions the app knows about. Each is a row in Competitions, added on startup with its
// default; after that the system admin's on/off choice sticks. Adding one (the Premier League,
// the Copa del Rey) is a new line here plus scraper sources for it.
public static class Competitions
{
    public const string LaLiga = "la-liga";
    public const string ChampionsLeague = "champions-league";

    // Weeks from before competitions existed, and anything that doesn't name one, are La Liga's.
    public const string Default = LaLiga;

    public static readonly IReadOnlyList<CompetitionInfo> All =
    [
        new(LaLiga, "La Liga", true),
        new(ChampionsLeague, "Champions League", false),
    ];

    // The id for a competition name as the scraper and the admin week form give it.
    public static string IdFor(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Default
            : new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // Another competition's sources until the system admin picks: FotMob first, a different
    // lead site from La Liga's usual Flashscore, so neither site sees two competitions' polls;
    // Flashscore if FotMob comes back empty or refuses.
    public static ScraperSettings DefaultSourcePlan => new()
    {
        SourceMode = Scraping.SourceModes.Fallback,
        SourceName = "FotMob",
        SourceOrder = "FotMob,Flashscore",
    };

    // A competition's source choice in the shape SourcePlan reads. Not for La Liga, which keeps
    // ScraperSettings.
    public static ScraperSettings SourcePlanOf(Competition c) =>
        c.SourceMode is null ? DefaultSourcePlan : new() { SourceMode = c.SourceMode, SourceName = c.SourceName ?? "", SourceOrder = c.SourceOrder ?? "" };

    // Inserts any competitions the table doesn't have yet; existing rows are left alone.
    public static async Task SeedAsync(AppDbContext db)
    {
        var existing = await db.Competitions.Select(c => c.Id).ToListAsync();
        foreach (var (c, i) in All.Select((c, i) => (c, i)).Where(x => !existing.Contains(x.c.Id)))
            db.Competitions.Add(new Competition { Id = c.Id, Name = c.Name, Enabled = c.EnabledByDefault, SortOrder = i });
        await db.SaveChangesAsync();
    }

    // Ids of the competitions switched off. A competition without a row counts as on, so a
    // database that hasn't been seeded (or a test) behaves as before competitions existed.
    public static async Task<List<string>> DisabledIdsAsync(AppDbContext db, CancellationToken ct = default) =>
        await db.Competitions.AsNoTracking().Where(c => !c.Enabled).Select(c => c.Id).ToListAsync(ct);
}
