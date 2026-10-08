using Microsoft.EntityFrameworkCore;
using OctopusPrediction.Api.Entities;

namespace OctopusPrediction.Api.Data;

public static class DbSeeder
{
    // The one permanent admin — see User.IsSystemUser for what that protects.
    public static async Task SeedAdminUserAsync(AppDbContext db, IConfiguration config, ILogger logger)
    {
        var email = config["SeedAdmin:Email"];
        var password = config["SeedAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("[DbSeeder] SeedAdmin:Email/Password not configured; skipping admin seed");
            return;
        }

        email = email.ToLower();

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existing is not null)
        {
            if (existing.Role != UserRole.Admin)
            {
                logger.LogWarning(
                    "[DbSeeder] User {Email} already exists with role {Role}; not modifying it",
                    email, existing.Role);
                return;
            }

            // Retrofit: this account predates IsSystemUser — ensure the flag is set so the
            // system-user protections in AdminUsersController actually apply to it.
            if (!existing.IsSystemUser)
            {
                existing.IsSystemUser = true;
                await db.SaveChangesAsync();
                logger.LogInformation("[DbSeeder] Marked existing admin {Email} as the system user", email);
            }
            return;
        }

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Name = "Admin",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = UserRole.Admin,
            IsSystemUser = true,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        logger.LogInformation("[DbSeeder] Seeded system admin user {Email}", email);
    }
}
