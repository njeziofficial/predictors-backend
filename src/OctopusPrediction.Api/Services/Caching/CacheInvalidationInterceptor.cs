using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.Caching;

// Empties the cache regions a save touched — once the save has committed — so no controller
// has to remember to. Bulk ExecuteUpdate/ExecuteDelete bypass this; callers of those that touch
// cached tables must invalidate themselves (see SeedDataController.ClearAllData).
public sealed class CacheInvalidationInterceptor(AppCache cache, LiveUpdateNotifier notifier) : SaveChangesInterceptor
{
    private static readonly Dictionary<Type, CacheRegion[]> RegionsByEntity = new()
    {
        // Deleting a week or fixture cascades to its predictions in the database, so standings too.
        [typeof(MatchWeek)] = [CacheRegion.Fixtures, CacheRegion.Leaderboard],
        [typeof(Fixture)] = [CacheRegion.Fixtures, CacheRegion.Leaderboard],
        [typeof(Prediction)] = [CacheRegion.Leaderboard],
        [typeof(PreviousPoints)] = [CacheRegion.Leaderboard],
        [typeof(User)] = [CacheRegion.Leaderboard, CacheRegion.Access, CacheRegion.People],
        [typeof(ScraperSettings)] = [CacheRegion.Settings],
        [typeof(RolePermission)] = [CacheRegion.Access],
        [typeof(UserPermission)] = [CacheRegion.Access],
    };

    // Columns nothing cached depends on. The scraper stamps Fixture.UpdatedAt on every fixture
    // every poll and every sign-in stamps User.LastLoginAt; counting those would empty the caches
    // constantly for no reason.
    private static readonly Dictionary<Type, HashSet<string>> IgnoredColumns = new()
    {
        [typeof(Fixture)] = [nameof(Fixture.UpdatedAt)],
        [typeof(User)] = [nameof(User.LastLoginAt), nameof(User.PasswordHash), nameof(User.MustResetPassword)],
    };

    private readonly ConditionalWeakTable<DbContext, HashSet<CacheRegion>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        if (context.ChangeTracker.AutoDetectChangesEnabled) context.ChangeTracker.DetectChanges();

        var regions = new HashSet<CacheRegion>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var type = entry.Metadata.ClrType;
            if (!RegionsByEntity.TryGetValue(type, out var affected)) continue;

            if (entry.State == EntityState.Modified && IgnoredColumns.TryGetValue(type, out var ignored)
                && entry.Properties.Where(p => p.IsModified).All(p => ignored.Contains(p.Metadata.Name)))
                continue;

            regions.UnionWith(affected);
        }

        if (regions.Count > 0) _pending.AddOrUpdate(context, regions);
        else _pending.Remove(context);
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var regions)) return;
        _pending.Remove(context);
        cache.Invalidate(regions);
        notifier.Notify(regions);
    }
}
