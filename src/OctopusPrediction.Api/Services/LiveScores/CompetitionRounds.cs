using System.Text.RegularExpressions;

namespace OctopusPrediction.Api.Services.LiveScores;

// The prediction week a scraped round belongs to, for competitions other than La Liga (which
// keeps MatchReading.WeekId). Sources name rounds differently ("Round 3", "Matchday 3", "3";
// "1/8-finals", "Round of 16"), so each is mapped onto one key.
public static partial class CompetitionRounds
{
    // Key: the end of the week id ("md-3", "r16-leg1"). Name: what players see after the
    // competition's name. TwoLegged: the round is played home and away, one week per leg.
    public sealed record Round(string Key, string Name, bool TwoLegged);

    /// <summary>
    /// The Champions League round a source's round text names, or null for one this app doesn't
    /// predict (qualifying, which runs in July and August) or can't read. The kickoff tells a
    /// knockout play-off (February) from a qualifying play-off (August).
    /// </summary>
    public static Round? ChampionsLeague(string? round, DateTime? kickoff)
    {
        if (string.IsNullOrWhiteSpace(round)) return null;
        var text = round.Trim().ToLowerInvariant();
        if (text.Contains("qualif") || kickoff is { Month: 7 or 8 }) return null;

        if (text.Contains("semi") || text.Contains("1/2")) return new("sf", "Semi-finals", true);
        if (text.Contains("quarter") || text.Contains("1/4")) return new("qf", "Quarter-finals", true);
        if (text.Contains("1/8") || text.Contains("eighth") || text.Contains("round of 16") || text.Contains("last 16"))
            return new("r16", "Round of 16", true);
        if (text.Contains("play") || text.Contains("1/16") || text.Contains("knockout round"))
            return new("po", "Knockout play-offs", true);
        if (text.Contains("final")) return new("final", "Final", false);

        // League phase: the last number in "Round 3", "Matchday 3", "League phase - Round 3" or "3".
        var number = LastNumber().Match(text);
        return number.Success && int.TryParse(number.Value, out var md) && md is >= 1 and <= 8
            ? new($"md-{md}", $"Matchday {md}", false)
            : null;
    }

    // The week for one leg of a two-legged round, or the round itself.
    public static (string Key, string Name) Leg(Round round, int leg) =>
        round.TwoLegged ? ($"{round.Key}-leg{leg}", $"{round.Name} — {(leg == 1 ? "1st" : "2nd")} leg") : (round.Key, round.Name);

    public static string WeekId(string competitionId, string key) => $"{competitionId}-{key}";

    /// <summary>
    /// Whether a match in this week can go to extra time: a two-legged round's 2nd leg, or a
    /// one-off knockout match (the final). Only these need the 90-minute score kept apart.
    /// </summary>
    public static bool CanGoToExtraTime(string weekId) => weekId.EndsWith("-leg2") || weekId.EndsWith("-final");

    [GeneratedRegex(@"\d+(?!.*\d)")]
    private static partial Regex LastNumber();
}
