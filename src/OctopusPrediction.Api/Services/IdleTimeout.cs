namespace OctopusPrediction.Api.Services;

// The idle sign-out time (ScraperSettings.IdleTimeoutMinutes): how long the app may go without a
// tap, click, key press or scroll before it signs the user out.
public static class IdleTimeout
{
    public const int DefaultMinutes = 15;
    public const int MinMinutes = 1;
    // A day: past that, idle sign-out stops meaning much next to the refresh token's own expiry.
    public const int MaxMinutes = 1440;

    // A stored value outside the range (edited by hand in the database) falls back to the default.
    public static int Clamp(int minutes) => minutes is < MinMinutes or > MaxMinutes ? DefaultMinutes : minutes;
}

// GET /api/auth/session-settings, and the body of PUT /api/admin/idle-timeout.
public record IdleTimeoutDto(int Minutes);
