using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Predictions;
using OctopusPrediction.Api.Dtos.SpecialGames;
using OctopusPrediction.Api.Dtos.Wallet;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Tests;

// Special games: a score on their own tab, leaving normal predictions alone; the pot shared by
// those who get it, in their wallets, following any correction; paid status visible to the player.
public class SpecialGamesTests : IDisposable
{
    private const long Pot = 10_000_00;
    private readonly AppDbContext _db;
    private readonly User _system;
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _carol;
    private readonly MatchWeek _week;
    private readonly Fixture _match;

    public SpecialGamesTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _system = new User { Name = "System", Email = "system@test", Role = UserRole.Admin, IsSystemUser = true };
        _alice = new User { Name = "Alice", Email = "alice@test" };
        _bob = new User { Name = "Bob", Email = "bob@test" };
        _carol = new User { Name = "Carol", Email = "carol@test" };
        _db.Users.AddRange(_system, _alice, _bob, _carol);
        _db.ScraperSettings.Add(new ScraperSettings { Id = 1 });
        _db.Competitions.Add(new Competition { Id = "la-liga", Name = "La Liga", Enabled = true });
        _week = new MatchWeek { Id = "w1", Name = "Week 1", Competition = "La Liga", CompetitionId = "la-liga" };
        _match = new Fixture { Id = "f1", WeekId = "w1", HomeTeam = "Real", AwayTeam = "Barca", Kickoff = DateTime.UtcNow.AddHours(2) };
        _db.MatchWeeks.Add(_week);
        _db.Fixtures.Add(_match);
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

    private AdminSpecialGamesController Admin() => new(_db) { ControllerContext = As(_system) };
    private SpecialGamesController Player(User user) => new(_db) { ControllerContext = As(user) };
    private WalletController Wallet(User user) => new(_db) { ControllerContext = As(user) };
    private PredictionsController Predictions(User user) =>
        new(_db, Options.Create(new LiveScraperSettings()), new AppCache(new MemoryCache(new MemoryCacheOptions()))) { ControllerContext = As(user) };

    private static T Ok<T>(IActionResult result) => (T)Assert.IsType<OkObjectResult>(result).Value!;

    private static string Message(IActionResult result) =>
        (string)Assert.IsType<BadRequestObjectResult>(result).Value!.GetType().GetProperty("message")!.GetValue(((BadRequestObjectResult)result).Value)!;

    private void Picked(User user, int home, int away)
    {
        _db.SpecialGamePicks.Add(new SpecialGamePick { UserId = user.Id, FixtureId = _match.Id, HomeGoals = home, AwayGoals = away });
        _db.SaveChanges();
    }

    private Task<IActionResult> Submit(User user, int home, int away) =>
        Player(user).Submit(_match.Id, new SubmitSpecialPickRequest { HomeGoals = home, AwayGoals = away });

    private async Task Special(long pot = Pot) => Ok<List<SpecialGameDto>>(await Admin().SetPot(_match.Id, new SetSpecialPotRequest { PotKobo = pot }));

    private async Task Result(FixtureStatus status, int? home, int? away)
    {
        (_match.Status, _match.FinalScoreHome, _match.FinalScoreAway) = (status, home, away);
        _db.SaveChanges();
        await new ScoringService(_db).ScoreFixtureAsync(_match.Id);
    }

    private long Balance(User user) => _db.WalletEntries.Where(e => e.UserId == user.Id).Sum(e => e.AmountKobo);

    private async Task<List<SpecialGameDto>> SetPaid(User user, bool paid) =>
        Ok<List<SpecialGameDto>>(await Admin().SetPaid(_match.Id, user.Id, new SetSpecialPrizePaidRequest { Paid = paid }));

    private async Task Switch(bool on) => Assert.IsType<OkObjectResult>(await Admin().SetEnabled(new SetSpecialGamesEnabledRequest { Enabled = on }));

    private SubmitPredictionsRequest Normal(string outcome, int? home = null, int? away = null) =>
        new(_week.Id, [new PredictionItemRequest(_match.Id, outcome, home, away)]);

    [Fact]
    public void Running_special_games_needs_a_permission_nobody_has_by_default()
    {
        var required = Assert.Single(typeof(AdminSpecialGamesController).GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true));
        Assert.Equal(Permissions.SpecialGamesManage, ((RequirePermissionAttribute)required).Permission);
        Assert.False(Permissions.All.Single(p => p.Key == Permissions.SpecialGamesManage).DefaultAllowed);
    }

    [Fact]
    public async Task A_special_game_leaves_the_normal_prediction_and_points_alone()
    {
        await Special();

        // The usual options still work on the match, and the special score is kept apart.
        Ok<object>(await Predictions(_alice).Submit(Normal("home_win")));
        Ok<List<PlayerSpecialGameDto>>(await Submit(_alice, 2, 1));

        var normal = _db.Predictions.Single();
        Assert.Equal(OutcomeType.HomeWin, normal.Outcome);
        Assert.Single(_db.SpecialGamePicks);

        await Result(FixtureStatus.Ended, 2, 1);
        _db.ChangeTracker.Clear();

        // League points as for any match, and the pot on top.
        Assert.Equal(2, _db.Predictions.Single().PointsEarned);
        Assert.Equal(Pot, Balance(_alice));
    }

    [Fact]
    public async Task A_normal_correct_score_does_not_win_the_pot()
    {
        await Special();
        Ok<object>(await Predictions(_alice).Submit(Normal("correct_score", 2, 1)));

        await Result(FixtureStatus.Ended, 2, 1);

        Assert.Equal(ScoringService.CorrectScorePoints, _db.Predictions.AsNoTracking().Single().PointsEarned);
        Assert.Empty(_db.WalletEntries);
    }

    [Fact]
    public async Task A_score_can_be_changed_until_kickoff()
    {
        await Special();
        Ok<List<PlayerSpecialGameDto>>(await Submit(_alice, 1, 0));
        var game = Ok<List<PlayerSpecialGameDto>>(await Submit(_alice, 3, 2)).Single();
        Assert.Equal((3, 2, 1), (game.MyHomeGoals!.Value, game.MyAwayGoals!.Value, game.Entries));

        _match.Kickoff = DateTime.UtcNow.AddSeconds(10);
        _db.SaveChanges();
        Assert.Contains("locked", Message(await Submit(_alice, 0, 0)));
        Assert.False(Ok<List<PlayerSpecialGameDto>>(await Player(_alice).Get()).Single().Open);
    }

    [Fact]
    public async Task The_system_admin_does_not_play_and_nobody_sees_other_scores()
    {
        await Special();
        Assert.Contains("doesn't play", Message(await Submit(_system, 1, 1)));

        Picked(_alice, 2, 1);
        var game = Ok<List<PlayerSpecialGameDto>>(await Player(_bob).Get()).Single();
        Assert.Equal(1, game.Entries);
        Assert.Null(game.MyHomeGoals);
    }

    [Fact]
    public async Task The_tab_is_empty_with_no_game_or_a_closed_competition()
    {
        Assert.IsType<NoContentResult>(await Player(_alice).Get());

        await Special();
        Assert.Single(Ok<List<PlayerSpecialGameDto>>(await Player(_alice).Get()));

        _db.Competitions.Single().Enabled = false;
        _db.SaveChanges();
        Assert.IsType<NoContentResult>(await Player(_alice).Get());
        Assert.IsType<NotFoundObjectResult>(await Submit(_alice, 1, 0));
    }

    [Fact]
    public async Task A_match_can_only_be_made_special_before_kickoff()
    {
        _match.Kickoff = DateTime.UtcNow.AddMinutes(-5);
        _db.SaveChanges();
        Assert.Contains("before it kicks off", Message(await Admin().SetPot(_match.Id, new SetSpecialPotRequest { PotKobo = Pot })));
        Assert.Null(_db.Fixtures.Single().SpecialPotKobo);
    }

    [Fact]
    public async Task The_pot_is_shared_equally_by_everyone_with_the_exact_score()
    {
        await Special();
        Picked(_alice, 2, 1);
        Picked(_bob, 2, 1);
        Picked(_carol, 1, 0);
        Picked(_system, 2, 1);

        await Result(FixtureStatus.Ended, 2, 1);

        Assert.Equal(5_000_00, Balance(_alice));
        Assert.Equal(5_000_00, Balance(_bob));
        Assert.Equal(0, Balance(_carol));
        Assert.Equal(0, Balance(_system));
        // The organiser's money, not the season pot's.
        Assert.All(_db.WalletEntries, e => Assert.Equal(WalletKinds.Adjustment, e.Kind));

        var game = Ok<List<PlayerSpecialGameDto>>(await Player(_alice).Get()).Single();
        Assert.Equal((2, 5_000_00L), (game.Winners, game.MyPrizeKobo!.Value));

        // Scoring the match again pays nobody twice.
        await new ScoringService(_db).ScoreFixtureAsync(_match.Id);
        Assert.Equal(2, _db.WalletEntries.Count());
    }

    [Fact]
    public async Task Nobody_is_paid_while_the_match_is_unfinished_or_nobody_got_it()
    {
        await Special();
        Picked(_alice, 2, 1);

        await Result(FixtureStatus.Live, 2, 1);
        Assert.Empty(_db.WalletEntries);
        await Result(FixtureStatus.Ended, 0, 0);
        Assert.Empty(_db.WalletEntries);
    }

    [Fact]
    public async Task A_corrected_result_moves_the_prize()
    {
        await Special();
        Picked(_alice, 2, 1);
        Picked(_bob, 2, 2);

        await Result(FixtureStatus.Ended, 2, 1);
        Assert.Equal(Pot, Balance(_alice));

        await Result(FixtureStatus.Ended, 2, 2);
        Assert.Equal(0, Balance(_alice));
        Assert.Equal(Pot, Balance(_bob));
        Assert.Equal(1, _db.WalletEntries.Count(e => e.ReversesEntryId != null));
        Assert.Equal(_bob.Id, _db.SpecialGamePrizes.Single().UserId);
    }

    [Fact]
    public async Task Marking_paid_records_a_payout_the_player_sees_and_can_be_undone()
    {
        await Special();
        Picked(_alice, 2, 1);
        await Result(FixtureStatus.Ended, 2, 1);

        var mine = Ok<MyWalletDto>(await Wallet(_alice).Get(null));
        Assert.Equal("owed", Assert.Single(mine.Entries).PrizeStatus);
        Assert.Equal(Pot, mine.BalanceKobo);

        Assert.True((await SetPaid(_alice, true)).Single().Prizes.Single().Paid);
        await SetPaid(_alice, true); // twice records it once
        mine = Ok<MyWalletDto>(await Wallet(_alice).Get(null));
        Assert.Equal(2, mine.Entries.Count);
        Assert.Equal("paid", mine.Entries.Single(e => e.Kind == WalletKinds.Adjustment).PrizeStatus);
        Assert.Equal((Pot, 0L), (mine.PaidOutKobo, mine.BalanceKobo));

        Assert.False((await SetPaid(_alice, false)).Single().Prizes.Single().Paid);
        mine = Ok<MyWalletDto>(await Wallet(_alice).Get(null));
        Assert.Equal("owed", mine.Entries.Single(e => e.Kind == WalletKinds.Adjustment && !e.Reversed).PrizeStatus);
        Assert.Equal((0L, Pot), (mine.PaidOutKobo, mine.BalanceKobo));
    }

    [Fact]
    public async Task A_correction_waits_for_a_paid_prize_to_be_marked_unpaid()
    {
        await Special();
        Picked(_alice, 2, 1);
        Picked(_bob, 2, 2);
        await Result(FixtureStatus.Ended, 2, 1);
        await SetPaid(_alice, true);

        await Result(FixtureStatus.Ended, 2, 2);
        var game = Ok<List<SpecialGameDto>>(await Admin().List()).Single();
        Assert.Contains("Alice", game.Problem);
        Assert.Equal(0, Balance(_bob));

        game = (await SetPaid(_alice, false)).Single();
        Assert.Null(game.Problem);
        Assert.Equal("Bob", game.Prizes.Single().Name);
        Assert.Equal((0L, Pot), (Balance(_alice), Balance(_bob)));
    }

    [Fact]
    public async Task Changing_or_removing_the_pot_moves_prizes_unless_already_paid()
    {
        await Special();
        Picked(_alice, 2, 1);
        await Result(FixtureStatus.Ended, 2, 1);

        await Special(20_000_00);
        Assert.Equal(20_000_00, Balance(_alice));

        await SetPaid(_alice, true);
        Assert.Contains("marked as paid", Message(await Admin().Remove(_match.Id)));
        Assert.NotNull(_db.Fixtures.Single().SpecialPotKobo);

        await SetPaid(_alice, false);
        Ok<List<SpecialGameDto>>(await Admin().Remove(_match.Id));
        Assert.Equal(0, Balance(_alice));
        Assert.Null(_db.Fixtures.Single().SpecialPotKobo);
    }

    [Fact]
    public async Task Prizes_cannot_be_reversed_from_the_general_wallet_page()
    {
        await Special();
        Picked(_alice, 2, 1);
        await Result(FixtureStatus.Ended, 2, 1);
        var wallet = new AdminWalletController(_db) { ControllerContext = As(_system) };

        var message = Message(await wallet.Reverse(_db.WalletEntries.Single().Id, new ReverseEntryRequest { Reason = "oops" }));
        Assert.Contains("Special games page", message);
    }

    [Fact]
    public async Task Switched_off_the_tab_goes_but_the_admin_still_runs_it()
    {
        await Special();
        await Switch(false);

        Assert.IsType<NoContentResult>(await Player(_alice).Get());
        Assert.IsType<NotFoundObjectResult>(await Submit(_alice, 2, 1));
        Assert.Equal(Pot, Ok<List<SpecialGameDto>>(await Admin().List()).Single().PotKobo);
    }

    [Fact]
    public async Task Switched_off_no_prize_is_paid_until_it_is_switched_back_on()
    {
        await Special();
        Picked(_alice, 2, 1);
        await Switch(false);

        await Result(FixtureStatus.Ended, 2, 1);
        Assert.Empty(_db.WalletEntries);

        await Switch(true);
        Assert.Equal(Pot, Balance(_alice));
    }

    [Fact]
    public async Task Switching_off_leaves_prizes_already_won_and_they_can_still_be_paid()
    {
        await Special();
        Picked(_alice, 2, 1);
        await Result(FixtureStatus.Ended, 2, 1);

        await Switch(false);
        Assert.Equal(Pot, Balance(_alice));

        await SetPaid(_alice, true);
        Assert.Equal("paid", Ok<MyWalletDto>(await Wallet(_alice).Get(null)).Entries.Single(e => e.Kind == WalletKinds.Adjustment).PrizeStatus);
    }
}
