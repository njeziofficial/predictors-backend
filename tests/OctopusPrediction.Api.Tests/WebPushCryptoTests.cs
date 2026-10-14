using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// WebPushCrypto, checked from the browser's side: the message decrypts as RFC 8291 says a
// browser decrypts it, and the VAPID token verifies against the server's public key.
public class WebPushCryptoTests
{
    private static string B64(byte[] b) => WebPushCrypto.Base64Url(b);
    private static byte[] Raw(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    [Fact]
    public void A_browser_can_decrypt_the_message()
    {
        // The browser's subscription: its key pair and auth secret.
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = Raw(browser);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var message = Encoding.UTF8.GetBytes("{\"title\":\"Predict Week 9\",\"url\":\"/predict\"}");

        var body = WebPushCrypto.Encrypt(message, B64(uaPublic), B64(authSecret));

        // RFC 8188 header: salt (16), record size (4), key id length (1), key id (the sender's key).
        var salt = body[..16];
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16)));
        Assert.Equal(65, body[20]);
        var asPublic = body[21..86];
        var ciphertext = body[86..^16];
        var tag = body[^16..];

        // Decrypt as the browser: ECDH with the sender's key, then RFC 8291's key schedule.
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..] },
        });
        var ecdh = browser.DeriveRawSecretAgreement(sender.PublicKey);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, HKDF.Extract(HashAlgorithmName.SHA256, ecdh, authSecret), 32,
            [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        var plain = new byte[ciphertext.Length];
        using (var aes = new AesGcm(cek, 16))
            aes.Decrypt(nonce, ciphertext, tag, plain);

        Assert.Equal(0x02, plain[^1]); // the single, last record
        Assert.Equal(message, plain[..^1]);
    }

    [Fact]
    public void Each_message_uses_a_fresh_salt_and_key()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p256dh = B64(Raw(browser));
        var auth = B64(RandomNumberGenerator.GetBytes(16));
        var a = WebPushCrypto.Encrypt("x"u8.ToArray(), p256dh, auth);
        var b = WebPushCrypto.Encrypt("x"u8.ToArray(), p256dh, auth);
        Assert.NotEqual(a[..16], b[..16]);
        Assert.NotEqual(a[21..86], b[21..86]);
    }

    [Fact]
    public void The_vapid_token_verifies_against_the_public_key_and_names_the_push_service()
    {
        var (publicKey, privateKey) = WebPushCrypto.GenerateKeys();
        var keys = new VapidKeys(publicKey, privateKey, "mailto:admin@test");
        var now = DateTimeOffset.UtcNow;

        var header = WebPushCrypto.VapidAuthorization("https://web.push.apple.com/QGxabc", keys, now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith($", k={publicKey}", header);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        var q = WebPushCrypto.FromBase64Url(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = q[1..33], Y = q[33..] },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            WebPushCrypto.FromBase64Url(parts[2]), HashAlgorithmName.SHA256));

        using var claims = JsonDocument.Parse(WebPushCrypto.FromBase64Url(parts[1]));
        Assert.Equal("https://web.push.apple.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:admin@test", claims.RootElement.GetProperty("sub").GetString());
        var exp = claims.RootElement.GetProperty("exp").GetInt64();
        Assert.InRange(exp - now.ToUnixTimeSeconds(), 3600, 24 * 3600);
    }

    [Fact]
    public void Generated_keys_are_a_raw_P256_public_key_and_a_32_byte_private_key()
    {
        var (publicKey, privateKey) = WebPushCrypto.GenerateKeys();
        var pub = WebPushCrypto.FromBase64Url(publicKey);
        Assert.Equal(65, pub.Length);
        Assert.Equal(0x04, pub[0]);
        Assert.Equal(32, WebPushCrypto.FromBase64Url(privateKey).Length);
    }
}
