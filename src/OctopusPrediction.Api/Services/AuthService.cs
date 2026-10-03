using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Dtos.Auth;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Services;

public class AuthService(AppDbContext db, IConfiguration config) : IAuthService
{
    private const int DefaultExpiryMinutes = 60 * 24 * 7; // 7 days, used if Jwt:ExpiryMinutes is unset
    public async Task<LoginResult> LoginAsync(LoginRequest request)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower());
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        if (user.IsDisabled)
            return new LoginResult(null, LoginFailureReason.AccountDisabled);

        user.LastLoginAt = DateTime.UtcNow;
        AuditLogger.Log(db, subject: user, actor: user, action: "Login");
        await db.SaveChangesAsync();

        return new LoginResult(GenerateToken(user), null);
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
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
        await db.SaveChangesAsync();

        return GenerateToken(user);
    }

    private AuthResponse GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiryMinutes = config.GetValue("Jwt:ExpiryMinutes", DefaultExpiryMinutes);
        var expiry = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
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
