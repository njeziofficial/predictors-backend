-- Scraping competitions other than La Liga. The backend runs the same SQL on startup
-- (Program.cs); this script is for applying it by hand.
--   * Knockout matches are marked on the score after 90 minutes. "Fixtures" keeps the last score
--     seen in normal time, whether the match went to extra time, and the score after it.

ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "RegularTimeScoreHome" integer NULL;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "RegularTimeScoreAway" integer NULL;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "AfterExtraTimeHome" integer NULL;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "AfterExtraTimeAway" integer NULL;
ALTER TABLE "Fixtures" ADD COLUMN IF NOT EXISTS "WentToExtraTime" boolean NOT NULL DEFAULT false;

--   * Each competition other than La Liga picks its own sources (the system admin's choice;
--     null means the default, FotMob then Flashscore). La Liga keeps "ScraperSettings".
ALTER TABLE "Competitions" ADD COLUMN IF NOT EXISTS "SourceMode" character varying(20) NULL;
ALTER TABLE "Competitions" ADD COLUMN IF NOT EXISTS "SourceName" character varying(50) NULL;
ALTER TABLE "Competitions" ADD COLUMN IF NOT EXISTS "SourceOrder" character varying(500) NULL;
