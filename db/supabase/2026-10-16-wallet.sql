-- The wallet ledger: entry fees paid into the pot, winnings under the sharing formula, and
-- payouts to players' bank accounts. No money moves through the app; this records it. Entries
-- are never changed or deleted: a mistake is reversed by an opposite entry (ReversesEntryId).
-- The backend runs the same SQL on startup (Program.cs); this script is for applying it by hand.
CREATE TABLE IF NOT EXISTS "WalletEntries" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE RESTRICT,
    "Season" character varying(16) NOT NULL,
    "Kind" character varying(20) NOT NULL,
    "AmountKobo" bigint NOT NULL,
    "Note" character varying(300) NULL,
    "OccurredAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "CreatedByUserId" uuid NULL,
    "CreatedByName" character varying(200) NOT NULL,
    "ReversesEntryId" uuid NULL REFERENCES "WalletEntries" ("Id")
);
CREATE INDEX IF NOT EXISTS "IX_WalletEntries_Season_UserId" ON "WalletEntries" ("Season", "UserId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WalletEntries_ReversesEntryId" ON "WalletEntries" ("ReversesEntryId");
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "WalletSeason" character varying(16) NOT NULL DEFAULT '2026/27';
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "WalletEntryFeeKobo" bigint NOT NULL DEFAULT 300000;
