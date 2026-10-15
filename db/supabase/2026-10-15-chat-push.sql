-- Chat messages and announcements pushed to the phone while the app isn't in front, and whether
-- the notification shows the message itself. The backend runs the same SQL on startup
-- (Program.cs); this script is for applying it by hand.
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "NotifyMessages" boolean NOT NULL DEFAULT true;
ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "ShowMessagePreview" boolean NOT NULL DEFAULT true;
