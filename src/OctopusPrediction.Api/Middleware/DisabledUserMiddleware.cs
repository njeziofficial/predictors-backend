using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Data;

namespace OctopusPrediction.Api.Middleware;

// JWTs are stateless, so a user disabled mid-session would otherwise keep working until
// their token expires. This re-checks the live IsDisabled flag on every authenticated
// request and cuts them off immediately, wherever in the app they are.
public class DisabledUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userIdClaim, out var userId))
            {
                var isDisabled = await db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.IsDisabled)
                    .FirstOrDefaultAsync();

                if (isDisabled)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "account_disabled",
                        message = "Your account has been disabled. Please contact an admin."
                    });
                    return;
                }
            }
        }

        await next(context);
    }
}
