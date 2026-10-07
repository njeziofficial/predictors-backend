using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public class AuthService(AppDbContext db, IConfiguration config) : IAuthService
{
    // Session id claim: the refresh-token family this access token was issued under. Deliberately
    // not "sid", which the JWT handler's inbound claim mapping would rename to ClaimTypes.Sid.
    public const string SessionIdClaim = "session_id";

    private const int DefaultAccessTokenMinutes = 15;
    private const int DefaultRefreshTokenDays = 30;

    // Two tabs can refresh at the same moment with the same cookie. The first rotates it; the
    // second then presents a just-revoked token. Within this window that's treated as benign
    // (answered with an access token only) rather than as token theft.
    private static readonly TimeSpan RotationGracePeriod = TimeSpan.FromSeconds(30);

    public async Task<LoginResult> LoginAsync(LoginRequest request)
    {
        var login = (request.Login ?? request.Email ?? "").Trim();
        var byEmail = request.Method switch
        {
            "email" => true,
            "whatsapp" => false,
            _ => login.Contains('@'),
        };
        User? user;
        if (byEmail)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Email == login.ToLower());
            if (user is not null && !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash)) user = null;
        }
        else
        {
            // Names registered before duplicates were refused may be shared; the password then
            // picks the account, and if it fits more than one the user has to use their email.
            var matching = (await WhatsAppNames.FindAsync(db, login))
                .Where(u => BCrypt.Net.BCrypt.Verify(request.Password, u.PasswordHash))
                .ToList();
            user = matching.Count == 1 ? matching[0] : null;
        }
        if (user is null)
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        if (user.IsDisabled)
            return new LoginResult(null, LoginFailureReason.AccountDisabled);

        user.LastLoginAt = DateTime.UtcNow;
        AuditLogger.Log(db, subject: user, actor: user, action: "Login");
        var session = await StartSessionAsync(user);
        await db.SaveChangesAsync();

        return new LoginResult(session, null);
    }

    public async Task<AuthSession?> RegisterAsync(RegisterRequest request)
    {
        var exists = await db.Users.AnyAsync(u => u.Email == request.Email.ToLower());
        if (exists) return null;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Email = request.Email.ToLower(),
            PhoneNumber = request.PhoneNumber,
            WhatsAppName = request.WhatsAppName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        AuditLogger.Log(db, subject: user, actor: user, action: "Register");
        var session = await StartSessionAsync(user);
        await db.SaveChangesAsync();

        return session;
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken)
    {
        var now = DateTime.UtcNow;
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
            return new RefreshResult(null, RefreshFailureReason.InvalidToken);

        if (token.RevokedAt is not null)
        {
            if (token.RevokedReason != "rotated")
                return new RefreshResult(null, RefreshFailureReason.InvalidToken);

            var familyAlive = await db.RefreshTokens.AnyAsync(t =>
                t.FamilyId == token.FamilyId && t.RevokedAt == null && t.ExpiresAt > now);

            if (familyAlive && now - token.RevokedAt.Value <= RotationGracePeriod)
            {
                if (token.User.IsDisabled)
                    return new RefreshResult(null, RefreshFailureReason.AccountDisabled);
                return new RefreshResult(
                    new AuthSession(GenerateAccessToken(token.User, token.FamilyId), null, token.ExpiresAt), null);
            }

            // A rotated token showing up again later means someone else has a copy of it —
            // end the whole session so neither copy keeps working.
            await RevokeFamilyAsync(token.FamilyId, "reuse_detected");
            AuditLogger.Log(db, subject: token.User, actor: null, action: "SessionRevoked",
                details: "Refresh token reuse detected");
            await db.SaveChangesAsync();
            return new RefreshResult(null, RefreshFailureReason.InvalidToken);
        }

        if (token.ExpiresAt <= now)
            return new RefreshResult(null, RefreshFailureReason.InvalidToken);

        if (token.User.IsDisabled)
        {
            await RevokeFamilyAsync(token.FamilyId, "disabled");
            await db.SaveChangesAsync();
            return new RefreshResult(null, RefreshFailureReason.AccountDisabled);
        }

        var (raw, replacement) = CreateRefreshToken(token.User, token.FamilyId);
        token.RevokedAt = now;
        token.RevokedReason = "rotated";
        token.ReplacedByTokenId = replacement.Id;
        await db.SaveChangesAsync();

        return new RefreshResult(
            new AuthSession(GenerateAccessToken(token.User, token.FamilyId), raw, replacement.ExpiresAt), null);
    }

    public async Task LogoutAsync(string refreshToken)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null) return;

        await RevokeFamilyAsync(token.FamilyId, "logout");
        await db.SaveChangesAsync();
    }

    private async Task<AuthSession> StartSessionAsync(User user)
    {
        // Housekeeping: a user's expired tokens are dead weight, so drop them on each new login.
        var now = DateTime.UtcNow;
        var expired = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.ExpiresAt <= now).ToListAsync();
        db.RefreshTokens.RemoveRange(expired);

        var familyId = Guid.NewGuid();
        var (raw, token) = CreateRefreshToken(user, familyId);
        return new AuthSession(GenerateAccessToken(user, familyId), raw, token.ExpiresAt);
    }

    private (string Raw, RefreshToken Token) CreateRefreshToken(User user, Guid familyId)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var now = DateTime.UtcNow;
        var token = new RefreshToken
        {
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.AddDays(config.GetValue("Jwt:RefreshTokenDays", DefaultRefreshTokenDays))
        };
        db.RefreshTokens.Add(token);
        return (raw, token);
    }

    private async Task RevokeFamilyAsync(Guid familyId, string reason)
    {
        var now = DateTime.UtcNow;
        var active = await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync();
        foreach (var t in active)
        {
            t.RevokedAt = now;
            t.RevokedReason = reason;
        }
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private AuthResponse GenerateAccessToken(User user, Guid sessionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiryMinutes = config.GetValue("Jwt:AccessTokenMinutes", DefaultAccessTokenMinutes);
        var expiry = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(SessionIdClaim, sessionId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: expiry,
            signingCredentials: creds
        );

        return new AuthResponse(
            Token: new JwtSecurityTokenHandler().WriteToken(token),
            UserId: user.Id.ToString(),
            Name: user.Name,
            Email: user.Email,
            Role: user.Role.ToString().ToLower(),
            ExpiresAt: expiry,
            MustResetPassword: user.MustResetPassword,
            IsSystemUser: user.IsSystemUser
        );
    }
}
