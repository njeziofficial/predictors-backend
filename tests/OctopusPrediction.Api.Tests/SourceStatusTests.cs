using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Tests;

// How each source's statuses read, above all for a match called off: Rayo Vallecano v Athletic
// Club (10 October 2026) was abandoned at 0-0, and BBC Sport and Flashscore both read it as a
// finished 0-0, which would have marked every prediction on that score. The samples are the
// shapes each site answered with, cut down.
public class SourceStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Postp.", FixtureStatus.Postponed)]
    [InlineData("Postponed", FixtureStatus.Postponed)]
    [InlineData("Abn", FixtureStatus.Postponed)]
    [InlineData("Aband.", FixtureStatus.Postponed)]
    [InlineData("Abandoned", FixtureStatus.Postponed)]
    [InlineData("Suspended", FixtureStatus.Postponed)]
    [InlineData("Int.", FixtureStatus.Postponed)]
    [InlineData("Canc.", FixtureStatus.Cancelled)]
    [InlineData("Canceled", FixtureStatus.Cancelled)]
    public void Called_off_words_win_over_a_finished_looking_row(string stage, FixtureStatus expected)
    {
        // As Flashscore sends it: neither scheduled nor live (which reads as finished), with the score at the time.
        var row = new ScrapedMatchDto { Home = "Rayo", Away = "Athletic", ScoreHome = "0", ScoreAway = "0", Stage = stage, IsScheduled = false, IsLive = false };
        Assert.Equal(expected, MatchReading.MapStatus(row));
        // And as a text-only source sends it.
        Assert.Equal(expected, MatchReading.MapStatus(new ScrapedMatchDto { Home = "Rayo", Away = "Athletic", Stage = stage }));
    }

    [Theory]
    [InlineData("FT", FixtureStatus.Ended)]
    [InlineData("AET", FixtureStatus.Ended)]
    [InlineData("HT", FixtureStatus.HalfTime)]
    [InlineData("67'", FixtureStatus.Live)]
    [InlineData("NS", FixtureStatus.PreMatch)]
    [InlineData(null, FixtureStatus.PreMatch)]
    public void Ordinary_stage_text_reads_as_before(string? stage, FixtureStatus expected) =>
        Assert.Equal(expected, MatchReading.MapStatus(new ScrapedMatchDto { Home = "A", Away = "B", Stage = stage }));

    [Fact]
    public void Flashscore_passes_on_only_called_off_notes()
    {
        ScrapedMatchDto Row(string? note, bool live = false) => new() { Home = "A", Away = "B", Stage = note, IsScheduled = false, IsLive = live };
        var rows = FlashscoreSource.KeepCalledOffNotes([Row("Abn"), Row("AET"), Row(null), Row("67'", live: true)]);

        Assert.Equal(FixtureStatus.Postponed, MatchReading.MapStatus(rows[0]));
        // A finished knockout match's "AET" is dropped, so it reads exactly as it did before.
        Assert.Null(rows[1].Stage);
        Assert.Equal(FixtureStatus.Ended, MatchReading.MapStatus(rows[1]));
        Assert.Equal(FixtureStatus.Ended, MatchReading.MapStatus(rows[2]));
        // A live row keeps its minute.
        Assert.Equal("67'", rows[3].Stage);
    }

    [Fact]
    public void Bbc_statuses()
    {
        BbcSportSource.BbcEvent Event(string status, string? period, string? home = "0", string? away = "0") =>
            new("c6x2wlljr18zt", "Rayo Vallecano", "Athletic Club", home, away, status, period, "2026-10-10T12:00:00Z");

        var suspended = BbcSportSource.ToDto(Event("Suspended", "Suspended"));
        Assert.Equal(FixtureStatus.Postponed, MatchReading.MapStatus(suspended));

        var finished = BbcSportSource.ToDto(Event("PostEvent", "FT", "3", "3"));
        Assert.Equal(FixtureStatus.Ended, MatchReading.MapStatus(finished));
        Assert.Equal(("3", "3"), (finished.ScoreHome, finished.ScoreAway));

        var live = BbcSportSource.ToDto(Event("MidEvent", "67'", "1", "0"));
        Assert.Equal(FixtureStatus.Live, MatchReading.MapStatus(live));
        Assert.Equal(67, MatchReading.ParseMinute(live.Stage));
        Assert.Equal(FixtureStatus.HalfTime, MatchReading.MapStatus(BbcSportSource.ToDto(Event("MidEvent", "HT"))));

        var upcoming = BbcSportSource.ToDto(Event("PreEvent", "Scheduled", null, null));
        Assert.Equal(FixtureStatus.PreMatch, MatchReading.MapStatus(upcoming));
        // The kickoff comes through as a full UTC time, not just "13:00".
        Assert.Equal(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), MatchReading.ParseKickoff(upcoming.Time)!.Value.Utc);
    }

    private const string LivescoreJson = """
        {"Stages":[{"Snm":"LaLiga","Events":[
          {"Eid":"1810715","Esd":20261010120000,"Eps":"Aband.","Tr1":"0","Tr2":"0","T1":[{"Nm":"Rayo Vallecano"}],"T2":[{"Nm":"Athletic Club"}]},
          {"Eid":"1810700","Esd":20261012190000,"Eps":"FT","Tr1":"3","Tr2":"3","T1":[{"Nm":"Levante"}],"T2":[{"Nm":"Sevilla"}]},
          {"Eid":"1810726","Esd":20261017163000,"Eps":"NS","T1":[{"Nm":"Real Betis"}],"T2":[{"Nm":"Barcelona"}]},
          {"Eid":"1810800","Esd":20261014193000,"Eps":"34'","Tr1":"1","Tr2":"0","T1":[{"Nm":"Elche"}],"T2":[{"Nm":"Getafe"}]},
          {"Eid":"1810638","Esd":20260815173000,"Eps":"FT","Tr1":"3","Tr2":"0","T1":[{"Nm":"Deportivo Alaves"}],"T2":[{"Nm":"Getafe"}]}
        ]}]}
        """;

    [Fact]
    public void Livescore_reads_its_api()
    {
        var matches = LivescoreSource.Parse(LivescoreJson, Now).ToDictionary(m => m.Home);

        // August is outside the two-week window.
        Assert.Equal(4, matches.Count);
        Assert.Equal(FixtureStatus.Postponed, MatchReading.MapStatus(matches["Rayo Vallecano"]));
        Assert.Equal(FixtureStatus.Ended, MatchReading.MapStatus(matches["Levante"]));
        Assert.Equal(("3", "3"), (matches["Levante"].ScoreHome, matches["Levante"].ScoreAway));
        Assert.Equal(FixtureStatus.Live, MatchReading.MapStatus(matches["Elche"]));
        var upcoming = matches["Real Betis"];
        Assert.Equal(FixtureStatus.PreMatch, MatchReading.MapStatus(upcoming));
        Assert.Null(upcoming.ScoreHome);
        Assert.Equal(new DateTime(2026, 10, 17, 16, 30, 0, DateTimeKind.Utc), MatchReading.ParseKickoff(upcoming.Time)!.Value.Utc);
        Assert.Equal("ls-1810726", upcoming.Id);
    }

    [Theory]
    [InlineData("https://www.livescore.com/en/football/spain/laliga", "https://prod-public-api.livescore.com/v1/api/app/stage/soccer/spain/laliga/0?MD=1")]
    [InlineData("https://www.livescore.com/en/football/europe/champions-league/fixtures/", "https://prod-public-api.livescore.com/v1/api/app/stage/soccer/europe/champions-league/0?MD=1")]
    [InlineData("https://prod-public-api.livescore.com/v1/api/app/stage/soccer/spain/laliga/0?MD=1", "https://prod-public-api.livescore.com/v1/api/app/stage/soccer/spain/laliga/0?MD=1")]
    public void Livescore_api_address_comes_from_the_configured_page(string configured, string expected) =>
        Assert.Equal(expected, LivescoreSource.ApiUrl(configured));

    private const string EspnJson = """
        {"events":[
          {"id":"748001","date":"2026-10-10T12:00Z","status":{"displayClock":"90'","type":{"name":"STATUS_ABANDONED","state":"post","completed":false}},
           "competitions":[{"competitors":[{"homeAway":"home","score":"0","team":{"displayName":"Rayo Vallecano"}},{"homeAway":"away","score":"0","team":{"displayName":"Athletic Club"}}]}]},
          {"id":"748002","date":"2026-10-12T19:00Z","status":{"displayClock":"90'+5'","type":{"name":"STATUS_FULL_TIME","state":"post","completed":true}},
           "competitions":[{"competitors":[{"homeAway":"home","score":"3","team":{"displayName":"Levante"}},{"homeAway":"away","score":"3","team":{"displayName":"Sevilla"}}]}]},
          {"id":"748003","date":"2026-10-14T19:00Z","status":{"displayClock":"62'","type":{"name":"STATUS_SECOND_HALF","state":"in","completed":false}},
           "competitions":[{"competitors":[{"homeAway":"away","score":"1","team":{"displayName":"Getafe"}},{"homeAway":"home","score":"2","team":{"displayName":"Elche"}}]}]},
          {"id":"748004","date":"2026-10-14T19:00Z","status":{"displayClock":"45'+2'","type":{"name":"STATUS_HALFTIME","state":"in","completed":false}},
           "competitions":[{"competitors":[{"homeAway":"home","score":"0","team":{"displayName":"Osasuna"}},{"homeAway":"away","score":"0","team":{"displayName":"Girona"}}]}]},
          {"id":"748005","date":"2026-10-17T16:30Z","status":{"displayClock":"0'","type":{"name":"STATUS_SCHEDULED","state":"pre","completed":false}},
           "competitions":[{"competitors":[{"homeAway":"home","score":"0","team":{"displayName":"Real Betis"}},{"homeAway":"away","score":"0","team":{"displayName":"Barcelona"}}]}]},
          {"id":"748006","date":"2026-10-11T19:00Z","status":{"type":{"name":"STATUS_POSTPONED","state":"pre","completed":false}},
           "competitions":[{"competitors":[{"homeAway":"home","score":"0","team":{"displayName":"Valencia"}},{"homeAway":"away","score":"0","team":{"displayName":"Mallorca"}}]}]}
        ]}
        """;

    [Fact]
    public void Espn_reads_its_scoreboard()
    {
        var matches = EspnSource.Parse(EspnJson).ToDictionary(m => m.Home);

        Assert.Equal(FixtureStatus.Postponed, MatchReading.MapStatus(matches["Rayo Vallecano"]));
        Assert.Equal(FixtureStatus.Postponed, MatchReading.MapStatus(matches["Valencia"]));
        Assert.Equal(FixtureStatus.Ended, MatchReading.MapStatus(matches["Levante"]));

        // Home and away come from homeAway, not their order in the list.
        var live = matches["Elche"];
        Assert.Equal(("Elche", "Getafe", "2", "1"), (live.Home, live.Away, live.ScoreHome, live.ScoreAway));
        Assert.Equal(FixtureStatus.Live, MatchReading.MapStatus(live));
        Assert.Equal(62, MatchReading.ParseMinute(live.Stage));
        Assert.Equal(FixtureStatus.HalfTime, MatchReading.MapStatus(matches["Osasuna"]));

        var upcoming = matches["Real Betis"];
        Assert.Equal(FixtureStatus.PreMatch, MatchReading.MapStatus(upcoming));
        Assert.Null(upcoming.ScoreHome);
        Assert.Equal(new DateTime(2026, 10, 17, 16, 30, 0, DateTimeKind.Utc), MatchReading.ParseKickoff(upcoming.Time)!.Value.Utc);
    }

    [Theory]
    [InlineData("https://www.espn.com/soccer/schedule/_/league/esp.1", "esp.1")]
    [InlineData("https://www.espn.com/soccer/schedule/_/league/uefa.champions", "uefa.champions")]
    [InlineData("eng.2", "eng.2")]
    public void Espn_league_code_comes_from_the_configured_page(string configured, string expected) =>
        Assert.Equal(expected, EspnSource.LeagueCode(configured));

    [Fact]
    public void A_retired_source_drops_out_of_a_saved_plan()
    {
        IReadOnlyList<string> registered = ["Flashscore", "FotMob", "ESPN"];

        var (name, order) = SourcePlan.Usable("Fox Sports", "fotmob,Fox Sports,ESPN", registered);
        Assert.Equal("Flashscore", name);
        Assert.Equal(["FotMob", "ESPN"], order);

        Assert.Equal("FotMob", SourcePlan.Usable("FotMob", "", registered).SourceName);
    }
}
