-- One-off: records the ₦3,000 entry fee for 2026/27 that every active player paid before the
-- wallet ledger existed, except the system admin and the test account (njezichigozie@yahoo.com),
-- who didn't pay. Run it in the Supabase SQL editor AFTER deploying the backend (which creates
-- the "WalletEntries" table). Safe to run twice: anyone already marked as paid is skipped.

-- 1. Check who it will mark as paid.
SELECT u."Name", u."Email"
FROM "Users" u
WHERE NOT u."IsSystemUser"
  AND NOT u."IsDisabled"
  AND lower(u."Email") <> 'njezichigozie@yahoo.com'
  AND NOT EXISTS (
      SELECT 1 FROM "WalletEntries" e
      WHERE e."UserId" = u."Id" AND e."Season" = '2026/27' AND e."Kind" = 'entry_fee')
ORDER BY u."Name";

-- 2. Record it, as entered by the system admin.
INSERT INTO "WalletEntries"
    ("Id", "UserId", "Season", "Kind", "AmountKobo", "Note", "OccurredAt", "CreatedAt", "CreatedByUserId", "CreatedByName", "ReversesEntryId")
SELECT gen_random_uuid(), u."Id", '2026/27', 'entry_fee', 300000, 'Paid before the wallet ledger', now(), now(),
       sa."Id", sa."Name", NULL
FROM "Users" u
CROSS JOIN (SELECT "Id", "Name" FROM "Users" WHERE "IsSystemUser" LIMIT 1) sa
WHERE NOT u."IsSystemUser"
  AND NOT u."IsDisabled"
  AND lower(u."Email") <> 'njezichigozie@yahoo.com'
  AND NOT EXISTS (
      SELECT 1 FROM "WalletEntries" e
      WHERE e."UserId" = u."Id" AND e."Season" = '2026/27' AND e."Kind" = 'entry_fee');

-- 3. The pot: players paid and the total.
SELECT count(*) AS players_paid, sum("AmountKobo") / 100 AS pot_naira
FROM "WalletEntries"
WHERE "Season" = '2026/27' AND "Kind" = 'entry_fee';
