/* A16: READ ONLY. No UPDATE, DELETE, DDL or cleanup.
   Evaluate only ApplicationIds that belong to both the run and its CURRENT HR labels.
   New labels absent from an old run require a new run; they are not invented evaluations.
   Failed/Pending/null predictions remain in the denominator. KeywordOnly is not Unscored.
   Final percentages are published only for Completed runs; zero comparable labels => NULL. */

WITH Matched AS
(
    SELECT r.RunId, e.ApplicationId, e.Status, e.PredictedPass, e.ScoreSource, h.IsPass
    FROM dbo.EvaluationRuns r
    JOIN dbo.Evaluations e ON e.RunId = r.RunId
    JOIN dbo.HrLabels h ON h.EvaluationSetId = r.EvaluationSetId AND h.ApplicationId = e.ApplicationId
    WHERE r.RunType = N'PromptTest'
), Totals AS
(
    SELECT RunId, COUNT(*) AS Tong,
           SUM(CASE WHEN Status = N'Completed' AND PredictedPass = IsPass THEN 1 ELSE 0 END) AS Dung,
           SUM(CASE WHEN Status = N'Completed' AND IsPass = 1 AND PredictedPass = 0 THEN 1 ELSE 0 END) AS BoSot,
           SUM(CASE WHEN Status = N'Completed' AND IsPass = 0 AND PredictedPass = 1 THEN 1 ELSE 0 END) AS ChonNham,
           SUM(CASE WHEN Status <> N'Completed' OR PredictedPass IS NULL THEN 1 ELSE 0 END) AS KhongCham,
           SUM(CASE WHEN Status = N'Completed' AND ScoreSource = N'KeywordOnly' THEN 1 ELSE 0 END) AS ChiKeyword
    FROM Matched
    GROUP BY RunId
)
SELECT r.RunId, p.VersionName, r.Status,
       COALESCE(t.Tong, 0) AS Tong, COALESCE(t.Dung, 0) AS Dung,
       COALESCE(t.BoSot, 0) AS BoSot, COALESCE(t.ChonNham, 0) AS ChonNham,
       COALESCE(t.KhongCham, 0) AS KhongCham, COALESCE(t.ChiKeyword, 0) AS ChiKeyword,
       CASE WHEN r.Status = N'Completed'
            THEN CAST(ROUND(100.0 * t.Dung / NULLIF(t.Tong, 0), 2) AS DECIMAL(5,2)) END AS TyLeTinhLai,
       r.AccuracyPercent AS TyLeDaLuu
FROM dbo.EvaluationRuns r
JOIN dbo.PromptVersions p ON p.PromptVersionId = r.PromptVersionId
LEFT JOIN Totals t ON t.RunId = r.RunId
WHERE r.RunType = N'PromptTest'
ORDER BY r.RunId;

-- Mismatched individual predictions, scoped by both set and application.
SELECT r.RunId, p.VersionName, e.ApplicationId, c.OriginalFileName,
       h.IsPass AS HR_Dat, e.PredictedPass AS AI_Dat, e.FinalScore,
       CASE WHEN h.IsPass = 1 THEN N'Missed' ELSE N'WrongPick' END AS MismatchType,
       h.Note AS GhiChuHR
FROM dbo.EvaluationRuns r
JOIN dbo.PromptVersions p ON p.PromptVersionId = r.PromptVersionId
JOIN dbo.Evaluations e ON e.RunId = r.RunId
JOIN dbo.HrLabels h ON h.EvaluationSetId = r.EvaluationSetId AND h.ApplicationId = e.ApplicationId
JOIN dbo.Applications a ON a.ApplicationId = e.ApplicationId
JOIN dbo.Cvs c ON c.CvId = a.CvId
WHERE r.RunType = N'PromptTest' AND e.Status = N'Completed' AND e.PredictedPass <> h.IsPass
ORDER BY r.RunId, e.ApplicationId;

-- Verify that the currently active prompt has not been changed by evaluation.
SELECT PromptVersionId, VersionName, IsActive FROM dbo.PromptVersions ORDER BY PromptVersionId;
