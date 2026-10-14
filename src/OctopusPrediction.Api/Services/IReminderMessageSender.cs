namespace OctopusPrediction.Api.Services;

public interface IReminderMessageSender
{
    // False when nothing was sent because the channel isn't configured.
    Task<bool> SendReminderAsync(string toPhoneNumber, string weekName, DateTime kickoffUtc, CancellationToken ct = default);
}
