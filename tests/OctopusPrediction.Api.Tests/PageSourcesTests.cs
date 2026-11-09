using System.Net;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Tests;

// AS, Marca, OneFootball, Mundo Deportivo and The Guardian, on data shaped like their real pages.
// What matters most is that a result only reads as finished on a clear signal: that's what scores
// predictions.
public class PageSourcesTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static FixtureStatus Status(ScrapedMatchDto m) => MatchReading.MapStatus(m);

    // ---- Madrid time ----

    [Theory]
    [InlineData("2026-08-15 19:30", "2026-08-15 17:30")] // summer: UTC+2
    [InlineData("2026-11-20 21:00", "2026-11-20 20:00")] // winter: UTC+1
    [InlineData("2027-03-28 01:59", "2027-03-28 00:59")] // just before the clocks go forward
    [InlineData("2027-03-28 03:00", "2027-03-28 01:00")] // just after
    [InlineData("2026-10-25 03:00", "2026-10-25 02:00")] // the clocks have gone back
    public void MadridTimeConvertsToUtc(string local, string utc) =>
        Assert.Equal(DateTime.Parse(utc), PageReading.MadridToUtc(DateTime.Parse(local)));

    // ---- AS ----

    private static string AsPage(params string[] events) =>
        """<a href="#" data-day="08-11-2026" data-title="Jornada 12" data-date="Domingo"> J12 </a>"""
        + "<script>const sportEvents = [" + string.Join(",", events) + "]; const teams = {"
        + """ "a":{"id":"a","name":"Atlético"},"b":{"id":"b","name":"Barcelona"}"""
        + "}; const tvs = {};</script>";

    private static string AsEvent(long date, string state, string? period, string minute, string goalsHome, string goalsAway) =>
        $$$"""{"day":"08-11-2026","date":{{{date}}},"groupNumber":null,"state":{"id":"{{{state}}}","name":"x"},"moment":{"period":{{{(period is null ? "null" : $"\"{period}\"")}}},"minute":{{{minute}}}},"local":{"team":"a","score":{"goals":{{{goalsHome}}},"penalties":null}},"visitor":{"team":"b","score":{"goals":{{{goalsAway}}},"penalties":null}},"tvs":[]}""";

    // 2026-11-08 20:00 UTC.
    private const long Sunday = 1794168000;

    [Fact]
    public void AsReadsAFinishedMatchWithItsRoundAndUtcKickoff()
    {
        var m = Assert.Single(AsSource.Parse(AsPage(AsEvent(Sunday, "SPORT_EVENT_STATE_FINISHED", "Finalizado", "null", "1", "2")), Now));

        Assert.Equal("Atlético", m.Home);
        Assert.Equal("Barcelona", m.Away);
        Assert.Equal(("1", "2"), (m.ScoreHome, m.ScoreAway));
        Assert.Equal("Jornada 12", m.Round);
        Assert.Equal("2026-11-08T20:00:00Z", m.Time);
        Assert.Equal(FixtureStatus.Ended, Status(m));
    }

    [Fact]
    public void AsKeepsOnlyMatchesNearNow()
    {
        var farAway = Sunday + 40 * 86400;
        var matches = AsSource.Parse(AsPage(
            AsEvent(Sunday, "SPORT_EVENT_STATE_FINISHED", "Finalizado", "null", "1", "2"),
            AsEvent(farAway, "SPORT_EVENT_STATE_PRE_SPORT_EVENT", "TEXT_", "null", "null", "null")), Now);
        Assert.Single(matches);
    }

    [Theory]
    [InlineData("SPORT_EVENT_STATE_PRE_SPORT_EVENT", "TEXT_", null, FixtureStatus.PreMatch)]
    [InlineData("SPORT_EVENT_STATE_LIVE", "1ª parte", "34", FixtureStatus.Live)]
    [InlineData("SPORT_EVENT_STATE_LIVE", "SPORT_EVENT_PERIOD_HALF_TIME", "45", FixtureStatus.HalfTime)]
    [InlineData("SPORT_EVENT_STATE_LIVE", "SPORT_EVENT_PERIOD_FINISHED", "90", FixtureStatus.Live)] // only the state ends a match
    [InlineData("SPORT_EVENT_STATE_ABANDONED", "Finalizado", null, FixtureStatus.Postponed)]
    [InlineData("SPORT_EVENT_STATE_SUSPENDED", null, null, FixtureStatus.Postponed)]
    [InlineData("SPORT_EVENT_STATE_SOMETHING_NEW", null, null, FixtureStatus.PreMatch)]
    public void AsReadsItsStates(string state, string? period, string? minute, FixtureStatus expected)
    {
        var m = new ScrapedMatchDto();
        AsSource.Apply(m, state, period, minute, "1", "0");
        Assert.Equal(expected, Status(m));
    }

    [Fact]
    public void AsPassesOnTheMinute()
    {
        var m = new ScrapedMatchDto();
        AsSource.Apply(m, "SPORT_EVENT_STATE_LIVE", "1ª parte", "34", "1", "0");
        Assert.Equal(34, MatchReading.ParseMinute(m.Stage));
    }

    [Fact]
    public void AsCopesWithAPageWithoutItsData() => Assert.Empty(AsSource.Parse("<html></html>", Now));

    // ---- Marca ----

    private static MarcaSource.MarcaRow MarcaRow(string? score, string? status = null, string kickoff = "2026-11-08T21:00:00Z") =>
        new("Jornada 12", "Atlético", "Barcelona", score, status, kickoff);

    [Fact]
    public void MarcaTreatsItsKickoffsAsMadridTime()
    {
        var m = MarcaSource.ToDto(MarcaRow("1-2"), Now)!;
        Assert.Equal("2026-11-08T20:00:00Z", m.Time);
        Assert.Equal("Jornada 12", m.Round);
    }

    [Fact]
    public void MarcaCallsAResultFinalOnlyWhenTheMatchMustBeOver()
    {
        var kickoff = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

        var midMatch = MarcaSource.ToDto(MarcaRow("1-0"), kickoff.AddMinutes(150))!;
        Assert.Equal(FixtureStatus.Live, Status(midMatch));
        Assert.Equal("1", midMatch.ScoreHome);

        var over = MarcaSource.ToDto(MarcaRow("1-2"), kickoff.AddHours(3))!;
        Assert.Equal(FixtureStatus.Ended, Status(over));
    }

    [Fact]
    public void MarcaReadsAMatchToCome() =>
        Assert.Equal(FixtureStatus.PreMatch, Status(MarcaSource.ToDto(MarcaRow(null, kickoff: "2026-11-20T21:00:00Z"), Now)!));

    [Fact]
    public void MarcaReadsACalledOffMatch() =>
        Assert.Equal(FixtureStatus.Postponed, Status(MarcaSource.ToDto(MarcaRow(null, "Suspendido"), Now)!));

    [Fact]
    public void MarcaLeavesOutMatchesItCantPlace()
    {
        Assert.Null(MarcaSource.ToDto(MarcaRow("1-2", kickoff: "nonsense"), Now));
        Assert.Null(MarcaSource.ToDto(MarcaRow(null, kickoff: "2027-05-30T17:00:00Z"), Now));
    }

    // ---- Mundo Deportivo ----

    private static MundoDeportivoSource.MundoPanel Panel(string home, string away, string? info, bool future = false) =>
        new("Atlético de Madrid", "Barcelona", home, away, info, future, "2026-11-08T20:00:00Z");

    [Fact]
    public void MundoDeportivoReadsAFinishedMatch()
    {
        var m = MundoDeportivoSource.ToDto(Panel("1", "2", "Fin"));
        Assert.Equal(FixtureStatus.Ended, Status(m));
        Assert.Equal(("1", "2"), (m.ScoreHome, m.ScoreAway));
        Assert.Equal("2026-11-08T20:00:00Z", m.Time);
    }

    [Fact]
    public void MundoDeportivoReadsAMatchToCome() =>
        Assert.Equal(FixtureStatus.PreMatch, Status(MundoDeportivoSource.ToDto(Panel("", "", "21:00 CET", future: true))));

    [Theory]
    [InlineData("67'", FixtureStatus.Live, 67)]
    [InlineData("Descanso", FixtureStatus.HalfTime, null)]
    [InlineData("En juego", FixtureStatus.Live, null)] // words it hasn't been seen using: never finished
    public void MundoDeportivoReadsAScoreWithoutFinAsInPlay(string info, FixtureStatus expected, int? minute)
    {
        var m = MundoDeportivoSource.ToDto(Panel("1", "0", info));
        Assert.Equal(expected, Status(m));
        Assert.Equal(minute, MatchReading.ParseMinute(m.Stage));
    }

    [Fact]
    public void MundoDeportivoReadsACalledOffMatch() =>
        Assert.Equal(FixtureStatus.Postponed, Status(MundoDeportivoSource.ToDto(Panel("", "", "Aplazado"))));

    // ---- OneFootball ----

    private static string OneFootballCard(string state, string timePeriod, string minute, string scoreHome, string scoreAway, string kickoff = "2026-11-08T20:00:00Z") =>
        $$$"""{"kickoff":"{{{kickoff}}}","timePeriod":"{{{timePeriod}}}","period":11,"homeTeam":{"name":"Atlético de Madrid","score":"{{{scoreHome}}}"},"awayTeam":{"name":"Barcelona","score":"{{{scoreAway}}}"},"matchId":"2700295","trackingEvents":[{"type":3,"typedServerParameter":{"match_state":{"value":"{{{state}}}","type":2},"match_minute":{"value":"{{{minute}}}","type":4""" + "}}}]}";

    private static string OneFootballPage(string card) =>
        """<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"__namespaces":{"a":{"MATCHES":"Matches"}},"containers":[{"type":{"grid":{"items":[{"components":[{"contentType":{"matchCardsListsAppender":{"lists":[{"matchCards":["""
        + card
        + """],"sectionHeader":{"title":"","subtitle":"Matchday 12"}}]}}}]}]}}}]}}}</script><script>next</script>""";

    [Fact]
    public void OneFootballReadsAFinishedMatchWithItsRound()
    {
        var m = Assert.Single(OneFootballSource.Parse(OneFootballPage(OneFootballCard("FullTime", "Full time", "90", "1", "2"))));

        Assert.Equal("Atlético de Madrid", m.Home);
        Assert.Equal(("1", "2"), (m.ScoreHome, m.ScoreAway));
        Assert.Equal("Matchday 12", m.Round);
        Assert.Equal("2026-11-08T20:00:00Z", m.Time);
        Assert.Equal(FixtureStatus.Ended, Status(m));
    }

    [Theory]
    [InlineData("PreMatch", "", "0", "", "", FixtureStatus.PreMatch)]
    [InlineData("Live", "2nd half", "67", "1", "0", FixtureStatus.Live)]
    [InlineData("Live", "Half time", "45", "1", "0", FixtureStatus.HalfTime)]
    [InlineData("SomethingNew", "", "0", "1", "0", FixtureStatus.Live)] // has a score but isn't full time: not finished
    [InlineData("Postponed", "Postponed", "0", "", "", FixtureStatus.Postponed)]
    public void OneFootballReadsItsStates(string state, string period, string minute, string home, string away, FixtureStatus expected)
    {
        var m = new ScrapedMatchDto();
        OneFootballSource.Apply(m, state, period, minute, home, away);
        Assert.Equal(expected, Status(m));
    }

    [Fact]
    public void OneFootballCopesWithAPageWithoutItsData() => Assert.Empty(OneFootballSource.Parse("<html></html>"));

    // ---- The Guardian ----

    private const string LaLigaTag = "football/laligafootball";

    private static string GuardianPage(params string[] matches)
    {
        var props = $$"""{"regions":[],"initialDays":[{"dateISOString":"2026-11-08T00:00:00.000Z","competitions":[{"tag":"{{LaLigaTag}}","matches":[{{string.Join(",", matches)}}]},{"tag":"football/premierleague","matches":[{{GuardianMatch("Result", "Arsenal", "Chelsea")}}]}]}]}""";
        return $"""<gu-island name="FootballMatchesPageWrapper" priority="feature" props="{WebUtility.HtmlEncode(props)}"></gu-island>""";
    }

    private static string GuardianMatch(string kind, string home = "Atlético", string away = "Barcelona", string extra = "") =>
        $$"""{"kind":"{{kind}}","homeTeam":{"name":"{{home}}","score":1,"id":"1"},"awayTeam":{"name":"{{away}}","score":2,"id":"2"},"dateTimeISOString":"2026-11-08T20:00:00.000Z","paId":"4561475"{{extra}}}""";

    [Fact]
    public void GuardianReadsAResultInItsOwnCompetitionOnly()
    {
        var m = Assert.Single(GuardianSource.Parse(GuardianPage(GuardianMatch("Result")), LaLigaTag));

        Assert.Equal("Atlético", m.Home);
        Assert.Equal(("1", "2"), (m.ScoreHome, m.ScoreAway));
        Assert.Equal(FixtureStatus.Ended, Status(m));
        Assert.NotNull(MatchReading.ParseKickoff(m.Time));
        Assert.Null(m.Round);
    }

    [Theory]
    [InlineData("Fixture", null, null, FixtureStatus.PreMatch)]
    [InlineData("Live", "2nd", null, FixtureStatus.Live)]
    [InlineData("Live", "HT", null, FixtureStatus.HalfTime)]
    [InlineData("Result", null, "Abandoned", FixtureStatus.Postponed)]
    public void GuardianReadsItsKinds(string kind, string? status, string? comment, FixtureStatus expected)
    {
        var m = new ScrapedMatchDto();
        Assert.True(GuardianSource.Apply(m, kind, status, comment, "1", "0"));
        Assert.Equal(expected, Status(m));
    }

    [Fact]
    public void GuardianLeavesOutAKindItDoesntKnow() =>
        Assert.Empty(GuardianSource.Parse(GuardianPage(GuardianMatch("Mystery")), LaLigaTag));

    [Fact]
    public void GuardianCopesWithAPageWithoutItsData() => Assert.Empty(GuardianSource.Parse("<html></html>", LaLigaTag));
}
