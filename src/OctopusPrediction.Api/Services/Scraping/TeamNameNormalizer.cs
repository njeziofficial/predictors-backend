using System.Globalization;
using System.Text;

namespace OctopusPrediction.Api.Services.Scraping;

/// <summary>
/// Sources spell the same club differently ("Ath Bilbao" vs "Athletic Club" vs
/// "Athletic Bilbao"). Fixture identity is built from this canonical slug so that
/// whichever source syncs a match, it lands on the same Fixture row instead of creating
/// a duplicate.
/// </summary>
internal static class TeamNameNormalizer
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["atl madrid"] = "atletico-madrid",
        ["atletico madrid"] = "atletico-madrid",
        ["atletico de madrid"] = "atletico-madrid",
        ["ath bilbao"] = "athletic-bilbao",
        ["athletic club"] = "athletic-bilbao",
        ["athletic bilbao"] = "athletic-bilbao",
        ["athletic"] = "athletic-bilbao",
        ["betis"] = "real-betis",
        ["real betis"] = "real-betis",
        ["dep a coruna"] = "deportivo-la-coruna",
        ["deportivo a coruna"] = "deportivo-la-coruna",
        ["deportivo de a coruna"] = "deportivo-la-coruna",
        ["deportivo"] = "deportivo-la-coruna",
        ["alaves"] = "alaves",
        ["deportivo alaves"] = "alaves",
        ["rayo vallecano"] = "rayo-vallecano",
        ["rayo"] = "rayo-vallecano",
        ["racing santander"] = "racing-santander",
        ["racing de santander"] = "racing-santander",
        ["racing sant"] = "racing-santander", // Fox Sports truncates to "Racing Sant."
        ["racing"] = "racing-santander",
        ["espanyol"] = "espanyol",
        ["rcd espanyol"] = "espanyol",
        ["malaga"] = "malaga",
        ["celta vigo"] = "celta-vigo",
        ["celta de vigo"] = "celta-vigo",
        ["celta"] = "celta-vigo",

        // Champions League clubs (and, later, the Premier League's). Keys are spellings as they
        // read after cleaning: accents and punctuation gone, "FC"/"AC"/... stripped from the ends.
        // None may be a La Liga spelling: TeamNameNormalizerTests pins La Liga's slugs.
        ["bayern"] = "bayern-munich",
        ["bayern munich"] = "bayern-munich",
        ["bayern munchen"] = "bayern-munich",
        ["paris saint germain"] = "psg",
        ["paris sg"] = "psg",
        ["paris"] = "psg",
        ["inter milan"] = "inter",
        ["inter milano"] = "inter",
        ["internazionale"] = "inter",
        ["internazionale milano"] = "inter",
        ["man city"] = "manchester-city",
        ["manchester city"] = "manchester-city",
        ["man utd"] = "manchester-united",
        ["man united"] = "manchester-united",
        ["manchester utd"] = "manchester-united",
        ["manchester united"] = "manchester-united",
        ["newcastle"] = "newcastle",
        ["newcastle utd"] = "newcastle",
        ["newcastle united"] = "newcastle",
        ["tottenham"] = "tottenham",
        ["tottenham hotspur"] = "tottenham",
        ["spurs"] = "tottenham",
        ["psv eindhoven"] = "psv",
        ["dortmund"] = "borussia-dortmund",
        ["bvb"] = "borussia-dortmund",
        ["leverkusen"] = "bayer-leverkusen",
        ["bayer 04 leverkusen"] = "bayer-leverkusen",
        ["leipzig"] = "rb-leipzig",
        ["rasenballsport leipzig"] = "rb-leipzig",
        ["frankfurt"] = "eintracht-frankfurt",
        ["e frankfurt"] = "eintracht-frankfurt",
        ["vfb stuttgart"] = "stuttgart",
        ["ssc napoli"] = "napoli",
        ["atalanta bc"] = "atalanta",
        ["sl benfica"] = "benfica",
        ["sporting cp"] = "sporting-cp",
        ["sporting lisbon"] = "sporting-cp",
        ["sporting clube de portugal"] = "sporting-cp",
        ["sporting"] = "sporting-cp",
        ["club brugge kv"] = "club-brugge",
        ["club bruges"] = "club-brugge",
        ["brugge"] = "club-brugge",
        ["as monaco"] = "monaco",
        ["olympique marseille"] = "marseille",
        ["olympique de marseille"] = "marseille",
        ["om"] = "marseille",
        ["olympique lyonnais"] = "lyon",
        ["ol"] = "lyon",
        ["losc lille"] = "lille",
        ["losc"] = "lille",
        ["afc ajax"] = "ajax",
        ["ajax amsterdam"] = "ajax",
        ["shakhtar"] = "shakhtar-donetsk",
        ["shakhtar d"] = "shakhtar-donetsk",
        ["gnk dinamo zagreb"] = "dinamo-zagreb",
        ["d zagreb"] = "dinamo-zagreb",
        ["red bull salzburg"] = "salzburg",
        ["rb salzburg"] = "salzburg",
        ["bsc young boys"] = "young-boys",
        ["red star belgrade"] = "red-star-belgrade",
        ["red star"] = "red-star-belgrade",
        ["crvena zvezda"] = "red-star-belgrade",
        ["fk crvena zvezda"] = "red-star-belgrade",
        ["slavia praha"] = "slavia-prague",
        ["sk slavia praha"] = "slavia-prague",
        ["sk slavia prague"] = "slavia-prague",
        ["olympiakos"] = "olympiacos",
        ["olympiacos piraeus"] = "olympiacos",
        ["qarabag fk"] = "qarabag",
        ["kairat almaty"] = "kairat",
        ["copenhagen"] = "copenhagen",
        ["københavn"] = "copenhagen",
        ["bodøglimt"] = "bodo-glimt",
        ["fk bodøglimt"] = "bodo-glimt",
        ["bodoglimt"] = "bodo-glimt",
        ["bodo glimt"] = "bodo-glimt",
        ["bodø glimt"] = "bodo-glimt", // BBC writes "Bodø / Glimt"
        ["fk bodø glimt"] = "bodo-glimt",
        ["sabah fk"] = "sabah",
        ["sabah baku"] = "sabah",
        ["as roma"] = "roma",
        ["viking fk"] = "viking",
        ["feyenoord rotterdam"] = "feyenoord",
        ["lask linz"] = "lask",
        ["union saint gilloise"] = "union-saint-gilloise",
        ["royale union sg"] = "union-saint-gilloise",
        ["union sg"] = "union-saint-gilloise",
    };

    // Spanish/Catalan clubs' legal-form tokens (CF, CD, UD, etc.) get included by some
    // sources and dropped by others ("Getafe CF" vs "Getafe", "Elche CF" vs "Elche") —
    // strip them from either end before alias lookup so both variants land on one slug.
    private static readonly HashSet<string> ClubSuffixes =
        ["cf", "cd", "ud", "sd", "ad", "ca", "rc", "rcd", "fc", "ac"];

    public static string Slug(string teamName)
    {
        var cleaned = StripClubSuffixes(StripAccentsAndPunctuation(teamName));
        return Aliases.TryGetValue(cleaned, out var canonical) ? canonical : cleaned.Replace(' ', '-');
    }

    private static string StripClubSuffixes(string name)
    {
        var words = new List<string>(name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        while (words.Count > 1 && ClubSuffixes.Contains(words[0])) words.RemoveAt(0);
        while (words.Count > 1 && ClubSuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    private static string StripAccentsAndPunctuation(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '.' or '-') sb.Append(' ');
        }
        var collapsed = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed;
    }
}
