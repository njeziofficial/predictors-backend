using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Chat;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Hubs;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Tests;

// Images in chat (ChatController.SendImage): only real images, kept in storage under the message,
// shown through a link only to the people in the conversation, and removed from storage when the
// message is deleted.
public class ChatImageTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly FakeStore _store = new();
    private readonly FakeHub _hub = new();
    private readonly User _alice;
    private readonly User _bob;
    private readonly Conversation _chat;

    // Stands in for Supabase Storage: keeps what's put in memory.
    private sealed class FakeStore : IChatImageStore
    {
        public bool IsConfigured { get; set; } = true;
        public Dictionary<string, (byte[] Data, string ContentType)> Objects { get; } = [];
        public Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
        {
            Objects[key] = (data, contentType);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, string>> UrlsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                keys.Where(Objects.ContainsKey).Distinct().ToDictionary(k => k, k => $"https://storage.test/{k}?signed"));
    }

    // Records what the hub would have sent live.
    private sealed class FakeHub : IHubContext<ChatHub, IChatClient>, IHubClients<IChatClient>, IChatClient
    {
        public List<ChatMessageDto> Received { get; } = [];
        public List<ChatMessageDto> Deleted { get; } = [];
        public IHubClients<IChatClient> Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IChatClient All => this;
        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public IChatClient Client(string connectionId) => this;
        IChatClient IHubClients<IChatClient>.Clients(IReadOnlyList<string> connectionIds) => this;
        public IChatClient Group(string groupName) => this;
        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        IChatClient IHubClients<IChatClient>.Groups(IReadOnlyList<string> groupNames) => this;
        public IChatClient User(string userId) => this;
        public IChatClient Users(IReadOnlyList<string> userIds) => this;
        public Task MessageReceived(ChatMessageDto message) { Received.Add(message); return Task.CompletedTask; }
        public Task MessageDeleted(ChatMessageDto message) { Deleted.Add(message); return Task.CompletedTask; }
        public Task ConversationRead(ReadReceiptDto receipt) => Task.CompletedTask;
        public Task Typing(string conversationId, string userId) => Task.CompletedTask;
        public Task PresenceChanged(string userId, bool online) => Task.CompletedTask;
        public Task DataChanged(string[] areas) => Task.CompletedTask;
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    public ChatImageTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _alice = new User { Name = "Alice", Email = "alice@test" };
        _bob = new User { Name = "Bob", Email = "bob@test" };
        _chat = new Conversation { DirectKey = Conversation.KeyFor(_alice.Id, _bob.Id) };
        _chat.Participants.Add(new ConversationParticipant { ConversationId = _chat.Id, UserId = _alice.Id });
        _chat.Participants.Add(new ConversationParticipant { ConversationId = _chat.Id, UserId = _bob.Id });
        _db.AddRange(_alice, _bob, _chat);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private ChatController As(User user)
    {
        // Phone notifications run in the background and aren't what's tested here.
        var pushes = new ChatPushNotifier(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new PresenceTracker(), NullLogger<ChatPushNotifier>.Instance);
        return new ChatController(_db, new AppCache(new MemoryCache(new MemoryCacheOptions())), _hub, new PresenceTracker(), pushes,
            _store, NullLogger<ChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")),
                },
            },
        };
    }

    private static SendImageRequest Image(byte[] bytes, string? caption = null, string? clientId = null) => new()
    {
        File = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "photo.png"),
        Body = caption,
        ClientId = clientId,
        Width = 800,
        Height = 600,
    };

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g' }, null)]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F' }, null)]
    public void Recognises_images_by_their_bytes(byte[] bytes, string? contentType) =>
        Assert.Equal(contentType, ChatImages.Detect(bytes)?.ContentType);

    [Fact]
    public async Task An_image_is_stored_under_its_message_and_sent_with_a_link()
    {
        var sent = (ChatMessageDto)((OkObjectResult)await As(_alice).SendImage(_chat.Id, Image(PngBytes, " Look at this "))).Value!;

        var key = $"chat/{_chat.Id:N}/{Guid.Parse(sent.Id):N}.png";
        Assert.Equal("image/png", _store.Objects[key].ContentType);
        Assert.Equal(new ChatImageDto($"https://storage.test/{key}?signed", 800, 600), sent.Image);
        Assert.Equal("Look at this", sent.Body);
        Assert.Equal(sent.Id, Assert.Single(_hub.Received).Id);

        // Bob sees it in his copy of the conversation too.
        var page = (MessagePageDto)((OkObjectResult)await As(_bob).Messages(_chat.Id, null)).Value!;
        Assert.Equal(sent.Image, Assert.Single(page.Items).Image);
    }

    [Fact]
    public async Task Anything_but_an_image_is_refused()
    {
        Assert.IsType<BadRequestObjectResult>(await As(_alice).SendImage(_chat.Id, Image("<svg onload=alert(1)>"u8.ToArray())));
        Assert.Empty(_store.Objects);
        Assert.Empty(_db.ChatMessages);
    }

    [Fact]
    public async Task Only_someone_in_the_conversation_can_send_an_image_to_it()
    {
        var carol = new User { Name = "Carol", Email = "carol@test" };
        _db.Users.Add(carol);
        _db.SaveChanges();

        Assert.IsType<NotFoundObjectResult>(await As(carol).SendImage(_chat.Id, Image(PngBytes)));
        Assert.Empty(_store.Objects);
    }

    [Fact]
    public async Task Without_storage_set_up_images_are_refused_and_text_still_works()
    {
        _store.IsConfigured = false;

        var refused = (ObjectResult)await As(_alice).SendImage(_chat.Id, Image(PngBytes));
        Assert.Equal(503, refused.StatusCode);
        Assert.IsType<OkObjectResult>(await As(_alice).Send(_chat.Id, new SendMessageRequest { Body = "hi" }));
    }

    [Fact]
    public async Task A_retried_send_is_not_uploaded_twice()
    {
        var first = (ChatMessageDto)((OkObjectResult)await As(_alice).SendImage(_chat.Id, Image(PngBytes, clientId: "c1"))).Value!;
        var again = (ChatMessageDto)((OkObjectResult)await As(_alice).SendImage(_chat.Id, Image(PngBytes, clientId: "c1"))).Value!;

        Assert.Equal(first.Id, again.Id);
        Assert.Single(_store.Objects);
        Assert.Single(_db.ChatMessages);
    }

    [Fact]
    public async Task Deleting_the_message_removes_the_image_from_storage()
    {
        var sent = (ChatMessageDto)((OkObjectResult)await As(_alice).SendImage(_chat.Id, Image(PngBytes))).Value!;

        var deleted = (ChatMessageDto)((OkObjectResult)await As(_alice).Delete(Guid.Parse(sent.Id))).Value!;

        Assert.Null(deleted.Image);
        Assert.Empty(_store.Objects);
        Assert.Null(_db.ChatMessages.Single().ImageKey);
    }
}
