-- Competitions, each with its own weeks and tables (week and overall). Every week from before
-- this is La Liga's. The backend runs the same SQL on startup (Program.cs) and seeds the rows
-- from Services/Competitions.cs; this script is for applying it by hand.
CREATE TABLE IF NOT EXISTS "Competitions" (
    "Id" character varying(50) NOT NULL PRIMARY KEY,
    "Name" character varying(100) NOT NULL,
    "Enabled" boolean NOT NULL,
    "SortOrder" integer NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL
);

ALTER TABLE "MatchWeeks" ADD COLUMN IF NOT EXISTS "CompetitionId" character varying(50) NOT NULL DEFAULT 'la-liga';
CREATE INDEX IF NOT EXISTS "IX_MatchWeeks_CompetitionId" ON "MatchWeeks" ("CompetitionId");

INSERT INTO "Competitions" ("Id", "Name", "Enabled", "SortOrder", "UpdatedAt") VALUES
    ('la-liga', 'La Liga', true, 0, now()),
    ('champions-league', 'Champions League', false, 1, now())
ON CONFLICT ("Id") DO NOTHING;
