using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Email;

namespace OctopusPrediction.Api.Controllers;

// Email, for the system admin: which providers are set up, which one sends, who emails come from,
// how much of each one's day and month is used, and a test sent to themselves. Secret keys stay
// in config; the sender name and addresses aren't secret, so they can be changed here.
[ApiController]
[Route("api/admin/email")]
[Authorize(Roles = "Admin")]
[SystemUserOnly]
public class AdminEmailController(
    AppDbContext db, EmailSender email, IOptions<LiveScraperSettings> defaults) : ControllerBase
{
    // From: who emails come from now. SavedAddress: the address set on this page (null: none, so
    // ServerAddress, from config, is used).
    public record EmailProviderDto(
        string Name, bool Configured, bool Active, string? From, string? SavedAddress, string? ServerAddress,
        int SentToday, int DailyLimit, int SentThisMonth, int MonthlyLimit);

    public record EmailStatusDto(
        bool Configured, string Choice, string? Active, string AppUrl, int DailyReserve,
        string SenderName, string? SavedSenderName,
        IReadOnlyList<EmailProviderDto> Providers, int FailedToday, string? LastError);

    public record SetEmailProviderRequest(string Provider);

    // Blank fields go back to the sender in config.
    public record SetEmailSendersRequest(
        [MaxLength(100)] string? SenderName,
        [MaxLength(256)] string? Resend,
        [MaxLength(256)] string? Brevo,
        [MaxLength(256)] string? Gmail);

    private static readonly EmailAddressAttribute EmailCheck = new();

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus() => Ok(await StatusAsync());

    // Which provider sends: one that's set up, or "auto" for the first set up.
    [HttpPut("provider")]
    public async Task<IActionResult> SetProvider(SetEmailProviderRequest request)
    {
        var choice = request.Provider.Trim();
        if (!string.Equals(choice, EmailProviderNames.Auto, StringComparison.OrdinalIgnoreCase))
        {
            var provider = email.Providers.FirstOrDefault(p => string.Equals(p.Name, choice, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return BadRequest(new { message = $"Unknown email provider: {choice}" });
            if (!provider.IsConfigured)
                return BadRequest(new { message = $"{provider.Name} isn't set up: add its settings on the server first." });
            choice = provider.Name;
        }
        else choice = EmailProviderNames.Auto;

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        if (settings.EmailProvider != choice)
        {
            var me = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUserId());
            AuditLogger.Log(db, subject: null, actor: me, action: "EmailProviderChanged",
                field: "Email provider", previousValue: settings.EmailProvider, newValue: choice);
            settings.EmailProvider = choice;
            await db.SaveChangesAsync();
        }
        email.ForgetChoice();
        return Ok(await StatusAsync());
    }

    // Who emails come from: the name, and each provider's address.
    [HttpPut("senders")]
    public async Task<IActionResult> SetSenders(SetEmailSendersRequest request)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Replace("\r", "").Replace("\n", "").Trim();
        var name = Clean(request.SenderName);
        var addresses = new (string Provider, string? Address)[]
        {
            (EmailProviderNames.Resend, Clean(request.Resend)),
            (EmailProviderNames.Brevo, Clean(request.Brevo)),
            (EmailProviderNames.Gmail, Clean(request.Gmail)),
        };
        foreach (var (provider, address) in addresses)
            if (address is not null && (!EmailCheck.IsValid(address) || address.Contains('<') || address.Contains(' ')))
                return BadRequest(new { message = $"\"{address}\" isn't an email address ({provider})." });
        if (name is not null && (name.Contains('<') || name.Contains('>') || name.Contains('"')))
            return BadRequest(new { message = "The sender name can't contain <, > or \"." });

        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, defaults.Value);
        var changes = new List<(string Field, string? Previous, string? New)>
        {
            ("Email sender name", settings.EmailSenderName, name),
            ("Resend sender", settings.EmailResendFrom, addresses[0].Address),
            ("Brevo sender", settings.EmailBrevoSender, addresses[1].Address),
            ("Gmail sender", settings.EmailGmailSender, addresses[2].Address),
        }.Where(c => c.Previous != c.New).ToList();

        settings.EmailSenderName = name;
        settings.EmailResendFrom = addresses[0].Address;
        settings.EmailBrevoSender = addresses[1].Address;
        settings.EmailGmailSender = addresses[2].Address;

        if (changes.Count > 0)
        {
            var me = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUserId());
            foreach (var (field, previous, value) in changes)
                AuditLogger.Log(db, subject: null, actor: me, action: "EmailSenderChanged",
                    field: field, previousValue: previous ?? "(server setting)", newValue: value ?? "(server setting)");
            await db.SaveChangesAsync();
        }
        email.ForgetChoice();
        return Ok(await StatusAsync());
    }

    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        var provider = await email.ActiveAsync();
        if (provider is null)
            return BadRequest(new { message = "No email provider is set up: add Resend, Brevo or Gmail settings on the server." });
        var me = await db.Users.FirstAsync(u => u.Id == CurrentUserId());
        var message = EmailTemplates.Test(email.AppUrl, provider.Name);
        var sent = await email.SendAsync(new EmailMessage(me.Email, message.Subject, message.Html, message.Text, EmailKinds.Test, me.Id));
        return sent
            ? Ok(new { message = $"Test sent to {me.Email} through {provider.Name}." })
            : BadRequest(new { message = $"{provider.Name} didn't accept the test. The email status shows why." });
    }

    private async Task<EmailStatusDto> StatusAsync()
    {
        var sender = email;
        var active = await sender.ActiveAsync();
        var saved = await db.ScraperSettings.AsNoTracking().FirstOrDefaultAsync();
        var choice = saved?.EmailProvider ?? EmailProviderNames.Auto;
        var providers = new List<EmailProviderDto>();
        foreach (var name in EmailProviderNames.Order)
        {
            var p = sender.Providers.First(x => x.Name == name);
            var quota = await sender.QuotaAsync(p);
            var savedAddress = name switch
            {
                EmailProviderNames.Resend => saved?.EmailResendFrom,
                EmailProviderNames.Brevo => saved?.EmailBrevoSender,
                _ => saved?.EmailGmailSender,
            };
            providers.Add(new EmailProviderDto(p.Name, p.IsConfigured, p == active,
                (await sender.SenderAsync(p))?.ToString(), savedAddress, p.ConfiguredSender?.Address,
                quota.SentToday, p.DailyLimit, quota.SentThisMonth, p.MonthlyLimit));
        }
        var senderName = saved?.EmailSenderName ?? active?.ConfiguredSender?.Name ?? EmailSender.DefaultSenderName;
        var today = DateTime.UtcNow.Date;
        var failedToday = await db.EmailLogs.CountAsync(e => !e.Sent && e.CreatedAt >= today);
        var lastError = await db.EmailLogs.Where(e => !e.Sent && e.Error != null)
            .OrderByDescending(e => e.CreatedAt).Select(e => e.Provider + ": " + e.Error).FirstOrDefaultAsync();
        return new EmailStatusDto(sender.IsConfigured, choice, active?.Name, sender.AppUrl,
            (await sender.QuotaAsync(active)).DailyReserve, senderName, saved?.EmailSenderName,
            providers, failedToday, lastError);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
