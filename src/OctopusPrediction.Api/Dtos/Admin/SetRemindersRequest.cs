using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// Prediction reminders: on or off, and how many hours before each week's first match each one
// goes out (72 = 3 days; validated by ReminderSchedule.Validate). The system user's alone.
public record SetRemindersRequest(bool Enabled, [Required] int[] HoursBeforeFirstGame);
