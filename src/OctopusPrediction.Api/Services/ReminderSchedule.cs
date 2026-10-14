using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// When prediction reminders go out: one for each configured time before a week's first match,
// kept in hours (by default 72, 48 and 3: three days, two days and three hours before). Each week
// remembers only when it was last reminded (MatchWeek.ReminderSentAt), which is enough: a
// reminder is due once its time has come and nothing has been sent since. If several came due
// together (the server was asleep or down), only the latest is sent, so nobody gets two at once.
public static class ReminderSchedule
{
    public const int MinHours = 1;
    public const int MaxHours = 14 * 24;
    public const int MaxReminders = 5;
    public static readonly IReadOnlyList<int> DefaultHours = [72, 48, 3];
    public const string DefaultHoursText = "72,48,3";

    /// <summary>The saved times in hours, earliest reminder first; the default when nothing valid is saved.</summary>
    public static IReadOnlyList<int> ParseHours(string? text)
    {
        var hours = (text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var h) ? h : 0)
            .Where(h => h is >= MinHours and <= MaxHours)
            .Distinct()
            .OrderDescending()
            .Take(MaxReminders)
            .ToList();
        return hours.Count > 0 ? hours : DefaultHours;
    }

    public static string FormatHours(IEnumerable<int> hours) => string.Join(",", hours.Distinct().OrderDescending());

    /// <summary>"3 days, 2 days and 3 hours": for the audit log.</summary>
    public static string Describe(IEnumerable<int> hours)
    {
        var parts = hours.Distinct().OrderDescending().Select(Duration).ToList();
        return parts.Count > 1 ? $"{string.Join(", ", parts[..^1])} and {parts[^1]}" : parts.FirstOrDefault() ?? "none";
    }

    // Whole days read as days ("2 days"), anything else as hours ("36 hours").
    public static string Duration(int hours) => hours % 24 == 0
        ? hours == 24 ? "1 day" : $"{hours / 24} days"
        : hours == 1 ? "1 hour" : $"{hours} hours";

    /// <summary>Why the times can't be saved, or null if they can.</summary>
    public static string? Validate(IReadOnlyCollection<int> hours)
    {
        if (hours.Count == 0) return "Choose at least one reminder.";
        if (hours.Count > MaxReminders) return $"At most {MaxReminders} reminders a week.";
        if (hours.Any(h => h is < MinHours or > MaxHours)) return "Each reminder must be between 1 hour and 14 days before the first match.";
        if (hours.Distinct().Count() != hours.Count) return "Each reminder time can only be used once.";
        return null;
    }

    /// <summary>
    /// The week's first match: the earliest kickoff that's still going ahead, played or not, so the
    /// reminders don't start over for the second match once the first has been played.
    /// </summary>
    public static DateTime? FirstKickoff(IEnumerable<Fixture> fixtures) =>
        fixtures
            .Where(f => f.Status is not (FixtureStatus.Postponed or FixtureStatus.Cancelled))
            .Select(f => (DateTime?)f.Kickoff)
            .Min();

    /// <summary>
    /// How many hours before the first match the reminder due now was set for, or null if none is
    /// due: none of their times has come, one has already been sent since, or the week has started.
    /// </summary>
    public static int? Due(DateTime firstKickoff, DateTime? lastSentAt, IReadOnlyList<int> hours, DateTime now)
    {
        if (now >= firstKickoff) return null;
        var latest = hours.Where(h => now >= firstKickoff.AddHours(-h)).Order().Cast<int?>().FirstOrDefault();
        if (latest is not { } h) return null;
        return lastSentAt >= firstKickoff.AddHours(-h) ? null : h;
    }

    /// <summary>When each reminder not yet sent is due, for the server's wake-up windows.</summary>
    public static IEnumerable<DateTime> PendingTimes(DateTime firstKickoff, DateTime? lastSentAt, IReadOnlyList<int> hours) =>
        hours.Select(h => firstKickoff.AddHours(-h)).Where(at => !(lastSentAt >= at));

    /// <summary>
    /// Who to remind: active players (admins play too; the system admin account doesn't) who
    /// haven't turned reminders off and are missing a prediction for any of the week's matches
    /// still to be played.
    /// </summary>
    public static Task<List<User>> PlayersStillToPredictAsync(AppDbContext db, MatchWeek week, CancellationToken ct = default) =>
        StillToPredict(db.Users.Where(u => u.NotifyReminders), week).ToListAsync(ct);

    /// <summary>Active players missing a prediction for any of the week's matches still to be played.</summary>
    public static IQueryable<User> StillToPredict(IQueryable<User> users, MatchWeek week)
    {
        var open = week.Fixtures.Where(f => f.Status == FixtureStatus.PreMatch).Select(f => f.Id).ToList();
        if (open.Count == 0) return users.Where(_ => false);
        return users
            .Where(u => !u.IsSystemUser && !u.IsDisabled)
            .Where(u => u.Predictions.Count(p => open.Contains(p.FixtureId)) < open.Count);
    }

    /// <summary>
    /// The week as players see it: "Week 8" from "La Liga — Week 8" (the app's weekLabel), since
    /// the competition is named in the message itself.
    /// </summary>
    public static string WeekLabel(MatchWeek week)
    {
        var prefix = $"{week.Competition} — ";
        return week.Name.StartsWith(prefix, StringComparison.Ordinal) ? week.Name[prefix.Length..] : week.Name;
    }

    /// <summary>"in 3 days", "in 1 day", "in 3 hours": how far off the first match is.</summary>
    public static string TimeUntil(DateTime firstKickoff, DateTime now)
    {
        var left = firstKickoff - now;
        if (left.TotalHours >= 24)
        {
            var days = (int)Math.Round(left.TotalDays);
            return days == 1 ? "in 1 day" : $"in {days} days";
        }
        if (left.TotalMinutes < 60)
        {
            var minutes = Math.Max(1, (int)Math.Round(left.TotalMinutes));
            if (minutes >= 10) minutes = (int)Math.Round(minutes / 5.0) * 5;
            return minutes == 1 ? "in 1 minute" : $"in {minutes} minutes";
        }
        var hours = (int)Math.Round(left.TotalHours);
        return hours == 1 ? "in 1 hour" : $"in {hours} hours";
    }
}
