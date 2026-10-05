namespace OctopusPrediction.Api.Dtos.Admin;

// Partial update: a null field is left as it is, so any one detail (or all of them) can be
// changed at once. An empty PhoneNumber/WhatsAppName clears it; Name and Email can't be cleared.
public record UpdateUserDetailsRequest(
    string? Name,
    string? Email,
    string? PhoneNumber,
    string? WhatsAppName
);
