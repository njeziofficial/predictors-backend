using OctopusPrediction.Api.Services.LiveScores;

namespace OctopusPrediction.Api.Tests;

// Team names decide fixture ids, so a La Liga spelling that changed slug would orphan its
// fixture and the predictions on it. La Liga's slugs are pinned as they were before other
// competitions' clubs were added; new aliases must never move one.
public class TeamNameNormalizerTests
{
    // The slug inside a La Liga fixture id ("fs-<slug>-vs-x").
    private static string Slug(string team) => MatchReading.FixtureId("La Liga", team, "x")[3..^5];

    public static TheoryData<string, string> LaLigaSpellings => new()
    {
        { "Alaves", "alaves" }, { "Deportivo Alavés", "alaves" },
        { "Ath Bilbao", "athletic-bilbao" }, { "Athletic Club", "athletic-bilbao" },
        { "Athletic Bilbao", "athletic-bilbao" }, { "Athletic", "athletic-bilbao" },
        { "Atl. Madrid", "atletico-madrid" }, { "Atl Madrid", "atletico-madrid" },
        { "Atlético Madrid", "atletico-madrid" }, { "Atletico de Madrid", "atletico-madrid" },
        { "Barcelona", "barcelona" }, { "FC Barcelona", "barcelona" },
        { "Betis", "real-betis" }, { "Real Betis", "real-betis" },
        { "CA Osasuna", "osasuna" }, { "Osasuna", "osasuna" },
        { "Celta Vigo", "celta-vigo" }, { "Celta de Vigo", "celta-vigo" }, { "Celta", "celta-vigo" },
        { "Dep. A Coruna", "deportivo-la-coruna" }, { "Deportivo A Coruña", "deportivo-la-coruna" },
        { "Deportivo de A Coruña", "deportivo-la-coruna" }, { "Deportivo", "deportivo-la-coruna" },
        { "Elche", "elche" }, { "Elche CF", "elche" },
        { "Espanyol", "espanyol" }, { "RCD Espanyol", "espanyol" },
        { "Getafe", "getafe" }, { "Getafe CF", "getafe" },
        { "Girona", "girona" }, { "Girona FC", "girona" },
        { "Levante", "levante" }, { "Levante UD", "levante" },
        { "Malaga", "malaga" }, { "Málaga", "malaga" }, { "Málaga CF", "malaga" },
        { "Mallorca", "mallorca" }, { "RCD Mallorca", "mallorca" },
        { "Racing Santander", "racing-santander" }, { "Racing de Santander", "racing-santander" },
        { "Racing Sant.", "racing-santander" }, { "Racing", "racing-santander" },
        { "Rayo Vallecano", "rayo-vallecano" }, { "Rayo", "rayo-vallecano" },
        { "Real Madrid", "real-madrid" }, { "Real Sociedad", "real-sociedad" },
        { "Real Oviedo", "real-oviedo" }, { "Oviedo", "oviedo" },
        { "Sevilla", "sevilla" }, { "Sevilla FC", "sevilla" },
        { "Valencia", "valencia" }, { "Valencia CF", "valencia" },
        { "Villarreal", "villarreal" }, { "Villarreal CF", "villarreal" },
        { "UD Las Palmas", "las-palmas" }, { "Las Palmas", "las-palmas" },
        { "Leganés", "leganes" }, { "CD Leganés", "leganes" },
        { "Real Valladolid", "real-valladolid" }, { "Valladolid", "valladolid" },
    };

    [Theory]
    [MemberData(nameof(LaLigaSpellings))]
    public void LaLigaSlugsAreUnchanged(string team, string slug) => Assert.Equal(slug, Slug(team));

    [Theory]
    [InlineData("bayern-munich", "Bayern Munich", "Bayern München", "FC Bayern München", "Bayern")]
    [InlineData("psg", "Paris Saint-Germain", "Paris SG", "PSG", "Paris Saint Germain FC")]
    [InlineData("inter", "Inter", "Inter Milan", "Internazionale", "FC Internazionale Milano")]
    [InlineData("milan", "AC Milan", "Milan")]
    [InlineData("manchester-city", "Manchester City", "Man City")]
    [InlineData("borussia-dortmund", "Borussia Dortmund", "Dortmund", "BVB")]
    [InlineData("bayer-leverkusen", "Bayer Leverkusen", "Leverkusen", "Bayer 04 Leverkusen")]
    [InlineData("sporting-cp", "Sporting CP", "Sporting Lisbon", "Sporting")]
    [InlineData("club-brugge", "Club Brugge", "Club Brugge KV", "Club Bruges")]
    [InlineData("copenhagen", "FC Copenhagen", "FC København", "Copenhagen")]
    [InlineData("bodo-glimt", "Bodø/Glimt", "FK Bodø/Glimt", "Bodo/Glimt")]
    [InlineData("red-star-belgrade", "Crvena Zvezda", "Red Star Belgrade", "FK Crvena zvezda")]
    public void ChampionsLeagueSpellingsMeet(string slug, params string[] spellings) =>
        Assert.All(spellings, s => Assert.Equal(slug, Slug(s)));
}
