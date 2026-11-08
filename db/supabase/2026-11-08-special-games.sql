-- Special games: a pot on a match, with players predicting its exact score on their own tab,
-- separately from their normal predictions. Everyone who gets it right shares the pot. Prizes go into the winners' wallets as
-- adjustments; marking one as paid records a payout.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.

ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "SpecialGamesEnabled" boolean NOT NULL DEFAULT true;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "SpecialPotKobo" bigint NULL;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "SpecialProblem" character varying(500) NULL;
CREATE TABLE IF NOT EXISTS "SpecialGamePrizes" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "FixtureId" character varying(100) NOT NULL REFERENCES "Fixtures" ("Id") ON DELETE CASCADE,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE RESTRICT,
    "AmountKobo" bigint NOT NULL,
    "EntryId" uuid NOT NULL,
    "PayoutEntryId" uuid NULL,
    "PaidAt" timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_SpecialGamePrizes_FixtureId_UserId" ON "SpecialGamePrizes" ("FixtureId", "UserId");
CREATE TABLE IF NOT EXISTS "SpecialGamePicks" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "FixtureId" character varying(100) NOT NULL REFERENCES "Fixtures" ("Id") ON DELETE CASCADE,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "HomeGoals" integer NOT NULL,
    "AwayGoals" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_SpecialGamePicks_FixtureId_UserId" ON "SpecialGamePicks" ("FixtureId", "UserId");


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first. Prizes already recorded stay in the wallet ledger. Then:
-- DROP TABLE IF EXISTS "SpecialGamePrizes";
-- DROP TABLE IF EXISTS "SpecialGamePicks";
-- ALTER TABLE "Fixtures" DROP COLUMN IF EXISTS "SpecialProblem";
-- ALTER TABLE "Fixtures" DROP COLUMN IF EXISTS "SpecialPotKobo";
-- ALTER TABLE "ScraperSettings" DROP COLUMN IF EXISTS "SpecialGamesEnabled";
