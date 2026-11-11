/* READ ONLY: no UPDATE/DELETE/DDL, no database selection, no cleanup.
   Select the intended database explicitly. NULL @RunId inspects all runs.
   Historical snapshots are authoritative. Current job definitions and cap=69
   below are a WHAT-IF only: cap, keyword rules and job definitions were not
   independently snapshotted. Never use this output to overwrite history.
*/
SET NOCOUNT ON;
DECLARE @RunId int = NULL;
DECLARE @PreviewCap decimal(5,2) = 69.00;
SELECT DB_NAME() AS SelectedDatabase, @RunId AS SelectedRunId,
       @PreviewCap AS CurrentCapForWhatIfOnly;

-- 1. Actual stored weights, including legitimate non-default experiments.
SELECT r.RunId, r.RunType, r.Status, p.VersionName, r.EvaluationSetId,
       r.ModelName, r.Temperature, r.KeywordWeight, r.AiWeight,
       r.PassThreshold, r.AccuracyPercent, r.CreatedAt, r.CompletedAt,
       CASE WHEN r.KeywordWeight < 0 OR r.AiWeight < 0 OR r.KeywordWeight + r.AiWeight <> 1
            THEN N'Invalid weights'
            WHEN r.KeywordWeight = 0.30 AND r.AiWeight = 0.70 THEN N'Default 30/70 snapshot'
            ELSE N'Valid alternative snapshot; provenance requires review' END AS WeightFinding
FROM dbo.EvaluationRuns r
JOIN dbo.PromptVersions p ON p.PromptVersionId = r.PromptVersionId
WHERE @RunId IS NULL OR r.RunId = @RunId
ORDER BY r.RunId;

-- 2. Arithmetic and pass consistency. A reduced score can be a valid historical cap.
SELECT e.EvaluationId, e.RunId, e.ApplicationId, c.CvId, c.OriginalFileName,
       c.FileHash, c.UploadedAt,
       e.Status, e.ScoreSource, e.KeywordScore, e.AiScore, e.FinalScore,
       e.PredictedPass, r.PassThreshold, x.UncappedSnapshotScore,
       CASE WHEN e.Status <> N'Completed' THEN N'Unscored; no arithmetic conclusion'
            WHEN e.FinalScore IS NULL OR x.UncappedSnapshotScore IS NULL THEN N'Incomplete score fields: investigate'
            WHEN e.FinalScore < x.UncappedSnapshotScore THEN N'Possible historical cap; metadata unavailable'
            WHEN e.FinalScore <> x.UncappedSnapshotScore THEN N'Arithmetic differs: investigate'
            ELSE N'Matches stored weights before cap' END AS ArithmeticFinding,
       CASE WHEN e.Status = N'Completed' AND
            (e.FinalScore IS NULL OR e.PredictedPass IS NULL OR
             e.PredictedPass <> CASE WHEN e.FinalScore >= r.PassThreshold THEN 1 ELSE 0 END)
            THEN N'FinalScore/PredictedPass mismatch' ELSE N'Consistent or unscored' END AS PassFinding,
       s.MissingMustHaveByCurrentJob, s.SavedSkills, s.CurrentJobSkills,
       CASE WHEN s.SavedSkills = s.CurrentJobSkills AND s.SavedSkills > 0
            THEN CASE WHEN s.MissingMustHaveByCurrentJob > 0 AND x.UncappedSnapshotScore > @PreviewCap
                      THEN @PreviewCap ELSE x.UncappedSnapshotScore END END AS WhatIfCapOnFrozenScores,
       N'WHAT-IF ONLY; not a new Keyword/Gemini evaluation; current criteria may differ from history' AS PreviewScope
FROM dbo.Evaluations e
JOIN dbo.EvaluationRuns r ON r.RunId = e.RunId
JOIN dbo.Applications a ON a.ApplicationId = e.ApplicationId
JOIN dbo.Cvs c ON c.CvId = a.CvId
CROSS APPLY (SELECT CASE WHEN e.ScoreSource = N'Combined'
                        THEN ROUND(r.KeywordWeight * e.KeywordScore + r.AiWeight * e.AiScore, 2)
                        WHEN e.ScoreSource = N'KeywordOnly' THEN e.KeywordScore END AS UncappedSnapshotScore) x
CROSS APPLY (SELECT
    (SELECT COUNT(*) FROM dbo.EvaluationSkills es
     JOIN dbo.JobSkills js ON js.JobId = es.JobId AND js.SkillCode = es.SkillCode
     WHERE es.EvaluationId = e.EvaluationId AND js.JobId = a.JobId
       AND js.SkillType = N'MustHave' AND es.IsMatched = 0) AS MissingMustHaveByCurrentJob,
    (SELECT COUNT(*) FROM dbo.EvaluationSkills es WHERE es.EvaluationId = e.EvaluationId) AS SavedSkills,
    (SELECT COUNT(*) FROM dbo.JobSkills js WHERE js.JobId = a.JobId) AS CurrentJobSkills) s
WHERE @RunId IS NULL OR e.RunId = @RunId
ORDER BY e.RunId, e.ApplicationId;

-- 3. A16 independently calculated from stored predictions and CURRENT HR labels.
WITH Counts AS (
    SELECT r.RunId, COUNT(*) AS TotalLabeled,
           SUM(CASE WHEN e.Status = N'Completed' AND e.PredictedPass = h.IsPass THEN 1 ELSE 0 END) AS Correct,
           SUM(CASE WHEN e.Status = N'Completed' AND h.IsPass = 1 AND e.PredictedPass = 0 THEN 1 ELSE 0 END) AS Missed,
           SUM(CASE WHEN e.Status = N'Completed' AND h.IsPass = 0 AND e.PredictedPass = 1 THEN 1 ELSE 0 END) AS WrongPick,
           SUM(CASE WHEN e.Status <> N'Completed' OR e.PredictedPass IS NULL THEN 1 ELSE 0 END) AS Unscored
    FROM dbo.EvaluationRuns r
    JOIN dbo.Evaluations e ON e.RunId = r.RunId
    JOIN dbo.HrLabels h ON h.EvaluationSetId = r.EvaluationSetId AND h.ApplicationId = e.ApplicationId
    WHERE r.RunType = N'PromptTest' AND (@RunId IS NULL OR r.RunId = @RunId)
    GROUP BY r.RunId
)
SELECT r.RunId, r.Status, COALESCE(c.TotalLabeled, 0) AS TotalLabeled,
       COALESCE(c.Correct, 0) AS Correct, COALESCE(c.Missed, 0) AS Missed,
       COALESCE(c.WrongPick, 0) AS WrongPick, COALESCE(c.Unscored, 0) AS Unscored,
       r.AccuracyPercent AS StoredAccuracyPercent,
       CASE WHEN r.Status = N'Completed' THEN
            CAST(ROUND(100.0 * c.Correct / NULLIF(c.TotalLabeled, 0), 2) AS decimal(5,2)) END AS CalculatedAccuracyPercent
FROM dbo.EvaluationRuns r LEFT JOIN Counts c ON c.RunId = r.RunId
WHERE r.RunType = N'PromptTest' AND (@RunId IS NULL OR r.RunId = @RunId)
ORDER BY r.RunId;
