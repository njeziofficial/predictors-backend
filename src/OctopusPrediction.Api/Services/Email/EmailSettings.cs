namespace OctopusPrediction.Api.Services.Email;

// Email:* in config; on Render, environment variables like Email__Brevo__ApiKey. Each provider's
// credentials live here, never in the database, so the admin page can choose a provider but
// can't reveal a key. A provider with no credentials is simply unavailable.
public class EmailSettings
{
    // The app's own address, for links in emails (no trailing slash).
    public string AppUrl { get; set; } = "https://octopus-predictor.njezi.workers.dev";
    // Kept back each day for password and welcome emails, which reminders and announcements
    // can't use up.
    public int DailyReserve { get; set; } = 10;

    public ResendSettings Resend { get; set; } = new();
    public BrevoSettings Brevo { get; set; } = new();
    public GmailSettings Gmail { get; set; } = new();
}

public abstract class ProviderSettings
{
    public int DailyLimit { get; set; }
    public int MonthlyLimit { get; set; }
}

// resend.com. Needs a verified domain to email anyone but the account's owner.
public class ResendSettings : ProviderSettings
{
    public ResendSettings() => (DailyLimit, MonthlyLimit) = (100, 3000);
    public string ApiKey { get; set; } = string.Empty;
    // An address on the verified domain, e.g. "Octopus Prediction <noreply@example.com>".
    public string From { get; set; } = string.Empty;
}

// brevo.com. Free plan: 300 a day. Without a domain of your own, Brevo sends from an
// @brevosend.com address in place of the sender's (a gmail.com address can't be authenticated).
public class BrevoSettings : ProviderSettings
{
    public BrevoSettings() => (DailyLimit, MonthlyLimit) = (300, 9000);
    public string ApiKey { get; set; } = string.Empty;
    // A sender verified in Brevo (Senders, domains & dedicated IPs → Senders).
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Octopus Prediction";
}

// The Gmail API, sending as one Gmail account over HTTPS (Render's free tier blocks SMTP).
// A Google account sends to about 500 people a day; 450 leaves room. The refresh token comes
// from a one-time sign-in to that account with the OAuth client below (see the email setup steps).
public class GmailSettings : ProviderSettings
{
    public GmailSettings() => (DailyLimit, MonthlyLimit) = (450, 13500);
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    // The Gmail address the refresh token belongs to; emails come from it.
    public string Sender { get; set; } = string.Empty;
    public string SenderName { get; set; } = "Octopus Prediction";
}
