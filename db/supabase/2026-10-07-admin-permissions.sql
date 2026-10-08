-- Back-office permissions for admins.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same CREATE TABLE IF NOT EXISTS statements (Program.cs) and inserts any missing
-- default rows (Services/Permissions.cs, SeedDefaultsAsync) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.
-- Steps 1–3 are the change itself. Step 4 checks it. Step 5 is optional hardening (read it first).


-- ── 1. What every admin may do by default ─────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS "RolePermissions" (
    "Permission" character varying(64) NOT NULL PRIMARY KEY,
    "Allowed" boolean NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "UpdatedByUserId" uuid NULL
);


-- ── 2. Per-admin exceptions to those defaults (no row = follow the default) ────────────────
CREATE TABLE IF NOT EXISTS "UserPermissions" (
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Permission" character varying(64) NOT NULL,
    "Allowed" boolean NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "UpdatedByUserId" uuid NULL,
    PRIMARY KEY ("UserId", "Permission")
);


-- ── 3. Default permissions (match what ordinary admins could do before) ───────────────────
-- ON CONFLICT DO NOTHING keeps any choice the system admin has already made.
INSERT INTO "RolePermissions" ("Permission", "Allowed", "UpdatedAt") VALUES
    ('fixtures.manage',        true,  now()),
    ('participation.view',     true,  now()),
    ('users.view',             true,  now()),
    ('users.create',           false, now()),
    ('users.edit',             false, now()),
    ('users.status',           true,  now()),
    ('users.reset_password',   true,  now()),
    ('users.delete',           true,  now()),
    ('previous_points.view',   true,  now()),
    ('previous_points.manage', false, now()),
    ('audit.view',             true,  now()),
    ('settings.view',          true,  now()),
    ('settings.manage',        true,  now())
ON CONFLICT ("Permission") DO NOTHING;


-- ── 4. Check ───────────────────────────────────────────────────────────────────────────────
-- Expect 13 rows here, and 0 rows from the second query until you set an exception.
SELECT "Permission", "Allowed" FROM "RolePermissions" ORDER BY "Permission";
SELECT u."Email", p."Permission", p."Allowed"
FROM "UserPermissions" p JOIN "Users" u ON u."Id" = p."UserId"
ORDER BY u."Email", p."Permission";


-- ── 5. OPTIONAL hardening: close Supabase's public REST API to these tables ────────────────
-- Today every table in "public" has row level security off and the "anon" role can SELECT,
-- so anyone holding the project URL and anon key can read Users (emails, phones, password
-- hashes) through https://<project>.supabase.co/rest/v1/. The backend connects as the table
-- owner ("postgres"), which bypasses RLS, so switching RLS on with no policies blocks the public
-- API without affecting the app. Uncomment to apply; turn one back off with
-- ALTER TABLE "<name>" DISABLE ROW LEVEL SECURITY;
--
-- ALTER TABLE "RolePermissions" ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "UserPermissions" ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "Users"           ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "RefreshTokens"   ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "AuditLogs"       ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "Predictions"     ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "PreviousPoints"  ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "PlayerAliases"   ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "MatchWeeks"      ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "Fixtures"        ENABLE ROW LEVEL SECURITY;
-- ALTER TABLE "ScraperSettings" ENABLE ROW LEVEL SECURITY;


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- DROP TABLE IF EXISTS "UserPermissions";
-- DROP TABLE IF EXISTS "RolePermissions";
