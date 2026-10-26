-- Ballon d'Or side game: players rank their top 5 nominees before the ceremony. The organiser's
-- prizes (one for calling the winner, one for the top 5 in order) are paid into the winners'
-- wallets as adjustments when an admin enters the result.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same statements (Program.cs) every time it starts.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. The event (one a year) and the players' picks ──────────────────────────────────────
CREATE TABLE IF NOT EXISTS "BallonDorEvents" (
    "Year" integer NOT NULL PRIMARY KEY,
    "Nominees" text[] NOT NULL,
    "LocksAt" timestamp with time zone NOT NULL,
    "Result" text[] NULL,
    "WinnerPotKobo" bigint NOT NULL,
    "TopFivePotKobo" bigint NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);
CREATE TABLE IF NOT EXISTS "BallonDorPicks" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Year" integer NOT NULL,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Picks" text[] NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "WinnerPrizeEntryId" uuid NULL,
    "TopFivePrizeEntryId" uuid NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_BallonDorPicks_Year_UserId" ON "BallonDorPicks" ("Year", "UserId");

-- Off hides the game from players (the system user's switch in the back office).
ALTER TABLE "BallonDorEvents" ADD COLUMN IF NOT EXISTS "Enabled" boolean NOT NULL DEFAULT true;


-- ── 2. Check ───────────────────────────────────────────────────────────────────────────────
SELECT * FROM "BallonDorEvents";
SELECT count(*) AS picks FROM "BallonDorPicks";


-- ── Rollback (only if you need to undo this change) ──────────────────────────────────────
-- Deploy the previous backend first. Prizes already paid stay in the wallet ledger. Then:
-- DROP TABLE IF EXISTS "BallonDorPicks";
-- DROP TABLE IF EXISTS "BallonDorEvents";
