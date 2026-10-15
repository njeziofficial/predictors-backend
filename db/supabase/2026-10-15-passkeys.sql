-- Passkeys: players can sign in with their phone's fingerprint, face or screen lock (WebAuthn).
-- Only the public key is stored. The backend runs the same SQL on startup (Program.cs); this
-- script is for applying it by hand.
CREATE TABLE IF NOT EXISTS "Passkeys" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "CredentialId" bytea NOT NULL,
    "PublicKey" bytea NOT NULL,
    "SignCount" bigint NOT NULL,
    "Name" character varying(60) NOT NULL,
    "Transports" character varying(100) NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastUsedAt" timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Passkeys_CredentialId" ON "Passkeys" ("CredentialId");
CREATE INDEX IF NOT EXISTS "IX_Passkeys_UserId" ON "Passkeys" ("UserId");

-- The system admin's choice: whether a password reset also removes the account's passkeys.
ALTER TABLE "ScraperSettings" ADD COLUMN IF NOT EXISTS "RemovePasskeysOnPasswordReset" boolean NOT NULL DEFAULT false;
