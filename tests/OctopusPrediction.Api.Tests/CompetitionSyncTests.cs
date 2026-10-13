using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;

namespace OctopusPrediction.Api.Tests;

// Scraped Champions League matches through the real FixtureSync: which week each lands in, and
// knockout matches marked on the score after 90 minutes. La Liga's path is checked alongside.
public class CompetitionSyncTests : IDisposable
{
    private const string Ucl = "Champions League";
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private readonly WarningLog _log = new();

    private async Task Sync(string competition, params ScrapedMatchDto[] matches)
    {
        var sync = new FixtureSync(_db, new ScoringService(_db), _log);
        await sync.SyncAsync(matches, competition, CancellationToken.None);
        _db.ChangeTracker.Clear();
    }

    // Keeps the warnings FixtureSync logs.
    private sealed class WarningLog : ILogger<FixtureSync>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (level == LogLevel.Warning) Warnings.Add(format(state, ex));
        }
    }

    [Fact]
    public async Task UnknownRound_IsSkippedWithAWarning_OncePerRoundName()
    {
        // Unique, as the warned-about names last the whole test run.
        var round = $"Mystery stage {Guid.NewGuid():N}";
        var kickoff = new DateTime(2027, 2, 17, 20, 0, 0, DateTimeKind.Utc);

        await Sync(Ucl, Scheduled("Arsenal", "Inter", round, kickoff), Scheduled("PSG", "Napoli", round, kickoff));
        await Sync(Ucl, Scheduled("Arsenal", "Inter", round, kickoff));

        Assert.Empty(_db.Fixtures);
        Assert.Single(_log.Warnings, w => w.Contains(round));
    }

    [Fact]
    public async Task Qualifying_IsSkippedQuietly()
    {
        await Sync(Ucl, Scheduled("Celtic", "Rangers", $"Qualification - Round 3 {Guid.NewGuid():N}",
            new DateTime(2026, 8, 5, 19, 0, 0, DateTimeKind.Utc)));

        Assert.Empty(_db.Fixtures);
        Assert.Empty(_log.Warnings);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static ScrapedMatchDto Scheduled(string home, string away, string round, DateTime kickoff) =>
        new() { Home = home, Away = away, Round = round, Time = Iso(kickoff), IsScheduled = true, IsLive = false };

    private static ScrapedMatchDto Live(string home, string away, int h, int a, string stage, DateTime kickoff) =>
        new() { Home = home, Away = away, ScoreHome = $"{h}", ScoreAway = $"{a}", Stage = stage, Time = Iso(kickoff), IsScheduled = false, IsLive = true };

    private static ScrapedMatchDto Ended(string home, string away, int h, int a, DateTime kickoff, string? stage = null) =>
        new() { Home = home, Away = away, ScoreHome = $"{h}", ScoreAway = $"{a}", Stage = stage, Time = Iso(kickoff), IsScheduled = false, IsLive = false };

    private Fixture FixtureOf(string competition, string home, string away) =>
        _db.Fixtures.Single(f => f.Id == MatchReading.FixtureId(competition, home, away));

    [Theory]
    [InlineData("Round 3", "md-3")]
    [InlineData("League phase - Round 3", "md-3")]
    [InlineData("Matchday 8", "md-8")]
    [InlineData("3", "md-3")]
    [InlineData("Knockout phase - Play Offs", "po")]
    [InlineData("1/8-finals", "r16")]
    [InlineData("Round of 16", "r16")]
    [InlineData("Quarter-finals", "qf")]
    [InlineData("1/2", "sf")]
    [InlineData("Final", "final")]
    public void ChampionsLeagueRounds(string text, string key) =>
        Assert.Equal(key, CompetitionRounds.ChampionsLeague(text, new DateTime(2026, 10, 21))?.Key);

    [Theory]
    [InlineData("Qualification - Round 3", 10)]
    [InlineData("Play Offs", 8)] // August: the qualifying play-off, not the knockout one
    [InlineData("Round 12", 10)]
    public void QualifyingAndUnreadableRoundsAreNotPredicted(string text, int month) =>
        Assert.Null(CompetitionRounds.ChampionsLeague(text, new DateTime(2026, month, 20)));

    [Fact]
    public async Task LaLiga_WeeksAndIdsAreAsBefore()
    {
        await Sync("La Liga", Scheduled("Real Madrid", "Barcelona", "Round 8", new DateTime(2026, 10, 25, 15, 0, 0, DateTimeKind.Utc)));

        var week = _db.MatchWeeks.Single();
        Assert.Equal(("la-liga-week-8", "La Liga — Week 8", "la-liga"), (week.Id, week.Name, week.CompetitionId));
        Assert.Equal("fs-real-madrid-vs-barcelona", _db.Fixtures.Single().Id);
    }

    [Fact]
    public async Task LeaguePhase_EachMatchdayIsAWeek_QualifyingIsSkipped()
    {
        await Sync(Ucl,
            Scheduled("Bayern Munich", "PSG", "League phase - Round 3", new DateTime(2026, 10, 21, 19, 0, 0, DateTimeKind.Utc)),
            Scheduled("Celtic", "Rangers", "Qualification - Round 3", new DateTime(2026, 10, 21, 19, 0, 0, DateTimeKind.Utc)));

        var week = _db.MatchWeeks.Single();
        Assert.Equal(("champions-league-md-3", "Champions League — Matchday 3", Competitions.ChampionsLeague),
            (week.Id, week.Name, week.CompetitionId));
        Assert.Equal("fs-champions-league-bayern-munich-vs-psg", _db.Fixtures.Single().Id);
    }

    [Fact]
    public async Task KnockoutLegs_AreSeparateWeeks_WhateverOrderTheSourceListsThem()
    {
        await Sync(Ucl,
            Scheduled("Inter", "Real Madrid", "1/8-finals", new DateTime(2027, 3, 10, 20, 0, 0, DateTimeKind.Utc)),
            Scheduled("Real Madrid", "Inter", "1/8-finals", new DateTime(2027, 2, 17, 20, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("champions-league-r16-leg1", FixtureOf(Ucl, "Real Madrid", "Inter").WeekId);
        Assert.Equal("champions-league-r16-leg2", FixtureOf(Ucl, "Inter", "Real Madrid").WeekId);
        Assert.Equal("Champions League — Round of 16 — 2nd leg", _db.MatchWeeks.Find("champions-league-r16-leg2")!.Name);
    }

    // A 2nd leg already filed (the week resolution is covered above); kickoff 2½ hours ago.
    private DateTime SecondLegUnderWay()
    {
        var kickoff = DateTime.UtcNow.AddMinutes(-150);
        _db.MatchWeeks.Add(new MatchWeek { Id = "champions-league-r16-leg2", Name = "x", Competition = Ucl, CompetitionId = Competitions.ChampionsLeague });
        _db.Fixtures.Add(new Fixture
        {
            Id = MatchReading.FixtureId(Ucl, "Inter", "Real Madrid"), WeekId = "champions-league-r16-leg2",
            HomeTeam = "Inter", AwayTeam = "Real Madrid", Kickoff = kickoff, Status = FixtureStatus.Live,
        });
        _db.SaveChanges();
        return kickoff;
    }

    [Fact]
    public async Task ExtraTime_PredictionsAreMarkedOnTheScoreAfter90Minutes()
    {
        var kickoff = SecondLegUnderWay();
        _db.Predictions.Add(new Prediction
        {
            UserId = Guid.NewGuid(), FixtureId = MatchReading.FixtureId(Ucl, "Inter", "Real Madrid"),
            WeekId = "champions-league-r16-leg2", Outcome = OutcomeType.Draw,
        });
        _db.SaveChanges();

        await Sync(Ucl, Live("Inter", "Real Madrid", 0, 1, "88'", kickoff));
        await Sync(Ucl, Live("Inter", "Real Madrid", 1, 1, "90+4'", kickoff)); // equaliser in stoppage time
        await Sync(Ucl, Live("Inter", "Real Madrid", 2, 1, "105'", kickoff));  // extra time
        await Sync(Ucl, Ended("Inter", "Real Madrid", 2, 1, kickoff, "AET"));

        var f = FixtureOf(Ucl, "Inter", "Real Madrid");
        Assert.Equal((1, 1), (f.FinalScoreHome, f.FinalScoreAway));
        Assert.Equal((2, 1), (f.AfterExtraTimeHome, f.AfterExtraTimeAway));
        Assert.True(f.WentToExtraTime);
        Assert.Equal(5, _db.Predictions.Single().PointsEarned); // the draw after 90 minutes
    }

    [Fact]
    public async Task SecondLegDecidedIn90Minutes_KeepsItsFinalScore()
    {
        var kickoff = SecondLegUnderWay();

        await Sync(Ucl, Live("Inter", "Real Madrid", 1, 0, "80'", kickoff));
        await Sync(Ucl, Ended("Inter", "Real Madrid", 2, 0, kickoff));

        var f = FixtureOf(Ucl, "Inter", "Real Madrid");
        Assert.Equal((2, 0), (f.FinalScoreHome, f.FinalScoreAway));
        Assert.False(f.WentToExtraTime);
    }

    [Fact]
    public async Task SecondLegNeverSeenLive_IsLeftForAnAdmin()
    {
        var kickoff = SecondLegUnderWay();

        await Sync(Ucl, Ended("Inter", "Real Madrid", 2, 1, kickoff));

        var f = FixtureOf(Ucl, "Inter", "Real Madrid");
        Assert.Equal(FixtureStatus.Ended, f.Status);
        Assert.Null(f.FinalScoreHome);
    }

    [Fact]
    public async Task LaLiga_FinalScoreIsTheScrapedOne_EvenWithoutBeingSeenLive()
    {
        var kickoff = DateTime.UtcNow.AddMinutes(-150);
        await Sync("La Liga", Scheduled("Sevilla", "Betis", "Round 9", kickoff));
        await Sync("La Liga", Ended("Sevilla", "Betis", 2, 1, kickoff));

        var f = FixtureOf("La Liga", "Sevilla", "Betis");
        Assert.Equal((2, 1), (f.FinalScoreHome, f.FinalScoreAway));
        Assert.Null(f.RegularTimeScoreHome);
    }
}
