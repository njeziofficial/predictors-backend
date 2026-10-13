using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Controllers;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Leaderboard;
using OctopusPrediction.Api.Dtos.MatchWeeks;
using OctopusPrediction.Api.Dtos.Predictions;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;
using OctopusPrediction.Api.Services.LiveScores;

namespace OctopusPrediction.Api.Tests;

// La Liga and the Champions League side by side: separate tables, and a competition switched off
// is hidden from players and closed to predictions.
public class CompetitionsTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private const string LaLigaWeek = "la-liga-week-9", UclWeek = "champions-league-week-3";
    private const string LaLigaMatch = "fs-real-madrid-vs-barcelona", UclMatch = "fs-champions-league-real-madrid-vs-barcelona";

    private readonly AppDbContext _db;
    private readonly Guid _alice = Guid.NewGuid(), _bob = Guid.NewGuid();

    public CompetitionsTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Competitions.SeedAsync(_db).GetAwaiter().GetResult();
        _db.Users.AddRange(
            new User { Id = _alice, Name = "Alice", Email = "alice@test" },
            new User { Id = _bob, Name = "Bob", Email = "bob@test" });
        _db.MatchWeeks.AddRange(
            new MatchWeek { Id = LaLigaWeek, Name = "La Liga — Week 9", Competition = "La Liga", CompetitionId = Competitions.LaLiga },
            new MatchWeek { Id = UclWeek, Name = "Champions League — Week 3", Competition = "Champions League", CompetitionId = Competitions.ChampionsLeague });
        _db.Fixtures.AddRange(
            new Fixture { Id = LaLigaMatch, WeekId = LaLigaWeek, HomeTeam = "Real Madrid", AwayTeam = "Barcelona", Kickoff = Now.AddDays(2) },
            new Fixture { Id = UclMatch, WeekId = UclWeek, HomeTeam = "Real Madrid", AwayTeam = "Barcelona", Kickoff = Now.AddDays(5) });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private AppCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    private static ControllerContext As(Guid userId, bool admin = false) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, admin ? "Admin" : "User")], "test")),
        },
    };

    private void SetEnabled(string competitionId, bool enabled)
    {
        _db.Competitions.Find(competitionId)!.Enabled = enabled;
        _db.SaveChanges();
    }

    private void Points(Guid user, string fixtureId, string weekId, int points)
    {
        _db.Predictions.Add(new Prediction { UserId = user, FixtureId = fixtureId, WeekId = weekId, Outcome = OutcomeType.HomeWin, PointsEarned = points });
        _db.SaveChanges();
    }

    private static T Value<T>(IActionResult result) => (T)((OkObjectResult)result).Value!;

    [Fact]
    public void FixtureIds_LaLigaKeepsItsIds_OtherCompetitionsAreKeptApart()
    {
        Assert.Equal(LaLigaMatch, MatchReading.FixtureId("La Liga", "Real Madrid", "Barcelona"));
        Assert.Equal(UclMatch, MatchReading.FixtureId("Champions League", "Real Madrid", "Barcelona"));
        Assert.Equal("champions-league-week-3", MatchReading.WeekId("Champions League", "Matchday 3"));
    }

    [Fact]
    public async Task OverallTables_CountOnlyTheirOwnCompetition()
    {
        Points(_alice, LaLigaMatch, LaLigaWeek, 3);
        Points(_bob, UclMatch, UclWeek, 5);
        _db.PreviousPoints.Add(new PreviousPoints { UserId = _bob, Points = 1, Label = "Before the app" });
        _db.SaveChanges();
        var cache = NewCache();

        var laLiga = await cache.OverallLeaderboardAsync(_db, Competitions.LaLiga);
        var ucl = await cache.OverallLeaderboardAsync(_db, Competitions.ChampionsLeague);

        // Points from before the app were La Liga's, so they count there and nowhere else.
        Assert.Equal([("Alice", 3), ("Bob", 1)], laLiga.Select(e => (e.Name, e.TotalPoints)));
        Assert.Equal([("Bob", 5), ("Alice", 0)], ucl.Select(e => (e.Name, e.TotalPoints)));
        Assert.All(ucl, e => Assert.Equal(0, e.PreviousPoints));
    }

    [Fact]
    public async Task SwitchedOff_PlayersDontSeeItsWeeksOrTable_AdminsDo()
    {
        SetEnabled(Competitions.ChampionsLeague, false);
        var cache = NewCache();

        var player = new MatchWeeksController(_db, cache) { ControllerContext = As(_alice) };
        var admin = new MatchWeeksController(_db, cache) { ControllerContext = As(_bob, admin: true) };

        Assert.Equal([LaLigaWeek], Value<IEnumerable<MatchWeekDto>>(await player.GetAll(null)).Select(w => w.Id));
        Assert.IsType<NotFoundObjectResult>(await player.GetAll(Competitions.ChampionsLeague));
        Assert.IsType<NotFoundResult>(await player.GetById(UclWeek));
        Assert.Equal([LaLigaWeek, UclWeek], Value<IEnumerable<MatchWeekDto>>(await admin.GetAll(null)).Select(w => w.Id));

        var table = new LeaderboardController(_db, cache) { ControllerContext = As(_alice) };
        Assert.IsType<NotFoundObjectResult>(await table.GetOverall(Competitions.ChampionsLeague));
        Assert.IsType<OkObjectResult>(await table.GetOverall(null));
    }

    [Fact]
    public async Task CurrentWeek_IsPerCompetition_LaLigaWhenNoneIsNamed()
    {
        SetEnabled(Competitions.ChampionsLeague, true);
        var controller = new MatchWeeksController(_db, NewCache()) { ControllerContext = As(_alice) };

        Assert.Equal(LaLigaWeek, Value<MatchWeekDto>(await controller.GetCurrent(null)).Id);
        Assert.Equal(UclWeek, Value<MatchWeekDto>(await controller.GetCurrent(Competitions.ChampionsLeague)).Id);
    }

    [Fact]
    public async Task SwitchedOff_NobodyCanPredictIt()
    {
        SetEnabled(Competitions.ChampionsLeague, false);
        var controller = new PredictionsController(_db, Options.Create(new LiveScraperSettings()), NewCache())
        {
            ControllerContext = As(_alice, admin: true),
        };

        var result = await controller.Submit(new SubmitPredictionsRequest(UclWeek,
            [new PredictionItemRequest(UclMatch, "home_win", null, null)]));

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(_db.Predictions.Any());
    }
}
