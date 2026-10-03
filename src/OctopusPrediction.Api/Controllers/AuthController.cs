using Microsoft.AspNetCore.Mvc;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth) : ControllerBase
{
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

        if (result.Response is null) return Unauthorized(new { message = "Invalid email or password." });
        return Ok(result.Response);
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await auth.RegisterAsync(request);
        if (result is null) return Conflict(new { message = "Email already registered." });
        return StatusCode(201, result);
    }
}
