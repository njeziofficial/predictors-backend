using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// Passkeys end to end, with a pretend phone that makes real keys and signatures. The phone's
// answers go through JSON exactly as the app sends them, so the request shapes are checked too.
public class PasskeyTests : IDisposable
{
    private const string Origin = "https://octopus.example";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly PasskeyService _passkeys;
    private readonly User _pat;

    public PasskeyTests()
    {
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "octopus.example",
            ServerName = "Octopus Prediction",
            Origins = new HashSet<string> { Origin },
        });
        _passkeys = new PasskeyService(_db, fido2, _cache);
        _pat = new User { Name = "Pat", Email = "pat@test" };
        _db.Users.Add(_pat);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }

    // A phone's passkey: its key pair, id, and how many times it has signed.
    private sealed class FakeDevice(string origin = Origin, string rpId = "octopus.example")
    {
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
        public byte[] UserHandle { get; private set; } = [];
        public uint Count { get; set; }

        private byte[] RpIdHash => SHA256.HashData(Encoding.UTF8.GetBytes(rpId));

        private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private byte[] ClientData(string type, JsonNode options) =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                type,
                challenge = options["challenge"]!.GetValue<string>(),
                origin,
                crossOrigin = false,
            }));

        private byte[] CoseKey()
        {
            var p = Key.ExportParameters(false);
            var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);   // kty: EC2
            w.WriteInt32(3); w.WriteInt32(-7);  // alg: ES256
            w.WriteInt32(-1); w.WriteInt32(1);  // crv: P-256
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
            w.WriteEndMap();
            return w.Encode();
        }

        // flags: user present (0x01) and verified (0x04), plus attested data (0x40) when creating.
        private byte[] AuthData(bool create, byte flags = 0x05)
        {
            var counter = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(counter, Count);
            var data = new List<byte>(RpIdHash) { create ? (byte)(flags | 0x40) : flags };
            data.AddRange(counter);
            if (create)
            {
                data.AddRange(new byte[16]); // AAGUID
                data.Add((byte)(CredentialId.Length >> 8));
                data.Add((byte)CredentialId.Length);
                data.AddRange(CredentialId);
                data.AddRange(CoseKey());
            }
            return [.. data];
        }

        /// <summary>The JSON the app posts after navigator.credentials.create().</summary>
        public string Create(JsonNode options)
        {
            UserHandle = Convert.FromBase64String(Pad(options["user"]!["id"]!.GetValue<string>()));
            var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
            w.WriteStartMap(3);
            w.WriteTextString("fmt"); w.WriteTextString("none");
            w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
            w.WriteTextString("authData"); w.WriteByteString(AuthData(create: true));
            w.WriteEndMap();
            return JsonSerializer.Serialize(new
            {
                id = B64(CredentialId),
                rawId = B64(CredentialId),
                type = "public-key",
                response = new
                {
                    attestationObject = B64(w.Encode()),
                    clientDataJSON = B64(ClientData("webauthn.create", options)),
                    transports = new[] { "internal", "hybrid" },
                },
                clientExtensionResults = new { },
            });
        }

        /// <summary>The JSON the app posts after navigator.credentials.get().</summary>
        public string Sign(JsonNode options, byte flags = 0x05, byte[]? userHandle = null)
        {
            Count++;
            var authData = AuthData(create: false, flags);
            var clientData = ClientData("webauthn.get", options);
            var signature = Key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            return JsonSerializer.Serialize(new
            {
                id = B64(CredentialId),
                rawId = B64(CredentialId),
                type = "public-key",
                response = new
                {
                    authenticatorData = B64(authData),
                    signature = B64(signature),
                    clientDataJSON = B64(clientData),
                    userHandle = B64(userHandle ?? UserHandle),
                },
                clientExtensionResults = new { },
            });
        }

        private static string Pad(string b64url)
        {
            var s = b64url.Replace('-', '+').Replace('_', '/');
            return s + new string('=', (4 - s.Length % 4) % 4);
        }
    }

    // What ASP.NET does with the body the app posts.
    private static T Bind<T>(string json) where T : class
    {
        // As the controllers do: the posted body (web JSON), then PasskeyService.ReadAnswer.
        var body = JsonSerializer.Deserialize<JsonElement>(json, Web);
        var (answer, problem) = PasskeyService.ReadAnswer<T>(body);
        Assert.True(answer is not null, problem);
        return answer!;
    }

    private async Task<FakeDevice> AddPasskey(string? name = "Pixel 8")
    {
        var device = new FakeDevice();
        var (ceremony, problem) = await _passkeys.StartRegistrationAsync(_pat);
        Assert.Null(problem);
        var (passkey, error) = await _passkeys.FinishRegistrationAsync(_pat.Id, ceremony!.FlowId, name,
            Bind<AuthenticatorAttestationRawResponse>(device.Create(ceremony.Options)));
        Assert.Null(error);
        Assert.NotNull(passkey);
        return device;
    }

    [Fact]
    public async Task Registration_asks_for_this_device_with_the_user_verified()
    {
        var (ceremony, _) = await _passkeys.StartRegistrationAsync(_pat);
        var options = ceremony!.Options;
        Assert.Equal("octopus.example", options["rp"]!["id"]!.GetValue<string>());
        Assert.Equal("platform", options["authenticatorSelection"]!["authenticatorAttachment"]!.GetValue<string>());
        Assert.Equal("required", options["authenticatorSelection"]!["userVerification"]!.GetValue<string>());
        Assert.Equal("required", options["authenticatorSelection"]!["residentKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Answers_with_the_extras_Android_Chrome_adds_are_read()
    {
        var device = new FakeDevice();
        var (ceremony, _) = await _passkeys.StartRegistrationAsync(_pat);
        var answer = JsonNode.Parse(device.Create(ceremony!.Options))!;
        answer["authenticatorAttachment"] = "platform";
        answer["clientExtensionResults"] = JsonNode.Parse("""{"credProps":{"rk":true}}""");
        answer["response"]!["publicKeyAlgorithm"] = -7;
        answer["response"]!["transports"] = JsonNode.Parse("""["hybrid","internal"]""");

        var (passkey, problem) = await _passkeys.FinishRegistrationAsync(_pat.Id, ceremony.FlowId, null,
            Bind<AuthenticatorAttestationRawResponse>(answer.ToJsonString()));
        Assert.True(passkey is not null, problem);
    }

    [Fact]
    public void An_unreadable_answer_says_why()
    {
        var (answer, problem) = PasskeyService.ReadAnswer<AuthenticatorAttestationRawResponse>(
            JsonSerializer.Deserialize<JsonElement>("""{"id":"x","rawId":"not base64url!!","type":"public-key","response":{}}"""));
        Assert.Null(answer);
        Assert.StartsWith("Your device's answer couldn't be read", problem);
    }

    [Fact]
    public async Task A_player_adds_a_passkey_and_signs_in_with_it()
    {
        var device = await AddPasskey();
        var saved = await _db.Passkeys.SingleAsync();
        Assert.Equal("Pixel 8", saved.Name);
        Assert.Equal("internal,hybrid", saved.Transports);

        var login = _passkeys.StartLogin();
        var (user, problem) = await _passkeys.FinishLoginAsync(login.FlowId,
            Bind<AuthenticatorAssertionRawResponse>(device.Sign(login.Options)));
        Assert.Null(problem);
        Assert.Equal(_pat.Id, user!.Id);
        Assert.Equal(1, saved.SignCount);
        Assert.NotNull(saved.LastUsedAt);
    }

    [Fact]
    public async Task Each_ceremony_works_once()
    {
        var device = await AddPasskey();
        var login = _passkeys.StartLogin();
        var answer = device.Sign(login.Options);
        Assert.NotNull((await _passkeys.FinishLoginAsync(login.FlowId, Bind<AuthenticatorAssertionRawResponse>(answer))).User);
        var (again, problem) = await _passkeys.FinishLoginAsync(login.FlowId, Bind<AuthenticatorAssertionRawResponse>(answer));
        Assert.Null(again);
        Assert.Contains("try again", problem);
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused()
    {
        var device = await AddPasskey();
        var login = _passkeys.StartLogin();
        // A different key, signing as the real passkey (its id and the player's handle).
        var forged = JsonNode.Parse(new FakeDevice().Sign(login.Options, userHandle: _pat.Id.ToByteArray()))!;
        var realId = Convert.ToBase64String(device.CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        forged["id"] = realId;
        forged["rawId"] = realId;

        var (user, _) = await _passkeys.FinishLoginAsync(login.FlowId, Bind<AuthenticatorAssertionRawResponse>(forged.ToJsonString()));
        Assert.Null(user);
    }

    [Fact]
    public async Task Without_the_fingerprint_or_face_check_it_is_refused()
    {
        var device = await AddPasskey();
        var login = _passkeys.StartLogin();
        // User present (0x01) but not verified.
        var (user, _) = await _passkeys.FinishLoginAsync(login.FlowId,
            Bind<AuthenticatorAssertionRawResponse>(device.Sign(login.Options, flags: 0x01)));
        Assert.Null(user);
    }

    [Fact]
    public async Task A_passkey_must_belong_to_the_account_it_claims()
    {
        var device = await AddPasskey();
        var login = _passkeys.StartLogin();
        var (user, _) = await _passkeys.FinishLoginAsync(login.FlowId,
            Bind<AuthenticatorAssertionRawResponse>(device.Sign(login.Options, userHandle: Guid.NewGuid().ToByteArray())));
        Assert.Null(user);
    }

    [Fact]
    public async Task Only_the_app_itself_can_ask()
    {
        var phishing = new FakeDevice(origin: "https://octopus-login.example");
        var (ceremony, _) = await _passkeys.StartRegistrationAsync(_pat);
        var (passkey, problem) = await _passkeys.FinishRegistrationAsync(_pat.Id, ceremony!.FlowId, null,
            Bind<AuthenticatorAttestationRawResponse>(phishing.Create(ceremony.Options)));
        Assert.Null(passkey);
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task Someone_else_cant_finish_a_players_registration()
    {
        var sam = new User { Name = "Sam", Email = "sam@test" };
        _db.Users.Add(sam);
        await _db.SaveChangesAsync();
        var (ceremony, _) = await _passkeys.StartRegistrationAsync(_pat);
        var (passkey, _) = await _passkeys.FinishRegistrationAsync(sam.Id, ceremony!.FlowId, null,
            Bind<AuthenticatorAttestationRawResponse>(new FakeDevice().Create(ceremony.Options)));
        Assert.Null(passkey);
    }

    [Fact]
    public async Task Removing_a_passkey_stops_it_signing_in_and_names_are_tidied()
    {
        var device = await AddPasskey("   My    phone  ");
        var saved = await _db.Passkeys.SingleAsync();
        Assert.Equal("My phone", saved.Name);

        Assert.False(await _passkeys.RemoveAsync(Guid.NewGuid(), saved.Id)); // not theirs
        Assert.True(await _passkeys.RemoveAsync(_pat.Id, saved.Id));

        var login = _passkeys.StartLogin();
        var (user, problem) = await _passkeys.FinishLoginAsync(login.FlowId, Bind<AuthenticatorAssertionRawResponse>(device.Sign(login.Options)));
        Assert.Null(user);
        Assert.Contains("isn't set up", problem);
    }

    [Fact]
    public async Task The_same_device_isnt_offered_twice()
    {
        await AddPasskey();
        var (ceremony, _) = await _passkeys.StartRegistrationAsync(_pat);
        Assert.Single(ceremony!.Options["excludeCredentials"]!.AsArray());
    }

    [Fact]
    public async Task Signing_in_with_a_passkey_respects_a_disabled_account()
    {
        _pat.IsDisabled = true;
        await _db.SaveChangesAsync();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) })
            .Build();
        var result = await new AuthService(_db, config).SignInAsync(_pat, "passkey");
        Assert.Equal(LoginFailureReason.AccountDisabled, result.FailureReason);
        Assert.Null(result.Session);
    }

    [Fact]
    public void Passkeys_work_on_the_apps_own_address()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AppUrl"] = "https://octopus-predictor.njezi.workers.dev/" })
            .Build();
        var env = new FakeEnv("Production");
        var fido = PasskeySetup.Configuration(config, env);
        Assert.Equal("octopus-predictor.njezi.workers.dev", fido.ServerDomain);
        Assert.Equal(["https://octopus-predictor.njezi.workers.dev"], fido.Origins);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task A_password_reset_removes_passkeys_only_when_the_system_admin_chose_that(bool remove, int left)
    {
        _db.ScraperSettings.Add(new ScraperSettings { RemovePasskeysOnPasswordReset = remove });
        await _db.SaveChangesAsync();
        await AddPasskey();

        await PasskeyService.RemoveOnPasswordResetAsync(_db, _pat.Id);
        await _db.SaveChangesAsync();
        Assert.Equal(left, await _db.Passkeys.CountAsync());
    }

    [Fact]
    public async Task Each_passkey_says_which_device_it_is()
    {
        var device = await AddPasskey();
        var listed = Assert.Single(await _passkeys.ListAsync(_pat.Id));
        Assert.Equal(device.CredentialId, Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(listed.CredentialId));
    }

    [Fact]
    public void Only_the_system_admin_can_change_the_passkey_setting()
    {
        foreach (var name in new[] { nameof(AdminController.GetPasskeySettings), nameof(AdminController.SetPasskeySettings) })
            Assert.NotNull(typeof(AdminController).GetMethod(name)!
                .GetCustomAttributes(inherit: true).FirstOrDefault(a => a.GetType().Name == "SystemUserOnlyAttribute"));
    }

    private sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
