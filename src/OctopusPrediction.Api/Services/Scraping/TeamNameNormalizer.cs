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
