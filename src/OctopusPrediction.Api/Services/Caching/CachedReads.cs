using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Competitions;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Dtos.Leaderboard;
using OctopusPrediction.Api.Dtos.MatchWeeks;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;

namespace OctopusPrediction.Api.Services.Caching;

// What the auth checks need about a user on every request.
public record UserAccess(bool IsDisabled, UserRole Role, bool IsSystemUser, IReadOnlyDictionary<string, bool> Permissions)
{
    public bool Can(string permission) => Permissions.GetValueOrDefault(permission);
}

public record SettingsSnapshot(
    bool PredictionsLocked,
    bool RegistrationClosed,
    bool AllowPartialPredictions,
    bool PredictionsFinal,
    bool LockWeekAtFirstKickoff,
    bool AllowLatePredictions);

// Every week with its fixtures, already mapped, plus the lookups the endpoints need.
public sealed class WeeksSnapshot
{
    // Ordered by first kickoff; weeks without fixtures last.
    public required IReadOnlyList<MatchWeekDto> Weeks { get; init; }
    public required IReadOnlyDictionary<string, MatchWeekDto> ById { get; init; }
    // Per competition id: its first week (by kickoff) with a fixture still to play or being played.
    public required IReadOnlyDictionary<string, MatchWeekDto> CurrentByCompetition { get; init; }
    // Ordered by kickoff. Status kept as the enum so filters can tell Live from HalfTime.
    public required IReadOnlyList<(FixtureStatus Status, FixtureDto Fixture)> Fixtures { get; init; }
    public required IReadOnlyDictionary<string, FixtureDto> FixturesById { get; init; }
}

// The competitions in display order, and which are switched off.
public sealed class CompetitionsSnapshot
{
    public required IReadOnlyList<CompetitionDto> All { get; init; }
    // Ids with a row switched off. A competition without a row counts as on (see Competitions).
    public required IReadOnlySet<string> DisabledIds { get; init; }

    // Players only see competitions that are on; admins see every one, to set them up first.
    // An unknown id is never visible, unless the table is empty (not seeded yet, or a test).
    public bool IsVisible(string competitionId, bool isAdmin) =>
        (All.Count == 0 || All.Any(c => c.Id == competitionId)) && (isAdmin || !DisabledIds.Contains(competitionId));
}

// Every cached read in the API: key, region and TTL live here so they're easy to review.
// Values are immutable DTOs/records, never tracked entities, so a cached copy can't be edited
// by one request and saved by accident.
public static class CachedReads
{
    // Checked on every authenticated request (DisabledUserMiddleware) and every admin endpoint.
    public static Task<UserAccess?> UserAccessAsync(this AppCache cache, AppDbContext db, Guid userId) =>
        cache.GetOrCreateAsync($"access:{userId}", CacheRegion.Access, TimeSpan.FromMinutes(10), async () =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return null;
            return new UserAccess(user.IsDisabled, user.Role, user.IsSystemUser,
                await Services.Permissions.EffectiveAsync(db, user));
        });

    public static Task<SettingsSnapshot> SettingsAsync(this AppCache cache, AppDbContext db, LiveScraperSettings defaults) =>
        cache.GetOrCreateAsync("settings", CacheRegion.Settings, TimeSpan.FromMinutes(10), async () =>
        {
            var s = await ScraperSettingsStore.GetOrCreateAsync(db, defaults);
            return new SettingsSnapshot(s.PredictionsLocked, s.RegistrationClosed, s.AllowPartialPredictions,
                s.PredictionsFinal, s.LockWeekAtFirstKickoff, s.AllowLatePredictions);
        });

    // Competitions change only when the system admin switches one, so they live with the settings.
    public static Task<CompetitionsSnapshot> CompetitionsAsync(this AppCache cache, AppDbContext db) =>
        cache.GetOrCreateAsync("competitions", CacheRegion.Settings, TimeSpan.FromMinutes(10), async () =>
        {
            var rows = await db.Competitions.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
            return new CompetitionsSnapshot
            {
                All = rows.Select(c => new CompetitionDto(c.Id, c.Name, c.Enabled, c.SortOrder)).ToList(),
                DisabledIds = rows.Where(c => !c.Enabled).Select(c => c.Id).ToHashSet(),
            };
        });

    public static Task<WeeksSnapshot> WeeksAsync(this AppCache cache, AppDbContext db) =>
        cache.GetOrCreateAsync("weeks", CacheRegion.Fixtures, TimeSpan.FromMinutes(5), async () =>
        {
            var weeks = await db.MatchWeeks.AsNoTracking().Include(w => w.Fixtures).ToListAsync();
            var ordered = weeks
                .OrderBy(w => w.Fixtures.Count == 0 ? DateTime.MaxValue : w.Fixtures.Min(f => f.Kickoff))
                .ToList();

            var dtos = ordered.Select(FixtureMappers.ToDto).ToList();
            var byId = dtos.ToDictionary(w => w.Id);
            var current = ordered
                .Where(w => w.Fixtures.Any(f => f.Status is FixtureStatus.PreMatch or FixtureStatus.Live))
                .GroupBy(w => w.CompetitionId)
                .ToDictionary(g => g.Key, g => byId[g.First().Id]);
            var fixtures = weeks.SelectMany(w => w.Fixtures)
                .OrderBy(f => f.Kickoff)
                .Select(f => (f.Status, FixtureMappers.ToDto(f)))
                .ToList();

            return new WeeksSnapshot
            {
                Weeks = dtos,
                ById = byId,
                CurrentByCompetition = current,
                Fixtures = fixtures,
                FixturesById = fixtures.ToDictionary(f => f.Item2.Id, f => f.Item2),
            };
        });

    // The heaviest query in the app (aggregates over every prediction), and what everyone looks
    // at right after a result comes in. One table per competition: only its weeks' points count.
    public static Task<IReadOnlyList<LeaderboardEntryDto>> OverallLeaderboardAsync(
        this AppCache cache, AppDbContext db, string competitionId) =>
        cache.GetOrCreateAsync<IReadOnlyList<LeaderboardEntryDto>>($"leaderboard:overall:{competitionId}", CacheRegion.Leaderboard, TimeSpan.FromMinutes(5), async () =>
        {
            var weekIds = db.MatchWeeks.Where(w => w.CompetitionId == competitionId).Select(w => w.Id);
            // Points carried over from before the app were all La Liga's.
            var withPrevious = competitionId == Competitions.LaLiga;
            var entries = await db.Users
                .Select(u => new
                {
                    u.Id,
                    u.Name,
                    u.WhatsAppName,
                    // Overall standings include points and correct scores carried over from before the app.
                    PreviousPoints = withPrevious ? u.PreviousPoints.Sum(pp => pp.Points) : 0,
                    TotalPoints = u.Predictions.Where(p => weekIds.Contains(p.WeekId)).Sum(p => p.PointsEarned)
                        + (withPrevious ? u.PreviousPoints.Sum(pp => pp.Points) : 0),
                    CorrectScores = u.Predictions.Count(p => weekIds.Contains(p.WeekId)
                            && p.PointsEarned == ScoringService.CorrectScorePoints)
                        + (withPrevious ? u.PreviousPoints.Sum(pp => pp.CorrectScores) : 0),
                    LastSubmittedAt = u.Predictions.Any(p => weekIds.Contains(p.WeekId))
                        ? (DateTime?)u.Predictions.Where(p => weekIds.Contains(p.WeekId)).Max(p => p.SubmittedAt)
                        : null
                })
                .OrderByDescending(x => x.TotalPoints)
                .ThenByDescending(x => x.CorrectScores)
                .ThenBy(x => x.LastSubmittedAt)
                .ToListAsync();

            return entries.Select((e, i) => new LeaderboardEntryDto(
                e.Id.ToString(), e.Name, e.WhatsAppName, e.TotalPoints, e.PreviousPoints, e.CorrectScores, i + 1, e.LastSubmittedAt
            )).ToList();
        });

    public static Task<IReadOnlyList<LeaderboardEntryDto>> WeekLeaderboardAsync(this AppCache cache, AppDbContext db, string weekId) =>
        cache.GetOrCreateAsync<IReadOnlyList<LeaderboardEntryDto>>($"leaderboard:week:{weekId}", CacheRegion.Leaderboard, TimeSpan.FromMinutes(5), async () =>
        {
            var entries = await db.Users
                .Select(u => new
                {
                    u.Id,
                    u.Name,
                    u.WhatsAppName,
                    TotalPoints = u.Predictions
                        .Where(p => p.WeekId == weekId)
                        .Sum(p => p.PointsEarned),
                    CorrectScores = u.Predictions
                        .Count(p => p.WeekId == weekId && p.PointsEarned == ScoringService.CorrectScorePoints),
                    LastSubmittedAt = u.Predictions.Any(p => p.WeekId == weekId)
                        ? (DateTime?)u.Predictions.Where(p => p.WeekId == weekId).Max(p => p.SubmittedAt)
                        : null
                })
                .OrderByDescending(x => x.TotalPoints)
                .ThenByDescending(x => x.CorrectScores)
                .ThenBy(x => x.LastSubmittedAt)
                .ToListAsync();

            return entries.Select((e, i) => new LeaderboardEntryDto(
                e.Id.ToString(), e.Name, e.WhatsAppName, e.TotalPoints, 0, e.CorrectScores, i + 1, e.LastSubmittedAt
            )).ToList();
        });

    public record DirectoryEntry(Guid Id, string Name, string? WhatsAppName, bool IsAdmin);

    // Everyone who can be messaged. Callers drop themselves and add live presence.
    public static Task<IReadOnlyList<DirectoryEntry>> ChatDirectoryAsync(this AppCache cache, AppDbContext db) =>
        cache.GetOrCreateAsync<IReadOnlyList<DirectoryEntry>>("chat:directory", CacheRegion.People, TimeSpan.FromMinutes(10), async () => await
            db.Users
                .Where(u => !u.IsDisabled)
                .OrderBy(u => u.Name)
                .Select(u => new DirectoryEntry(u.Id, u.Name, u.WhatsAppName, u.Role == UserRole.Admin))
                .ToListAsync());

    // Who is in a conversation. Never changes once a conversation exists, so no region; the TTL
    // just keeps idle conversations from piling up. Hit on every typing signal.
    public static Task<IReadOnlyList<Guid>> ChatParticipantsAsync(this AppCache cache, AppDbContext db, Guid conversationId) =>
        cache.GetOrCreateAsync<IReadOnlyList<Guid>>($"chat:participants:{conversationId}", null, TimeSpan.FromMinutes(30), async () => await
            db.ConversationParticipants
                .Where(p => p.ConversationId == conversationId)
                .Select(p => p.UserId)
                .ToListAsync());
}
