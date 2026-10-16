-- Images sent in chat. The file itself is kept in Supabase Storage (bucket "chat-images") under "ImageKey"; the width and
-- height let the app hold the image's space before it loads. The backend runs the same SQL on
-- startup (Program.cs); this script is for applying it by hand.
ALTER TABLE "ChatMessages" ADD COLUMN IF NOT EXISTS "ImageKey" character varying(200) NULL;
ALTER TABLE "ChatMessages" ADD COLUMN IF NOT EXISTS "ImageWidth" integer NULL;
ALTER TABLE "ChatMessages" ADD COLUMN IF NOT EXISTS "ImageHeight" integer NULL;
