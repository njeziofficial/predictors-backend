using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Wallet;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// The back office's side of the wallet ledger (WalletEntry): who has paid, the pot, and recording
// entry fees, winnings, payouts and reversals. Every change is in the audit trail.
[ApiController]
[Route("api/admin/wallet")]
[Authorize(Roles = "Admin")]
public class AdminWalletController(AppDbContext db) : ControllerBase
{
    // One change at a time, so two admins recording at once can't share out the same money
    // twice: each checks the totals as they stand after the other has saved. (One server.)
    private static readonly SemaphoreSlim Writes = new(1, 1);

    [HttpGet]
    [RequirePermission(Permissions.WalletView)]
    public async Task<IActionResult> Get([FromQuery] string? season)
    {
        var (current, fee) = await WalletLedger.CurrentAsync(db);
        season ??= current;
        if (!WalletLedger.IsSeason(season)) return BadRequest(new { message = "A season looks like 2026/27." });
        return Ok(await SummaryAsync(season, current, fee));
    }

    // One player's entries for the season, newest first.
    [HttpGet("users/{userId:guid}")]
    [RequirePermission(Permissions.WalletView)]
    public async Task<IActionResult> Player(Guid userId, [FromQuery] string? season)
    {
        var (current, _) = await WalletLedger.CurrentAsync(db);
        season ??= current;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return NotFound(new { message = "Player not found." });

        var totals = (await WalletLedger.TotalsAsync(db, season)).GetValueOrDefault(userId, WalletLedger.Totals.None);
        return Ok(new PlayerLedgerDto(PlayerDto(user, totals), await EntriesAsync(db, userId, season)));
    }

    // Marks players as having paid the season's entry fee. Those already paid are skipped.
    [HttpPost("entry-fees")]
    [RequirePermission(Permissions.WalletManage)]
    public async Task<IActionResult> RecordEntryFees(RecordEntryFeesRequest request)
    {
        var (current, fee) = await WalletLedger.CurrentAsync(db);
        var season = request.Season ?? current;
        if (!WalletLedger.IsSeason(season)) return BadRequest(new { message = "A season looks like 2026/27." });
        if (Future(request.OccurredAt)) return BadRequest(new { message = "The payment date can't be in the future." });

        var ids = request.UserIds.Distinct().ToList();
        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        if (users.Count != ids.Count) return NotFound(new { message = "One of those players wasn't found." });
        if (users.Any(u => u.IsSystemUser))
            return BadRequest(new { message = "The system admin account doesn't play, so it can't pay an entry fee." });

        var actor = await CurrentUserAsync();
        await Writes.WaitAsync();
        try
        {
            var totals = await WalletLedger.TotalsAsync(db, season);
            var unpaid = users.Where(u => !totals.GetValueOrDefault(u.Id, WalletLedger.Totals.None).Paid).ToList();
            foreach (var user in unpaid)
            {
                db.WalletEntries.Add(NewEntry(user.Id, season, WalletKinds.EntryFee, fee, request.Note, request.OccurredAt, actor));
                AuditLogger.Log(db, subject: user, actor: actor, action: "WalletEntryFeeRecorded",
                    details: $"{WalletLedger.Naira(fee)} entry fee for {season}" + NoteSuffix(request.Note));
            }
            await db.SaveChangesAsync();
            return Ok(new RecordEntryFeesResultDto(unpaid.Count, users.Count - unpaid.Count, await SummaryAsync(season, current, fee)));
        }
        finally
        {
            Writes.Release();
        }
    }

    // Winnings, a payout or an adjustment for one player.
    [HttpPost("entries")]
    [RequirePermission(Permissions.WalletManage)]
    public async Task<IActionResult> Record(RecordEntryRequest request)
    {
        var kind = request.Kind.Trim().ToLowerInvariant();
        if (kind is not (WalletKinds.Winnings or WalletKinds.Payout or WalletKinds.Adjustment))
            return BadRequest(new { message = "Record winnings, a payout or an adjustment here. Entry fees are marked as paid on their own." });
        if (request.AmountKobo == 0 || Math.Abs(request.AmountKobo) > WalletLedger.MaxAmountKobo)
            return BadRequest(new { message = $"The amount must be more than nothing and at most {WalletLedger.Naira(WalletLedger.MaxAmountKobo)}." });
        if (kind != WalletKinds.Adjustment && request.AmountKobo < 0)
            return BadRequest(new { message = "The amount must be more than nothing. To undo an entry, reverse it." });
        var note = request.Note?.Trim();
        if (kind == WalletKinds.Adjustment && string.IsNullOrEmpty(note))
            return BadRequest(new { message = "Say why in the note: an adjustment needs a reason." });
        if (Future(request.OccurredAt)) return BadRequest(new { message = "The date can't be in the future." });

        var (current, fee) = await WalletLedger.CurrentAsync(db);
        var season = request.Season ?? current;
        if (!WalletLedger.IsSeason(season)) return BadRequest(new { message = "A season looks like 2026/27." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId);
        if (user is null) return NotFound(new { message = "Player not found." });
        if (user.IsSystemUser) return BadRequest(new { message = "The system admin account doesn't play." });

        // Stored signed: a payout takes from what the player is owed.
        var amount = kind == WalletKinds.Payout ? -request.AmountKobo : request.AmountKobo;
        var actor = await CurrentUserAsync();
        await Writes.WaitAsync();
        try
        {
            var totals = await WalletLedger.TotalsAsync(db, season);
            var mine = totals.GetValueOrDefault(user.Id, WalletLedger.Totals.None);
            if (WalletLedger.Check(kind, amount, mine, WalletLedger.Pot(totals.Values)) is { } problem)
                return BadRequest(new { message = problem });

            var entry = NewEntry(user.Id, season, kind, amount, note, request.OccurredAt, actor);
            db.WalletEntries.Add(entry);
            AuditLogger.Log(db, subject: user, actor: actor, action: "WalletEntryRecorded",
                details: $"{WalletKinds.Label(kind)} {WalletLedger.Naira(amount)} for {season}" + NoteSuffix(note));
            await db.SaveChangesAsync();
            return Ok(await SummaryAsync(season, current, fee));
        }
        finally
        {
            Writes.Release();
        }
    }

    // Cancels an entry with an opposite one. The original stays, marked as reversed.
    [HttpPost("entries/{id:guid}/reverse")]
    [RequirePermission(Permissions.WalletManage)]
    public async Task<IActionResult> Reverse(Guid id, ReverseEntryRequest request)
    {
        var reason = request.Reason.Trim();
        if (reason.Length == 0) return BadRequest(new { message = "Say why it's being reversed." });

        var actor = await CurrentUserAsync();
        await Writes.WaitAsync();
        try
        {
            var original = await db.WalletEntries.Include(e => e.User).FirstOrDefaultAsync(e => e.Id == id);
            if (original is null) return NotFound(new { message = "Entry not found." });
            if (original.ReversesEntryId is not null)
                return BadRequest(new { message = "A reversal can't itself be reversed. Record a new entry instead." });
            if (await db.WalletEntries.AnyAsync(e => e.ReversesEntryId == id))
                return BadRequest(new { message = "This entry has already been reversed." });

            var totals = await WalletLedger.TotalsAsync(db, original.Season);
            var mine = totals.GetValueOrDefault(original.UserId, WalletLedger.Totals.None);
            // Undoing a payout puts the money back on what they're owed, which is always allowed;
            // undoing an entry fee or winnings must leave the pot and the player's balance whole.
            if (WalletLedger.Check(original.Kind, -original.AmountKobo, mine, WalletLedger.Pot(totals.Values)) is { } problem)
                return BadRequest(new { message = problem });

            var reversal = NewEntry(original.UserId, original.Season, original.Kind, -original.AmountKobo, reason, null, actor);
            reversal.ReversesEntryId = original.Id;
            db.WalletEntries.Add(reversal);
            AuditLogger.Log(db, subject: original.User, actor: actor, action: "WalletEntryReversed",
                details: $"{WalletKinds.Label(original.Kind)} {WalletLedger.Naira(original.AmountKobo)} for {original.Season} reversed: {reason}");
            await db.SaveChangesAsync();

            var (current, fee) = await WalletLedger.CurrentAsync(db);
            return Ok(await SummaryAsync(original.Season, current, fee));
        }
        finally
        {
            Writes.Release();
        }
    }

    // Starts a new season, or changes the entry fee. Earlier seasons keep their own entries.
    [HttpPut("settings")]
    [SystemUserOnly]
    public async Task<IActionResult> UpdateSettings(UpdateWalletSettingsRequest request)
    {
        var season = request.Season.Trim();
        if (!WalletLedger.IsSeason(season)) return BadRequest(new { message = "A season looks like 2026/27." });
        if (request.EntryFeeKobo <= 0 || request.EntryFeeKobo > WalletLedger.MaxAmountKobo)
            return BadRequest(new { message = "Set an entry fee above nothing." });

        var settings = await db.ScraperSettings.FirstOrDefaultAsync();
        if (settings is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Settings aren't ready yet. Try again shortly." });

        var actor = await CurrentUserAsync();
        if (settings.WalletSeason != season)
            AuditLogger.Log(db, subject: null, actor: actor, action: "WalletSeasonChanged", field: "Season",
                previousValue: settings.WalletSeason, newValue: season);
        if (settings.WalletEntryFeeKobo != request.EntryFeeKobo)
            AuditLogger.Log(db, subject: null, actor: actor, action: "WalletEntryFeeChanged", field: "Entry fee",
                previousValue: WalletLedger.Naira(settings.WalletEntryFeeKobo), newValue: WalletLedger.Naira(request.EntryFeeKobo));
        (settings.WalletSeason, settings.WalletEntryFeeKobo) = (season, request.EntryFeeKobo);
        await db.SaveChangesAsync();
        return Ok(await SummaryAsync(season, season, request.EntryFeeKobo));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<AdminWalletDto> SummaryAsync(string season, string current, long fee)
    {
        var totals = await WalletLedger.TotalsAsync(db, season);
        // Active players, plus anyone (even disabled) with money in this season.
        var withEntries = totals.Keys.ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => !u.IsSystemUser && (!u.IsDisabled || withEntries.Contains(u.Id)))
            .OrderBy(u => u.Name)
            .ToListAsync();
        var pot = WalletLedger.Pot(totals.Values);
        return new AdminWalletDto(
            season, current, await WalletLedger.SeasonsAsync(db, current), fee,
            pot.Pot, pot.Allocated, pot.Unallocated, pot.PaidOut, pot.Owed,
            totals.Values.Count(t => t.Paid),
            [.. users.Select(u => PlayerDto(u, totals.GetValueOrDefault(u.Id, WalletLedger.Totals.None)))]);
    }

    private static WalletPlayerDto PlayerDto(User u, WalletLedger.Totals t) =>
        new(u.Id.ToString(), u.Name, u.WhatsAppName, u.IsDisabled, t.Paid, t.PaidIn, t.Won, t.PaidOut, t.Balance);

    internal static async Task<List<WalletEntryDto>> EntriesAsync(AppDbContext db, Guid userId, string season)
    {
        var entries = await db.WalletEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.Season == season)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.CreatedAt)
            .ToListAsync();
        var reversed = entries.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();
        return [.. entries.Select(e => new WalletEntryDto(
            e.Id.ToString(), e.UserId.ToString(), e.Season, e.Kind, e.AmountKobo, e.Note, e.OccurredAt, e.CreatedAt,
            e.CreatedByName, e.ReversesEntryId?.ToString(), reversed.Contains(e.Id)))];
    }

    private static WalletEntry NewEntry(Guid userId, string season, string kind, long amount, string? note, DateTime? occurredAt, User? actor) => new()
    {
        UserId = userId,
        Season = season,
        Kind = kind,
        AmountKobo = amount,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        OccurredAt = occurredAt?.ToUniversalTime() ?? DateTime.UtcNow,
        CreatedByUserId = actor?.Id,
        CreatedByName = actor?.Name ?? "",
    };

    // A little slack for clocks that run ahead.
    private static bool Future(DateTime? at) => at?.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5);

    private static string NoteSuffix(string? note) => string.IsNullOrWhiteSpace(note) ? "" : $" ({note.Trim()})";

    private Task<User?> CurrentUserAsync()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}
