-- Email (Resend, Brevo or the Gmail API): single-use password links (Forgot password and welcome), a log of every
-- email (kept inside the free tier's limits), players' choice to get notifications by email, and
-- how many each admin notification emailed. The backend runs the same SQL on startup
-- (Program.cs); this script is for applying it by hand.
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "NotifyByEmail" boolean NOT NULL DEFAULT true;
ALTER TABLE "PushBroadcasts" ADD COLUMN IF NOT EXISTS "Emailed" integer NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS "PasswordTokens" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "TokenHash" character varying(64) NOT NULL,
    "Purpose" character varying(20) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "UsedAt" timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_PasswordTokens_TokenHash" ON "PasswordTokens" ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_PasswordTokens_UserId" ON "PasswordTokens" ("UserId");

CREATE TABLE IF NOT EXISTS "EmailLogs" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NULL,
    "To" character varying(256) NOT NULL,
    "Kind" character varying(20) NOT NULL,
    "Subject" character varying(200) NOT NULL,
    "Sent" boolean NOT NULL,
    "Error" character varying(500) NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_EmailLogs_CreatedAt" ON "EmailLogs" ("CreatedAt");

-- Which provider sent each email (each has its own limits), and which one the system admin chose
-- (Resend, Brevo, Gmail, or auto for the first set up).
ALTER TABLE "EmailLogs" ADD COLUMN IF NOT EXISTS "Provider" character varying(20) NULL;
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "EmailProvider" character varying(20) NOT NULL DEFAULT 'auto';
