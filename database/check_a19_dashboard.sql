-- A19: read-only reconciliation. Only session-local #temp tables are created/dropped.
-- Use the SAME filters and ReviewFlags options as the API; no application table is written.
SET NOCOUNT ON;
DECLARE @JobId int = NULL;
DECLARE @From date = NULL;
DECLARE @To date = NULL;
DECLARE @ScoreGap decimal(5,2) = 30;
DECLARE @MinTextLength int = 500;
DECLARE @CappedHighAiScore decimal(5,2) = 80;
IF @JobId <= 0 OR @From > @To OR @From = '00010101' OR @To = '99991231'
    THROW 50001, N'Bộ lọc không hợp lệ', 1;
IF @JobId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Jobs WHERE JobId = @JobId)
    THROW 50002, N'Không tìm thấy Job', 1;
DECLARE @FromUtc datetime2 = CASE WHEN @From IS NOT NULL THEN DATEADD(HOUR, -7, CAST(@From AS datetime2)) END;
DECLARE @ToUtc datetime2 = CASE WHEN @To IS NOT NULL THEN DATEADD(HOUR, -7, CAST(DATEADD(DAY, 1, @To) AS datetime2)) END;
SELECT a.ApplicationId, a.CvId, a.JobId, a.CreatedAt, a.Stage, a.RejectReason INTO #Apps
FROM Applications a
WHERE (@JobId IS NULL OR a.JobId = @JobId)
  AND (@FromUtc IS NULL OR a.CreatedAt >= @FromUtc) AND (@ToUtc IS NULL OR a.CreatedAt < @ToUtc);
CREATE UNIQUE CLUSTERED INDEX IX_A19_Apps ON #Apps(ApplicationId);

-- 1. Cards: CV upload scope differs from application creation scope when no Job is selected.
SELECT TotalCvs = CASE WHEN @JobId IS NULL
    THEN (SELECT COUNT(*) FROM Cvs WHERE (@FromUtc IS NULL OR UploadedAt >= @FromUtc) AND (@ToUtc IS NULL OR UploadedAt < @ToUtc))
    ELSE (SELECT COUNT(DISTINCT CvId) FROM #Apps) END,
    FailedCvs = (SELECT COUNT(*) FROM Cvs c WHERE c.ExtractionStatus = N'Failed' AND
        ((@JobId IS NULL AND (@FromUtc IS NULL OR c.UploadedAt >= @FromUtc) AND (@ToUtc IS NULL OR c.UploadedAt < @ToUtc))
         OR (@JobId IS NOT NULL AND EXISTS (SELECT 1 FROM #Apps a WHERE a.CvId = c.CvId)))),
    TotalApplications = (SELECT COUNT(*) FROM #Apps),
    ActiveJobs = (SELECT COUNT(*) FROM Jobs WHERE IsActive = 1 AND (@JobId IS NULL OR JobId = @JobId)),
    RunningRuns = (SELECT COUNT(*) FROM EvaluationRuns WHERE Status IN (N'Queued', N'Running')),
    RejectedCount = (SELECT COUNT(*) FROM #Apps WHERE Stage = N'Rejected'),
    OnHoldCount = (SELECT COUNT(*) FROM #Apps WHERE Stage = N'OnHold');

-- 2. Furthest stage considers current + BOTH ends of all history, once per application.
WITH History AS (
    SELECT ApplicationId, Stage FROM #Apps
    UNION ALL SELECT e.ApplicationId, e.FromStage FROM ApplicationEvents e JOIN #Apps a ON a.ApplicationId = e.ApplicationId
    UNION ALL SELECT e.ApplicationId, e.ToStage FROM ApplicationEvents e JOIN #Apps a ON a.ApplicationId = e.ApplicationId
)
-- Materialize once: repeated CTE expansion can produce expensive plans for large cohorts.
SELECT ApplicationId, MAX(CASE Stage WHEN N'Interview' THEN 3 WHEN N'Shortlisted' THEN 2 WHEN N'Reviewing' THEN 1 ELSE 0 END) AS Step
INTO #Furthest FROM History GROUP BY ApplicationId;
WITH Stages AS (
    SELECT * FROM (VALUES (0,N'New'),(1,N'Reviewing'),(2,N'Shortlisted'),(3,N'Interview')) s(Step, Stage)
), Counts AS (
    SELECT s.Step, s.Stage, (SELECT COUNT(*) FROM #Apps a WHERE a.Stage = s.Stage) AS [Current],
        (SELECT COUNT(*) FROM #Furthest f WHERE f.Step >= s.Step) AS Reached FROM Stages s
)
SELECT Stage, [Current], Reached, CASE WHEN Step = 0 THEN NULL
    WHEN LAG(Reached) OVER (ORDER BY Step) = 0 THEN 0
    ELSE CAST(ROUND(Reached * 100.0 / LAG(Reached) OVER (ORDER BY Step),1) AS decimal(5,1)) END AS ConversionPercent
FROM Counts ORDER BY Step;

-- 3. First HR event, with negative intervals clamped to zero. No CV upload timestamp here.
WITH Durations AS (
    SELECT CASE WHEN MIN(e.CreatedAt) < a.CreatedAt THEN CAST(0 AS decimal(28,10))
        ELSE CAST(DATEDIFF_BIG(MICROSECOND,a.CreatedAt,MIN(e.CreatedAt)) AS decimal(28,4)) / 3600000000 END AS Hours
    FROM #Apps a JOIN ApplicationEvents e ON e.ApplicationId = a.ApplicationId GROUP BY a.ApplicationId,a.CreatedAt
), Timing AS (
    SELECT Hours, PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY Hours) OVER () AS Median FROM Durations
)
SELECT COUNT(*) AS [Count], CAST(ROUND(AVG(Hours),1) AS decimal(28,1)) AS AverageHours,
    CAST(ROUND(MAX(Median),1) AS decimal(28,1)) AS MedianHours FROM Timing;

-- 4. Six stable reasons; denominator INCLUDES rejected rows with missing legacy reasons.
WITH Reasons AS (
    SELECT * FROM (VALUES (0,N'MissingSkills'),(1,N'NotEnoughExperience'),(2,N'OtherRequirement'),
        (3,N'CandidateWithdrew'),(4,N'PositionFilled'),(5,N'Duplicate')) r(Ordinal,Reason)
)
SELECT r.Reason, COUNT(a.ApplicationId) AS [Count], CAST(COALESCE(ROUND(COUNT(a.ApplicationId) * 100.0 /
    NULLIF((SELECT COUNT(*) FROM #Apps WHERE Stage = N'Rejected'),0),1),0) AS decimal(5,1)) AS [Percent]
FROM Reasons r LEFT JOIN #Apps a ON a.Stage = N'Rejected' AND a.RejectReason = r.Reason
GROUP BY r.Ordinal,r.Reason ORDER BY [Count] DESC,r.Ordinal;

-- 5. Recent runs do not use the date filter.
SELECT TOP (5) r.RunId, r.RunType, r.Status, r.TotalItems, r.ProcessedItems, r.FailedItems, r.CreatedAt
FROM EvaluationRuns r WHERE @JobId IS NULL OR EXISTS (
    SELECT 1 FROM Evaluations e JOIN Applications a ON a.ApplicationId = e.ApplicationId WHERE e.RunId = r.RunId AND a.JobId = @JobId)
ORDER BY r.CreatedAt DESC,r.RunId DESC;

-- 6. Independent A40 cross-check against saved evidence, not today's scoring cap.
-- Ordinal marker comparison and sentinel text length match EvaluationReviewReader/ReviewFlags.
WITH Ranked AS (
    -- Sort only keys/timestamps. Sorting e.* (nvarchar(max) AI content) can request an excessive memory grant.
    SELECT e.EvaluationId, a.JobId, ROW_NUMBER() OVER (PARTITION BY e.ApplicationId ORDER BY e.CreatedAt DESC,e.EvaluationId DESC) AS rn
    FROM Evaluations e JOIN EvaluationRuns r ON r.RunId = e.RunId
    JOIN #Apps a ON a.ApplicationId = e.ApplicationId
    WHERE r.RunType = N'Screening' AND a.Stage IN (N'New',N'Reviewing')
)
SELECT EvaluationId, JobId INTO #LatestScreening FROM Ranked WHERE rn = 1;
CREATE UNIQUE CLUSTERED INDEX IX_A19_Latest ON #LatestScreening(EvaluationId);
WITH Flagged AS (
    SELECT latest.JobId FROM #LatestScreening latest JOIN Evaluations e ON e.EvaluationId = latest.EvaluationId
    JOIN EvaluationRuns r ON r.RunId = e.RunId
    JOIN Applications a ON a.ApplicationId = e.ApplicationId JOIN Cvs c ON c.CvId = a.CvId
    WHERE e.Status = N'Completed' AND (
        (e.KeywordScore IS NOT NULL AND (e.ScoreSource = N'KeywordOnly' OR e.AiScore IS NULL))
        OR (e.KeywordScore IS NOT NULL AND e.AiScore IS NOT NULL AND ISNULL(e.ScoreSource,N'') <> N'KeywordOnly' AND ABS(e.KeywordScore-e.AiScore) >= @ScoreGap)
        OR (c.ExtractedText IS NOT NULL AND LEN(c.ExtractedText + N'#') - 1 < @MinTextLength)
        OR (e.AiScore >= @CappedHighAiScore AND e.ScoreSource = N'Combined' AND e.FinalScore BETWEEN 0 AND 100
            AND r.KeywordWeight >= 0 AND r.AiWeight >= 0 AND r.KeywordWeight + r.AiWeight = 1
            AND ROUND(e.KeywordScore*r.KeywordWeight+e.AiScore*r.AiWeight,2) > e.FinalScore
            AND EXISTS (SELECT 1 FROM EvaluationSkills es JOIN JobSkills js ON js.JobId=es.JobId AND js.SkillCode=es.SkillCode
                WHERE es.EvaluationId=e.EvaluationId AND es.IsMatched=0 AND js.SkillType=N'MustHave')
            AND CHARINDEX((N'(Điểm cuối giới hạn '+CONVERT(nvarchar(30),CAST(ROUND(e.FinalScore,0) AS int))+
                N' vì thiếu kỹ năng bắt buộc: ') COLLATE Latin1_General_100_BIN2, e.Reasoning COLLATE Latin1_General_100_BIN2)>0)
    )
)
SELECT JobId, COUNT(*) AS [Count] INTO #FlaggedJobs FROM Flagged GROUP BY JobId;
SELECT COALESCE(SUM([Count]),0) AS FlaggedUnresolved,
    (SELECT TOP(1) JobId FROM #FlaggedJobs ORDER BY [Count] DESC,JobId) AS FlaggedJobId FROM #FlaggedJobs;
DROP TABLE #FlaggedJobs;
DROP TABLE #LatestScreening;
DROP TABLE #Furthest;
DROP TABLE #Apps;
