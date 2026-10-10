using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// The source isn't here: it's the system user's alone, set through SetScraperSourceRequest.
// A "sourceName" sent by an older admin page is ignored.
public record UpdateScraperSettingsRequest(
    bool Enabled,
    [Range(15, 3600)] int PollIntervalSeconds,
    [Required, MinLength(1), MaxLength(100)] string Competition,
    bool ReminderEnabled,
    [Range(1, 336)] int ReminderHoursBeforeFirstGame
);
