using System.Net;
using System.Text;

namespace OctopusPrediction.Api.Services.Email;

// The emails the app sends, each as HTML and plain text. Anything a player or admin wrote (names,
// titles, messages) is HTML-encoded before it goes into the HTML. Inline styles and a single
// table column, as email clients ignore stylesheets and most layout.
public static class EmailTemplates
{
    private const string Brand = "Octopus Prediction";
    private const string Accent = "#f97316";

    public record Email(string Subject, string Html, string Text);

    public static Email PasswordReset(string name, string link) => Build(
        subject: "Reset your Octopus Prediction password",
        heading: "Reset your password",
        paragraphs: [$"Hi {name},", "Someone asked to reset the password for your Octopus Prediction account. If it was you, choose a new one below. The link works once, for the next hour."],
        button: ("Choose a new password", link),
        footer: "If you didn't ask for this, ignore this email: your password stays the same.");

    public static Email Welcome(string name, string email, string? whatsAppName, string link) => Build(
        subject: "Welcome to Octopus Prediction",
        heading: "Welcome to the league",
        paragraphs:
        [
            $"Hi {name},",
            "An admin has made you an Octopus Prediction account. Choose your password to get started. The link works once, for the next 7 days.",
            whatsAppName is { Length: > 0 }
                ? $"You'll sign in with your email ({email}) or your WhatsApp name ({whatsAppName})."
                : $"You'll sign in with your email ({email}).",
        ],
        button: ("Choose your password", link),
        footer: "If the link has expired, ask an admin to send a new one, or use Forgot password on the sign-in page.");

    public static Email Reminder(string name, string week, string competition, string timeUntil, string appUrl) => Build(
        subject: $"{week} predictions: {competition} kicks off {timeUntil}",
        heading: $"{week} predictions",
        paragraphs: [$"Hi {name},", $"{competition} kicks off {timeUntil} and you haven't finished your predictions yet."],
        button: ("Make your predictions", $"{appUrl}/predict"),
        footer: PreferencesFooter(appUrl));

    public static Email Announcement(string name, string title, string body, string link, string appUrl) => Build(
        subject: title,
        heading: title,
        paragraphs: [$"Hi {name},", body],
        button: ("Open Octopus Prediction", link),
        footer: PreferencesFooter(appUrl));

    public static Email Test(string appUrl, string provider) => Build(
        subject: "Octopus Prediction email is working",
        heading: "Email is working",
        paragraphs: [$"This test came from the Octopus Prediction backend through {provider}. Password resets, welcome emails, reminders and announcements can now go out by email."],
        button: ("Open Octopus Prediction", appUrl),
        footer: null);

    private static string PreferencesFooter(string appUrl) =>
        $"You get this by email because notifications aren't turned on in any of your browsers. Choose what you get on your Profile: {appUrl}/profile";

    private static Email Build(string subject, string heading, IReadOnlyList<string> paragraphs, (string Text, string Url) button, string? footer)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);

        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"margin:0;padding:0;background:#f4f4f5;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#18181b\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f4f5;padding:24px 12px\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#ffffff;border-radius:12px;padding:28px\">");
        html.Append($"<tr><td style=\"font-size:13px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:{Accent}\">{E(Brand)}</td></tr>");
        html.Append($"<tr><td style=\"padding-top:8px;font-size:22px;font-weight:700\">{E(heading)}</td></tr>");
        foreach (var p in paragraphs)
            html.Append($"<tr><td style=\"padding-top:14px;font-size:15px;line-height:1.55;white-space:pre-line\">{E(p)}</td></tr>");
        html.Append("<tr><td style=\"padding-top:24px\">");
        html.Append($"<a href=\"{E(button.Url)}\" style=\"display:inline-block;background:{Accent};color:#ffffff;text-decoration:none;font-weight:600;font-size:15px;padding:12px 22px;border-radius:8px\">{E(button.Text)}</a>");
        html.Append("</td></tr>");
        html.Append($"<tr><td style=\"padding-top:16px;font-size:12px;color:#71717a;word-break:break-all\">Or open this link: {E(button.Url)}</td></tr>");
        if (footer is not null)
            html.Append($"<tr><td style=\"padding-top:24px;border-top:1px solid #e4e4e7;margin-top:24px;font-size:12px;line-height:1.5;color:#71717a\">{E(footer)}</td></tr>");
        html.Append("</table></td></tr></table></body></html>");

        var text = new StringBuilder();
        text.AppendLine(heading).AppendLine();
        foreach (var p in paragraphs) text.AppendLine(p).AppendLine();
        text.AppendLine($"{button.Text}: {button.Url}");
        if (footer is not null) text.AppendLine().AppendLine("--").AppendLine(footer);

        return new Email(subject, html.ToString(), text.ToString());
    }
}
