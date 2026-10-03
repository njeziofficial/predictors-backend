namespace OctopusPrediction.Api.Services;

public class TwilioSettings
{
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    // "whatsapp" or "sms" — which channel reminders go out on.
    public string Channel { get; set; } = "whatsapp";
    // Twilio WhatsApp sender, e.g. "whatsapp:+14155238886" (the shared sandbox number every
    // trial account gets — swap for your own approved WhatsApp Business sender in production).
    public string WhatsAppFromNumber { get; set; } = string.Empty;
    // Twilio SMS-capable phone number, e.g. "+15017122661" — only needed if Channel is "sms".
    public string SmsFromNumber { get; set; } = string.Empty;
}
