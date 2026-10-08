-- In-app chat: one-to-one conversations, their participants (with read state) and messages.
--
-- Safe to run more than once, and safe to run before or after deploying the backend: the API
-- runs the same CREATE TABLE IF NOT EXISTS statements (Program.cs) every time it starts. The
-- "Send announcements" permission is inserted with its default by Permissions.SeedDefaultsAsync.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.


-- ── 1. Conversations (DirectKey = "<smaller user id>:<larger user id>") ────────────────────
CREATE TABLE IF NOT EXISTS "Conversations" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "DirectKey" character varying(80) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastMessageAt" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Conversations_DirectKey" ON "Conversations" ("DirectKey");


-- ── 2. Who is in each conversation, and how far they've read ───────────────────────────────
CREATE TABLE IF NOT EXISTS "ConversationParticipants" (
    "ConversationId" uuid NOT NULL REFERENCES "Conversations" ("Id") ON DELETE CASCADE,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "JoinedAt" timestamp with time zone NOT NULL,
    "LastReadAt" timestamp with time zone NULL,
    PRIMARY KEY ("ConversationId", "UserId")
);
CREATE INDEX IF NOT EXISTS "IX_ConversationParticipants_UserId" ON "ConversationParticipants" ("UserId");


-- ── 3. Messages (soft-deleted: DeletedAt set, Body cleared) ────────────────────────────────
CREATE TABLE IF NOT EXISTS "ChatMessages" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ConversationId" uuid NOT NULL REFERENCES "Conversations" ("Id") ON DELETE CASCADE,
    "SenderId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Body" character varying(2000) NOT NULL,
    "IsAnnouncement" boolean NOT NULL DEFAULT false,
    "ClientId" character varying(64) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "DeletedAt" timestamp with time zone NULL
);
CREATE INDEX IF NOT EXISTS "IX_ChatMessages_ConversationId_CreatedAt" ON "ChatMessages" ("ConversationId", "CreatedAt");
-- Makes a retried send idempotent. NULL ClientIds don't collide in Postgres.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatMessages_SenderId_ClientId" ON "ChatMessages" ("SenderId", "ClientId");


-- ── 4. Check ──────────────────────────────────────────────────────────────────────────────
SELECT table_name FROM information_schema.tables
WHERE table_name IN ('Conversations', 'ConversationParticipants', 'ChatMessages');
