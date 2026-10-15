using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Hubs;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// Chat messages pushed to phones (ChatPushNotifier): only to those without the app in front,
// only if they want message notifications, with or without the text, linking to their own copy
// of the conversation. Plus the "app in front" tracking it relies on.
public class ChatPushTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _services;
    private readonly FakePushService _pushService = new();
    private readonly PresenceTracker _presence = new();

    // Records which endpoint each push went to.
    private sealed class FakePushService : HttpMessageHandler
    {
        public List<string> Endpoints { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Endpoints) Endpoints.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    public ChatPushTests()
    {
        var (publicKey, privateKey) = WebPushCrypto.GenerateKeys();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Push:PublicKey"] = publicKey, ["Push:PrivateKey"] = privateKey, ["Push:Subject"] = "mailto:a@test" })
            .Build();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(sp => new PushKeyProvider(config, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PushKeyProvider>.Instance));
        services.AddScoped(sp => new PushSender(new HttpClient(_pushService), sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<PushKeyProvider>(), NullLogger<PushSender>.Instance));
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private ChatPushNotifier Notifier() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _presence, NullLogger<ChatPushNotifier>.Instance);

    private User AddPlayer(string name, bool messages = true, bool preview = true, bool admin = false)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Name = name, Email = $"{name}@test", Role = admin ? UserRole.Admin : UserRole.User,
            NotifyMessages = messages, ShowMessagePreview = preview,
        };
        db.Users.Add(user);
        using var key = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = user.Id,
            Endpoint = $"https://fcm.googleapis.com/fcm/send/{name}",
            P256dh = WebPushCrypto.Base64Url([0x04, .. q.X!, .. q.Y!]),
            Auth = WebPushCrypto.Base64Url(new byte[16]),
        });
        db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task Pushes_only_to_those_without_the_app_in_front_who_want_message_notifications()
    {
        var looking = AddPlayer("looking");
        var away = AddPlayer("away");
        var background = AddPlayer("background");
        var muted = AddPlayer("muted", messages: false);
        _presence.Track("c1", looking.Id);
        _presence.Track("c2", background.Id);
        _presence.SetActive("c2", false);

        var conversation = Guid.NewGuid();
        var reached = await Notifier().NotifyAsync(
            [.. new[] { looking, away, background, muted }.Select(u => new ChatRecipient(u.Id, conversation))], "Pat", "Hi!", announcement: false);

        Assert.Equal(2, reached);
        Assert.Equal(["/fcm/send/away", "/fcm/send/background"], _pushService.Endpoints.Order());
    }

    [Fact]
    public void The_notification_says_who_from_with_the_message_unless_previews_are_hidden()
    {
        var conversation = Guid.NewGuid();
        var shown = ChatPushNotifier.Message("Pat Player", "Are you predicting Barça to win?", false, preview: true, "/messages/x", conversation);
        Assert.Equal(("Pat Player", "Are you predicting Barça to win?"), (shown.Title, shown.Body));
        Assert.Equal($"chat-{conversation:N}", shown.Tag);

        var hidden = ChatPushNotifier.Message("Pat Player", "Secret plans", false, preview: false, "/messages/x", conversation);
        Assert.Equal("New message from Pat Player", hidden.Body);

        var announcement = ChatPushNotifier.Message("Admin", "Week 9 is open.", true, preview: true, "/messages/x", conversation);
        Assert.Equal("Announcement from Admin", announcement.Title);
    }

    [Fact]
    public void Long_messages_are_cut_to_one_line_of_140_characters()
    {
        Assert.Equal("Hi there", ChatPushNotifier.Preview("  Hi\n\nthere  "));
        var preview = ChatPushNotifier.Preview(new string('a', 500));
        Assert.Equal(140, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public async Task Admins_are_pushed_too_and_sent_to_the_back_office_conversation()
    {
        var admin = AddPlayer("admin", admin: true);
        var player = AddPlayer("player");
        var conversation = Guid.NewGuid();

        Assert.Equal(2, await Notifier().NotifyAsync([new ChatRecipient(admin.Id, conversation), new ChatRecipient(player.Id, conversation)], "Pat", "Hi", false));
        Assert.Equal($"/admin/messages/{conversation}", ChatPushNotifier.UrlFor(UserRole.Admin, conversation));
        Assert.Equal($"/messages/{conversation}", ChatPushNotifier.UrlFor(UserRole.User, conversation));
    }

    [Fact]
    public void A_connection_is_active_until_its_app_goes_to_the_background_and_gone_once_closed()
    {
        var user = Guid.NewGuid();
        Assert.False(_presence.IsActive(user));

        _presence.Track("phone", user);
        _presence.Track("laptop", user);
        Assert.True(_presence.IsActive(user));

        _presence.SetActive("phone", false);
        Assert.True(_presence.IsActive(user)); // the laptop is still in front
        _presence.SetActive("laptop", false);
        Assert.False(_presence.IsActive(user));

        _presence.SetActive("phone", true);
        Assert.True(_presence.IsActive(user));
        _presence.Forget("phone");
        Assert.False(_presence.IsActive(user));
    }
}
