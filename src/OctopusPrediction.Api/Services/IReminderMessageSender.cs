namespace OctopusPrediction.Api.Services;

public interface IReminderMessageSender
{
    Task SendReminderAsync(string toPhoneNumber, string weekName, DateTime kickoffUtc, CancellationToken ct = default);
}
