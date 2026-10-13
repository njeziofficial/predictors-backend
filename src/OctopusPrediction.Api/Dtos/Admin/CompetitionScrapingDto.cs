namespace OctopusPrediction.Api.Dtos.Admin;

// Where a competition other than La Liga is scraped from: its source choice (as SourceModes)
// and the sources that have a page set for it.
public record CompetitionScrapingDto(
    string Id, string Name, bool Enabled, string SourceMode, string SourceName,
    IReadOnlyList<string> SourceOrder, IReadOnlyList<string> AvailableSources);
