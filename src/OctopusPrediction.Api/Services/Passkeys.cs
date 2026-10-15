using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

// Where passkeys work: the app's own address(es). A passkey is tied to the site's domain, so it
// only works there; Passkeys:Origins in configuration, else the app's public URL (Email:AppUrl),
// plus the Vite dev server in development.
public static class PasskeySetup
{
    public static Fido2Configuration Configuration(IConfiguration config, IHostEnvironment env)
    {
        var origins = config.GetSection("Passkeys:Origins").Get<string[]>() ?? [];
        if (origins.Length == 0)
            origins = [config["Email:AppUrl"] ?? "https://octopus-predictor.njezi.workers.dev"];
        if (env.IsDevelopment())
            origins = [.. origins, "http://localhost:8080"];
        var normalized = origins.Select(o => o.TrimEnd('/')).Distinct().ToList();
        return new Fido2Configuration
        {
            ServerDomain = config["Passkeys:RpId"] ?? new Uri(normalized[0]).Host,
            ServerName = "Octopus Prediction",
            Origins = normalized.ToHashSet(),
            TimestampDriftTolerance = 300_000,
        };
    }
}

// CredentialId (base64url) lets the app tell which one is the device it's running on.
public record PasskeyDto(string Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt, string CredentialId)
{
    public static PasskeyDto From(Passkey p) =>
        new(p.Id.ToString(), p.Name, p.CreatedAt, p.LastUsedAt, Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(p.CredentialId));
}

// A started ceremony: the browser gets Options (plus FlowId to come back with); the server keeps
// what it asked for, so the answer can be checked against it.
public record PasskeyCeremony(string FlowId, JsonNode Options);

/// <summary>
/// Adding passkeys to an account and signing in with them (WebAuthn, via Fido2NetLib). Only
/// passkeys on the device itself (platform authenticators: fingerprint, face, screen lock) and
/// always with the user verified, so a passkey is as good as the password it stands in for.
/// </summary>
public class PasskeyService(AppDbContext db, IFido2 fido2, IMemoryCache cache)
{
    public const int MaxPerUser = 10;
    public const int MaxName = 60;
    // How long the phone's prompt can stay up before the ceremony has to start again.
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    private sealed record PendingRegistration(Guid UserId, CredentialCreateOptions Options);
    private sealed record PendingLogin(AssertionOptions Options);

    public async Task<List<PasskeyDto>> ListAsync(Guid userId) =>
        (await db.Passkeys.Where(p => p.UserId == userId).OrderBy(p => p.CreatedAt).ToListAsync())
        .Select(PasskeyDto.From)
        .ToList();

    /// <summary>
    /// After a password reset: removes the account's passkeys when the system admin has chosen
    /// that (ScraperSettings.RemovePasskeysOnPasswordReset). Staged; the caller saves. How many go.
    /// </summary>
    public static async Task<int> RemoveOnPasswordResetAsync(AppDbContext db, Guid userId)
    {
        var remove = await db.ScraperSettings.Select(s => s.RemovePasskeysOnPasswordReset).FirstOrDefaultAsync();
        if (!remove) return 0;
        var passkeys = await db.Passkeys.Where(p => p.UserId == userId).ToListAsync();
        db.Passkeys.RemoveRange(passkeys);
        return passkeys.Count;
    }

    /// <summary>Starts adding a passkey to the user's account; null with why when they can't.</summary>
    public async Task<(PasskeyCeremony? Ceremony, string? Problem)> StartRegistrationAsync(User user)
    {
        var existing = await db.Passkeys.Where(p => p.UserId == user.Id).Select(p => p.CredentialId).ToListAsync();
        if (existing.Count >= MaxPerUser)
            return (null, $"You can have up to {MaxPerUser} passkeys. Remove one first.");

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = user.Id.ToByteArray(),
                // What the phone shows when it asks which account to sign in to.
                Name = user.Email,
                DisplayName = user.Name,
            },
            // Each device only once per account.
            ExcludeCredentials = [.. existing.Select(id => new PublicKeyCredentialDescriptor(id))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                // Kept on the device, so signing in needs no email or name first.
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var flowId = Remember(new PendingRegistration(user.Id, options));
        return (new PasskeyCeremony(flowId, JsonNode.Parse(options.ToJson())!), null);
    }

    /// <summary>Checks the device's answer and saves the passkey; null with why when it fails.</summary>
    public async Task<(PasskeyDto? Passkey, string? Problem)> FinishRegistrationAsync(
        Guid userId, string flowId, string? name, AuthenticatorAttestationRawResponse response, CancellationToken ct = default)
    {
        if (Take(flowId) is not PendingRegistration pending || pending.UserId != userId)
            return (null, "That took too long. Please try again.");

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = pending.Options,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.Passkeys.AnyAsync(p => p.CredentialId == args.CredentialId, token),
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return (null, "Your device's answer couldn't be checked. Please try again.");
        }

        var passkey = new Passkey
        {
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Name = CleanName(name),
            Transports = credential.Transports is { Length: > 0 } t ? string.Join(',', t.Select(x => x.ToString().ToLowerInvariant())) : null,
            CreatedAt = DateTime.UtcNow,
        };
        db.Passkeys.Add(passkey);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        AuditLogger.Log(db, subject: user, actor: user, action: "PasskeyAdded", details: $"\"{passkey.Name}\"");
        await db.SaveChangesAsync(ct);
        return (PasskeyDto.From(passkey), null);
    }

    /// <summary>Starts signing in: the phone offers whichever of its passkeys are for this site.</summary>
    public PasskeyCeremony StartLogin()
    {
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        var flowId = Remember(new PendingLogin(options));
        return new PasskeyCeremony(flowId, JsonNode.Parse(options.ToJson())!);
    }

    /// <summary>The user the device's answer signs in, or null with why.</summary>
    public async Task<(User? User, string? Problem)> FinishLoginAsync(
        string flowId, AuthenticatorAssertionRawResponse response, CancellationToken ct = default)
    {
        if (Take(flowId) is not PendingLogin pending)
            return (null, "That took too long. Please try again.");

        var credentialId = response.RawId;
        var passkey = await db.Passkeys.Include(p => p.User).FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct);
        if (passkey is null)
            return (null, "This passkey isn't set up for any account any more. Sign in with your password.");

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = pending.Options,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                // The device says whose passkey it is; it has to be this one's owner.
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(passkey.UserId.ToByteArray())),
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return (null, "Your device's answer couldn't be checked. Please try again.");
        }

        passkey.SignCount = result.SignCount;
        passkey.LastUsedAt = DateTime.UtcNow;
        return (passkey.User, null);
    }

    public async Task<bool> RenameAsync(Guid userId, Guid id, string? name)
    {
        var passkey = await db.Passkeys.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (passkey is null) return false;
        passkey.Name = CleanName(name);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid id)
    {
        var passkey = await db.Passkeys.Include(p => p.User).FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (passkey is null) return false;
        db.Passkeys.Remove(passkey);
        AuditLogger.Log(db, subject: passkey.User, actor: passkey.User, action: "PasskeyRemoved", details: $"\"{passkey.Name}\"");
        await db.SaveChangesAsync();
        return true;
    }

    private static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) clean = "This device";
        return clean.Length > MaxName ? clean[..MaxName] : clean;
    }

    // Each ceremony can be finished once, within CeremonyLifetime.
    private string Remember(object pending)
    {
        var flowId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        cache.Set(CacheKey(flowId), pending, CeremonyLifetime);
        return flowId;
    }

    private object? Take(string flowId)
    {
        if (string.IsNullOrEmpty(flowId) || flowId.Length > 64) return null;
        var key = CacheKey(flowId);
        if (!cache.TryGetValue(key, out var pending)) return null;
        cache.Remove(key);
        return pending;
    }

    private static string CacheKey(string flowId) => $"passkey-ceremony:{flowId}";
}
