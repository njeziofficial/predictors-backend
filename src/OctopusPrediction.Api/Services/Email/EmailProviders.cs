using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OctopusPrediction.Api.Services.Email;

// Who an email comes from: "Octopus Prediction <noreply@example.com>".
public record EmailIdentity(string Name, string Address)
{
    public override string ToString() => $"{Name} <{Address}>";

    /// <summary>"Name <a@b>" or a bare "a@b" (with the given name), or null for nothing.</summary>
    public static EmailIdentity? Parse(string? text, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var open = t.LastIndexOf('<');
        if (open >= 0 && t.EndsWith('>'))
        {
            var name = t[..open].Trim().Trim('"');
            return new EmailIdentity(name.Length > 0 ? name : defaultName, t[(open + 1)..^1].Trim());
        }
        return new EmailIdentity(defaultName, t);
    }
}

// One way of sending email. The system admin picks which one is used (EmailSender.ActiveAsync);
// each keeps its own daily and monthly limits, counted from EmailLog by its Name. A provider is
// set up once its secret keys are in config; who emails come from can be changed on the admin
// page (EmailSender.SenderAsync), with the config's sender as the fallback.
public interface IEmailProvider
{
    // Stored in ScraperSettings.EmailProvider and EmailLog.Provider: "Resend", "Brevo", "Gmail".
    string Name { get; }
    bool IsConfigured { get; }
    int DailyLimit { get; }
    int MonthlyLimit { get; }
    // The sender in config, if any.
    EmailIdentity? ConfiguredSender { get; }
    // How many emails one SendAsync call may carry.
    int MaxBatch { get; }

    /// <summary>Sends these emails from this sender; null on success, else why they failed (all of them).</summary>
    Task<string?> SendAsync(IReadOnlyList<EmailMessage> batch, EmailIdentity sender, CancellationToken ct);
}

public static class EmailProviderNames
{
    public const string Auto = "auto";
    public const string Resend = "Resend";
    public const string Brevo = "Brevo";
    public const string Gmail = "Gmail";

    // When the admin hasn't picked one ("auto"), the first of these that's set up is used.
    public static readonly IReadOnlyList<string> Order = [Resend, Brevo, Gmail];
}

internal static class EmailHttp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static StringContent JsonBody(object body) => new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    // Null on success, else the status and the start of the body, or the exception's message.
    public static async Task<string?> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var _ = request;
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            return $"{(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }
}

// resend.com: one email to /emails, several to /emails/batch (up to 100 a request).
public sealed class ResendEmailProvider(HttpClient http, IOptions<EmailSettings> options) : IEmailProvider
{
    public static readonly Uri Api = new("https://api.resend.com/");
    private readonly ResendSettings _s = options.Value.Resend;

    public string Name => EmailProviderNames.Resend;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.ApiKey);
    public int DailyLimit => _s.DailyLimit;
    public int MonthlyLimit => _s.MonthlyLimit;
    public EmailIdentity? ConfiguredSender => EmailIdentity.Parse(_s.From, "Octopus Prediction");
    public int MaxBatch => 100;

    public Task<string?> SendAsync(IReadOnlyList<EmailMessage> batch, EmailIdentity sender, CancellationToken ct)
    {
        var from = sender.ToString();
        object Body(EmailMessage m) => new { from, to = new[] { m.To }, subject = m.Subject, html = m.Html, text = m.Text };
        var request = batch.Count == 1
            ? new HttpRequestMessage(HttpMethod.Post, new Uri(Api, "emails")) { Content = EmailHttp.JsonBody(Body(batch[0])) }
            : new HttpRequestMessage(HttpMethod.Post, new Uri(Api, "emails/batch")) { Content = EmailHttp.JsonBody(batch.Select(Body).ToArray()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.ApiKey);
        return EmailHttp.SendAsync(http, request, ct);
    }
}

// brevo.com's transactional email API, one email a request.
public sealed class BrevoEmailProvider(HttpClient http, IOptions<EmailSettings> options) : IEmailProvider
{
    public static readonly Uri Api = new("https://api.brevo.com/v3/smtp/email");
    private readonly BrevoSettings _s = options.Value.Brevo;

    public string Name => EmailProviderNames.Brevo;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.ApiKey);
    public int DailyLimit => _s.DailyLimit;
    public int MonthlyLimit => _s.MonthlyLimit;
    public EmailIdentity? ConfiguredSender =>
        string.IsNullOrWhiteSpace(_s.SenderEmail) ? null : new EmailIdentity(_s.SenderName, _s.SenderEmail.Trim());
    public int MaxBatch => 1;

    public Task<string?> SendAsync(IReadOnlyList<EmailMessage> batch, EmailIdentity sender, CancellationToken ct)
    {
        var m = batch.Single();
        var request = new HttpRequestMessage(HttpMethod.Post, Api)
        {
            Content = EmailHttp.JsonBody(new
            {
                sender = new { name = sender.Name, email = sender.Address },
                to = new[] { new { email = m.To } },
                subject = m.Subject,
                htmlContent = m.Html,
                textContent = m.Text,
            }),
        };
        request.Headers.Add("api-key", _s.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return EmailHttp.SendAsync(http, request, ct);
    }
}

// The Gmail API: swaps the refresh token for an access token (kept until just before it expires),
// then sends each email as a MIME message to users/me/messages/send.
public sealed class GmailEmailProvider(HttpClient http, IOptions<EmailSettings> options) : IEmailProvider
{
    public static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    public static readonly Uri SendEndpoint = new("https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
    private readonly GmailSettings _s = options.Value.Gmail;

    // Shared across requests: an access token lasts an hour.
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Key, string Token, DateTime ExpiresAt)? _cached;

    public string Name => EmailProviderNames.Gmail;
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_s.ClientId) && !string.IsNullOrWhiteSpace(_s.ClientSecret)
        && !string.IsNullOrWhiteSpace(_s.RefreshToken);
    public int DailyLimit => _s.DailyLimit;
    public int MonthlyLimit => _s.MonthlyLimit;
    public EmailIdentity? ConfiguredSender =>
        string.IsNullOrWhiteSpace(_s.Sender) ? null : new EmailIdentity(_s.SenderName, _s.Sender.Trim());
    public int MaxBatch => 1;

    // Gmail sends as the account the refresh token belongs to: a From address that isn't that
    // account (or one of its verified "Send mail as" addresses) is replaced by it.
    public async Task<string?> SendAsync(IReadOnlyList<EmailMessage> batch, EmailIdentity sender, CancellationToken ct)
    {
        var m = batch.Single();
        var raw = Base64Url(Encoding.UTF8.GetBytes(GmailMime.Build(sender.Name, sender.Address, m)));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (token, tokenError) = await AccessTokenAsync(forceRefresh: attempt > 0, ct);
            if (token is null) return tokenError;
            using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint) { Content = EmailHttp.JsonBody(new { raw }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var error = await EmailHttp.SendAsync(http, request, ct);
            // A 401 means the access token went stale early: get a fresh one and try once more.
            if (error is null || !error.StartsWith("401")) return error;
        }
        return "401: Google refused the access token twice";
    }

    private async Task<(string? Token, string? Error)> AccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var key = $"{_s.ClientId}|{_s.RefreshToken}";
        await TokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is { } c && c.Key == key && c.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
                return (c.Token, null);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _s.ClientId,
                    ["client_secret"] = _s.ClientSecret,
                    ["refresh_token"] = _s.RefreshToken,
                    ["grant_type"] = "refresh_token",
                }),
            };
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // invalid_grant: the refresh token was revoked, or expired because the Google Cloud
                // app is still in "Testing" (tokens then last 7 days). Publish the app and sign in again.
                var hint = body.Contains("invalid_grant") ? " (refresh token revoked or expired: is the Google Cloud app published?)" : "";
                return (null, $"{(int)response.StatusCode}: Google token request failed{hint}");
            }
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _cached = (key, token, DateTime.UtcNow.AddSeconds(expiresIn));
            return (token, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, $"Google token request failed: {ex.Message}");
        }
        finally
        {
            TokenLock.Release();
        }
    }

    internal static void ForgetToken() => _cached = null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// A plain-text-and-HTML email as the MIME message the Gmail API sends (RFC 2045/2047/5322).
// Header values are stripped of line breaks, so nothing in them can add a header; the subject and
// sender name are encoded words, the bodies base64, so any character goes through.
public static class GmailMime
{
    public static string Build(string senderName, string sender, EmailMessage m)
    {
        var boundary = "op-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("From: ").Append(EncodedWord(senderName)).Append(" <").Append(Clean(sender)).Append(">\r\n");
        sb.Append("To: ").Append(Clean(m.To)).Append("\r\n");
        sb.Append("Subject: ").Append(EncodedWord(m.Subject)).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        foreach (var (type, content) in new[] { ("text/plain", m.Text), ("text/html", m.Html) })
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: ").Append(type).Append("; charset=UTF-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(content), Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    private static string Clean(string value) => value.Replace("\r", "").Replace("\n", "").Trim();

    // =?UTF-8?B?...?=, split so no encoded word passes 75 characters.
    private static string EncodedWord(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(Clean(value));
        var words = new List<string>();
        for (var i = 0; i < bytes.Length;)
        {
            // 45 bytes encode to 60 characters; back off so a UTF-8 character isn't split.
            var take = Math.Min(45, bytes.Length - i);
            while (take > 1 && i + take < bytes.Length && (bytes[i + take] & 0xC0) == 0x80) take--;
            words.Add($"=?UTF-8?B?{Convert.ToBase64String(bytes, i, take)}?=");
            i += take;
        }
        return words.Count == 0 ? "\"\"" : string.Join("\r\n ", words);
    }
}
