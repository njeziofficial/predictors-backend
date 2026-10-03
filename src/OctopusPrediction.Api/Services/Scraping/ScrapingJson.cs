using System.Text.Json;

namespace OctopusPrediction.Api.Services.Scraping;

internal static class ScrapingJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
