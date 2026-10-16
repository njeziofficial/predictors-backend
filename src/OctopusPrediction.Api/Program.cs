using Fido2NetLib;
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
using OctopusPrediction.Api.Services.Email;
using OctopusPrediction.Api.Services.LiveScores;
using OctopusPrediction.Api.Services.Monitoring;
using OctopusPrediction.Api.Services.Scraping;

var builder = WebApplication.CreateBuilder(args);

// Recent log lines, kept in memory for the system admin's Scraper Health page.
var recentLogs = new RecentLogs();
builder.Logging.AddProvider(recentLogs);
builder.Services.AddSingleton(recentLogs);

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
// Chat messages to the phones of those who don't have the app in front.
builder.Services.AddSingleton<ChatPushNotifier>();

// ── App Services ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuthService, AuthService>();
// Signing in with fingerprint or face (passkeys).
builder.Services.AddSingleton<IFido2>(new Fido2NetLib.Fido2(PasskeySetup.Configuration(builder.Configuration, builder.Environment)));
builder.Services.AddScoped<PasskeyService>();
builder.Services.AddScoped<IScoringService, ScoringService>();
builder.Services.AddScoped<PointsReconciler>();
builder.Services.AddScoped<WeekMarking>();
builder.Services.AddScoped<WeekReconciliation>();
builder.Services.AddHostedService<PointsReconciliationBackgroundService>();

// ── Live Score Scraper ────────────────────────────────────────────────────────
builder.Services.Configure<LiveScraperSettings>(builder.Configuration.GetSection("LiveScoreScraper"));
// Which of these the scraper reads, and in what order, is set in admin settings (SourcePlan).
// The first one registered is the last resort if the saved choice names no known source.
builder.Services.AddSingleton<IMatchSource, FlashscoreSource>();
builder.Services.AddSingleton<IMatchSource, LivescoreSource>();
builder.Services.AddSingleton<IMatchSource, BbcSportSource>();
builder.Services.AddSingleton<IMatchSource, WorldFootballSource>();
builder.Services.AddSingleton<IMatchSource, EspnSource>();
builder.Services.AddSingleton<IMatchSource, FotMobSource>();
// Rests a source that has started refusing the scraper; shown on the admin status card.
builder.Services.AddSingleton<SourceGuard>();
// Each competition's own copy of the sources (La Liga's are the ones registered above), and the
// pages loaded from each site in the last hour, which the other competitions stay under.
builder.Services.AddSingleton<CompetitionSources>();
builder.Services.AddSingleton<SiteBudget>();
// What the scraper and the points check are doing, for the Scraper Health page.
builder.Services.AddSingleton<ScraperMonitor>();
builder.Services.AddHostedService<LiveScraperBackgroundService>();
// The scraper only queues matches that changed (MatchUpdateQueue); the processor applies them
// to fixtures as they arrive, and the cleanup job deletes old applied rows.
builder.Services.AddSingleton<MatchUpdateQueue>();
builder.Services.AddScoped<FixtureSync>();
builder.Services.AddHostedService<MatchUpdateProcessor>();
builder.Services.AddHostedService<MatchUpdateCleanupService>();

// ── Prediction Reminders (push notifications, plus Twilio when configured) ───
builder.Services.Configure<TwilioSettings>(builder.Configuration.GetSection("Twilio"));
builder.Services.AddSingleton<IReminderMessageSender, TwilioReminderMessageSender>();
builder.Services.AddSingleton<PushKeyProvider>();
builder.Services.AddHttpClient<PushSender>(c => c.Timeout = TimeSpan.FromSeconds(15));

// ── Email (Brevo, Gmail, Resend) ──────────────────────────────────────────────
// Password links, welcome emails, and reminders/announcements for players without push, through
// Brevo, the Gmail API or Resend (all HTTPS: Render's free tier blocks SMTP). A provider is
// available once its credentials are set (Email__Brevo__ApiKey etc. on Render); the system admin
// picks which one sends on the Settings page, else the first set up (EmailProviderNames.Order).
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddHttpClient<BrevoEmailProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<GmailEmailProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<ResendEmailProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<BrevoEmailProvider>());
builder.Services.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<GmailEmailProvider>());
builder.Services.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<ResendEmailProvider>());
builder.Services.AddScoped<EmailSender>();
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
        // End-of-week marking check. Same SQL as db/supabase/2026-10-12-week-reconciliation.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"MatchWeeks\" ADD COLUMN IF NOT EXISTS \"ReconciledAt\" timestamp with time zone NULL;");
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
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"RemovePasskeysOnPasswordReset\" boolean NOT NULL DEFAULT false;");
        // Prediction rules. Same SQL as db/supabase/2026-10-09-prediction-rules.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"AllowPartialPredictions\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"PredictionsFinal\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"LockWeekAtFirstKickoff\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"AllowLatePredictions\" boolean NOT NULL DEFAULT false;");
        // Source modes. Same SQL as db/supabase/2026-10-10-scraper-source-modes.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"SourceMode\" text NOT NULL DEFAULT 'Single';");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"SourceOrder\" text NOT NULL DEFAULT 'Flashscore';");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"ScraperSettings\" SET \"SourceOrder\" = \"SourceName\" " +
            "WHERE \"SourceMode\" = 'Single' AND \"SourceOrder\" = 'Flashscore' AND \"SourceName\" <> 'Flashscore';");
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

        // Scraped match readings waiting to be applied. Same SQL as db/supabase/2026-10-11-match-updates.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MatchUpdates" (
                "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "FixtureId" character varying(100) NOT NULL,
                "Source" character varying(50) NOT NULL,
                "Competition" character varying(100) NOT NULL,
                "Fingerprint" character varying(64) NOT NULL,
                "Payload" jsonb NOT NULL,
                "ScrapedAt" timestamp with time zone NOT NULL,
                "ProcessedAt" timestamp with time zone NULL,
                "Outcome" character varying(20) NULL,
                "Attempts" integer NOT NULL DEFAULT 0,
                "Error" character varying(500) NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_MatchUpdates_ProcessedAt\" ON \"MatchUpdates\" (\"ProcessedAt\");");

        // Competitions, each with its own weeks and tables. Every week from before this is La Liga's.
        // Same SQL as db/supabase/2026-10-13-competitions.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Competitions" (
                "Id" character varying(50) NOT NULL PRIMARY KEY,
                "Name" character varying(100) NOT NULL,
                "Enabled" boolean NOT NULL,
                "SortOrder" integer NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"MatchWeeks\" ADD COLUMN IF NOT EXISTS \"CompetitionId\" character varying(50) NOT NULL DEFAULT 'la-liga';");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_MatchWeeks_CompetitionId\" ON \"MatchWeeks\" (\"CompetitionId\");");
        // Knockout scores and each competition's own sources. Same SQL as
        // db/supabase/2026-10-13-competition-scraping.sql.
        foreach (var column in new[] { "RegularTimeScoreHome", "RegularTimeScoreAway", "AfterExtraTimeHome", "AfterExtraTimeAway" })
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"Fixtures\" ADD COLUMN IF NOT EXISTS \"{column}\" integer NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Fixtures\" ADD COLUMN IF NOT EXISTS \"WentToExtraTime\" boolean NOT NULL DEFAULT false;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Competitions\" ADD COLUMN IF NOT EXISTS \"SourceMode\" character varying(20) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Competitions\" ADD COLUMN IF NOT EXISTS \"SourceName\" character varying(50) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Competitions\" ADD COLUMN IF NOT EXISTS \"SourceOrder\" character varying(500) NULL;");
        await Competitions.SeedAsync(db);

        // Push notifications, reminders at chosen times, players' notification choices and the
        // admins' sent notifications. Same SQL as db/supabase/2026-10-14-push-reminders.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"ReminderOffsetsHours\" character varying(100) NOT NULL DEFAULT '72,48,3';");
        // A days-only column from before reminders could be hours; never released.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" DROP COLUMN IF EXISTS \"ReminderDaysBeforeFirstGame\";");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"NotifyReminders\" boolean NOT NULL DEFAULT true;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"NotifyAnnouncements\" boolean NOT NULL DEFAULT true;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PushBroadcasts" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Title" character varying(100) NOT NULL,
                "Body" character varying(300) NOT NULL,
                "Url" character varying(200) NOT NULL,
                "Audience" character varying(20) NOT NULL,
                "WeekId" character varying(150) NULL,
                "Important" boolean NOT NULL,
                "Targeted" integer NOT NULL,
                "Reached" integer NOT NULL,
                "SentByUserId" uuid NULL,
                "SentByName" character varying(200) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PushBroadcasts_CreatedAt\" ON \"PushBroadcasts\" (\"CreatedAt\");");

        // Email through Resend. Same SQL as db/supabase/2026-10-15-email.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"NotifyByEmail\" boolean NOT NULL DEFAULT true;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"PushBroadcasts\" ADD COLUMN IF NOT EXISTS \"Emailed\" integer NOT NULL DEFAULT 0;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PasswordTokens" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "TokenHash" character varying(64) NOT NULL,
                "Purpose" character varying(20) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "UsedAt" timestamp with time zone NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PasswordTokens_TokenHash\" ON \"PasswordTokens\" (\"TokenHash\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PasswordTokens_UserId\" ON \"PasswordTokens\" (\"UserId\");");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "EmailLogs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NULL,
                "To" character varying(256) NOT NULL,
                "Kind" character varying(20) NOT NULL,
                "Subject" character varying(200) NOT NULL,
                "Sent" boolean NOT NULL,
                "Error" character varying(500) NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_EmailLogs_CreatedAt\" ON \"EmailLogs\" (\"CreatedAt\");");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"EmailLogs\" ADD COLUMN IF NOT EXISTS \"Provider\" character varying(20) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"EmailProvider\" character varying(20) NOT NULL DEFAULT 'auto';");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"EmailSenderName\" character varying(100) NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"EmailErrorsClearedAt\" timestamp with time zone NULL;");
        // Chat message notifications. Same SQL as db/supabase/2026-10-15-chat-push.sql.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"NotifyMessages\" boolean NOT NULL DEFAULT true;");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"ShowMessagePreview\" boolean NOT NULL DEFAULT true;");

        // Passkeys: signing in with fingerprint or face. Same SQL as db/supabase/2026-10-15-passkeys.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Passkeys" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "CredentialId" bytea NOT NULL,
                "PublicKey" bytea NOT NULL,
                "SignCount" bigint NOT NULL,
                "Name" character varying(60) NOT NULL,
                "Transports" character varying(100) NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "LastUsedAt" timestamp with time zone NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Passkeys_CredentialId\" ON \"Passkeys\" (\"CredentialId\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Passkeys_UserId\" ON \"Passkeys\" (\"UserId\");");

        // Players' in-app notifications. Same SQL as db/supabase/2026-10-15-inbox.sql.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "UserNotifications" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "Kind" character varying(20) NOT NULL,
                "Title" character varying(100) NOT NULL,
                "Body" character varying(300) NOT NULL,
                "Url" character varying(200) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ReadAt" timestamp with time zone NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_UserNotifications_UserId_CreatedAt\" ON \"UserNotifications\" (\"UserId\", \"CreatedAt\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_UserNotifications_CreatedAt\" ON \"UserNotifications\" (\"CreatedAt\");");
        foreach (var column in new[] { "EmailResendFrom", "EmailBrevoSender", "EmailGmailSender" })
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"ScraperSettings\" ADD COLUMN IF NOT EXISTS \"{column}\" character varying(256) NULL;");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PushSubscriptions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
                "Endpoint" character varying(1000) NOT NULL,
                "P256dh" character varying(200) NOT NULL,
                "Auth" character varying(100) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PushSubscriptions_Endpoint\" ON \"PushSubscriptions\" (\"Endpoint\");");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PushSubscriptions_UserId\" ON \"PushSubscriptions\" (\"UserId\");");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PushKeys" (
                "Id" integer NOT NULL PRIMARY KEY,
                "PublicKey" character varying(200) NOT NULL,
                "PrivateKey" character varying(200) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL
            );
            """);

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
