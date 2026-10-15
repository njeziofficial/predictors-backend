using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// Refresh-token rotation: a phone that never got the answer to a refresh keeps its session, while a
// token that was really used by someone else still ends it.
public class RefreshSessionTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly AuthService _auth;
    private readonly User _user;

    public RefreshSessionTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = new string('k', 64),
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test",
            })
            .Build();
        _auth = new AuthService(_db, config);
        _user = new User { Name = "pat", Email = "pat@test" };
        _db.Users.Add(_user);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // A login's first refresh token, as the cookie holds it.
    private async Task<string> SignIn()
    {
        var first = await _auth.RefreshAsync(await SeedToken());
        return first.Session!.RefreshToken!;
    }

    private async Task<string> SeedToken()
    {
        const string raw = "seed-token";
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = _user.Id,
            FamilyId = Guid.NewGuid(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))),
            ExpiresAt = DateTime.UtcNow.AddDays(30),
        });
        await _db.SaveChangesAsync();
        return raw;
    }

    // Pretends the grace window for parallel tabs has passed.
    private async Task Age(string reason)
    {
        foreach (var t in await _db.RefreshTokens.Where(t => t.RevokedReason == reason).ToListAsync())
            t.RevokedAt = DateTime.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_refresh_whose_answer_never_arrived_can_be_retried_later()
    {
        var cookie = await SignIn();
        // The server rotates, but the phone closes before the new cookie arrives.
        Assert.NotNull((await _auth.RefreshAsync(cookie)).Session);
        await Age("rotated");

        // Much later, the phone presents the old cookie again: still signed in, with a new one.
        var retry = await _auth.RefreshAsync(cookie);
        Assert.NotNull(retry.Session);
        Assert.NotNull(retry.Session!.RefreshToken);

        // And that new one keeps working.
        Assert.NotNull((await _auth.RefreshAsync(retry.Session.RefreshToken!)).Session);
    }

    [Fact]
    public async Task An_old_token_shown_after_its_replacement_was_used_still_ends_the_session()
    {
        var stolen = await SignIn();
        var next = (await _auth.RefreshAsync(stolen)).Session!.RefreshToken!;
        Assert.NotNull((await _auth.RefreshAsync(next)).Session); // the replacement got used
        await Age("rotated");

        Assert.Null((await _auth.RefreshAsync(stolen)).Session);
        Assert.False(await _db.RefreshTokens.AnyAsync(t => t.RevokedAt == null));
    }

    [Fact]
    public async Task A_superseded_token_turning_up_counts_as_reuse()
    {
        var cookie = await SignIn();
        var lost = (await _auth.RefreshAsync(cookie)).Session!.RefreshToken!;
        await Age("rotated");
        Assert.NotNull((await _auth.RefreshAsync(cookie)).Session); // recovered; `lost` is now superseded

        Assert.Null((await _auth.RefreshAsync(lost)).Session);
        Assert.False(await _db.RefreshTokens.AnyAsync(t => t.RevokedAt == null));
    }
}
