using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Predictions;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Tests;

// The prediction rules as PredictionsController.Submit enforces them, through the real endpoint
// on an in-memory database. The Predict page mirrors these (predictors/src/lib/predictionRules.ts).
public class PredictionRulesTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    // Week 8 is under way: its first match has been played, two are still to come.
    private const string Week8 = "week-8";
    private const string W8Played = "w8-played", W8Tonight = "w8-tonight", W8Monday = "w8-monday";
    // Week 9 hasn't started: its first match is on Friday.
    private const string Week9 = "week-9";
    private const string W9Friday = "w9-friday", W9Saturday = "w9-saturday";

    private readonly AppDbContext _db;
    private readonly Guid _player = Guid.NewGuid();

    public PredictionRulesTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Users.Add(new User { Id = _player, Name = "Player", Email = "player@test" });
        _db.MatchWeeks.AddRange(
            new MatchWeek { Id = Week8, Name = "Week 8", Competition = "La Liga" },
            new MatchWeek { Id = Week9, Name = "Week 9", Competition = "La Liga" });
        _db.Fixtures.AddRange(
            Match(W8Played, Week8, Now.AddDays(-1), FixtureStatus.Ended),
            Match(W8Tonight, Week8, Now.AddHours(3)),
            Match(W8Monday, Week8, Now.AddDays(1)),
            Match(W9Friday, Week9, Now.AddDays(4)),
            Match(W9Saturday, Week9, Now.AddDays(5)));
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static Fixture Match(string id, string week, DateTime kickoff, FixtureStatus status = FixtureStatus.PreMatch) =>
        new() { Id = id, WeekId = week, HomeTeam = $"{id} home", AwayTeam = $"{id} away", Kickoff = kickoff, Status = status };

    private void Rules(bool lockWeek = false, bool final = false, bool late = false, bool partial = true, bool adminLocked = false)
    {
        _db.ScraperSettings.Add(new ScraperSettings
        {
            Id = 1,
            LockWeekAtFirstKickoff = lockWeek,
            PredictionsFinal = final,
            AllowLatePredictions = late,
            AllowPartialPredictions = partial,
            PredictionsLocked = adminLocked,
        });
        _db.SaveChanges();
    }

    private void Saved(string fixtureId, string weekId, OutcomeType outcome)
    {
        _db.Predictions.Add(new Prediction { UserId = _player, FixtureId = fixtureId, WeekId = weekId, Outcome = outcome });
        _db.SaveChanges();
    }

    private async Task<IActionResult> Submit(string weekId, params (string Fixture, string Outcome)[] picks)
    {
        var controller = new PredictionsController(_db, Options.Create(new LiveScraperSettings()),
            new AppCache(new MemoryCache(new MemoryCacheOptions())))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _player.ToString())], "test")),
                },
            },
        };
        return await controller.Submit(new SubmitPredictionsRequest(weekId,
            picks.Select(p => new PredictionItemRequest(p.Fixture, p.Outcome, null, null))));
    }

    private OutcomeType? OutcomeOf(string fixtureId)
    {
        _db.ChangeTracker.Clear();
        return _db.Predictions.FirstOrDefault(p => p.UserId == _player && p.FixtureId == fixtureId)?.Outcome;
    }

    private static string Message(IActionResult result) =>
        ((ObjectResult)result).Value!.GetType().GetProperty("message")!.GetValue(((ObjectResult)result).Value)!.ToString()!;

    // ── Lock the whole week at first kickoff (with predictions not final) ───────────────────────

    [Fact]
    public async Task WeekLock_PlayersCanPredictNextWeekWhileThisOneIsUnderWay()
    {
        Rules(lockWeek: true);

        var result = await Submit(Week9, (W9Friday, "home_win"), (W9Saturday, "draw"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OutcomeType.HomeWin, OutcomeOf(W9Friday));
    }

    [Fact]
    public async Task WeekLock_PredictionsCanBeChangedUntilTheWeeksFirstKickoff()
    {
        Rules(lockWeek: true);
        Saved(W9Saturday, Week9, OutcomeType.HomeWin);

        var result = await Submit(Week9, (W9Saturday, "away_win"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OutcomeType.AwayWin, OutcomeOf(W9Saturday));
    }

    [Fact]
    public async Task WeekLock_OnceTheFirstMatchStarts_EveryPredictionThatWeekIsLocked()
    {
        Rules(lockWeek: true);
        Saved(W8Monday, Week8, OutcomeType.HomeWin);

        // Monday's match hasn't started, but the week's first match has.
        var result = await Submit(Week8, (W8Monday, "draw"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("locked when its first match kicked off", Message(bad));
        Assert.Equal(OutcomeType.HomeWin, OutcomeOf(W8Monday));
    }

    [Fact]
    public async Task WeekLock_StartsLockingThirtySecondsBeforeTheFirstKickoff()
    {
        Rules(lockWeek: true);
        _db.Fixtures.Find(W9Friday)!.Kickoff = DateTime.UtcNow.AddSeconds(20);
        _db.SaveChanges();
        Saved(W9Saturday, Week9, OutcomeType.HomeWin);

        var result = await Submit(Week9, (W9Saturday, "draw"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(OutcomeType.HomeWin, OutcomeOf(W9Saturday));
    }

    [Fact]
    public async Task WeekLock_APostponedFirstMatchDoesNotStartTheWeek()
    {
        Rules(lockWeek: true);
        var friday = _db.Fixtures.Find(W9Friday)!;
        friday.Kickoff = DateTime.UtcNow.AddHours(-2);
        friday.Status = FixtureStatus.Postponed;
        _db.SaveChanges();

        var result = await Submit(Week9, (W9Saturday, "draw"));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task WeekLock_WithoutLatePredictions_APlayerWhoHadNotPredictedIsLockedOutToo()
    {
        Rules(lockWeek: true, late: false);

        var result = await Submit(Week8, (W8Monday, "draw"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(OutcomeOf(W8Monday));
    }

    [Fact]
    public async Task WeekLock_WithLatePredictions_APlayerWhoHadNotPredictedGetsOneSubmission()
    {
        Rules(lockWeek: true, late: true);

        var first = await Submit(Week8, (W8Tonight, "home_win"), (W8Monday, "draw"));
        var second = await Submit(Week8, (W8Monday, "away_win"));

        Assert.IsType<OkObjectResult>(first);
        Assert.IsType<BadRequestObjectResult>(second);
        Assert.Equal(OutcomeType.Draw, OutcomeOf(W8Monday));
    }

    // ── Each match locks at its own kickoff (the week lock off) ─────────────────────────────────

    [Fact]
    public async Task NoWeekLock_MatchesStillToComeStayEditableAfterTheWeekStarts()
    {
        Rules(lockWeek: false);
        Saved(W8Monday, Week8, OutcomeType.HomeWin);

        var result = await Submit(Week8, (W8Monday, "draw"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OutcomeType.Draw, OutcomeOf(W8Monday));
    }

    [Fact]
    public async Task NoWeekLock_AMatchThatHasStartedCannotBeChanged()
    {
        Rules(lockWeek: false);
        Saved(W8Played, Week8, OutcomeType.HomeWin);

        // Skipped rather than refused, so the rest of a page left open across a kickoff still saves.
        var result = await Submit(Week8, (W8Played, "draw"), (W8Monday, "draw"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OutcomeType.HomeWin, OutcomeOf(W8Played));
        Assert.Equal(OutcomeType.Draw, OutcomeOf(W8Monday));
    }

    // ── Predictions are final ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Final_ASubmittedPredictionCannotBeChangedEvenBeforeKickoff()
    {
        Rules(lockWeek: true, final: true);
        Saved(W9Saturday, Week9, OutcomeType.HomeWin);

        var result = await Submit(Week9, (W9Saturday, "away_win"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("is final", Message(bad));
        Assert.Equal(OutcomeType.HomeWin, OutcomeOf(W9Saturday));
    }

    [Fact]
    public async Task Final_ASkippedMatchCanStillBeAddedWhileOpen()
    {
        Rules(final: true);
        Saved(W9Saturday, Week9, OutcomeType.HomeWin);

        var result = await Submit(Week9, (W9Saturday, "home_win"), (W9Friday, "draw"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(OutcomeType.Draw, OutcomeOf(W9Friday));
    }

    // ── Other rules ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IncompletePredictionsRefusedUnlessAllowed()
    {
        Rules(partial: false);

        var result = await Submit(Week9, (W9Friday, "draw"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Predict every match", Message(bad));
    }

    [Fact]
    public async Task AdminLockRefusesEverything()
    {
        Rules(adminLocked: true);

        var result = await Submit(Week9, (W9Friday, "draw"), (W9Saturday, "draw"));

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Null(OutcomeOf(W9Friday));
    }
}
