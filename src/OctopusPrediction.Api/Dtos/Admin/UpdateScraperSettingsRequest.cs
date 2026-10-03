using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

public record UpdateScraperSettingsRequest(
    bool Enabled,
    [Range(15, 3600)] int PollIntervalSeconds,
    [Required, MinLength(1), MaxLength(100)] string Competition,
    [Required] string SourceName,
    bool ReminderEnabled,
    [Range(1, 336)] int ReminderHoursBeforeFirstGame
);
