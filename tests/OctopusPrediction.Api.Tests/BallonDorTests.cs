using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.BallonDor;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// The Ballon d'Or side game: picks only from the nominees and only before the lock; the result
// shares each prize equally among those who earned it, in their wallets, and a corrected result
// takes back what's no longer due without ever paying anyone twice.
public class BallonDorTests : IDisposable
{
    private static readonly List<string> Nominees = ["Dembélé", "Yamal", "Vitinha", "Salah", "Raphinha", "Hakimi", "Kane"];
    private static readonly List<string> Top5 = ["Dembélé", "Yamal", "Vitinha", "Salah", "Raphinha"];
    private readonly AppDbContext _db;
    private readonly User _system;
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _carol;

    public BallonDorTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _system = new User { Name = "System", Email = "system@test", Role = UserRole.Admin, IsSystemUser = true };
        _alice = new User { Name = "Alice", Email = "alice@test" };
        _bob = new User { Name = "Bob", Email = "bob@test" };
        _carol = new User { Name = "Carol", Email = "carol@test" };
        _db.Users.AddRange(_system, _alice, _bob, _carol);
        _db.ScraperSettings.Add(new ScraperSettings { Id = 1 });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static ControllerContext As(User user) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test")),
        },
    };

    private BallonDorController Player(User user) => new(_db) { ControllerContext = As(user) };
    private AdminBallonDorController Admin() => new(_db) { ControllerContext = As(_system) };

    private static T Ok<T>(IActionResult result) => (T)Assert.IsType<OkObjectResult>(result).Value!;

    private static string Message(IActionResult result) =>
        (string)Assert.IsType<BadRequestObjectResult>(result).Value!.GetType().GetProperty("message")!.GetValue(((BadRequestObjectResult)result).Value)!;

    private void Event(DateTime locksAt)
    {
        _db.BallonDorEvents.Add(new BallonDorEvent { Year = 2026, Nominees = Nominees, LocksAt = locksAt });
        _db.SaveChanges();
    }

    private void Picked(User user, params string[] picks)
    {
        _db.BallonDorPicks.Add(new BallonDorPick { Year = 2026, UserId = user.Id, Picks = [.. picks] });
        _db.SaveChanges();
    }

    private long Balance(User user) => _db.WalletEntries.Where(e => e.UserId == user.Id).Sum(e => e.AmountKobo);

    [Fact]
    public async Task Picks_are_five_different_nominees_made_before_the_lock()
    {
        Event(DateTime.UtcNow.AddHours(1));

        Assert.Contains("5 different", Message(await Player(_alice).Submit(2026, new(["Dembélé", "Yamal", "Vitinha", "Salah"]))));
        Assert.Contains("5 different", Message(await Player(_alice).Submit(2026, new(["Dembélé", "Yamal", "Vitinha", "Salah", "Messi"]))));
        Assert.Contains("5 different", Message(await Player(_alice).Submit(2026, new(["Dembélé", "dembélé", "Vitinha", "Salah", "Kane"]))));

        // Spelled as the nominee list spells them; changing them replaces the old picks.
        Ok<BallonDorDto>(await Player(_alice).Submit(2026, new(["kane", "Yamal", "Vitinha", "Salah", "Hakimi"])));
        var dto = Ok<BallonDorDto>(await Player(_alice).Submit(2026, new(["YAMAL", "Kane", "Vitinha", "Salah", "Hakimi"])));
        Assert.Equal(["Yamal", "Kane", "Vitinha", "Salah", "Hakimi"], dto.MyPicks!);
        Assert.Equal(1, dto.Entries);
        // Nobody sees anyone else's picks until the lock.
        Assert.Null(Ok<BallonDorDto>(await Player(_bob).Get()).Board);

        Assert.Contains("doesn't play", Message(await Player(_system).Submit(2026, new(Top5))));
    }

    [Fact]
    public async Task A_game_switched_off_is_hidden_from_players_but_not_from_admins()
    {
        Event(DateTime.UtcNow.AddHours(1));
        Picked(_alice, [.. Top5]);

        Assert.False(Ok<BallonDorDto>(await Admin().SetEnabled(2026, new(false))).Enabled);
        Assert.IsType<NoContentResult>(await Player(_alice).Get());
        Assert.IsType<NotFoundObjectResult>(await Player(_bob).Submit(2026, new(Top5)));
        // The admin still runs it, picks kept.
        var admin = Ok<BallonDorDto>(await Admin().Get());
        Assert.False(admin.Enabled);
        Assert.Equal(1, admin.Entries);

        Assert.True(Ok<BallonDorDto>(await Admin().SetEnabled(2026, new(true))).Enabled);
        Assert.Equal(Top5, Ok<BallonDorDto>(await Player(_alice).Get()).MyPicks!);
        Assert.IsType<NotFoundObjectResult>(await Admin().SetEnabled(2027, new(false)));
    }

    [Fact]
    public void Only_the_system_user_switches_the_game()
    {
        var method = typeof(AdminBallonDorController).GetMethod(nameof(AdminBallonDorController.SetEnabled))!;
        Assert.NotNull(method.GetCustomAttributes(typeof(SystemUserOnlyAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public async Task Picks_close_at_the_lock_and_are_shown_to_everyone()
    {
        Event(DateTime.UtcNow.AddMinutes(-1));
        Picked(_alice, [.. Top5]);

        Assert.Contains("locked", Message(await Player(_bob).Submit(2026, new(Top5))));
        var board = Ok<BallonDorDto>(await Player(_bob).Get()).Board!;
        Assert.Equal("Alice", Assert.Single(board).Name);
    }

    [Fact]
    public async Task Each_prize_is_shared_equally_by_those_who_earned_it()
    {
        Event(DateTime.UtcNow.AddMinutes(-1));
        Picked(_alice, [.. Top5]);                                            // winner and top 5
        Picked(_bob, "Dembélé", "Vitinha", "Yamal", "Salah", "Raphinha");      // winner only
        Picked(_carol, "Yamal", "Dembélé", "Vitinha", "Salah", "Raphinha");    // neither

        var dto = Ok<BallonDorDto>(await Admin().SetResult(2026, new(Top5)));

        // ₦2,500 between two for the winner, ₦2,500 to Alice alone for the top 5.
        Assert.Equal(1_250_00 + 2_500_00, Balance(_alice));
        Assert.Equal(1_250_00, Balance(_bob));
        Assert.Equal(0, Balance(_carol));
        Assert.Equal(["Alice", "Bob", "Carol"], dto.Board!.Select(e => e.Name));
        Assert.Equal(3_750_00, dto.Board![0].PrizeKobo);
        // Paid as adjustments: the season pot isn't touched.
        Assert.All(_db.WalletEntries, e => Assert.Equal(WalletKinds.Adjustment, e.Kind));

        // Entering the same result again pays nobody twice.
        Ok<BallonDorDto>(await Admin().SetResult(2026, new(Top5)));
        Assert.Equal(3, _db.WalletEntries.Count());
    }

    [Fact]
    public async Task A_corrected_result_takes_back_what_is_no_longer_due()
    {
        Event(DateTime.UtcNow.AddMinutes(-1));
        Picked(_alice, [.. Top5]);
        Picked(_bob, "Yamal", "Dembélé", "Vitinha", "Salah", "Raphinha");

        Ok<BallonDorDto>(await Admin().SetResult(2026, new(Top5)));
        Assert.Equal(5_000_00, Balance(_alice));

        // Yamal actually won: Bob gets the winner prize, Alice's is reversed.
        Ok<BallonDorDto>(await Admin().SetResult(2026, new(["Yamal", "Dembélé", "Vitinha", "Salah", "Raphinha"])));
        Assert.Equal(0, Balance(_alice));
        Assert.Equal(5_000_00, Balance(_bob));
        Assert.Equal(2, _db.WalletEntries.Count(e => e.ReversesEntryId != null));

        // Clearing the result takes everything back.
        Ok<BallonDorDto>(await Admin().SetResult(2026, new(null)));
        Assert.Equal(0, Balance(_bob));
    }

    [Fact]
    public async Task A_prize_already_paid_out_is_not_taken_back_and_nothing_changes()
    {
        Event(DateTime.UtcNow.AddMinutes(-1));
        Picked(_alice, [.. Top5]);
        Ok<BallonDorDto>(await Admin().SetResult(2026, new(Top5)));
        _db.WalletEntries.Add(new WalletEntry { UserId = _alice.Id, Season = "2026/27", Kind = WalletKinds.Payout, AmountKobo = -5_000_00 });
        _db.SaveChanges();

        Assert.Contains("already been paid", Message(await Admin().SetResult(2026, new(null))));
        _db.ChangeTracker.Clear();
        Assert.Equal(Top5, _db.BallonDorEvents.Single().Result!);
    }

    [Fact]
    public async Task The_2026_game_opens_with_the_shortlist_only_before_its_lock()
    {
        await BallonDorGame.SeedAsync(_db, BallonDorGame.SeedLocksAt.AddMinutes(1));
        Assert.Empty(_db.BallonDorEvents);

        await BallonDorGame.SeedAsync(_db, BallonDorGame.SeedLocksAt.AddHours(-3));
        var ev = Assert.Single(_db.BallonDorEvents);
        Assert.Equal(30, ev.Nominees.Distinct().Count());
        Assert.Equal((2_500_00L, 2_500_00L), (ev.WinnerPotKobo, ev.TopFivePotKobo));

        // An admin's changes are never overwritten.
        ev.Nominees = [.. ev.Nominees.Append("Mohamed Salah")];
        _db.SaveChanges();
        await BallonDorGame.SeedAsync(_db, BallonDorGame.SeedLocksAt.AddHours(-2));
        Assert.Equal(31, Assert.Single(_db.BallonDorEvents).Nominees.Count);
    }

    [Fact]
    public async Task The_result_waits_for_the_lock()
    {
        Event(DateTime.UtcNow.AddHours(1));
        Assert.Contains("still open", Message(await Admin().SetResult(2026, new(Top5))));
    }
}
