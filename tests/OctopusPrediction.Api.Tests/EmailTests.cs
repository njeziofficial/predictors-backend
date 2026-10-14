using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Email;

namespace OctopusPrediction.Api.Tests;

// Email through Resend (against a fake Resend): the free tier's limits, the daily reserve for
// password emails, batching, and the password links from Forgot password and welcome emails.
public class EmailTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly FakeResend _resend = new();

    public void Dispose() => _db.Dispose();

    // Stands in for Resend, Brevo and Google: records what's sent (JSON bodies parsed, others as
    // text) and answers 200, or the status it's told to. Google's token endpoint gets a token.
    private sealed class FakeResend : HttpMessageHandler
    {
        public List<(string Host, string Path, JsonElement Body, string Raw, HttpRequestMessage Request)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        // Statuses to answer the next sends with, before falling back to Status.
        public Queue<HttpStatusCode> Next { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var raw = await request.Content!.ReadAsStringAsync(ct);
            var body = raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[') ? JsonDocument.Parse(raw).RootElement.Clone() : default;
            Requests.Add((request.RequestUri!.Host, request.RequestUri.AbsolutePath, body, raw, request));
            if (request.RequestUri.Host == "oauth2.googleapis.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"access_token":"tok-{{Requests.Count}}","expires_in":3600}""") };
            var status = Next.Count > 0 ? Next.Dequeue() : Status;
            return new HttpResponseMessage(status) { Content = new StringContent("""{"id":"x"}""") };
        }
    }

    private static EmailSettings Settings(bool resend = true, bool brevo = false, bool gmail = false, int daily = 100, int monthly = 3000, int reserve = 10)
    {
        var s = new EmailSettings { AppUrl = "https://app.example.com/", DailyReserve = reserve };
        if (resend) (s.Resend.ApiKey, s.Resend.From) = ("re_test", "Octopus Prediction <noreply@example.com>");
        (s.Resend.DailyLimit, s.Resend.MonthlyLimit) = (daily, monthly);
        if (brevo) (s.Brevo.ApiKey, s.Brevo.SenderEmail) = ("xkeysib-test", "njezichigozie@gmail.com");
        if (gmail) (s.Gmail.ClientId, s.Gmail.ClientSecret, s.Gmail.RefreshToken, s.Gmail.Sender) = ("cid", "secret", "refresh", "njezichigozie@gmail.com");
        return s;
    }

    private EmailSender Sender(EmailSettings settings)
    {
        var options = Options.Create(settings);
        GmailEmailProvider.ForgetToken();
        return new EmailSender(_db,
            [
                new ResendEmailProvider(new HttpClient(_resend), options),
                new BrevoEmailProvider(new HttpClient(_resend), options),
                new GmailEmailProvider(new HttpClient(_resend), options),
            ],
            options, NullLogger<EmailSender>.Instance);
    }

    private EmailSender Sender(bool configured = true, int daily = 100, int monthly = 3000, int reserve = 10) =>
        Sender(Settings(resend: configured, daily: daily, monthly: monthly, reserve: reserve));

    private static EmailMessage Message(string kind, int i = 0) => new($"p{i}@test", "Subject", "<p>Hi</p>", "Hi", kind);

    private void AlreadySent(int count, DateTime? at = null)
    {
        for (var i = 0; i < count; i++)
            _db.EmailLogs.Add(new EmailLog { Provider = EmailProviderNames.Resend, To = "x@test", Kind = EmailKinds.Reminder, Subject = "s", Sent = true, CreatedAt = at ?? DateTime.UtcNow });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Nothing_is_sent_until_email_is_set_up()
    {
        Assert.Equal(0, await Sender(configured: false).SendManyAsync([Message(EmailKinds.Reminder)]));
        Assert.Empty(_resend.Requests);
    }

    [Fact]
    public async Task One_email_goes_to_the_emails_endpoint_and_many_in_batches_of_100()
    {
        var sender = Sender(daily: 1000, monthly: 5000);
        Assert.True(await sender.SendAsync(Message(EmailKinds.Reset)));
        Assert.Equal(150, await sender.SendManyAsync([.. Enumerable.Range(0, 150).Select(i => Message(EmailKinds.Reminder, i))]));

        Assert.Equal("/emails", _resend.Requests[0].Path);
        Assert.Equal(JsonValueKind.Object, _resend.Requests[0].Body.ValueKind);
        Assert.Equal(["/emails/batch", "/emails/batch"], _resend.Requests.Skip(1).Select(r => r.Path));
        Assert.Equal([100, 50], _resend.Requests.Skip(1).Select(r => r.Body.GetArrayLength()));
        Assert.Equal(151, await _db.EmailLogs.CountAsync(e => e.Sent));
    }

    [Fact]
    public async Task Bulk_emails_stop_short_of_the_daily_reserve_but_password_emails_can_use_it()
    {
        AlreadySent(85);
        var sender = Sender();

        // 100 a day, 10 kept back: 5 reminders can still go.
        Assert.Equal(5, await sender.SendManyAsync([.. Enumerable.Range(0, 8).Select(i => Message(EmailKinds.Reminder, i))]));
        Assert.Equal(3, await _db.EmailLogs.CountAsync(e => !e.Sent && e.Error!.Contains("limit")));
        Assert.Equal(0, await sender.SendManyAsync([Message(EmailKinds.Announcement)]));

        // The reserve is there for someone waiting on a password email.
        Assert.True(await sender.SendAsync(Message(EmailKinds.Reset)));
    }

    [Fact]
    public async Task Yesterdays_emails_dont_count_against_today_but_do_against_the_month()
    {
        AlreadySent(100, DateTime.UtcNow.Date.AddMinutes(-1));
        var quota = await Sender(monthly: 105).QuotaAsync();
        if (DateTime.UtcNow.Day == 1) return; // "yesterday" was last month
        Assert.Equal(0, quota.SentToday);
        Assert.Equal(5, quota.Remaining(EmailKinds.Reset));
    }

    [Fact]
    public async Task A_refused_send_is_logged_as_failed()
    {
        _resend.Status = HttpStatusCode.Forbidden;
        Assert.False(await Sender().SendAsync(Message(EmailKinds.Test)));
        var log = await _db.EmailLogs.SingleAsync();
        Assert.False(log.Sent);
        Assert.StartsWith("403", log.Error);
    }

    [Fact]
    public void Templates_encode_what_players_and_admins_wrote()
    {
        var email = EmailTemplates.Announcement("<b>Pat</b>", "Fixture <change>", "Barça & Real <script>x</script>", "https://app.example.com/predict", "https://app.example.com");
        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&amp; Real &lt;script&gt;x&lt;/script&gt;", email.Html);
        Assert.Contains("&lt;b&gt;Pat&lt;/b&gt;", email.Html);
        Assert.Contains("Barça & Real <script>x</script>", email.Text);
        Assert.Equal("Fixture <change>", email.Subject);
    }

    // ── Password links ────────────────────────────────────────────────────────

    private User AddUser(UserRole role = UserRole.User, bool disabled = false)
    {
        var user = new User { Name = "Pat", Email = $"{Guid.NewGuid():N}@test", Role = role, IsDisabled = disabled, PasswordHash = BCrypt.Net.BCrypt.HashPassword("old-password"), MustResetPassword = true };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private AuthController Auth() => new(null!, _db, null!, null!, null!);

    private string LinkToken() =>
        _resend.Requests.Last().Body.GetProperty("text").GetString()!.Split("token=")[1].Split(['\r', '\n', ' '])[0];

    [Fact]
    public async Task Forgot_password_emails_a_link_that_sets_the_password_once()
    {
        var user = AddUser();
        _db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, FamilyId = Guid.NewGuid(), TokenHash = "h", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        _db.SaveChanges();

        Assert.IsType<OkObjectResult>(await Auth().ForgotPassword(new ForgotPasswordRequest(user.Email.ToUpper()), Sender()));
        var sent = _resend.Requests.Single().Body;
        Assert.Equal(user.Email, sent.GetProperty("to")[0].GetString());
        Assert.Contains("https://app.example.com/set-password?token=", sent.GetProperty("html").GetString());
        var token = LinkToken();

        var check = (PasswordTokenDto)((OkObjectResult)await Auth().CheckPasswordToken(new PasswordTokenRequest(token))).Value!;
        Assert.Equal(new PasswordTokenDto(true, PasswordTokenPurposes.Reset, "Pat"), check);

        Assert.IsType<NoContentResult>(await Auth().SetPassword(new SetPasswordRequest(token, "new-password")));
        var saved = await _db.Users.SingleAsync(u => u.Id == user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify("new-password", saved.PasswordHash));
        Assert.False(saved.MustResetPassword);
        // Signed out everywhere else, and the change is in the audit log.
        Assert.NotNull((await _db.RefreshTokens.SingleAsync()).RevokedAt);
        Assert.Contains(await _db.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "PasswordResetByEmail");

        // The link works once.
        Assert.IsType<BadRequestObjectResult>(await Auth().SetPassword(new SetPasswordRequest(token, "again-password")));
        Assert.False(((PasswordTokenDto)((OkObjectResult)await Auth().CheckPasswordToken(new PasswordTokenRequest(token))).Value!).Valid);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_whoever_asks_but_only_emails_active_players()
    {
        var admin = AddUser(UserRole.Admin);
        var disabled = AddUser(disabled: true);
        var answers = new List<IActionResult>
        {
            await Auth().ForgotPassword(new ForgotPasswordRequest("nobody@test"), Sender()),
            await Auth().ForgotPassword(new ForgotPasswordRequest(admin.Email), Sender()),
            await Auth().ForgotPassword(new ForgotPasswordRequest(disabled.Email), Sender()),
        };
        Assert.All(answers, a => Assert.IsType<OkObjectResult>(a));
        Assert.Single(answers.Select(a => JsonSerializer.Serialize(((OkObjectResult)a).Value)).Distinct());
        Assert.Empty(_resend.Requests);
    }

    [Fact]
    public async Task Forgot_password_sends_at_most_three_an_hour_and_only_the_latest_link_works()
    {
        var user = AddUser();
        var tokens = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await Auth().ForgotPassword(new ForgotPasswordRequest(user.Email), Sender());
            if (_resend.Requests.Count > tokens.Count) tokens.Add(LinkToken());
        }
        Assert.Equal(3, tokens.Count);
        Assert.IsType<BadRequestObjectResult>(await Auth().SetPassword(new SetPasswordRequest(tokens[0], "new-password")));
        Assert.IsType<NoContentResult>(await Auth().SetPassword(new SetPasswordRequest(tokens[2], "new-password")));
    }

    [Fact]
    public async Task An_expired_link_or_one_for_a_disabled_player_doesnt_work()
    {
        var user = AddUser();
        var token = await PasswordTokens.CreateAsync(_db, user, PasswordTokenPurposes.Reset);
        await _db.SaveChangesAsync();
        (await _db.PasswordTokens.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Auth().SetPassword(new SetPasswordRequest(token, "new-password")));

        var other = AddUser();
        var welcome = await PasswordTokens.CreateAsync(_db, other, PasswordTokenPurposes.Welcome);
        await _db.SaveChangesAsync();
        other.IsDisabled = true;
        await _db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await Auth().SetPassword(new SetPasswordRequest(welcome, "new-password")));
    }

    [Fact]
    public async Task A_welcome_link_lasts_a_week()
    {
        var user = AddUser();
        Assert.True(await PasswordTokens.SendAsync(_db, Sender(), user, PasswordTokenPurposes.Welcome));
        var link = await _db.PasswordTokens.SingleAsync();
        Assert.Equal(PasswordTokenPurposes.Welcome, link.Purpose);
        Assert.InRange(link.ExpiresAt - link.CreatedAt, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        Assert.Equal("Welcome to Octopus Prediction", _resend.Requests.Single().Body.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Forgot_password_says_so_when_email_isnt_set_up()
    {
        var result = await Auth().ForgotPassword(new ForgotPasswordRequest("anyone@test"), Sender(configured: false));
        Assert.Equal(503, ((ObjectResult)result).StatusCode);
    }

    // ── Providers ─────────────────────────────────────────────────────────────

    private void Choose(string provider)
    {
        _db.ScraperSettings.Add(new ScraperSettings { Id = 1, EmailProvider = provider });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Brevo_sends_one_email_a_request_with_its_api_key()
    {
        var sender = Sender(Settings(resend: false, brevo: true));
        Assert.Equal(2, await sender.SendManyAsync([Message(EmailKinds.Reminder, 1), Message(EmailKinds.Reminder, 2)]));

        Assert.Equal(2, _resend.Requests.Count);
        var (host, path, body, _, request) = _resend.Requests[0];
        Assert.Equal(("api.brevo.com", "/v3/smtp/email"), (host, path));
        Assert.Equal("xkeysib-test", request.Headers.GetValues("api-key").Single());
        Assert.Equal("njezichigozie@gmail.com", body.GetProperty("sender").GetProperty("email").GetString());
        Assert.Equal("p1@test", body.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("<p>Hi</p>", body.GetProperty("htmlContent").GetString());
        Assert.Equal("Hi", body.GetProperty("textContent").GetString());
        Assert.All(await _db.EmailLogs.ToListAsync(), l => Assert.Equal("Brevo", l.Provider));
    }

    [Fact]
    public async Task Gmail_gets_an_access_token_once_then_sends_each_email_as_a_mime_message()
    {
        var sender = Sender(Settings(resend: false, gmail: true));
        Assert.Equal(2, await sender.SendManyAsync([Message(EmailKinds.Reminder, 1), Message(EmailKinds.Reminder, 2)]));

        var token = _resend.Requests.Where(r => r.Host == "oauth2.googleapis.com").ToList();
        Assert.Single(token);
        Assert.Contains("grant_type=refresh_token", token[0].Raw);
        Assert.Contains("refresh_token=refresh", token[0].Raw);

        var sends = _resend.Requests.Where(r => r.Host == "gmail.googleapis.com").ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal("/gmail/v1/users/me/messages/send", sends[0].Path);
        Assert.Equal("Bearer", sends[0].Request.Headers.Authorization!.Scheme);
        var mime = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            sends[0].Body.GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/').PadRight(4 * ((sends[0].Body.GetProperty("raw").GetString()!.Length + 3) / 4), '=')));
        Assert.Contains("To: p1@test\r\n", mime);
        Assert.Contains("<njezichigozie@gmail.com>", mime);
        Assert.Contains("Content-Type: multipart/alternative", mime);
        Assert.Contains("Content-Type: text/html; charset=UTF-8", mime);
    }

    [Fact]
    public async Task Gmail_tries_once_more_with_a_fresh_token_after_a_401()
    {
        _resend.Next.Enqueue(HttpStatusCode.Unauthorized);
        Assert.True(await Sender(Settings(resend: false, gmail: true)).SendAsync(Message(EmailKinds.Test)));
        Assert.Equal(2, _resend.Requests.Count(r => r.Host == "oauth2.googleapis.com"));
        Assert.Equal(2, _resend.Requests.Count(r => r.Host == "gmail.googleapis.com"));
    }

    [Fact]
    public void Mime_headers_cant_be_injected_and_any_subject_goes_through()
    {
        var mime = GmailMime.Build("Octopus\r\nBcc: evil@x", "me@gmail.com",
            new EmailMessage("pat@test\r\nBcc: evil@x", "Barça ⚽ week 9\r\nBcc: evil@x", "<p>Hi</p>", "Hi", EmailKinds.Test));
        var headers = mime[..mime.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.DoesNotContain("\r\nBcc:", headers);
        Assert.Contains("To: pat@testBcc: evil@x\r\n", headers);
        Assert.Contains("Subject: =?UTF-8?B?", headers);
    }

    [Fact]
    public async Task The_admins_choice_sends_when_it_is_set_up_else_the_first_set_up()
    {
        var all = Settings(resend: true, brevo: true, gmail: true);

        Assert.Equal("Resend", (await Sender(all).ActiveAsync())!.Name); // auto: Resend first
        Choose("Brevo");
        Assert.Equal("Brevo", (await Sender(all).ActiveAsync())!.Name);
        // Chosen but not set up: the first that is.
        Assert.Equal("Resend", (await Sender(Settings(resend: true)).ActiveAsync())!.Name);
        Assert.Null(await Sender(Settings(resend: false)).ActiveAsync());
    }

    [Fact]
    public async Task Each_provider_counts_its_own_emails_against_its_own_limits()
    {
        AlreadySent(100); // Resend's whole day
        Choose("Brevo");
        var sender = Sender(Settings(resend: true, brevo: true));

        var brevo = await sender.QuotaAsync();
        Assert.Equal((0, 300), (brevo.SentToday, brevo.DailyLimit));
        Assert.True(await sender.SendAsync(Message(EmailKinds.Reminder)));

        var resend = await sender.QuotaAsync(sender.Providers.First(p => p.Name == "Resend"));
        Assert.Equal(0, resend.Remaining(EmailKinds.Reset));
    }

    [Fact]
    public async Task The_system_admin_can_choose_only_a_provider_that_is_set_up()
    {
        var admin = AddUser(UserRole.Admin);
        var sender = Sender(Settings(resend: false, brevo: true));
        var controller = new AdminEmailController(_db, sender, Options.Create(new Services.LiveScraperSettings()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, admin.Id.ToString())], "test")),
                },
            },
        };

        Assert.IsType<BadRequestObjectResult>(await controller.SetProvider(new("Resend")));
        Assert.IsType<BadRequestObjectResult>(await controller.SetProvider(new("Mailchimp")));

        var status = (AdminEmailController.EmailStatusDto)((OkObjectResult)await controller.SetProvider(new("brevo"))).Value!;
        Assert.Equal(("Brevo", "Brevo"), (status.Choice, status.Active));
        Assert.Equal(["Resend", "Brevo", "Gmail"], status.Providers.Select(p => p.Name));
        Assert.Equal([false, true, false], status.Providers.Select(p => p.Configured));
        Assert.Contains(await _db.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "EmailProviderChanged");

        Assert.Equal("auto", ((AdminEmailController.EmailStatusDto)((OkObjectResult)await controller.SetProvider(new("auto"))).Value!).Choice);
    }

    // ── Senders ───────────────────────────────────────────────────────────────

    private AdminEmailController EmailAdmin(EmailSender sender)
    {
        var admin = AddUser(UserRole.Admin);
        return new AdminEmailController(_db, sender, Options.Create(new Services.LiveScraperSettings()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, admin.Id.ToString())], "test")),
                },
            },
        };
    }

    [Fact]
    public async Task A_sender_set_on_the_admin_page_replaces_the_servers_and_blank_goes_back_to_it()
    {
        var sender = Sender(Settings(resend: false, brevo: true));
        var admin = EmailAdmin(sender);

        var status = (AdminEmailController.EmailStatusDto)((OkObjectResult)await admin.SetSenders(new("Octopus League", null, "league@example.com", null))).Value!;
        var brevo = status.Providers.Single(p => p.Name == "Brevo");
        Assert.Equal("Octopus League <league@example.com>", brevo.From);
        Assert.Equal(("league@example.com", "njezichigozie@gmail.com"), (brevo.SavedAddress, brevo.ServerAddress));
        Assert.Equal("Octopus League", status.SenderName);

        Assert.True(await sender.SendAsync(Message(EmailKinds.Test)));
        var body = _resend.Requests.Last().Body.GetProperty("sender");
        Assert.Equal(("Octopus League", "league@example.com"), (body.GetProperty("name").GetString(), body.GetProperty("email").GetString()));

        // Blank: back to the server's sender.
        status = (AdminEmailController.EmailStatusDto)((OkObjectResult)await admin.SetSenders(new(" ", null, "", null))).Value!;
        Assert.Equal("Octopus Prediction <njezichigozie@gmail.com>", status.Providers.Single(p => p.Name == "Brevo").From);
        Assert.Contains(await _db.AuditLogs.Select(a => a.Field).ToListAsync(), f => f == "Brevo sender");
    }

    [Fact]
    public async Task Gmail_sends_from_the_address_set_on_the_admin_page()
    {
        _db.ScraperSettings.Add(new ScraperSettings { Id = 1, EmailGmailSender = "league@gmail.com" });
        _db.SaveChanges();
        Assert.True(await Sender(Settings(resend: false, gmail: true)).SendAsync(Message(EmailKinds.Test)));
        var raw = _resend.Requests.Single(r => r.Host == "gmail.googleapis.com").Body.GetProperty("raw").GetString()!;
        var mime = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/').PadRight(4 * ((raw.Length + 3) / 4), '=')));
        Assert.Contains("<league@gmail.com>", mime);
    }

    [Fact]
    public async Task A_provider_with_keys_but_no_sender_sends_nothing_and_says_why()
    {
        var settings = Settings(resend: false, brevo: true);
        settings.Brevo.SenderEmail = "";
        var sender = Sender(settings);

        Assert.True(sender.IsConfigured);
        Assert.False(await sender.SendAsync(Message(EmailKinds.Test)));
        Assert.Empty(_resend.Requests);
        Assert.Contains("No sender address for Brevo", (await _db.EmailLogs.SingleAsync()).Error);
    }

    [Theory]
    [InlineData(null, "not-an-email", null)]
    [InlineData(null, "Name <a@b.com>", null)]
    [InlineData("Octopus <League>", null, null)]
    public async Task Bad_senders_are_refused(string? name, string? brevo, string? gmail)
    {
        var admin = EmailAdmin(Sender(Settings(resend: false, brevo: true)));
        Assert.IsType<BadRequestObjectResult>(await admin.SetSenders(new(name, null, brevo, gmail)));
    }
}
