using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.Email;

public record EmailMessage(string To, string Subject, string Html, string Text, string Kind, Guid? UserId = null);

public record EmailQuota(int SentToday, int SentThisMonth, int DailyLimit, int MonthlyLimit, int DailyReserve)
{
    /// <summary>How many more emails of this kind can go out today and this month.</summary>
    public int Remaining(string kind)
    {
        var daily = DailyLimit - SentToday - (EmailKinds.IsAccount(kind) ? 0 : DailyReserve);
        return Math.Max(0, Math.Min(daily, MonthlyLimit - SentThisMonth));
    }
}

// Sends email through whichever provider the system admin chose (ScraperSettings.EmailProvider:
// Resend, Brevo or the Gmail API), or the first one set up when they haven't chosen. It stays
// inside that provider's free limits: each email is logged with the provider (EmailLog), its
// day's and month's counts are checked first, and a few a day are kept back for password emails.
public sealed class EmailSender(
    AppDbContext db, IEnumerable<IEmailProvider> providers, IOptions<EmailSettings> options, ILogger<EmailSender> logger)
{
    private readonly IReadOnlyList<IEmailProvider> _providers = [.. providers];
    private readonly EmailSettings _settings = options.Value;
    private IEmailProvider? _active;
    private bool _resolved;
    private ScraperSettings? _saved;
    private bool _loaded;

    public const string DefaultSenderName = "Octopus Prediction";

    /// <summary>Whether any provider is set up (has its credentials).</summary>
    public bool IsConfigured => _providers.Any(p => p.IsConfigured);

    public IReadOnlyList<IEmailProvider> Providers => _providers;

    public string AppUrl => _settings.AppUrl.TrimEnd('/');

    /// <summary>The provider in use: the admin's choice if it's set up, else the first set up; null if none is.</summary>
    public async Task<IEmailProvider?> ActiveAsync(CancellationToken ct = default)
    {
        if (_resolved) return _active;
        var chosen = (await SavedAsync(ct))?.EmailProvider;
        _active = _providers.FirstOrDefault(p => p.IsConfigured && string.Equals(p.Name, chosen, StringComparison.OrdinalIgnoreCase))
            ?? EmailProviderNames.Order.Select(n => _providers.FirstOrDefault(p => p.Name == n && p.IsConfigured)).OfType<IEmailProvider>().FirstOrDefault();
        _resolved = true;
        return _active;
    }

    /// <summary>Drops the remembered choice, after the admin changes it.</summary>
    public void ForgetChoice() => (_resolved, _active, _loaded, _saved) = (false, null, false, null);

    private async Task<ScraperSettings?> SavedAsync(CancellationToken ct)
    {
        if (_loaded) return _saved;
        _saved = await db.ScraperSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        _loaded = true;
        return _saved;
    }

    /// <summary>
    /// Who a provider's emails come from: the name and that provider's address set on the admin
    /// page, else the sender in config. Null when it has no address at all.
    /// </summary>
    public async Task<EmailIdentity?> SenderAsync(IEmailProvider provider, CancellationToken ct = default)
    {
        var saved = await SavedAsync(ct);
        var address = (provider.Name switch
        {
            EmailProviderNames.Resend => saved?.EmailResendFrom,
            EmailProviderNames.Brevo => saved?.EmailBrevoSender,
            EmailProviderNames.Gmail => saved?.EmailGmailSender,
            _ => null,
        }) is { Length: > 0 } a ? a.Trim() : provider.ConfiguredSender?.Address;
        if (string.IsNullOrWhiteSpace(address)) return null;
        var name = saved?.EmailSenderName is { Length: > 0 } n ? n.Trim() : provider.ConfiguredSender?.Name ?? DefaultSenderName;
        return new EmailIdentity(name, address);
    }

    /// <summary>The day's and month's counts for a provider (the active one by default).</summary>
    public async Task<EmailQuota> QuotaAsync(IEmailProvider? provider = null, CancellationToken ct = default)
    {
        provider ??= await ActiveAsync(ct);
        if (provider is null) return new EmailQuota(0, 0, 0, 0, _settings.DailyReserve);
        var now = DateTime.UtcNow;
        var today = now.Date;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var sent = db.EmailLogs.Where(e => e.Sent && e.Provider == provider.Name);
        return new EmailQuota(
            await sent.CountAsync(e => e.CreatedAt >= today, ct),
            await sent.CountAsync(e => e.CreatedAt >= month, ct),
            provider.DailyLimit, provider.MonthlyLimit, _settings.DailyReserve);
    }

    /// <summary>Sends one email; false when email isn't set up, the limit is reached, or the provider refused it.</summary>
    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default) =>
        await SendManyAsync([message], ct) == 1;

    /// <summary>
    /// Sends as many of these as today's and this month's limits allow (in order), and returns
    /// how many the provider accepted. All are logged, including those left out by the limit.
    /// </summary>
    public async Task<int> SendManyAsync(IReadOnlyList<EmailMessage> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return 0;
        var provider = await ActiveAsync(ct);
        if (provider is null)
        {
            logger.LogInformation("[Email] No email provider set up: {Count} {Kind} email(s) not sent", messages.Count, messages[0].Kind);
            return 0;
        }

        var sender = await SenderAsync(provider, ct);
        if (sender is null)
        {
            foreach (var m in messages) db.EmailLogs.Add(Log(provider, m, sent: false, $"No sender address for {provider.Name}: set it on Settings → Email"));
            await db.SaveChangesAsync(ct);
            logger.LogWarning("[Email] No sender address for {Provider}: {Count} {Kind} email(s) not sent", provider.Name, messages.Count, messages[0].Kind);
            return 0;
        }

        var quota = await QuotaAsync(provider, ct);
        var allowed = messages.Take(quota.Remaining(messages[0].Kind)).ToList();
        foreach (var skipped in messages.Skip(allowed.Count))
            db.EmailLogs.Add(Log(provider, skipped, sent: false, "Daily or monthly email limit reached"));
        if (allowed.Count < messages.Count)
            logger.LogWarning("[Email] {Provider} limit reached: sending {Allowed} of {Count} {Kind} emails",
                provider.Name, allowed.Count, messages.Count, messages[0].Kind);

        var sent = 0;
        foreach (var batch in allowed.Chunk(provider.MaxBatch))
        {
            var error = await provider.SendAsync(batch, sender, ct);
            foreach (var m in batch) db.EmailLogs.Add(Log(provider, m, sent: error is null, error));
            if (error is null) sent += batch.Length;
            else logger.LogWarning("[Email] {Provider} refused {Count} {Kind} email(s): {Error}", provider.Name, batch.Length, batch[0].Kind, error);
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private static EmailLog Log(IEmailProvider provider, EmailMessage m, bool sent, string? error) => new()
    {
        UserId = m.UserId,
        Provider = provider.Name,
        To = m.To.Length > 256 ? m.To[..256] : m.To,
        Kind = m.Kind,
        Subject = m.Subject.Length > 200 ? m.Subject[..200] : m.Subject,
        Sent = sent,
        Error = error is { Length: > 500 } ? error[..500] : error,
    };
}
