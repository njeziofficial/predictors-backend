using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OctopusPrediction.Api.Data;
using OctopusPrediction.Api.Hubs;
using OctopusPrediction.Api.Middleware;
using OctopusPrediction.Api.Services;
using OctopusPrediction.Api.Services.Caching;
using OctopusPrediction.Api.Services.Scraping;

var builder = WebApplication.CreateBuilder(args);

// ── MVC ──────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.WriteIndented = false;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Octopus Prediction API",
        Version = "v1",
        Description = "Backend API for the Octopus Prediction football prediction game"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste your JWT token (without 'Bearer ' prefix)"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            []
        }
    });
});

// ── Database ──────────────────────────────────────────────────────────────────
// ── Caching ───────────────────────────────────────────────────────────────────
// Hot reads (auth checks, standings, fixtures, settings) come from memory; the interceptor on
// every DbContext empties the affected entries after each save. See Services/Caching.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AppCache>();
builder.Services.AddSingleton<LiveUpdateNotifier>();
builder.Services.AddSingleton<CacheInvalidationInterceptor>();

builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .AddInterceptors(sp.GetRequiredService<CacheInvalidationInterceptor>()));

// ── Auth ──────────────────────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        // Browsers can't set an Authorization header on a WebSocket (or SSE) request, so the
        // SignalR client sends the access token as ?access_token=… instead. Only honoured on
        // the hub's own path, so it can't be used to authenticate ordinary API calls.
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(ChatHub.Path))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// ── Real-time chat (SignalR) ─────────────────────────────────────────────────
builder.Services.AddSignalR(opts => opts.MaximumReceiveMessageSize = 16 * 1024)
    .AddJsonProtocol(opts =>
        opts.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<PresenceTracker>();

// ── App Services ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IScoringService, ScoringService>();

// ── Live Score Scraper ────────────────────────────────────────────────────────
builder.Services.Configure<LiveScraperSettings>(builder.Configuration.GetSection("LiveScoreScraper"));
// Registration order sets the base rotation order — LiveScraperBackgroundService
// round-robins which source leads each poll and falls through the rest in that order.
builder.Services.AddSingleton<IMatchSource, FlashscoreSource>();
builder.Services.AddSingleton<IMatchSource, LivescoreSource>();
builder.Services.AddSingleton<IMatchSource, BbcSportSource>();
builder.Services.AddSingleton<IMatchSource, WorldFootballSource>();
builder.Services.AddSingleton<IMatchSource, EspnSource>();
builder.Services.AddSingleton<IMatchSource, FotMobSource>();
builder.Services.AddSingleton<IMatchSource, FoxSportsSource>();
builder.Services.AddHostedService<LiveScraperBackgroundService>();

// ── Prediction Reminders (Twilio) ────────────────────────────────────────────
builder.Services.Configure<TwilioSettings>(builder.Configuration.GetSection("Twilio"));
builder.Services.AddSingleton<IReminderMessageSender, TwilioReminderMessageSender>();
builder.Services.AddHostedService<ReminderBackgroundService>();

// ── CORS ──────────────────────────────────────────────────────────────────────
// API auth is a bearer JWT. The one cookie (the refresh token) is SameSite=Strict and only
// ever sent same-origin through the Vite proxy / Cloudflare Worker, and this policy never
// enables AllowCredentials — so allowing any origin in Development still carries no
// CSRF/credential risk. It just lets a tunnel (ngrok, etc.) with an unpredictable URL
// reach the API without editing this allowlist every time. Production stays locked down.
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod();
        }
        else
        {
            // Cors:AllowedOrigins is a comma-separated list (e.g. the deployed Vercel URL),
            // set via the Cors__AllowedOrigins env var in production so the frontend's
            // origin can change without a backend redeploy. Falls back to the local dev
            // origins when unset, so `dotnet run` in Production mode still works.
            var configuredOrigins = builder.Configuration["Cors:AllowedOrigins"]
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var origins = configuredOrigins is { Length: > 0 }
                ? configuredOrigins
                : new[]
                {
                    "http://localhost:5173",
                    "http://localhost:3000",
                    "http://localhost:8081",
                    "http://localhost:8080",
                };
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    }));

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Ensure DB schema exists (creates tables if absent; switch to migrations for production)
try
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        // EnsureCreatedAsync only builds the schema for a brand-new database, so a column
        // added to an entity after the DB already exists needs to be patched in manually.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"IsDisabled\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"PhoneNumber\" character varying(30) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"WhatsAppName\" character varying(200) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"LastLoginAt\" timestamp with time zone NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"IsSystemUser\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"ReminderEnabled\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"ReminderHoursBeforeFirstGame\" integer NOT NULL DEFAULT 24;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AuditLogs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NULL,
                "UserName" character varying(200) NULL,
                "ActorUserId" uuid NULL,
                "ActorName" character varying(200) NULL,
                "Action" character varying(50) NOT NULL,
                "Field" character varying(50) NULL,
                "PreviousValue" character varying(500) NULL,
                "NewValue" character varying(500) NULL,
                "Details" character varying(500) NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_CreatedAt\" ON \"AuditLogs\" (\"CreatedAt\");");
        // AuditLogs may already exist from before Field/PreviousValue/NewValue were added.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"AuditLogs\" ADD COLUMN IF NOT EXISTS \"Field\" character varying(50) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"AuditLogs\" ADD COLUMN IF NOT EXISTS \"PreviousValue\" character varying(500) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"AuditLogs\" ADD COLUMN IF NOT EXISTS \"NewValue\" character varying(500) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"MatchWeeks\" ADD COLUMN IF NOT EXISTS \"ReminderSentAt\" timestamp with time zone NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"SourceName\" text NOT NULL DEFAULT 'Flashscore';");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"PredictionsLocked\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"RegistrationClosed\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"MustResetPassword\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"AuditLogEnabled\" boolean NOT NULL DEFAULT true;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PreviousPoints" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "Points" integer NOT NULL,
                "Label" character varying(100) NOT NULL,
                "CreatedByUserId" uuid NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PreviousPoints_UserId_Label\" ON \"PreviousPoints\" (\"UserId\", \"Label\");");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PreviousPoints\" ADD COLUMN IF NOT EXISTS \"CorrectScores\" integer NOT NULL DEFAULT 0;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PlayerAliases" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Key" character varying(200) NOT NULL,
                "Alias" character varying(200) NOT NULL,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PlayerAliases_Key\" ON \"PlayerAliases\" (\"Key\");");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RefreshTokens" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "FamilyId" uuid NOT NULL,
                "TokenHash" character varying(64) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "RevokedAt" timestamp with time zone NULL,
                "RevokedReason" character varying(50) NULL,
                "ReplacedByTokenId" uuid NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RefreshTokens_TokenHash\" ON \"RefreshTokens\" (\"TokenHash\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_RefreshTokens_UserId\" ON \"RefreshTokens\" (\"UserId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_RefreshTokens_FamilyId\" ON \"RefreshTokens\" (\"FamilyId\");");

        // Back-office permissions: defaults for every admin, and per-admin exceptions.
        // Same SQL as db/supabase/2026-10-07-admin-permissions.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RolePermissions" (
                "Permission" character varying(64) NOT NULL PRIMARY KEY,
                "Allowed" boolean NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "UpdatedByUserId" uuid NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "UserPermissions" (
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "Permission" character varying(64) NOT NULL,
                "Allowed" boolean NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "UpdatedByUserId" uuid NULL,
                PRIMARY KEY ("UserId", "Permission")
            );
            """);
        await Permissions.SeedDefaultsAsync(db);

        // In-app chat. Same SQL as db/supabase/2026-10-08-chat.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Conversations" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DirectKey" character varying(80) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "LastMessageAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Conversations_DirectKey\" ON \"Conversations\" (\"DirectKey\");");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ConversationParticipants" (
                "ConversationId" uuid NOT NULL REFERENCES "Conversations" ("Id") ON DELETE CASCADE,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "JoinedAt" timestamp with time zone NOT NULL,
                "LastReadAt" timestamp with time zone NULL,
                PRIMARY KEY ("ConversationId", "UserId")
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_ConversationParticipants_UserId\" ON \"ConversationParticipants\" (\"UserId\");");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ChatMessages" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "ConversationId" uuid NOT NULL REFERENCES "Conversations" ("Id") ON DELETE CASCADE,
                "SenderId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "Body" character varying(2000) NOT NULL,
                "IsAnnouncement" boolean NOT NULL DEFAULT false,
                "ClientId" character varying(64) NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "DeletedAt" timestamp with time zone NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_ChatMessages_ConversationId_CreatedAt\" ON \"ChatMessages\" (\"ConversationId\", \"CreatedAt\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ChatMessages_SenderId_ClientId\" ON \"ChatMessages\" (\"SenderId\", \"ClientId\");");

        var seedLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await DbSeeder.SeedAdminUserAsync(db, builder.Configuration, seedLogger);

        var scraperDefaults = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LiveScraperSettings>>();
        var settings = await ScraperSettingsStore.GetOrCreateAsync(db, scraperDefaults.Value);
        AuditLogSettings.Enabled = settings.AuditLogEnabled;
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Failed to ensure database creation");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Octopus Prediction API v1"));
}

app.MapGet("/", () => Results.Redirect("/swagger"));

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<DisabledUserMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>(ChatHub.Path);

app.Run();
