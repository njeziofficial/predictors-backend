-- Prediction rules: incomplete predictions, final predictions, week lock at first kickoff and
-- late predictions. All four start off, which keeps today's behaviour (every open match must be
-- predicted, predictions can be edited until each match kicks off).
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs) and inserts the missing permission row
-- (Services/Permissions.cs, SeedDefaultsAsync) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The rules ───────────────────────────────────────────────────────────────────────────
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "AllowPartialPredictions" boolean NOT NULL DEFAULT false;
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "PredictionsFinal" boolean NOT NULL DEFAULT false;
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "LockWeekAtFirstKickoff" boolean NOT NULL DEFAULT false;
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "AllowLatePredictions" boolean NOT NULL DEFAULT false;


-- ── 2. Who may change them: only the system admin, until granted to other admins ──────────
-- ON CONFLICT DO NOTHING keeps any choice the system admin has already made.
INSERT INTO "RolePermissions" ("Permission", "Allowed", "UpdatedAt") VALUES
    ('predictions.rules', false, now())
ON CONFLICT ("Permission") DO NOTHING;


-- ── 3. Check ───────────────────────────────────────────────────────────────────────────────
SELECT "AllowPartialPredictions", "PredictionsFinal", "LockWeekAtFirstKickoff", "AllowLatePredictions"
FROM "ScraperSettings";
SELECT "Permission", "Allowed" FROM "RolePermissions" WHERE "Permission" = 'predictions.rules';


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "AllowPartialPredictions";
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "PredictionsFinal";
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "LockWeekAtFirstKickoff";
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "AllowLatePredictions";
-- DELETE FROM "UserPermissions" WHERE "Permission" = 'predictions.rules';
-- DELETE FROM "RolePermissions" WHERE "Permission" = 'predictions.rules';
