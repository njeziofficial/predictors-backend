using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Checks, on a timer, whether any match week has crossed its admin-configured
// "remind N hours before kickoff" threshold and hasn't been reminded yet, and if so
// messages every non-admin, non-disabled user with a phone number on file.
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
        var sender = scope.ServiceProvider.GetRequiredService<IReminderMessageSender>();

        var settings = await db.ScraperSettings.FindAsync([1], ct);
        if (settings is null || !settings.ReminderEnabled) return;

        var now = DateTime.UtcNow;

        var candidateWeeks = await db.MatchWeeks
            .Include(w => w.Fixtures)
            .Where(w => w.ReminderSentAt == null)
            .ToListAsync(ct);

        foreach (var week in candidateWeeks)
        {
            var firstKickoff = week.Fixtures
                .Where(f => f.Status == FixtureStatus.PreMatch)
                .Select(f => (DateTime?)f.Kickoff)
                .Min();

            // No upcoming fixtures to remind about (empty week, or everything already
            // started/ended) — nothing to send, and nothing to mark either, in case fixtures
            // get added to this week later.
            if (firstKickoff is null) continue;

            var sendAt = firstKickoff.Value.AddHours(-settings.ReminderHoursBeforeFirstGame);
            if (now < sendAt) continue;

            // Past the window entirely (e.g. the service was down when it should've fired) —
            // mark it sent without messaging anyone rather than send a stale/late reminder.
            if (now >= firstKickoff.Value)
            {
                week.ReminderSentAt = now;
                await db.SaveChangesAsync(ct);
                continue;
            }

            var recipients = await db.Users
                .Where(u => u.Role == UserRole.User && !u.IsDisabled && u.PhoneNumber != null && u.PhoneNumber != "")
                .ToListAsync(ct);

            var sent = 0;
            foreach (var user in recipients)
            {
                try
                {
                    await sender.SendReminderAsync(user.PhoneNumber!, week.Name, firstKickoff.Value, ct);
                    sent++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Reminders] Failed to message {Email}", user.Email);
                }
            }

            week.ReminderSentAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[Reminders] Sent {Sent}/{Total} reminders for {Week} (kickoff {Kickoff:u})",
                sent, recipients.Count, week.Name, firstKickoff.Value);
        }
    }
}
