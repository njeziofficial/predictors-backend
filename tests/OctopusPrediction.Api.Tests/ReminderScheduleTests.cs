using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// When prediction reminders go out (ReminderSchedule), who they and admin notifications go to,
// and the checks on what can be sent.
public class ReminderScheduleTests
{
    private static readonly DateTime Kickoff = new(2026, 10, 17, 14, 0, 0, DateTimeKind.Utc);
    // The default: three days, two days and three hours before.
    private static readonly IReadOnlyList<int> Default = [72, 48, 3];

    [Theory]
    [InlineData("72,48,3", new[] { 72, 48, 3 })]
    [InlineData("3, 48, 72", new[] { 72, 48, 3 })]
    [InlineData("24,24,6", new[] { 24, 6 })]
    [InlineData("0,999,abc", new[] { 72, 48, 3 })]
    [InlineData("", new[] { 72, 48, 3 })]
    [InlineData(null, new[] { 72, 48, 3 })]
    public void ParseHours_keeps_valid_times_earliest_reminder_first_or_falls_back_to_the_default(string? text, int[] expected) =>
        Assert.Equal(expected, ReminderSchedule.ParseHours(text));

    [Fact]
    public void Validate_refuses_empty_out_of_range_repeated_or_too_many_times()
    {
        Assert.Null(ReminderSchedule.Validate([72, 48, 3]));
        Assert.Null(ReminderSchedule.Validate([1, 336]));
        Assert.NotNull(ReminderSchedule.Validate([]));
        Assert.NotNull(ReminderSchedule.Validate([0]));
        Assert.NotNull(ReminderSchedule.Validate([337]));
        Assert.NotNull(ReminderSchedule.Validate([3, 3]));
        Assert.NotNull(ReminderSchedule.Validate([1, 2, 3, 4, 5, 6]));
    }

    [Theory]
    [InlineData(72, "3 days")]
    [InlineData(24, "1 day")]
    [InlineData(36, "36 hours")]
    [InlineData(3, "3 hours")]
    [InlineData(1, "1 hour")]
    public void Durations_read_as_days_when_whole_days_and_hours_otherwise(int hours, string expected) =>
        Assert.Equal(expected, ReminderSchedule.Duration(hours));

    [Fact]
    public void Describe_lists_the_times_for_the_audit_log() =>
        Assert.Equal("3 days, 2 days and 3 hours", ReminderSchedule.Describe([3, 72, 48]));

    [Fact]
    public void Nothing_is_due_before_the_first_reminder_time() =>
        Assert.Null(ReminderSchedule.Due(Kickoff, null, Default, Kickoff.AddDays(-3).AddMinutes(-1)));

    [Fact]
    public void Each_reminder_is_due_in_turn_and_only_once()
    {
        var threeDays = Kickoff.AddDays(-3).AddMinutes(10);
        Assert.Equal(72, ReminderSchedule.Due(Kickoff, null, Default, threeDays));
        Assert.Null(ReminderSchedule.Due(Kickoff, threeDays, Default, Kickoff.AddDays(-2).AddMinutes(-1)));

        var twoDays = Kickoff.AddDays(-2).AddMinutes(5);
        Assert.Equal(48, ReminderSchedule.Due(Kickoff, threeDays, Default, twoDays));
        Assert.Null(ReminderSchedule.Due(Kickoff, twoDays, Default, Kickoff.AddHours(-3).AddMinutes(-1)));

        var threeHours = Kickoff.AddHours(-3).AddMinutes(5);
        Assert.Equal(3, ReminderSchedule.Due(Kickoff, twoDays, Default, threeHours));
        Assert.Null(ReminderSchedule.Due(Kickoff, threeHours, Default, Kickoff.AddMinutes(-5)));
    }

    [Fact]
    public void Missed_reminders_collapse_into_the_latest_one()
    {
        // The server slept from before the 3-day reminder until 2 hours before kickoff: one
        // reminder, the 3-hour one, not three at once.
        var now = Kickoff.AddHours(-2);
        Assert.Equal(3, ReminderSchedule.Due(Kickoff, null, Default, now));
        Assert.Null(ReminderSchedule.Due(Kickoff, now, Default, now.AddMinutes(15)));
    }

    [Fact]
    public void Nothing_is_due_once_the_week_has_started() =>
        Assert.Null(ReminderSchedule.Due(Kickoff, null, Default, Kickoff));

    [Fact]
    public void A_week_moved_later_is_reminded_again_on_its_new_dates()
    {
        var sentAt = Kickoff.AddDays(-3);
        var moved = Kickoff.AddDays(7);
        Assert.Null(ReminderSchedule.Due(moved, sentAt, Default, Kickoff));
        Assert.Equal(72, ReminderSchedule.Due(moved, sentAt, Default, moved.AddDays(-3)));
    }

    [Fact]
    public void Pending_times_are_the_reminders_not_yet_sent()
    {
        Assert.Equal([Kickoff.AddDays(-3), Kickoff.AddDays(-2), Kickoff.AddHours(-3)], ReminderSchedule.PendingTimes(Kickoff, null, Default));
        Assert.Equal([Kickoff.AddHours(-3)], ReminderSchedule.PendingTimes(Kickoff, Kickoff.AddDays(-2), Default));
        Assert.Empty(ReminderSchedule.PendingTimes(Kickoff, Kickoff.AddHours(-3), Default));
    }

    [Fact]
    public void The_first_kickoff_counts_played_matches_but_not_postponed_ones()
    {
        Fixture At(int hours, FixtureStatus status) => new() { Id = $"f{hours}", Kickoff = Kickoff.AddHours(hours), Status = status };
        Assert.Equal(Kickoff, ReminderSchedule.FirstKickoff([At(0, FixtureStatus.Ended), At(24, FixtureStatus.PreMatch)]));
        Assert.Equal(Kickoff.AddHours(24), ReminderSchedule.FirstKickoff([At(0, FixtureStatus.Postponed), At(24, FixtureStatus.PreMatch)]));
        Assert.Null(ReminderSchedule.FirstKickoff([At(0, FixtureStatus.Cancelled)]));
    }

    [Theory]
    [InlineData("La Liga — Week 8", "La Liga", "Week 8")]
    [InlineData("Champions League — Round of 16 — 1st leg", "Champions League", "Round of 16 — 1st leg")]
    [InlineData("Week 8", "La Liga", "Week 8")]
    public void The_week_label_drops_the_competition_named_in_the_message(string name, string competition, string expected) =>
        Assert.Equal(expected, ReminderSchedule.WeekLabel(new MatchWeek { Name = name, Competition = competition }));

    [Theory]
    [InlineData(72, "in 3 days")]
    [InlineData(47.9, "in 2 days")]
    [InlineData(25, "in 1 day")]
    [InlineData(3, "in 3 hours")]
    [InlineData(1, "in 1 hour")]
    [InlineData(0.6, "in 35 minutes")]
    [InlineData(0.01, "in 1 minute")]
    public void TimeUntil_reads_naturally(double hours, string expected) =>
        Assert.Equal(expected, ReminderSchedule.TimeUntil(Kickoff, Kickoff.AddHours(-hours)));

    // A week with two open matches and a postponed one, and players in every state.
    private sealed class League : IDisposable
    {
        public readonly AppDbContext Db = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly MatchWeek Week = new() { Id = "week-9", Name = "Week 9", Competition = "La Liga" };
        public readonly User Done, Half, None, Admin, System, Disabled, MutedReminders;

        public League()
        {
            Db.MatchWeeks.Add(Week);
            Db.Fixtures.AddRange(
                new Fixture { Id = "a", WeekId = Week.Id, HomeTeam = "A", AwayTeam = "B", Kickoff = Kickoff },
                new Fixture { Id = "b", WeekId = Week.Id, HomeTeam = "C", AwayTeam = "D", Kickoff = Kickoff.AddDays(1) },
                new Fixture { Id = "c", WeekId = Week.Id, HomeTeam = "E", AwayTeam = "F", Kickoff = Kickoff, Status = FixtureStatus.Postponed });
            User Player(string name, bool admin = false, bool system = false, bool disabled = false, bool reminders = true) =>
                new() { Name = name, Email = $"{name}@test", Role = admin ? UserRole.Admin : UserRole.User, IsSystemUser = system, IsDisabled = disabled, NotifyReminders = reminders };
            Done = Player("done");
            Half = Player("half");
            None = Player("none");
            Admin = Player("admin", admin: true);
            System = Player("system", admin: true, system: true);
            Disabled = Player("disabled", disabled: true);
            MutedReminders = Player("muted", reminders: false);
            Db.Users.AddRange(Done, Half, None, Admin, System, Disabled, MutedReminders);
            Prediction Pick(User u, string fixture) => new() { UserId = u.Id, FixtureId = fixture, WeekId = Week.Id };
            Db.Predictions.AddRange(Pick(Done, "a"), Pick(Done, "b"), Pick(Half, "a"));
            Db.SaveChanges();
        }

        public Task<MatchWeek> LoadWeekAsync() => Db.MatchWeeks.Include(w => w.Fixtures).SingleAsync();
        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Reminders_go_to_active_players_still_to_predict_who_kept_reminders_on()
    {
        using var league = new League();
        var names = (await ReminderSchedule.PlayersStillToPredictAsync(league.Db, await league.LoadWeekAsync())).Select(u => u.Name).Order();
        // Postponed "c" isn't open, so "done" has predicted everything there is to predict;
        // "muted" turned reminders off.
        Assert.Equal(["admin", "half", "none"], names);
    }

    [Fact]
    public async Task Admin_notification_audiences()
    {
        using var league = new League();
        async Task<IEnumerable<string>> Names(string audience, string? weekId = null, Guid[]? ids = null)
        {
            var (players, problem) = await NotificationAudiences.ResolveAsync(league.Db, audience, weekId, ids);
            Assert.Null(problem);
            return players!.Select(u => u.Name).Order();
        }

        Assert.Equal(["admin", "done", "half", "muted", "none"], await Names("everyone"));
        // Chasing a week isn't a reminder: it reaches those who turned reminders off too.
        Assert.Equal(["admin", "half", "muted", "none"], await Names("yet-to-predict", league.Week.Id));
        // Disabled players and the system account can't be picked.
        Assert.Equal(["half"], await Names("selected", ids: [league.Half.Id, league.Disabled.Id, league.System.Id]));

        Assert.NotNull((await NotificationAudiences.ResolveAsync(league.Db, "yet-to-predict", null, null)).Problem);
        Assert.NotNull((await NotificationAudiences.ResolveAsync(league.Db, "selected", null, [])).Problem);
        Assert.NotNull((await NotificationAudiences.ResolveAsync(league.Db, "anyone", null, null)).Problem);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/predict", true)]
    [InlineData("/leaderboard?week=3", true)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("https://evil.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("predict", false)]
    [InlineData("/a b", false)]
    public void Notification_links_must_stay_in_the_app(string url, bool allowed) =>
        Assert.Equal(allowed, AdminNotificationsController.IsAppPath(url));

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://web.push.apple.com/QGx", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", true)]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=x", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://evil.example.com/fcm.googleapis.com", false)]
    [InlineData("https://fcm.googleapis.com.evil.com/x", false)]
    [InlineData("https://localhost:5432/", false)]
    [InlineData("not a url", false)]
    public void Only_real_push_services_are_accepted_as_endpoints(string endpoint, bool allowed) =>
        Assert.Equal(allowed, PushSender.IsAllowedEndpoint(endpoint));
}
