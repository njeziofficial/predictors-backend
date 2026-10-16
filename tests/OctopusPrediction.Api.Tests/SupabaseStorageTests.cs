using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// The Supabase Storage calls behind chat images: where they go, which key they carry, and how
// signed links come back (relative to /storage/v1, and remembered so a page of messages doesn't
// sign the same image twice).
public class SupabaseStorageTests
{
    private const string ProjectUrl = "https://proj.supabase.co/";

    private sealed record Seen(HttpMethod Method, string Url, string? ApiKey, string? Authorization, string? ContentType, string Body);

    // Answers like Supabase Storage and records each request.
    private sealed class FakeSupabase(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Seen> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("apikey", out var key) ? key.Single() : null,
                request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));

            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"Bucket not found\"}") };
            if (request.RequestUri!.AbsolutePath.EndsWith("/object/sign/chat-images"))
            {
                var paths = JsonDocument.Parse(body).RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!);
                var signed = paths.Select(p => new { path = p, signedURL = $"/object/sign/chat-images/{p}?token=t-{p.Length}", error = (string?)null });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(signed), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static SupabaseChatImageStore Store(FakeSupabase fake, string key = "sb_secret_abc") =>
        new(fake, Options.Create(new SupabaseStorageSettings { Url = ProjectUrl, ServiceKey = key }), NullLogger<SupabaseChatImageStore>.Instance);

    [Fact]
    public async Task Uploads_into_the_private_bucket_with_the_secret_key()
    {
        var fake = new FakeSupabase();
        await Store(fake).PutAsync("chat/c1/m1.jpg", [0xFF, 0xD8, 0xFF], "image/jpeg");

        var put = Assert.Single(fake.Requests);
        Assert.Equal((HttpMethod.Post, "https://proj.supabase.co/storage/v1/object/chat-images/chat/c1/m1.jpg"), (put.Method, put.Url));
        Assert.Equal(("sb_secret_abc", (string?)null, "image/jpeg"), (put.ApiKey, put.Authorization, put.ContentType));
    }

    [Fact]
    public async Task A_legacy_service_role_key_also_goes_in_Authorization()
    {
        var fake = new FakeSupabase();
        await Store(fake, key: "eyJhbGciOi.service.role").DeleteAsync("chat/c1/m1.jpg");

        var delete = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal("Bearer eyJhbGciOi.service.role", delete.Authorization);
    }

    [Fact]
    public async Task Signs_a_page_of_images_in_one_request_and_remembers_the_links()
    {
        var fake = new FakeSupabase();
        var store = Store(fake);

        var urls = await store.UrlsAsync(["chat/a.jpg", "chat/b.png", "chat/a.jpg"]);
        Assert.Equal("https://proj.supabase.co/storage/v1/object/sign/chat-images/chat/a.jpg?token=t-10", urls["chat/a.jpg"]);
        Assert.Equal(2, urls.Count);
        Assert.Contains("\"expiresIn\":86400", Assert.Single(fake.Requests).Body);

        // Already signed: no second request, so the browser keeps its cached copy.
        Assert.Equal(urls["chat/b.png"], (await store.UrlsAsync(["chat/b.png"]))["chat/b.png"]);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task A_refusal_from_Supabase_is_an_error_not_a_silent_success()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Store(new FakeSupabase(HttpStatusCode.BadRequest)).PutAsync("chat/x.jpg", [1], "image/jpeg"));
        Assert.Contains("Bucket not found", error.Message);
    }

    [Fact]
    public void Is_off_without_the_url_or_key()
    {
        var store = new SupabaseChatImageStore(new FakeSupabase(), Options.Create(new SupabaseStorageSettings { Url = ProjectUrl }),
            NullLogger<SupabaseChatImageStore>.Instance);
        Assert.False(store.IsConfigured);
    }
}
