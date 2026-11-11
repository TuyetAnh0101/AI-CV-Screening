/* RETIRED: READ ONLY. Select the intended database explicitly in your client.
   The former script rewrote historical weights, scores and accuracy. A valid
   40/60 historical run must retain its actual snapshot; 30/70 is the default
   for NEW runs, not an instruction to rescore history.

   Kept at the old path so an existing command is harmless. This file performs
   no updates. Use database/check_scoring_consistency.sql for the full audit.
   A repair requires evidence of incorrect metadata, exact verified row scope,
   a reviewed transaction and rollback plan, and approval for user data.
   No repair is supplied because the inspected SmartCvDb has no 40/60 runs.
*/
SET NOCOUNT ON;
SELECT DB_NAME() AS SelectedDatabase,
       N'READ ONLY: legacy seed weight updater retired; historical snapshots preserved.' AS Notice;

SELECT r.RunId, r.RunType, r.Status, r.ModelName, r.Temperature,
       r.KeywordWeight, r.AiWeight, r.PassThreshold, r.AccuracyPercent,
       CASE WHEN r.KeywordWeight < 0 OR r.AiWeight < 0 OR r.KeywordWeight + r.AiWeight <> 1
            THEN N'Invalid weights: investigate'
            WHEN r.KeywordWeight = 0.30 AND r.AiWeight = 0.70 THEN N'Default 30/70 snapshot'
            ELSE N'Alternative historical/experimental weights: retain and investigate provenance'
       END AS Finding
FROM dbo.EvaluationRuns r
ORDER BY r.RunId;
