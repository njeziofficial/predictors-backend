-- Players' in-app notifications: every prediction reminder and admin notification also lands
-- here, for the app to show on the player's next visit. The backend runs the same SQL on startup
-- (Program.cs); this script is for applying it by hand.
CREATE TABLE IF NOT EXISTS "UserNotifications" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "Kind" character varying(20) NOT NULL,
    "Title" character varying(100) NOT NULL,
    "Body" character varying(300) NOT NULL,
    "Url" character varying(200) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ReadAt" timestamp with time zone NULL
);
CREATE INDEX IF NOT EXISTS "IX_UserNotifications_UserId_CreatedAt" ON "UserNotifications" ("UserId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_UserNotifications_CreatedAt" ON "UserNotifications" ("CreatedAt");
