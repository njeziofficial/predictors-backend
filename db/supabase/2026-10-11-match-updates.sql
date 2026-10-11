-- Match update queue: the scraper writes a row only when a match reads differently from the last
-- poll (a fingerprint of its status, score, minute, kickoff and round), and a background job
-- applies the rows to "Fixtures". Applied rows are deleted after LiveScoreScraper:UpdateRetentionHours
-- (48 by default).
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The table ───────────────────────────────────────────────────────────────────────────
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
-- Finds the rows still waiting (ProcessedAt IS NULL) and the old ones to delete.
CREATE INDEX IF NOT EXISTS "IX_MatchUpdates_ProcessedAt" ON "MatchUpdates" ("ProcessedAt");


-- ── 2. Check ───────────────────────────────────────────────────────────────────────────────
-- What came in over the last hour, newest first.
SELECT "Id", "FixtureId", "Source", "ScrapedAt", "ProcessedAt", "Outcome", "Attempts", "Error"
FROM "MatchUpdates" WHERE "ScrapedAt" > now() - interval '1 hour' ORDER BY "Id" DESC LIMIT 50;


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first, then:
-- DROP TABLE IF EXISTS "MatchUpdates";
