using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// Mode is Single, Fallback or Rotate (see SourceModes). SourceName is the one source Single
// uses; SourceOrder is the ordered list Fallback and Rotate use. Both are kept whatever the
// mode, so switching modes back and forth doesn't lose either choice.
public record SetScraperSourceRequest(
    [Required] string Mode,
    [Required] string SourceName,
    [Required, MaxLength(20)] IReadOnlyList<string> SourceOrder
);
