/* A12 - READ ONLY. Select the TEST database explicitly in your SQL client first.
   No USE, no demo RunId exclusions, no data preparation or rescoring.
   NULL @RunId examines every run in the selected database.
   Result sets 2 and 4 contain candidates for investigation, not proof of corruption:
   cap, job criteria and job skills are not independently snapshotted. */
DECLARE @RunId int = NULL;
SELECT DB_NAME() AS SelectedDatabase;

-- 1. Run snapshot, progress and actual stored counts.
SELECT r.RunId, r.PromptVersionId, p.VersionName, r.ModelName, r.Temperature,
       r.KeywordWeight, r.AiWeight, r.PassThreshold, r.Status,
       r.TotalItems, r.ProcessedItems, r.FailedItems, r.CompletedAt,
       COUNT(e.EvaluationId) AS ActualItems,
       SUM(CASE WHEN e.Status = N'Pending' THEN 1 ELSE 0 END) AS PendingItems,
       SUM(CASE WHEN e.Status = N'Failed' THEN 1 ELSE 0 END) AS ActualFailed,
       SUM(CASE WHEN e.Status = N'Completed' AND e.ScoreSource = N'Combined' THEN 1 ELSE 0 END) AS Combined,
       SUM(CASE WHEN e.Status = N'Completed' AND e.ScoreSource = N'KeywordOnly' THEN 1 ELSE 0 END) AS KeywordOnly
FROM EvaluationRuns r
LEFT JOIN PromptVersions p ON p.PromptVersionId = r.PromptVersionId
LEFT JOIN Evaluations e ON e.RunId = r.RunId
WHERE @RunId IS NULL OR r.RunId = @RunId
GROUP BY r.RunId, r.PromptVersionId, p.VersionName, r.ModelName, r.Temperature,
         r.KeywordWeight, r.AiWeight, r.PassThreshold, r.Status,
         r.TotalItems, r.ProcessedItems, r.FailedItems, r.CompletedAt;

-- Progress mismatches; run against an idle test database to avoid observing an in-flight commit.
SELECT r.RunId, r.Status, r.TotalItems, r.ProcessedItems, r.FailedItems,
       x.ActualItems, x.Processed, x.Failed, x.Pending
FROM EvaluationRuns r
CROSS APPLY (SELECT COUNT(*) AS ActualItems,
    COUNT(CASE WHEN e.Status <> N'Pending' THEN 1 END) AS Processed,
    COUNT(CASE WHEN e.Status = N'Failed' THEN 1 END) AS Failed,
    COUNT(CASE WHEN e.Status = N'Pending' THEN 1 END) AS Pending
    FROM Evaluations e WHERE e.RunId = r.RunId) x
WHERE (@RunId IS NULL OR r.RunId = @RunId)
AND (r.TotalItems <> x.ActualItems OR r.ProcessedItems <> x.Processed OR r.FailedItems <> x.Failed
 OR (r.Status = N'Completed' AND (x.Pending > 0 OR r.CompletedAt IS NULL)));

-- 2. Arithmetic review, without parsing Reasoning. A reduced score may be a valid cap.
SELECT e.EvaluationId, e.RunId, e.KeywordScore, e.AiScore, e.FinalScore, e.ScoreSource,
       x.UncappedScore,
       CASE WHEN e.FinalScore < x.UncappedScore THEN N'Review: possible historical cap; metadata unavailable'
            ELSE N'Review: invalid fields or arithmetic' END AS Finding
FROM Evaluations e JOIN EvaluationRuns r ON r.RunId = e.RunId
CROSS APPLY (SELECT CASE WHEN e.ScoreSource = N'Combined'
    THEN ROUND(r.KeywordWeight * e.KeywordScore + r.AiWeight * e.AiScore, 2)
    WHEN e.ScoreSource = N'KeywordOnly' THEN e.KeywordScore END AS UncappedScore) x
WHERE (@RunId IS NULL OR e.RunId = @RunId) AND e.Status = N'Completed'
AND (e.KeywordScore IS NULL OR e.FinalScore IS NULL OR e.ScoreSource IS NULL
 OR e.KeywordScore NOT BETWEEN 0 AND 100 OR e.FinalScore NOT BETWEEN 0 AND 100
 OR e.ScoreSource NOT IN (N'Combined', N'KeywordOnly')
 OR (e.ScoreSource = N'Combined' AND (e.AiScore IS NULL OR e.AiScore NOT BETWEEN 0 AND 100))
 OR (e.ScoreSource = N'KeywordOnly' AND (e.AiScore IS NOT NULL OR e.ErrorMessage IS NULL))
 OR ABS(e.FinalScore - x.UncappedScore) > 0.01);

-- 3. Label / pass integrity. NULLs are violations too.
SELECT e.EvaluationId, e.RunId, e.FinalScore, e.FitLabel, e.PredictedPass, r.PassThreshold
FROM Evaluations e JOIN EvaluationRuns r ON r.RunId = e.RunId
WHERE (@RunId IS NULL OR e.RunId = @RunId) AND e.Status = N'Completed'
AND (e.FinalScore IS NULL OR e.FitLabel IS NULL OR e.PredictedPass IS NULL
 OR e.FitLabel <> CASE WHEN e.FinalScore >= 85 THEN N'Rất phù hợp' WHEN e.FinalScore >= 70 THEN N'Phù hợp'
                      WHEN e.FinalScore >= 55 THEN N'Tiềm năng' ELSE N'Chưa phù hợp' END
 OR e.PredictedPass <> CASE WHEN e.FinalScore >= r.PassThreshold THEN 1 ELSE 0 END);

-- 4a. Current job comparison: historical criteria may have changed; review manually.
SELECT e.EvaluationId, a.JobId,
       (SELECT COUNT(*) FROM EvaluationSkills s WHERE s.EvaluationId = e.EvaluationId) AS SavedSkills,
       (SELECT COUNT(*) FROM JobSkills j WHERE j.JobId = a.JobId) AS CurrentJobSkills
FROM Evaluations e JOIN Applications a ON a.ApplicationId = e.ApplicationId
WHERE (@RunId IS NULL OR e.RunId = @RunId) AND e.Status = N'Completed'
AND (SELECT COUNT(*) FROM EvaluationSkills s WHERE s.EvaluationId = e.EvaluationId)
 <> (SELECT COUNT(*) FROM JobSkills j WHERE j.JobId = a.JobId);

-- 4b. Wrong job, missing skill definition, or repeated skill code within an evaluation.
SELECT s.EvaluationId, s.JobId, s.SkillCode
FROM EvaluationSkills s JOIN Evaluations e ON e.EvaluationId = s.EvaluationId
JOIN Applications a ON a.ApplicationId = e.ApplicationId
WHERE (@RunId IS NULL OR e.RunId = @RunId)
AND (s.JobId <> a.JobId OR NOT EXISTS
     (SELECT 1 FROM JobSkills j WHERE j.JobId = s.JobId AND j.SkillCode = s.SkillCode));
SELECT s.EvaluationId, s.SkillCode, COUNT(*) AS DuplicateCount
FROM EvaluationSkills s JOIN Evaluations e ON e.EvaluationId = s.EvaluationId
WHERE @RunId IS NULL OR e.RunId = @RunId
GROUP BY s.EvaluationId, s.SkillCode HAVING COUNT(*) > 1;
