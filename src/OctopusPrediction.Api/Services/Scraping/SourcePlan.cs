using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services.Scraping;

// How the scraper picks its source each poll (ScraperSettings.SourceMode):
//   Single   - only SourceName. If it fails or comes back empty, the poll is skipped.
//   Fallback - SourceOrder in order; the next one is tried only when the one before fails or
//              comes back empty.
//   Rotate   - SourceOrder, with a different one leading each poll, then falling through the
//              rest like Fallback. Spreads the load so no one site is hit every poll.
public static class SourceModes
{
    public const string Single = "Single";
    public const string Fallback = "Fallback";
    public const string Rotate = "Rotate";

    public static readonly IReadOnlyList<string> All = [Single, Fallback, Rotate];

    public static string? Normalize(string? mode) =>
        All.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
}

public static class SourcePlan
{
    // SourceOrder is stored comma-separated: no source name contains a comma.
    public static IReadOnlyList<string> ParseOrder(string? order) =>
        (order ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string FormatOrder(IEnumerable<string> names) => string.Join(",", names);

    // The sources to try this poll, in order. `poll` counts polls, so Rotate can turn.
    // Names no longer registered are skipped; if nothing usable is left, the first
    // registered source is used so syncing never stops on a bad setting.
    public static IReadOnlyList<IMatchSource> ForPoll(
        IReadOnlyList<IMatchSource> registered, ScraperSettings settings, int poll)
    {
        IMatchSource? Find(string name) =>
            registered.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        var mode = SourceModes.Normalize(settings.SourceMode) ?? SourceModes.Single;
        if (mode != SourceModes.Single)
        {
            var list = ParseOrder(settings.SourceOrder).Select(Find).OfType<IMatchSource>().ToList();
            if (list.Count > 0)
            {
                if (mode == SourceModes.Rotate)
                {
                    var lead = (int)((uint)poll % (uint)list.Count);
                    list = [.. list.Skip(lead), .. list.Take(lead)];
                }
                return list;
            }
        }

        return [Find(settings.SourceName) ?? registered[0]];
    }

    // For logs: "Flashscore", "Flashscore → FotMob" or "rotating Flashscore, FotMob".
    public static string Describe(ScraperSettings settings)
    {
        var mode = SourceModes.Normalize(settings.SourceMode) ?? SourceModes.Single;
        var order = ParseOrder(settings.SourceOrder);
        return mode switch
        {
            SourceModes.Fallback when order.Count > 0 => string.Join(" → ", order),
            SourceModes.Rotate when order.Count > 0 => "rotating " + string.Join(", ", order),
            _ => settings.SourceName,
        };
    }
}
