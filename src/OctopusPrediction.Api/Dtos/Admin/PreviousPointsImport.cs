using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// Name/PhoneNumber/WhatsAppName are only used when the email has no account yet — the row
// then creates one (Name required for that). Existing accounts are matched by email only.
public record PreviousPointsImportRow(
    string Email,
    int Points,
    string? Name,
    string? PhoneNumber,
    string? WhatsAppName
);

// DryRun = true validates and reports what would happen without saving anything.
public record PreviousPointsImportRequest(
    [Required, MaxLength(100)] string Label,
    bool DryRun,
    [Required, MinLength(1)] List<PreviousPointsImportRow> Rows
);

// Action: "add" (new row for an existing user), "update" (replaces this label's points for that
// user), "create_user" (account will be created), "unchanged", or "error" (see Error).
public record PreviousPointsImportRowResult(
    int RowNumber,
    string Email,
    string? Name,
    int Points,
    string Action,
    int? CurrentPoints,
    string? Error
);

public record CreatedAccountDto(string Name, string Email, string TemporaryPassword);

public record PreviousPointsImportResult(
    bool DryRun,
    bool Committed,
    int ErrorCount,
    List<PreviousPointsImportRowResult> Rows,
    List<CreatedAccountDto> CreatedAccounts
);

public record PreviousPointsEntryDto(
    string Id,
    string UserId,
    string UserName,
    string UserEmail,
    int Points,
    string Label,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
