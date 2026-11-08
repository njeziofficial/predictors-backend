using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Fixtures;
using OctopusPrediction.Api.Dtos.SpecialGames;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Mappers;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// The Special games tab for players: predict the exact score of each game with a pot. These picks
// are separate from a player's normal prediction on the match and earn no league points.
[ApiController]
[Route("api/special-games")]
[Authorize]
public class SpecialGamesController(AppDbContext db) : ControllerBase
{
    // How long a finished game stays on the tab.
    private static readonly TimeSpan ShownAfterKickoff = TimeSpan.FromDays(7);

    // The games to show, or 204 when there are none or they're switched off (the tab goes).
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!await SpecialGames.EnabledAsync(db)) return NoContent();
        var games = await GamesAsync(Me());
        return games.Count == 0 ? NoContent() : Ok(games);
    }

    // Saves the player's score for a game, replacing any earlier one, until it locks.
    [HttpPut("{fixtureId}/pick")]
    public async Task<IActionResult> Submit(string fixtureId, SubmitSpecialPickRequest request)
    {
        if (!await SpecialGames.EnabledAsync(db)) return NotFound(new { message = "That special game isn't available." });
        var fixture = await db.Fixtures.AsNoTracking().Include(f => f.Week).FirstOrDefaultAsync(f => f.Id == fixtureId);
        if (fixture?.SpecialPotKobo is null || await DisabledAsync(fixture.Week.CompetitionId))
            return NotFound(new { message = "That special game isn't available." });

        var me = Me();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me);
        if (user is null) return Unauthorized();
        if (user.IsSystemUser) return BadRequest(new { message = "The system admin account doesn't play." });
        if (user.MustResetPassword)
            return StatusCode(403, new { code = "must_reset_password", message = "You must reset your password before predicting." });
        if (await db.ScraperSettings.AsNoTracking().AnyAsync(s => s.PredictionsLocked))
            return StatusCode(403, new { message = "Predictions are currently locked by an admin." });
        if (!SpecialGames.IsOpen(fixture))
            return BadRequest(new { message = "This game is locked: it has kicked off." });

        var pick = await db.SpecialGamePicks.FirstOrDefaultAsync(p => p.FixtureId == fixtureId && p.UserId == me);
        if (pick is null)
            db.SpecialGamePicks.Add(new SpecialGamePick { FixtureId = fixtureId, UserId = me, HomeGoals = request.HomeGoals, AwayGoals = request.AwayGoals });
        else
            (pick.HomeGoals, pick.AwayGoals, pick.UpdatedAt) = (request.HomeGoals, request.AwayGoals, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return Ok(await GamesAsync(me));
    }

    private Task<bool> DisabledAsync(string competitionId) =>
        db.Competitions.AsNoTracking().AnyAsync(c => c.Id == competitionId && !c.Enabled);

    private async Task<List<PlayerSpecialGameDto>> GamesAsync(Guid me)
    {
        var since = DateTime.UtcNow - ShownAfterKickoff;
        var disabled = await db.Competitions.AsNoTracking().Where(c => !c.Enabled).Select(c => c.Id).ToListAsync();
        var fixtures = await db.Fixtures.AsNoTracking().Include(f => f.Week)
            .Where(f => f.SpecialPotKobo != null && f.Kickoff >= since && !disabled.Contains(f.Week.CompetitionId))
            .ToListAsync();
        var ids = fixtures.Select(f => f.Id).ToList();
        var picks = await db.SpecialGamePicks.AsNoTracking().Where(p => ids.Contains(p.FixtureId)).ToListAsync();
        var prizes = await db.SpecialGamePrizes.AsNoTracking().Where(p => ids.Contains(p.FixtureId)).ToListAsync();

        return [.. fixtures
            .OrderBy(f => SpecialGames.IsOpen(f) ? 0 : 1).ThenBy(f => f.Kickoff)
            .Select(f =>
            {
                var mine = picks.FirstOrDefault(p => p.FixtureId == f.Id && p.UserId == me);
                return new PlayerSpecialGameDto(
                    f.Id, f.Week.Competition, f.Week.Name, f.HomeTeam, f.AwayTeam, f.Kickoff, FixtureMappers.MapStatus(f.Status),
                    f.FinalScoreHome is { } home && f.FinalScoreAway is { } away ? new ScoreDto(home, away) : null,
                    f.SpecialPotKobo!.Value, SpecialGames.IsOpen(f), picks.Count(p => p.FixtureId == f.Id),
                    mine?.HomeGoals, mine?.AwayGoals,
                    prizes.Count(p => p.FixtureId == f.Id),
                    prizes.FirstOrDefault(p => p.FixtureId == f.Id && p.UserId == me)?.AmountKobo);
            })];
    }

    private Guid Me() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
