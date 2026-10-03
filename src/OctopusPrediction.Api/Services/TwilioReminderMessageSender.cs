using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace OctopusPrediction.Api.Services;

// Registered as a singleton so TwilioClient.Init runs exactly once. Silently no-ops (with a
// warning log) when credentials aren't configured yet, rather than throwing — reminders being
// unconfigured shouldn't take down the rest of the app.
public class TwilioReminderMessageSender : IReminderMessageSender
{
    private readonly TwilioSettings _settings;
    private readonly ILogger<TwilioReminderMessageSender> _logger;
    private readonly bool _configured;

    public TwilioReminderMessageSender(IOptions<TwilioSettings> settings, ILogger<TwilioReminderMessageSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _configured = !string.IsNullOrWhiteSpace(_settings.AccountSid) && !string.IsNullOrWhiteSpace(_settings.AuthToken);
        if (_configured)
            TwilioClient.Init(_settings.AccountSid, _settings.AuthToken);
        else
            _logger.LogWarning("[Twilio] AccountSid/AuthToken not configured — reminders will be skipped");
    }

    public async Task SendReminderAsync(string toPhoneNumber, string weekName, DateTime kickoffUtc, CancellationToken ct = default)
    {
        if (!_configured)
            return;

        var useWhatsApp = string.Equals(_settings.Channel, "whatsapp", StringComparison.OrdinalIgnoreCase);
        var from = useWhatsApp ? _settings.WhatsAppFromNumber : _settings.SmsFromNumber;

        if (string.IsNullOrWhiteSpace(from))
        {
            _logger.LogWarning(
                "[Twilio] {Channel} sender number not configured — skipping reminder to {To}", _settings.Channel, toPhoneNumber);
            return;
        }

        var normalized = NormalizePhone(toPhoneNumber);
        var to = useWhatsApp ? $"whatsapp:{normalized}" : normalized;
        var body = $"⚽ Reminder: {weekName} kicks off {kickoffUtc:MMM d 'at' HH:mm} UTC. " +
                    "Get your predictions in before it locks!";

        await MessageResource.CreateAsync(
            body: body,
            from: new PhoneNumber(from),
            to: new PhoneNumber(to)
        );
    }

    private static string NormalizePhone(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed}";
    }
}
