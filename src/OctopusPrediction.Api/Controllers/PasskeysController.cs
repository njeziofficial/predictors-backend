using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Fido2NetLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Services;

namespace OctopusPrediction.Api.Controllers;

// The signed-in user's passkeys (signing in with fingerprint or face): listing them, adding the
// device they're on, renaming and removing. Signing in with one is in AuthController.
[ApiController]
[Route("api/passkeys")]
[Authorize]
public class PasskeysController(AppDbContext db, PasskeyService passkeys) : ControllerBase
{
    public record FinishRegistrationRequest(
        [Required, MaxLength(64)] string FlowId,
        [MaxLength(100)] string? Name,
        JsonElement Response);

    public record RenamePasskeyRequest([MaxLength(100)] string? Name);

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await passkeys.ListAsync(CurrentUserId()));

    // Adding this device, step 1: what it should create.
    [HttpPost("register/options")]
    public async Task<IActionResult> RegistrationOptions()
    {
        var me = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUserId());
        if (me is null) return NotFound();
        var (ceremony, problem) = await passkeys.StartRegistrationAsync(me);
        if (ceremony is null) return BadRequest(new { message = problem });
        return Ok(new { flowId = ceremony.FlowId, options = ceremony.Options });
    }

    // Step 2: the new passkey, checked and saved.
    [HttpPost("register")]
    public async Task<IActionResult> Register(FinishRegistrationRequest request, CancellationToken ct)
    {
        var (answer, unreadable) = PasskeyService.ReadAnswer<AuthenticatorAttestationRawResponse>(request.Response);
        if (answer is null) return BadRequest(new { message = unreadable });
        var (passkey, problem) = await passkeys.FinishRegistrationAsync(CurrentUserId(), request.FlowId, request.Name, answer, ct);
        return passkey is null ? BadRequest(new { message = problem }) : Ok(passkey);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, RenamePasskeyRequest request) =>
        await passkeys.RenameAsync(CurrentUserId(), id, request.Name) ? NoContent() : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id) =>
        await passkeys.RemoveAsync(CurrentUserId(), id) ? NoContent() : NotFound();

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
