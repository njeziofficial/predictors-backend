using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthService auth,
    AppDbContext db,
    IOptions<LiveScraperSettings> defaults,
    AppCache cache,
    IWebHostEnvironment env) : ControllerBase
{
    // The refresh token never appears in a response body: it lives only in this httpOnly cookie,
    // so page scripts (and anything injected into them) can't read it. Scoped to /api/auth so the
    // browser only sends it to refresh/logout, not on every API call.
    private const string RefreshCookieName = "op_refresh";
    private const string RefreshCookiePath = "/api/auth";

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await auth.LoginAsync(request);

        if (result.FailureReason == LoginFailureReason.AccountDisabled)
            return StatusCode(403, new
            {
                code = "account_disabled",
                message = "Your account has been disabled. Please contact an admin."
            });

        if (result.Session is null)
            return Unauthorized(new
            {
                message = request.Method switch
                {
                    "email" => "Incorrect email or password.",
                    "whatsapp" => "Incorrect WhatsApp name or password.",
                    _ => "Incorrect email, WhatsApp name or password.",
                }
            });
        SetRefreshCookie(result.Session);
        return Ok(result.Session.Response);
    }

    // Anonymous: the login/register pages use this to hide or disable the sign-up form.
    [HttpGet("registration-status")]
    public async Task<IActionResult> GetRegistrationStatus()
    {
        var settings = await cache.SettingsAsync(db, defaults.Value);
        return Ok(new { open = !settings.RegistrationClosed });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var settings = await cache.SettingsAsync(db, defaults.Value);
        if (settings.RegistrationClosed)
            return StatusCode(403, new
            {
                code = "registration_closed",
                message = "Registration is currently closed. Please contact an admin."
            });

        // WhatsApp names sign people in, so each one can belong to one account only.
        if (await WhatsAppNames.IsTakenAsync(db, request.WhatsAppName))
            return Conflict(new { message = WhatsAppNames.TakenMessage });

        var session = await auth.RegisterAsync(request);
        if (session is null) return Conflict(new { message = "Email already registered." });
        SetRefreshCookie(session);
        return StatusCode(201, session.Response);
    }

    // Exchanges the refresh cookie for a new access token (and a rotated cookie).
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(refreshToken))
            return Unauthorized(new { code = "session_expired", message = "Session expired. Please log in again." });

        var result = await auth.RefreshAsync(refreshToken);

        if (result.FailureReason == RefreshFailureReason.AccountDisabled)
        {
            ClearRefreshCookie();
            return StatusCode(403, new
            {
                code = "account_disabled",
                message = "Your account has been disabled. Please contact an admin."
            });
        }

        if (result.Session is null)
        {
            ClearRefreshCookie();
            return Unauthorized(new { code = "session_expired", message = "Session expired. Please log in again." });
        }

        SetRefreshCookie(result.Session);
        return Ok(result.Session.Response);
    }

    // Anonymous on purpose: logging out must work even after the access token has expired.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(refreshToken))
            await auth.LogoutAsync(refreshToken);

        ClearRefreshCookie();
        return NoContent();
    }

    private void SetRefreshCookie(AuthSession session)
    {
        // Null means the refresh was served from the rotation grace window — the browser already
        // holds the newer cookie, and overwriting it would put back the revoked one.
        if (session.RefreshToken is null) return;

        Response.Cookies.Append(RefreshCookieName, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            // Requests reach the API over plain HTTP behind the Vite proxy / Cloudflare Worker, but
            // the browser-facing origin is HTTPS everywhere except local dev.
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = session.RefreshTokenExpiresAt
        });
    }

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath
        });
}
