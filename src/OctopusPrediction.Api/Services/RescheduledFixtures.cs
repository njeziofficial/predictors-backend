using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

/// <summary>
/// Matches moved out of their own week: they kick off after the competition's next week has
/// started (a postponed match being played weeks later, even one postponed before the app had it).
/// Players predict them on their own: each locks at its own kickoff, whatever its week's rules
/// did, and they're never needed to complete the week. Mirrors rescheduledFixtureIds in
/// predictors/src/lib/predictionRules.ts.
/// </summary>
public static class RescheduledFixtures
{
    public static async Task<HashSet<string>> InWeekAsync(AppDbContext db, MatchWeek week, CancellationToken ct = default)
    {
        if (ReminderSchedule.FirstKickoff(week.Fixtures) is not { } start) return [];

        // Every other week's first kickoff (a few dozen a season), as FirstKickoff counts it.
        var weekStarts = await db.Fixtures.AsNoTracking()
            .Where(f => f.Week.CompetitionId == week.CompetitionId && f.WeekId != week.Id)
            .Where(f => f.Status != FixtureStatus.Postponed && f.Status != FixtureStatus.Cancelled)
            .GroupBy(f => f.WeekId)
            .Select(g => g.Min(f => f.Kickoff))
            .ToListAsync(ct);
        var nextWeekStart = weekStarts.Where(k => k > start).Cast<DateTime?>().Min();

        return nextWeekStart is { } next
            ? week.Fixtures.Where(f => f.Kickoff > next).Select(f => f.Id).ToHashSet()
            : [];
    }
}
