namespace OctopusPrediction.Api.Dtos.Admin;

public record PagedAuditLogDto(
    IReadOnlyList<AuditLogEntryDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);
