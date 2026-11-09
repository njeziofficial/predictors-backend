using System.Globalization;
using System.Text;
using System.Text.Json;
using PuppeteerSharp;

namespace OctopusPrediction.Api.Services.Scraping;

// What the sources that read a whole page's embedded data (AS, Marca, OneFootball, Guardian,
// Mundo Deportivo) have in common.
internal static class PageReading
{
    // Sources listing the whole season keep only matches this close to now, so 380 matches aren't
    // synced every poll. Two weeks, not one, so the next round is seen across an international break.
    private static readonly TimeSpan SyncWindow = TimeSpan.FromDays(14);

    public static bool InSyncWindow(DateTime kickoffUtc, DateTime now) => (kickoffUtc - now).Duration() <= SyncWindow;

    /// <summary>A kickoff as MatchReading.ParseKickoff reads one with its date.</summary>
    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mainland Spain's clock time as UTC, by the EU's rule (summer time from the last Sunday of
    /// March to the last Sunday of October), so it doesn't depend on the host's time zone data.
    /// </summary>
    public static DateTime MadridToUtc(DateTime local)
    {
        var summerStarts = LastSunday(local.Year, 3).AddHours(2);
        var summerEnds = LastSunday(local.Year, 10).AddHours(3);
        var offset = local >= summerStarts && local < summerEnds ? 2 : 1;
        return DateTime.SpecifyKind(local.AddHours(-offset), DateTimeKind.Utc);
    }

    private static DateTime LastSunday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(int)last.DayOfWeek);
    }

    /// <summary>Spanish words for a called-off match, as the English MatchReading.CalledOff reads; else null.</summary>
    public static string? CalledOffInEnglish(string? spanish) => spanish?.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "aplazado" or "aplazada" or "apl" => "Postponed",
        "suspendido" or "suspendida" or "susp" => "Suspended",
        "interrumpido" or "interrumpida" => "Interrupted",
        "abandonado" or "abandonada" => "Abandoned",
        "cancelado" or "cancelada" => "Cancelled",
        _ => null,
    };

    public static bool IsHalfTimeInSpanish(string? text) =>
        text?.Trim().Equals("Descanso", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsScore(string? score) => !string.IsNullOrEmpty(score) && score.All(char.IsAsciiDigit);

    /// <summary>The page's HTML, or null (logged) when the site didn't answer with it.</summary>
    public static async Task<string?> HtmlAsync(IPage page, string url, ILogger logger, string source)
    {
        var response = await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 30_000
        });
        if (response is null || !response.Ok)
        {
            logger.LogWarning("[{Source}] {Url} answered {Status}", source, url, response is null ? "nothing" : (int)response.Status);
            return null;
        }
        return await response.TextAsync();
    }

    /// <summary>The JSON value starting just after the marker, ignoring whatever follows it; null when the marker isn't there.</summary>
    public static JsonDocument? JsonAfter(string html, string marker)
    {
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var bytes = Encoding.UTF8.GetBytes(html[(at + marker.Length)..]);
        var reader = new Utf8JsonReader(bytes);
        return JsonDocument.ParseValue(ref reader);
    }

    /// <summary>A string or number property as text; null if it's missing, null or anything else.</summary>
    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;
}
