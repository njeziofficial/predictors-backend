using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services.Scraping;

namespace OctopusPrediction.Api.Services.LiveScores;

// How a scraped row reads once its raw text is parsed: the status, scores, minute and kickoff
// it would put on the fixture. Shared by the fingerprint (did anything change since the last
// poll?) and FixtureSync (apply it), so both read a row the same way.
public static class MatchReading
{
    // Team-name based (not source-specific-id based) so that whichever source syncs a
    // given match, it resolves to the same Fixture row instead of creating a duplicate.
    public static string FixtureId(string home, string away) =>
        $"fs-{TeamNameNormalizer.Slug(home)}-vs-{TeamNameNormalizer.Slug(away)}";

    public static string WeekId(string competition, string? round) =>
        $"{Slug(competition)}-week-{NormalizeRound(round)}";

    public static string WeekName(string competition, string? round) =>
        $"{competition} — Week {NormalizeRound(round)}";

    public static string UnknownWeekId(string competition) => WeekId(competition, null);

    public static string NormalizeRound(string? round)
    {
        if (string.IsNullOrWhiteSpace(round)) return "?";
        var parts = round.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : round.Trim();
    }

    private static string Slug(string name) =>
        new string(name.ToLower().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>
    /// A hash of everything the row would change on its fixture. The same reading from any source
    /// gives the same fingerprint, so an unchanged match is skipped however it was scraped.
    /// </summary>
    public static string Fingerprint(ScrapedMatchDto m)
    {
        var kickoff = ParseKickoff(m.Time) is { HasDate: true } k ? k.Utc.ToString("O") : "";
        var reading = string.Join('|',
            FixtureId(m.Home, m.Away), MapStatus(m), ParseScore(m.ScoreHome), ParseScore(m.ScoreAway),
            ParseMinute(m.Stage), kickoff, NormalizeRound(m.Round));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reading)));
    }

    // Prefers a source's explicit IsScheduled/IsLive flags (Flashscore's status now lives
    // on a row modifier class, not in the stage text — see FlashscoreSource). Falls back
    // to parsing Stage text for sources that only expose that (Livescore, BBC Sport).
    public static FixtureStatus MapStatus(ScrapedMatchDto m)
    {
        // Flashscore always supplies both flags (true/false, never absent). When present,
        // trust them completely — a finished Flashscore match has both flags false and no
        // Stage text at all, which the text-parsing fallback below would misread as PreMatch.
        if (m.IsScheduled is not null || m.IsLive is not null)
        {
            if (m.IsScheduled == true) return FixtureStatus.PreMatch;
            if (m.IsLive == true) return IsHalfTime(m.Stage) ? FixtureStatus.HalfTime : FixtureStatus.Live;
            return FixtureStatus.Ended;
        }

        return CleanStage(m.Stage) switch
        {
            "FT" or "AET" or "AP" or "Finished" => FixtureStatus.Ended,
            var s when IsHalfTime(s) => FixtureStatus.HalfTime,
            "Postp." or "Postponed" => FixtureStatus.Postponed,
            "Canc." or "Cancelled" or "Canceled" or "Aband." => FixtureStatus.Cancelled,
            var s when s is not null && (s.EndsWith("'") || int.TryParse(s.Split('+')[0], out _)) => FixtureStatus.Live,
            _ => FixtureStatus.PreMatch
        };
    }

    public static bool IsHalfTime(string? stage) =>
        CleanStage(stage) is { } s && s.Equals("HT", StringComparison.OrdinalIgnoreCase);

    public static int? ParseMinute(string? stage)
    {
        var clean = CleanStage(stage);
        if (string.IsNullOrEmpty(clean)) return null;
        return int.TryParse(clean.TrimEnd('\'').Split('+')[0], out var m) ? m : null;
    }

    // FotMob writes the minute as "98‎’‎": invisible direction marks around a typographic
    // apostrophe. Strip the marks and use a plain "'" so it reads like every other source.
    private static string? CleanStage(string? stage)
    {
        if (stage is null) return null;
        var sb = new StringBuilder(stage.Length);
        foreach (var c in stage)
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c is '’' or '′' ? '\'' : c);
        }
        return sb.ToString().Trim();
    }

    public static int? ParseScore(string? score) =>
        int.TryParse(score?.Trim(), out var v) ? v : null;

    // HasDate is false for a bare "HH:mm", which is assumed to be today.
    public readonly record struct ScrapedKickoff(DateTime Utc, bool HasDate);

    public static ScrapedKickoff? ParseKickoff(string? time)
    {
        if (string.IsNullOrWhiteSpace(time) || time.Contains("'") || time is "HT" or "FT") return null;

        // FotMob supplies a full ISO-8601 UTC instant ("2026-10-11T16:30:00.000Z") instead of a
        // bare time-of-day string — trust it outright rather than running it through the "assume
        // today" fallback below, which exists only because the other sources never give us a real
        // date to work with. TryParseExact (not the general-purpose TryParse) so a bare "HH:mm"
        // from another source can never be misread as an ISO timestamp and take this branch.
        if (DateTimeOffset.TryParseExact(time, ["yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ"], null,
            DateTimeStyles.AssumeUniversal, out var iso))
            return new ScrapedKickoff(iso.UtcDateTime, HasDate: true);

        // Day and month, no year: this year.
        if (DateTime.TryParseExact(time, ["d.MM. HH:mm", "dd.MM. HH:mm"],
            null, DateTimeStyles.None, out var dated))
            return new ScrapedKickoff(DateTime.SpecifyKind(dated, DateTimeKind.Utc), HasDate: true);

        if (DateTime.TryParseExact(time, "HH:mm", null, DateTimeStyles.None, out var t))
        {
            var today = DateTime.UtcNow.Date;
            return new ScrapedKickoff(DateTime.SpecifyKind(today + t.TimeOfDay, DateTimeKind.Utc), HasDate: false);
        }
        return null;
    }
}
