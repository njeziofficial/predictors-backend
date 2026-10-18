-- Idle sign-out: after this many minutes without activity the app signs the user out. Starts at
-- 15 minutes. Only the system admin may change it, until granted to other admins on Access control.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statement (Program.cs) and inserts the missing permission row
-- (Services/Permissions.cs, SeedDefaultsAsync) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The setting ─────────────────────────────────────────────────────────────────────────
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "IdleTimeoutMinutes" integer NOT NULL DEFAULT 15;


-- ── 2. Who may change it: only the system admin, until granted to other admins ────────────
-- ON CONFLICT DO NOTHING keeps any choice the system admin has already made.
INSERT INTO "RolePermissions" ("Permission", "Allowed", "UpdatedAt") VALUES
    ('security.idle_timeout', false, now())
ON CONFLICT ("Permission") DO NOTHING;


-- ── 3. Check ───────────────────────────────────────────────────────────────────────────────
SELECT "IdleTimeoutMinutes" FROM "ScraperSettings";
SELECT "Permission", "Allowed" FROM "RolePermissions" WHERE "Permission" = 'security.idle_timeout';


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "IdleTimeoutMinutes";
-- DELETE FROM "UserPermissions" WHERE "Permission" = 'security.idle_timeout';
-- DELETE FROM "RolePermissions" WHERE "Permission" = 'security.idle_timeout';
