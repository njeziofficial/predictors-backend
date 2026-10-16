using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace OctopusPrediction.Api.Services;

// Supabase Storage, where chat images are kept: a private bucket ("chat-images"), so images are
// shown through signed links that expire, handed out only with the messages of a conversation
// you're in. Set Supabase__Url (the project URL) and Supabase__ServiceKey (a secret or
// service_role key: server only, it can do anything in the project) on Render; without them,
// sending an image is refused and text chat carries on as normal.
public class SupabaseStorageSettings
{
    public string Url { get; set; } = string.Empty;
    public string ServiceKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "chat-images";
    // How long a signed image link works. The app fetches new ones with the messages.
    public int LinkHours { get; set; } = 24;
}

public interface IChatImageStore
{
    bool IsConfigured { get; }
    Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Links that show these images for a while without signing in, by key. Keys it couldn't sign are left out.</summary>
    Task<IReadOnlyDictionary<string, string>> UrlsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
}

public sealed class SupabaseChatImageStore : IChatImageStore, IDisposable
{
    public const string HttpClientName = "supabase-storage";

    private readonly IHttpClientFactory _clients;
    private readonly SupabaseStorageSettings _settings;
    private readonly string _base;
    // The same link for an image until it's half used up, so the browser can cache the image
    // instead of downloading it again each time the messages are fetched.
    private readonly MemoryCache _links = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public SupabaseChatImageStore(IHttpClientFactory clients, IOptions<SupabaseStorageSettings> options, ILogger<SupabaseChatImageStore> logger)
    {
        _clients = clients;
        _settings = options.Value;
        _base = $"{_settings.Url.TrimEnd('/')}/storage/v1";
        IsConfigured = !string.IsNullOrWhiteSpace(_settings.Url) && !string.IsNullOrWhiteSpace(_settings.ServiceKey)
            && !string.IsNullOrWhiteSpace(_settings.Bucket);
        if (!IsConfigured) logger.LogWarning("[Storage] Supabase__Url/ServiceKey not configured; chat images are off");
    }

    public bool IsConfigured { get; }

    // A client per call: the factory recycles connections, which a long-lived one wouldn't.
    private HttpClient Http => _clients.CreateClient(HttpClientName);

    public async Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, $"/object/{_settings.Bucket}/{key}");
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // An image never changes under its key.
        request.Headers.Add("cache-control", "31536000");
        request.Headers.Add("x-upsert", "false");
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, "upload", ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _links.Remove(key);
        using var request = Request(HttpMethod.Delete, $"/object/{_settings.Bucket}/{key}");
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, "delete", ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> UrlsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        var urls = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var key in keys.Distinct())
        {
            if (_links.TryGetValue(key, out string? cached)) urls[key] = cached!;
            else missing.Add(key);
        }
        if (missing.Count == 0) return urls;

        // One request signs them all.
        var lifetime = TimeSpan.FromHours(Math.Max(1, _settings.LinkHours));
        using var request = Request(HttpMethod.Post, $"/object/sign/{_settings.Bucket}");
        request.Content = JsonContent.Create(new { expiresIn = (int)lifetime.TotalSeconds, paths = missing });
        using var response = await Http.SendAsync(request, ct);
        await EnsureOkAsync(response, "sign", ct);

        var signed = await response.Content.ReadFromJsonAsync<List<SignedLink>>(ct) ?? [];
        foreach (var s in signed.Where(s => s.Path is not null && !string.IsNullOrEmpty(s.SignedUrl) && s.Error is null))
        {
            // Relative to /storage/v1: "/object/sign/chat-images/…?token=…".
            var url = _base + s.SignedUrl;
            _links.Set(s.Path!, url, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime / 2, Size = 1 });
            urls[s.Path!] = url;
        }
        return urls;
    }

    private sealed record SignedLink(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("signedURL")] string? SignedUrl,
        [property: JsonPropertyName("error")] string? Error);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!IsConfigured) throw new InvalidOperationException("Supabase Storage isn't configured.");
        var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Add("apikey", _settings.ServiceKey);
        // Legacy service_role keys are JWTs and go in Authorization too; the newer sb_secret_
        // keys aren't, and Supabase accepts them in apikey alone.
        if (_settings.ServiceKey.StartsWith("eyJ", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ServiceKey);
        return request;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Supabase Storage {what} failed: {(int)response.StatusCode} {(body.Length > 300 ? body[..300] : body)}",
            null, response.StatusCode);
    }

    public void Dispose() => _links.Dispose();
}

// What a chat image may be, worked out from its bytes rather than the name or type the browser
// claims, so nothing but a real image ends up behind a link.
public static class ChatImages
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxSide = 20_000;

    public sealed record Kind(string ContentType, string Extension);

    private static readonly Kind Jpeg = new("image/jpeg", "jpg");
    private static readonly Kind Png = new("image/png", "png");
    private static readonly Kind Gif = new("image/gif", "gif");
    private static readonly Kind Webp = new("image/webp", "webp");

    /// <summary>The image type of these bytes, or null if they aren't a JPEG, PNG, GIF or WebP.</summary>
    public static Kind? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((byte[])[0xFF, 0xD8, 0xFF])) return Jpeg;
        if (data.StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return Png;
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return Gif;
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return Webp;
        return null;
    }

    /// <summary>Where a message's image is kept: chat/{conversation}/{message}.{ext}.</summary>
    public static string KeyFor(Guid conversationId, Guid messageId, Kind kind) =>
        $"chat/{conversationId:N}/{messageId:N}.{kind.Extension}";
}
