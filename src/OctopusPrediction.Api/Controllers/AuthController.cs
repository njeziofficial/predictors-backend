using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Entities;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;
using OctopusPrediction.Api.Services.Email;

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

    public record PasskeyLoginRequest(
        [property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(64)] string FlowId,
        [property: System.ComponentModel.DataAnnotations.Required] Fido2NetLib.AuthenticatorAssertionRawResponse Response);

    // Signing in with fingerprint or face, step 1: what the phone should sign.
    [HttpPost("passkey/options")]
    public IActionResult PasskeyOptions([FromServices] PasskeyService passkeys)
    {
        var ceremony = passkeys.StartLogin();
        return Ok(new { flowId = ceremony.FlowId, options = ceremony.Options });
    }

    // Step 2: the phone's signature, which signs its owner in like a password would.
    [HttpPost("passkey")]
    public async Task<IActionResult> PasskeyLogin(PasskeyLoginRequest request, [FromServices] PasskeyService passkeys, CancellationToken ct)
    {
        var (user, problem) = await passkeys.FinishLoginAsync(request.FlowId, request.Response, ct);
        if (user is null) return Unauthorized(new { message = problem });

        var result = await auth.SignInAsync(user, "passkey");
        if (result.FailureReason == LoginFailureReason.AccountDisabled)
            return StatusCode(403, new
            {
                code = "account_disabled",
                message = "Your account has been disabled. Please contact an admin."
            });
        SetRefreshCookie(result.Session!);
        return Ok(result.Session!.Response);
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

    // Emails a link to choose a new password. Always answers the same way, whether or not the
    // email has an account, so the form can't be used to find out who plays. Players only:
    // admins can't change their own password, so theirs stays with the system admin.
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, [FromServices] EmailSender email)
    {
        if (!email.IsConfigured)
            return StatusCode(503, new { message = "Password emails aren't set up yet. Ask an admin to reset your password." });
        var answer = Ok(new { message = "If that email belongs to a player, a link to choose a new password is on its way. Check your spam folder too." });

        var address = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address);
        if (user is null || user.IsDisabled || user.Role != UserRole.User) return answer;
        if (!await PasswordTokens.CanSendResetAsync(db, user.Id)) return answer;

        await PasswordTokens.SendAsync(db, email, user, PasswordTokenPurposes.Reset);
        return answer;
    }

    // What the Set password page needs to know about a link before showing the form.
    [HttpPost("password-token")]
    public async Task<IActionResult> CheckPasswordToken(PasswordTokenRequest request)
    {
        var token = await PasswordTokens.FindValidAsync(db, request.Token);
        return Ok(token is null
            ? new PasswordTokenDto(false, null, null)
            : new PasswordTokenDto(true, token.Purpose, token.User.Name));
    }

    // Chooses a password with a link from a Forgot password or welcome email. The link works
    // once; every other session of the account is signed out.
    [HttpPost("set-password")]
    public async Task<IActionResult> SetPassword(SetPasswordRequest request)
    {
        var token = await PasswordTokens.FindValidAsync(db, request.Token);
        if (token is null)
            return BadRequest(new { message = "This link has expired or was already used. Ask for a new one from Forgot password." });

        var user = token.User;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.MustResetPassword = false;
        token.UsedAt = DateTime.UtcNow;
        AuditLogger.Log(db, subject: user, actor: user,
            action: token.Purpose == PasswordTokenPurposes.Welcome ? "PasswordSetFromWelcomeEmail" : "PasswordResetByEmail");
        await SessionRevoker.RevokeAllAsync(db, user.Id, "password_reset");
        // And their fingerprint/face sign-in, if the system admin chose that.
        await PasskeyService.RemoveOnPasswordResetAsync(db, user.Id);
        await db.SaveChangesAsync();
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
