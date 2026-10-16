using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Wallet;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Tests;

// The wallet ledger: entry fees make the pot, winnings share it out (never more than it holds),
// payouts settle what a player is owed (never more), and mistakes are reversed, never edited.
public class WalletTests : IDisposable
{
    private const long Fee = 3_000_00;
    private readonly AppDbContext _db;
    private readonly User _system;
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _carol;

    public WalletTests()
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

    private AdminWalletController Admin() => new(_db) { ControllerContext = As(_system) };

    private static T Ok<T>(IActionResult result) => (T)Assert.IsType<OkObjectResult>(result).Value!;

    private static string Message(IActionResult result) =>
        (string)Assert.IsType<BadRequestObjectResult>(result).Value!.GetType().GetProperty("message")!.GetValue(((BadRequestObjectResult)result).Value)!;

    private async Task<AdminWalletDto> PayAll() =>
        Ok<RecordEntryFeesResultDto>(await Admin().RecordEntryFees(new() { UserIds = [_alice.Id, _bob.Id, _carol.Id] })).Wallet;

    private Task<IActionResult> Record(User user, string kind, long kobo) =>
        Admin().Record(new() { UserId = user.Id, Kind = kind, AmountKobo = kobo, Note = kind == WalletKinds.Adjustment ? "why" : null });

    [Fact]
    public async Task Entry_fees_make_the_pot_and_are_recorded_once_a_season()
    {
        var wallet = await PayAll();
        Assert.Equal((3 * Fee, 3), (wallet.PotKobo, wallet.PaidPlayers));
        Assert.Equal("2026/27", wallet.Season);
        Assert.DoesNotContain(wallet.Players, p => p.UserId == _system.Id.ToString());

        // Marking them again records nothing new.
        var again = Ok<RecordEntryFeesResultDto>(await Admin().RecordEntryFees(new() { UserIds = [_alice.Id] }));
        Assert.Equal((0, 1, 3 * Fee), (again.Recorded, again.AlreadyPaid, again.Wallet.PotKobo));

        Assert.IsType<BadRequestObjectResult>(await Admin().RecordEntryFees(new() { UserIds = [_system.Id] }));
        Assert.Equal(3, _db.AuditLogs.Count(a => a.Action == "WalletEntryFeeRecorded"));
    }

    [Fact]
    public async Task Winnings_can_never_be_more_than_the_pot_has_left()
    {
        await PayAll(); // ₦9,000

        Ok<AdminWalletDto>(await Record(_alice, WalletKinds.Winnings, 6_000_00));
        Assert.Contains("₦3,000", Message(await Record(_bob, WalletKinds.Winnings, 4_000_00)));

        var wallet = Ok<AdminWalletDto>(await Record(_bob, WalletKinds.Winnings, 3_000_00));
        Assert.Equal((9_000_00, 0L, 9_000_00), (wallet.AllocatedKobo, wallet.UnallocatedKobo, wallet.OwedKobo));
    }

    [Fact]
    public async Task Payouts_settle_what_a_player_is_owed_and_never_more()
    {
        await PayAll();
        await Record(_alice, WalletKinds.Winnings, 5_000_00);

        Assert.Contains("₦5,000", Message(await Record(_alice, WalletKinds.Payout, 6_000_00)));
        var wallet = Ok<AdminWalletDto>(await Record(_alice, WalletKinds.Payout, 5_000_00));

        var alice = wallet.Players.Single(p => p.UserId == _alice.Id.ToString());
        Assert.Equal((Fee, 5_000_00L, 5_000_00L, 0L), (alice.PaidInKobo, alice.WonKobo, alice.PaidOutKobo, alice.BalanceKobo));
        Assert.Equal((5_000_00L, 0L), (wallet.PaidOutKobo, wallet.OwedKobo));
        // Stored as money leaving what they're owed.
        Assert.Equal(-5_000_00, _db.WalletEntries.Single(e => e.Kind == WalletKinds.Payout).AmountKobo);
    }

    [Fact]
    public async Task Bad_amounts_and_kinds_are_refused()
    {
        await PayAll();
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, WalletKinds.Winnings, 0));
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, WalletKinds.Winnings, -100));
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, WalletKinds.EntryFee, Fee));
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, "bonus", 100));
        Assert.IsType<BadRequestObjectResult>(await Admin().Record(new() { UserId = _alice.Id, Kind = WalletKinds.Adjustment, AmountKobo = 100 }));
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, WalletKinds.Adjustment, -100)); // owed nothing yet
        Assert.IsType<BadRequestObjectResult>(await Record(_alice, WalletKinds.Winnings, WalletLedger.MaxAmountKobo + 1));
    }

    [Fact]
    public async Task A_mistake_is_reversed_once_and_the_original_stays()
    {
        await PayAll();
        await Record(_alice, WalletKinds.Winnings, 2_000_00);
        await Record(_alice, WalletKinds.Payout, 2_000_00);
        var payout = _db.WalletEntries.Single(e => e.Kind == WalletKinds.Payout);

        var wallet = Ok<AdminWalletDto>(await Admin().Reverse(payout.Id, new() { Reason = "Transfer bounced" }));
        Assert.Equal(2_000_00, wallet.Players.Single(p => p.UserId == _alice.Id.ToString()).BalanceKobo);

        Assert.Contains("already", Message(await Admin().Reverse(payout.Id, new() { Reason = "again" })));
        var reversal = _db.WalletEntries.Single(e => e.ReversesEntryId == payout.Id);
        Assert.Contains("can't itself", Message(await Admin().Reverse(reversal.Id, new() { Reason = "undo" })));

        var entries = await AdminWalletController.EntriesAsync(_db, _alice.Id, "2026/27");
        Assert.True(entries.Single(e => e.Id == payout.Id.ToString()).Reversed);
        Assert.Equal(4, entries.Count);
    }

    [Fact]
    public async Task Paid_out_winnings_and_shared_out_entry_fees_cannot_be_reversed_from_under_it()
    {
        await PayAll();
        await Record(_alice, WalletKinds.Winnings, 9_000_00);
        await Record(_alice, WalletKinds.Payout, 9_000_00);

        var winnings = _db.WalletEntries.Single(e => e.Kind == WalletKinds.Winnings);
        Assert.Contains("only owed", Message(await Admin().Reverse(winnings.Id, new() { Reason = "wrong" })));

        // The whole pot is shared out, so no entry fee can leave it.
        var bobsFee = _db.WalletEntries.Single(e => e.Kind == WalletKinds.EntryFee && e.UserId == _bob.Id);
        Assert.Contains("smaller than the winnings", Message(await Admin().Reverse(bobsFee.Id, new() { Reason = "refund" })));
    }

    [Fact]
    public async Task Marking_a_player_unpaid_takes_their_fee_out_of_the_pot()
    {
        await PayAll(); // ₦9,000 from three

        var wallet = Ok<AdminWalletDto>(await Admin().MarkUnpaid(_bob.Id, new() { Reason = "Transfer never arrived" }));

        Assert.Equal((2 * Fee, 2), (wallet.PotKobo, wallet.PaidPlayers));
        Assert.False(wallet.Players.Single(p => p.UserId == _bob.Id.ToString()).Paid);
        // The payment stays in the history, reversed, with the reason.
        var entries = await AdminWalletController.EntriesAsync(_db, _bob.Id, "2026/27");
        Assert.True(entries.Single(e => e.ReversesEntryId is null).Reversed);
        Assert.Equal("Transfer never arrived", entries.Single(e => e.ReversesEntryId is not null).Note);
        Assert.Single(_db.AuditLogs, a => a.Action == "WalletMarkedUnpaid");

        // Already unpaid: nothing to undo. Paying again counts again.
        Assert.Contains("hasn't paid", Message(await Admin().MarkUnpaid(_bob.Id, new() { Reason = "again" })));
        var repaid = Ok<RecordEntryFeesResultDto>(await Admin().RecordEntryFees(new() { UserIds = [_bob.Id] }));
        Assert.Equal((1, 3 * Fee), (repaid.Recorded, repaid.Wallet.PotKobo));
    }

    [Fact]
    public async Task A_player_cannot_be_marked_unpaid_once_their_fee_is_shared_out()
    {
        await PayAll();
        await Record(_alice, WalletKinds.Winnings, 9_000_00);

        Assert.Contains("smaller than the winnings", Message(await Admin().MarkUnpaid(_bob.Id, new() { Reason = "refund" })));
        Assert.Equal(3 * Fee, Ok<AdminWalletDto>(await Admin().Get(null)).PotKobo);
    }

    [Fact]
    public async Task Only_the_system_admin_can_reverse_an_entry_fee()
    {
        await PayAll();
        var admin = new User { Name = "Dave", Email = "dave@test", Role = UserRole.Admin };
        _db.Users.Add(admin);
        _db.SaveChanges();
        var fee = _db.WalletEntries.First(e => e.Kind == WalletKinds.EntryFee);

        var refused = Assert.IsType<ObjectResult>(
            await new AdminWalletController(_db) { ControllerContext = As(admin) }.Reverse(fee.Id, new() { Reason = "oops" }));
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(3 * Fee, Ok<AdminWalletDto>(await Admin().Get(null)).PotKobo);
    }

    [Fact]
    public async Task A_player_sees_their_own_wallet_and_the_pot()
    {
        await PayAll();
        await Record(_bob, WalletKinds.Winnings, 4_500_00);

        var mine = Ok<MyWalletDto>(await new WalletController(_db) { ControllerContext = As(_bob) }.Get(null));
        Assert.True(mine.Paid);
        Assert.Equal((Fee, 4_500_00L, 4_500_00L, 9_000_00L, 3), (mine.PaidInKobo, mine.WonKobo, mine.BalanceKobo, mine.PotKobo, mine.PaidPlayers));
        Assert.All(mine.Entries, e => Assert.Equal(_bob.Id.ToString(), e.UserId));
        Assert.Equal(2, mine.Entries.Count);
    }

    [Fact]
    public async Task A_new_season_starts_with_an_empty_pot_and_keeps_the_old_one()
    {
        await PayAll();

        var next = Ok<AdminWalletDto>(await Admin().UpdateSettings(new() { Season = "2027/28", EntryFeeKobo = 5_000_00 }));
        Assert.Equal(("2027/28", 0L, 5_000_00L), (next.Season, next.PotKobo, next.EntryFeeKobo));
        Assert.Equal(["2027/28", "2026/27"], next.Seasons);

        var old = Ok<AdminWalletDto>(await Admin().Get("2026/27"));
        Assert.Equal(3 * Fee, old.PotKobo);
        Assert.IsType<BadRequestObjectResult>(await Admin().UpdateSettings(new() { Season = "next year", EntryFeeKobo = Fee }));
    }
}
