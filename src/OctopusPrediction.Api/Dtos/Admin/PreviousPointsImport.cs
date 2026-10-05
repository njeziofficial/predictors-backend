using System.ComponentModel.DataAnnotations;

namespace OctopusPrediction.Api.Dtos.Admin;

// A row is matched to an account by Email when given, otherwise by WhatsAppName (see
// AdminPreviousPointsController.MatchByName). Name/PhoneNumber/WhatsAppName are only stored when
// an email has no account yet — the row then creates one (Name required for that).
// CorrectScores null leaves an existing count as it is (0 for a new entry).
public record PreviousPointsImportRow(
    string? Email,
    int Points,
    int? CorrectScores,
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
// user), "create_user" (account will be created), "unchanged", "skipped" (a WhatsApp name with no
// matching account — reported, not saved, and doesn't block the rest), or "error" (see Error).
// MatchedBy says how the account was found: "email", "alias" (a name linked in an earlier
// import), "whatsapp", "name", or null.
public record PreviousPointsImportRowResult(
    int RowNumber,
    string Email,
    string? Name,
    string? WhatsAppName,
    int Points,
    int CorrectScores,
    string Action,
    int? CurrentPoints,
    int? CurrentCorrectScores,
    string? MatchedBy,
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
    int CorrectScores,
    string Label,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
