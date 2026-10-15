using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Web Push's Urgency header (RFC 8030): how soon the push service delivers.
public static class PushUrgency
{
    public const string Normal = "normal";
    public const string High = "high";
}

// What a push notification shows. Url is where tapping it opens the app; a later notification
// with the same Tag replaces the earlier one on the device instead of stacking under it.
public record PushMessage(string Title, string Body, string Url, string? Tag = null);

// The server's VAPID identity: a P-256 key pair (base64url, the format every Web Push library
// uses) and a contact the push services can reach.
public record VapidKeys(string PublicKey, string PrivateKey, string Subject);

// Web Push as the standards define it, on .NET's own crypto: the message is encrypted for the
// browser with "aes128gcm" (RFC 8291 / RFC 8188), which every push service accepts and Apple's
// requires, and the request is signed with a VAPID token (RFC 8292).
public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    public static (string PublicKey, string PrivateKey) GenerateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (Base64Url([0x04, .. p.Q.X!, .. p.Q.Y!]), Base64Url(p.D!));
    }

    /// <summary>The request body for a browser with these subscription keys (p256dh, auth).</summary>
    public static byte[] Encrypt(byte[] plaintext, string p256dh, string auth)
    {
        var uaPublic = FromBase64Url(p256dh);
        var authSecret = FromBase64Url(auth);
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04) throw new ArgumentException("Not a P-256 public key.", nameof(p256dh));

        using var asKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asQ = asKey.ExportParameters(false).Q;
        byte[] asPublic = [0x04, .. asQ.X!, .. asQ.Y!];
        using var uaKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(uaKey.PublicKey);

        // RFC 8291 §3.4: mix in the browser's auth secret and both public keys...
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret), 32, keyInfo);

        // ...then RFC 8188's content key and nonce for a single record.
        var salt = RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        // One record: the message then 0x02, the "last record" delimiter.
        byte[] padded = [.. plaintext, 0x02];
        if (padded.Length + 16 > RecordSize) throw new ArgumentException("Push message too long.", nameof(plaintext));
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
            aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt, record size, and our ephemeral public key as the key id.
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return [.. header, .. ciphertext, .. tag];
    }

    /// <summary>The Authorization header value for a push to this endpoint ("vapid t=…, k=…").</summary>
    public static string VapidAuthorization(string endpoint, VapidKeys keys, DateTimeOffset now)
    {
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        // Push services refuse tokens valid for more than 24 hours.
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = keys.Subject }));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");

        var q = FromBase64Url(keys.PublicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = FromBase64Url(keys.PrivateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..] },
        });
        // .NET signs in the r||s form JWTs use.
        var signature = Base64Url(key.SignData(signingInput, HashAlgorithmName.SHA256));
        return $"vapid t={header}.{claims}.{signature}, k={keys.PublicKey}";
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

// The VAPID keys every push is signed with. Push:PublicKey and Push:PrivateKey from config win;
// otherwise a pair is generated on first use and stored (PushKeys), so it needs no setup and
// stays the same across redeploys: a new pair would orphan every existing subscription.
public sealed class PushKeyProvider(IConfiguration config, IServiceScopeFactory scopes, ILogger<PushKeyProvider> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private VapidKeys? _keys;

    public async Task<VapidKeys> GetAsync(CancellationToken ct = default)
    {
        if (_keys is { } cached) return cached;
        await _lock.WaitAsync(ct);
        try
        {
            if (_keys is { } loaded) return loaded;
            var (publicKey, privateKey) = await LoadAsync(ct);
            return _keys = new VapidKeys(publicKey, privateKey, Subject());
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string Public, string Private)> LoadAsync(CancellationToken ct)
    {
        var configuredPublic = config["Push:PublicKey"];
        var configuredPrivate = config["Push:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(configuredPublic) && !string.IsNullOrWhiteSpace(configuredPrivate))
            return (configuredPublic.Trim(), configuredPrivate.Trim());

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PushKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (stored is not null) return (stored.PublicKey, stored.PrivateKey);

        var (publicKey, privateKey) = WebPushCrypto.GenerateKeys();
        db.PushKeys.Add(new PushKeys { PublicKey = publicKey, PrivateKey = privateKey });
        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("[Push] Generated the server's push keys");
            return (publicKey, privateKey);
        }
        catch (DbUpdateException)
        {
            // Another instance stored a pair first: use that one.
            var winner = await db.PushKeys.AsNoTracking().FirstAsync(k => k.Id == 1, ct);
            return (winner.PublicKey, winner.PrivateKey);
        }
    }

    // Who to contact about these pushes. Apple refuses pushes without a valid mailto: or https: one.
    private string Subject()
    {
        var subject = config["Push:Subject"];
        if (!string.IsNullOrWhiteSpace(subject)) return subject.Trim();
        var email = config["SeedAdmin:Email"];
        return !string.IsNullOrWhiteSpace(email) ? $"mailto:{email.Trim()}" : "https://octopus-predictor.njezi.workers.dev";
    }
}

// Sends push notifications to every browser a user has turned them on in, and forgets the ones
// the push service says are gone (the app was uninstalled or notifications switched off).
// A typed HttpClient (Program.cs), so tests can hand it a fake push service.
public sealed class PushSender(HttpClient http, AppDbContext db, PushKeyProvider keys, ILogger<PushSender> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Push services an endpoint may point at. Endpoints come from browsers, but they're sent to us
    // by the client, so anything else is refused rather than have the server POST to any URL given.
    private static readonly string[] PushHosts =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com",
    ];

    public static bool IsAllowedEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && PushHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sends to every subscription of these users; returns how many users it reached. Urgency
    /// "high" has the push service deliver at once even to a sleeping phone (reminders, admin
    /// notifications); "normal" lets it wait for the phone's next wake-up.
    /// </summary>
    public async Task<int> SendAsync(IReadOnlyCollection<Guid> userIds, PushMessage message, TimeSpan timeToLive,
        CancellationToken ct = default, string urgency = PushUrgency.Normal)
    {
        if (userIds.Count == 0) return 0;
        var subscriptions = await db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)).ToListAsync(ct);
        if (subscriptions.Count == 0) return 0;

        var vapid = await keys.GetAsync(ct);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var ttl = Math.Max(0, (int)timeToLive.TotalSeconds);
        var reached = new ConcurrentDictionary<Guid, byte>();
        var gone = new ConcurrentBag<Entities.PushSubscription>();

        // A few at a time: a notification to every player shouldn't take minutes, nor open a
        // connection per device at once. Only the HTTP calls run in parallel, never the DbContext.
        await Parallel.ForEachAsync(subscriptions, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (sub, token) =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint)
                {
                    Content = new ByteArrayContent(WebPushCrypto.Encrypt(payload, sub.P256dh, sub.Auth)),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentEncoding.Add("aes128gcm");
                request.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(sub.Endpoint, vapid, DateTimeOffset.UtcNow));
                request.Headers.Add("TTL", ttl.ToString());
                request.Headers.Add("Urgency", urgency);

                using var response = await http.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                    reached.TryAdd(sub.UserId, 0);
                else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    gone.Add(sub);
                else
                    logger.LogWarning("[Push] {Host} refused a push for user {UserId}: {Status} {Body}",
                        request.RequestUri!.Host, sub.UserId, (int)response.StatusCode, await response.Content.ReadAsStringAsync(token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning(ex, "[Push] Couldn't send to a subscription of user {UserId}", sub.UserId);
            }
        });

        if (!gone.IsEmpty)
        {
            db.PushSubscriptions.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("[Push] Removed {Count} expired subscriptions", gone.Count);
        }
        return reached.Count;
    }

    /// <summary>Which of these users have notifications on in at least one browser.</summary>
    public async Task<HashSet<Guid>> WithSubscriptionsAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.ToList();
        return [.. await db.PushSubscriptions.Where(s => ids.Contains(s.UserId)).Select(s => s.UserId).Distinct().ToListAsync(ct)];
    }
}
