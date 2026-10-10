-- Scraper source modes: use one source (Single), fall back through a list (Fallback), or rotate
-- through a list (Rotate). Starts as Single on whatever source is already chosen, which keeps
-- today's behaviour. Only the system admin can change these.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The settings ────────────────────────────────────────────────────────────────────────
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "SourceMode" text NOT NULL DEFAULT 'Single';
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "SourceOrder" text NOT NULL DEFAULT 'Flashscore';
-- Start the Fallback/Rotate list on the source already in use, not the column default. Only
-- touches a row still on Single with that default, so a list chosen since is left alone.
UPDATE "ScraperSettings" SET "SourceOrder" = "SourceName"
WHERE "SourceMode" = 'Single' AND "SourceOrder" = 'Flashscore' AND "SourceName" <> 'Flashscore';


-- ── 2. Check ───────────────────────────────────────────────────────────────────────────────
SELECT "SourceName", "SourceMode", "SourceOrder" FROM "ScraperSettings";


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "SourceMode";
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "SourceOrder";
