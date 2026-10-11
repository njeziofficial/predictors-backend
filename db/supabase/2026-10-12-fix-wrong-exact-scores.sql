-- Corrects points given for wrong exact-score picks.
--
-- Until this fix, ScoringService gave a correct-score pick that got the score wrong the points
-- for the result it implied (a wrong "2-1" on a 1-0 home win earned 2). A correct-score pick is
-- all or nothing: 10 for the exact score, otherwise 0. This sets those picks to 0 and records
-- each change in the audit trail, as the points reconciler does.
--
-- RUN ONLY ONCE THE FIXED BACKEND IS THE ONLY ONE RUNNING. A backend with the old scoring
-- re-marks every prediction every 15 minutes (PointsReconciler) and would put the wrong points
-- back. The fixed backend makes these same corrections by itself within a minute of starting,
-- so this script is only needed to correct the database before that deploy.
--
-- Safe to run more than once: a second run finds nothing to change.
--
-- Run in Supabase: Dashboard → SQL Editor → New query → paste this file → Run.

BEGIN;

-- ── 1. What will change ────────────────────────────────────────────────────────────────────
CREATE TEMP TABLE wrong_exact ON COMMIT DROP AS
SELECT p."Id", p."UserId", u."Name", p."PointsEarned", p."HomeGoals", p."AwayGoals",
       f."HomeTeam", f."AwayTeam", f."FinalScoreHome", f."FinalScoreAway"
FROM "Predictions" p
JOIN "Fixtures" f ON f."Id" = p."FixtureId"
JOIN "Users" u ON u."Id" = p."UserId"
WHERE p."Outcome" = 3                -- CorrectScore
  AND p."PointsEarned" > 0
  AND f."Status" = 3                 -- Ended
  AND NOT (p."HomeGoals" = f."FinalScoreHome" AND p."AwayGoals" = f."FinalScoreAway");

SELECT "Name", "HomeTeam" || ' v ' || "AwayTeam" AS match,
       "FinalScoreHome" || '-' || "FinalScoreAway" AS result,
       "HomeGoals" || '-' || "AwayGoals" AS picked, "PointsEarned" AS was, 0 AS now
FROM wrong_exact ORDER BY "Name", match;


-- ── 2. Audit trail, then the fix ───────────────────────────────────────────────────────────
INSERT INTO "AuditLogs" ("Id", "UserId", "UserName", "ActorUserId", "ActorName", "Action",
                         "Field", "PreviousValue", "NewValue", "Details", "CreatedAt")
SELECT gen_random_uuid(), "UserId", "Name", NULL, NULL, 'PointsCorrected',
       left("HomeTeam" || ' v ' || "AwayTeam", 50), "PointsEarned"::text, '0',
       'Predicted score ' || "HomeGoals" || '-' || "AwayGoals" || '; match ended '
         || "FinalScoreHome" || '-' || "FinalScoreAway" || '; a wrong exact score earns 0', now()
FROM wrong_exact;

UPDATE "Predictions" SET "PointsEarned" = 0 WHERE "Id" IN (SELECT "Id" FROM wrong_exact);

COMMIT;


-- ── 3. Check: week totals per player ───────────────────────────────────────────────────────
SELECT u."Name", p."WeekId", sum(p."PointsEarned") AS points
FROM "Predictions" p JOIN "Users" u ON u."Id" = p."UserId"
GROUP BY u."Name", p."WeekId" HAVING sum(p."PointsEarned") > 0
ORDER BY p."WeekId", points DESC;
