using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Push;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Email;

namespace OctopusPrediction.Api.Tests;

// Players' in-app notifications: listed with an unread count, read one at a time or all at once,
// and filled by admin notifications (respecting "news from the admins"). Plus push urgency.
public class InboxTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private User AddUser(string name, bool admin = false, bool system = false, bool news = true)
    {
        var u = new User { Name = name, Email = $"{name}@test", Role = admin ? UserRole.Admin : UserRole.User, IsSystemUser = system, NotifyAnnouncements = news };
        _db.Users.Add(u);
        _db.SaveChanges();
        return u;
    }

    private static T As<T>(T controller, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
            },
        };
        return controller;
    }

    private async Task<NotificationsController.InboxDto> InboxOf(Guid userId) =>
        (NotificationsController.InboxDto)((OkObjectResult)await As(new NotificationsController(_db), userId).Get()).Value!;

    [Fact]
    public async Task Reading_or_dismissing_one_lowers_the_count_and_all_can_be_read_at_once()
    {
        var pat = AddUser("pat");
        var sam = AddUser("sam");
        NotificationInbox.Add(_db, [pat.Id], NotificationInbox.Reminder, "Week 9 predictions", "La Liga kicks off in 3 hours.", "/predict");
        NotificationInbox.Add(_db, [pat.Id, sam.Id], NotificationInbox.Announcement, "Fixture change", "Moved to Sunday.", "/predict");
        await _db.SaveChangesAsync();

        var inbox = await InboxOf(pat.Id);
        Assert.Equal(2, inbox.Unread);
        Assert.Equal(2, inbox.Items.Count);

        var controller = As(new NotificationsController(_db), pat.Id);
        Assert.IsType<NoContentResult>(await controller.MarkRead(Guid.Parse(inbox.Items[0].Id)));
        inbox = await InboxOf(pat.Id);
        Assert.Equal(1, inbox.Unread);
        Assert.Single(inbox.Items, i => i.Read);

        // Someone else's can't be touched.
        var samsId = Guid.Parse((await InboxOf(sam.Id)).Items.Single().Id);
        Assert.IsType<NotFoundResult>(await controller.MarkRead(samsId));

        Assert.IsType<NoContentResult>(await controller.MarkAllRead());
        Assert.Equal(0, (await InboxOf(pat.Id)).Unread);
        Assert.Equal(1, (await InboxOf(sam.Id)).Unread);
    }

    [Fact]
    public async Task An_admin_notification_lands_in_the_inbox_of_everyone_who_kept_admin_news_on()
    {
        var system = AddUser("system", admin: true, system: true);
        var pat = AddUser("pat");
        var quiet = AddUser("quiet", news: false);

        var options = Options.Create(new EmailSettings());
        var email = new EmailSender(_db, [], options, NullLogger<EmailSender>.Instance);
        var push = Push(new FakePushService());
        var admin = As(new AdminNotificationsController(_db, push, email), system.Id);

        // Nobody has push or email, yet it can go out: it's in the app.
        var result = await admin.Send(new SendNotificationRequest("Fixture change", "The Barcelona match moved to Sunday.", "/predict", "everyone", null, null, false));
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, (await InboxOf(pat.Id)).Unread);
        Assert.Equal(0, (await InboxOf(quiet.Id)).Unread);
        Assert.Equal("Fixture change", (await InboxOf(pat.Id)).Items.Single().Title);

        // Everyone left out: refused.
        var onlyQuiet = await admin.Send(new SendNotificationRequest("Hi", "Hello", "/", "selected", null, [quiet.Id], false));
        Assert.IsType<BadRequestObjectResult>(onlyQuiet);
    }

    // Records the Urgency header each push was sent with.
    private sealed class FakePushService : HttpMessageHandler
    {
        public List<string> Urgencies { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urgencies.Add(request.Headers.GetValues("Urgency").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    // Push keys from config, so no database lookup is needed for them.
    private PushSender Push(HttpMessageHandler handler)
    {
        var (publicKey, privateKey) = WebPushCrypto.GenerateKeys();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Push:PublicKey"] = publicKey, ["Push:PrivateKey"] = privateKey, ["Push:Subject"] = "mailto:a@test" })
            .Build();
        return new PushSender(new HttpClient(handler), _db, new PushKeyProvider(config, null!, NullLogger<PushKeyProvider>.Instance), NullLogger<PushSender>.Instance);
    }

    private void Subscribe(User user)
    {
        using var key = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        _db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = $"https://fcm.googleapis.com/fcm/send/{user.Name}",
            P256dh = WebPushCrypto.Base64Url([0x04, .. q.X!, .. q.Y!]),
            Auth = WebPushCrypto.Base64Url(new byte[16]),
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Pushes_say_how_urgent_they_are()
    {
        var pat = AddUser("pat");
        Subscribe(pat);
        var service = new FakePushService();
        var push = Push(service);

        Assert.Equal(1, await push.SendAsync([pat.Id], new PushMessage("Test", "Body", "/"), TimeSpan.FromMinutes(5)));
        Assert.Equal(1, await push.SendAsync([pat.Id], new PushMessage("Week 9 predictions", "Body", "/predict"), TimeSpan.FromHours(3), urgency: PushUrgency.High));
        Assert.Equal(["normal", "high"], service.Urgencies);
    }

    [Fact]
    public async Task An_admin_notification_is_pushed_as_high_urgency()
    {
        var system = AddUser("system", admin: true, system: true);
        var pat = AddUser("pat");
        Subscribe(pat);
        var service = new FakePushService();
        var email = new EmailSender(_db, [], Options.Create(new EmailSettings()), NullLogger<EmailSender>.Instance);
        var admin = As(new AdminNotificationsController(_db, Push(service), email), system.Id);

        Assert.IsType<OkObjectResult>(await admin.Send(new SendNotificationRequest("Fixture change", "Moved.", "/predict", "everyone", null, null, false)));
        Assert.Equal(["high"], service.Urgencies);
    }

    [Fact]
    public async Task The_inbox_pages_searches_and_filters_while_the_unread_count_stays_the_whole_inbox()
    {
        var pat = AddUser("pat");
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 1; i <= 5; i++)
            _db.UserNotifications.Add(new UserNotification
            {
                UserId = pat.Id, Kind = NotificationInbox.Announcement, Title = $"Notice {i}",
                Body = i == 3 ? "Kick-off moved to Sunday." : "Nothing much.", CreatedAt = start.AddMinutes(i),
                ReadAt = i == 1 ? DateTime.UtcNow : null,
            });
        await _db.SaveChangesAsync();
        var controller = As(new NotificationsController(_db), pat.Id);

        var page2 = (NotificationsController.InboxDto)((OkObjectResult)await controller.Get(page: 2, pageSize: 2)).Value!;
        Assert.Equal(5, page2.Total);
        Assert.Equal(4, page2.Unread);
        Assert.Equal(["Notice 3", "Notice 2"], page2.Items.Select(i => i.Title));

        var search = (NotificationsController.InboxDto)((OkObjectResult)await controller.Get(q: "SUNDAY")).Value!;
        Assert.Equal("Notice 3", Assert.Single(search.Items).Title);
        Assert.Equal(4, search.Unread);

        var unread = (NotificationsController.InboxDto)((OkObjectResult)await controller.Get(unreadOnly: true)).Value!;
        Assert.Equal(4, unread.Total);
    }

    [Fact]
    public async Task Players_delete_their_own_notifications_only()
    {
        var pat = AddUser("pat");
        var sam = AddUser("sam");
        NotificationInbox.Add(_db, [pat.Id, sam.Id], NotificationInbox.Announcement, "Fixture change", "Moved.", "/");
        NotificationInbox.Add(_db, [pat.Id], NotificationInbox.Reminder, "Week 9", "Predict.", "/predict");
        NotificationInbox.Add(_db, [pat.Id], NotificationInbox.Test, "Test", "On.", "/");
        await _db.SaveChangesAsync();
        var patNotes = await _db.UserNotifications.Where(n => n.UserId == pat.Id).ToListAsync();
        var samNote = await _db.UserNotifications.SingleAsync(n => n.UserId == sam.Id);
        var controller = As(new NotificationsController(_db), pat.Id);

        // Someone else's: not found, and left alone.
        Assert.IsType<NotFoundResult>(await controller.Delete(samNote.Id));
        Assert.IsType<NoContentResult>(await controller.Delete(patNotes.Single(n => n.Title == "Test").Id));

        var many = (NotificationsController.DeletedDto)((OkObjectResult)await controller.DeleteMany(
            new([patNotes.Single(n => n.Title == "Week 9").Id, samNote.Id]))).Value!;
        Assert.Equal(1, many.Deleted);

        patNotes.Single(n => n.Title == "Fixture change").ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var readOnes = (NotificationsController.DeletedDto)((OkObjectResult)await controller.DeleteRead()).Value!;
        Assert.Equal(1, readOnes.Deleted);

        Assert.Empty(await _db.UserNotifications.Where(n => n.UserId == pat.Id).ToListAsync());
        Assert.True(await _db.UserNotifications.AnyAsync(n => n.Id == samNote.Id));
    }

    [Fact]
    public async Task Sending_says_how_many_got_it_in_the_app()
    {
        var system = AddUser("system", admin: true, system: true);
        AddUser("pat");
        AddUser("sam");
        var email = new EmailSender(_db, [], Options.Create(new EmailSettings()), NullLogger<EmailSender>.Instance);
        var admin = As(new AdminNotificationsController(_db, Push(new FakePushService()), email), system.Id);

        var result = (SendNotificationResultDto)((OkObjectResult)await admin.Send(
            new SendNotificationRequest("Fixture change", "Moved.", "/predict", "everyone", null, null, false))).Value!;
        Assert.Equal(0, result.Targeted);
        Assert.Equal(2, result.InApp);
    }
}
