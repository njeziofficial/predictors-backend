namespace OctopusPrediction.Api.Dtos.Admin;

public record PermissionDefinitionDto(string Key, string Group, string Label, string Description);

// What the signed-in admin may do, so the back office can hide what they can't.
public record MyPermissionsDto(bool IsSystemUser, IReadOnlyDictionary<string, bool> Permissions);

public record AdminPermissionsDto(
    string UserId,
    string Name,
    string Email,
    string? WhatsAppName,
    bool IsDisabled,
    // Only the permissions this admin has an exception for; the rest follow the defaults.
    IReadOnlyDictionary<string, bool> Overrides,
    IReadOnlyDictionary<string, bool> Effective
);

public record PermissionsOverviewDto(
    IReadOnlyList<PermissionDefinitionDto> Permissions,
    IReadOnlyDictionary<string, bool> Defaults,
    IReadOnlyList<AdminPermissionsDto> Admins
);

public record UpdatePermissionDefaultsRequest(Dictionary<string, bool> Permissions);

// A null value removes the exception, so the admin follows the default again.
public record UpdateAdminPermissionsRequest(Dictionary<string, bool?> Overrides);
