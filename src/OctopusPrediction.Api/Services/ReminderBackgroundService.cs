using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Email;

namespace OctopusPrediction.Api.Services;

// Checks, on a timer, whether a prediction reminder is due for any week (by default 3 days and
// again 2 days before its first match; see ReminderSchedule), and if so reminds every active
// player who hasn't predicted all of the week's matches yet: by push notification, by email for
// those with push on in no browser (EmailSender), and by WhatsApp/SMS too when Twilio is configured.
internal class ReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReminderBackgroundService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the HTTP server bind first, same as the live scraper.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendRemindersAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Reminders] Check cycle failed");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task CheckAndSendRemindersAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<PushSender>();
        var sms = scope.ServiceProvider.GetRequiredService<IReminderMessageSender>();
        var email = scope.ServiceProvider.GetRequiredService<EmailSender>();

        var settings = await db.ScraperSettings.FindAsync([1], ct);
        if (settings is null || !settings.ReminderEnabled) return;

        var hours = ReminderSchedule.ParseHours(settings.ReminderOffsetsHours);
        var now = DateTime.UtcNow;
        var horizon = now.AddHours(hours.Max());

        // A competition switched off gets no reminders. Only weeks with a match still to come
        // within reach of the earliest reminder can have one due.
        var disabled = await Competitions.DisabledIdsAsync(db, ct);
        var candidateWeeks = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .Where(w => !disabled.Contains(w.CompetitionId)
                && w.Fixtures.Any(f => f.Status == FixtureStatus.PreMatch && f.Kickoff > now && f.Kickoff <= horizon))
            .ToListAsync(ct);

        foreach (var week in candidateWeeks)
        {
            if (ReminderSchedule.FirstKickoff(week.Fixtures) is not { } firstKickoff) continue;
            if (ReminderSchedule.Due(firstKickoff, week.ReminderSentAt, hours, now) is not { } hoursBefore) continue;

            var recipients = await ReminderSchedule.PlayersStillToPredictAsync(db, week, ct);
            var message = new PushMessage(
                Title: $"{ReminderSchedule.WeekLabel(week)} predictions",
                Body: $"{week.Competition} kicks off {ReminderSchedule.TimeUntil(firstKickoff, now)} and you haven't finished your predictions.",
                Url: "/predict",
                Tag: $"reminder-{week.Id}");
            // Kept by the push service until the phone is next online, but not past kickoff.
            var pushed = await push.SendAsync([.. recipients.Select(u => u.Id)], message, firstKickoff - now, ct);

            // Players push can't reach (on in none of their browsers) get it by email, unless
            // they turned emails off.
            var emailed = 0;
            if (email.IsConfigured)
            {
                var withPush = await push.WithSubscriptionsAsync(recipients.Select(u => u.Id), ct);
                var timeUntil = ReminderSchedule.TimeUntil(firstKickoff, now);
                var emails = recipients
                    .Where(u => u.NotifyByEmail && !withPush.Contains(u.Id))
                    .Select(u =>
                    {
                        var e = EmailTemplates.Reminder(u.Name, ReminderSchedule.WeekLabel(week), week.Competition, timeUntil, email.AppUrl);
                        return new EmailMessage(u.Email, e.Subject, e.Html, e.Text, EmailKinds.Reminder, u.Id);
                    })
                    .ToList();
                emailed = await email.SendManyAsync(emails, ct);
            }

            var texted = 0;
            foreach (var user in recipients.Where(u => !string.IsNullOrWhiteSpace(u.PhoneNumber)))
            {
                try
                {
                    if (await sms.SendReminderAsync(user.PhoneNumber!, week.Name, firstKickoff, ct)) texted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Reminders] Failed to message {Email}", user.Email);
                }
            }

            week.ReminderSentAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[Reminders] {Before} reminder for {Week} (kickoff {Kickoff:u}): {Count} players still to predict, {Pushed} by push, {Emailed} by email, {Texted} by message",
                ReminderSchedule.Duration(hoursBefore), week.Name, firstKickoff, recipients.Count, pushed, emailed, texted);
        }
    }
}
