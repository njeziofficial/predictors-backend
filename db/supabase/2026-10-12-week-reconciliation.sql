-- End-of-week marking check and the admin Marking page.
--   * "MatchWeeks"."ReconciledAt": when a week was signed off, once every match was over and every
--     stored point agreed with the scoring rule (Services/WeekReconciliation.cs).
--   * The "marking.view" permission ("Check marking"), on for admins by default.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs, Permissions.SeedDefaultsAsync) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The column and the permission ───────────────────────────────────────────────────────
ALTER TABLE "MatchWeeks" ADD COLUMN IF NOT EXISTS "ReconciledAt" timestamp with time zone NULL;

INSERT INTO "RolePermissions" ("Permission", "Allowed", "UpdatedAt", "UpdatedByUserId")
VALUES ('marking.view', true, now(), NULL)
ON CONFLICT ("Permission") DO NOTHING;


-- ── 2. Check ───────────────────────────────────────────────────────────────────────────────
SELECT "Id", "Name", "ReconciledAt" FROM "MatchWeeks" ORDER BY "Id";
SELECT "Permission", "Allowed" FROM "RolePermissions" WHERE "Permission" = 'marking.view';


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- ALTER TABLE "MatchWeeks" DROP COLUMN IF EXISTS "ReconciledAt";
-- DELETE FROM "RolePermissions" WHERE "Permission" = 'marking.view';
-- DELETE FROM "UserPermissions" WHERE "Permission" = 'marking.view';
