/* =====================================================================
   update_seed_weights_30_70.sql
   Đồng bộ dữ liệu seed ĐÃ CÓ trong SmartCvDb từ trọng số 40/60 sang 30/70.

   Phạm vi (chỉ dữ liệu seed, xác định bằng khóa):
     - EvaluationRuns  RunId 1..4             : KeywordWeight 0.40 -> 0.30, AiWeight 0.60 -> 0.70
     - Evaluations     32 dòng Combined/Completed (liệt kê tường minh bên dưới):
                       FinalScore = ROUND(0.30*KeywordScore + 0.70*AiScore, 2)
                       FitLabel   theo ngưỡng 85 / 70 / 55
                       PredictedPass = FinalScore >= 70
     - Giữ nguyên      EvaluationId 31 (KeywordOnly = 56.67), 33 (Failed), 35, 36 (Pending)
     - AccuracyPercent của RunId 1, 2 được tính lại từ HrLabels, phải ra 70.00 và 80.00

   An toàn:
     - Chỉ chạy trên database SmartCvDb.
     - Kiểm tra trước: mỗi dòng mục tiêu phải đang ở trạng thái CŨ (40/60) hoặc MỚI (30/70);
       nếu có giá trị khác (dữ liệu đã bị sửa tay, khác seed) -> dừng, ROLLBACK, không sửa gì.
     - Toàn bộ trong 1 transaction, XACT_ABORT ON + TRY/CATCH -> lỗi là ROLLBACK.
     - Kiểm tra sau ngay trong transaction; sai bất kỳ điều kiện nào -> ROLLBACK.
     - Chạy lại lần 2: mọi dòng đã ở trạng thái mới -> 0 dòng bị UPDATE.
     - Không INSERT/DELETE; số dòng 12 bảng được so sánh trước/sau.

   Chạy (bắt buộc -I vì bảng PromptVersions có filtered index; -f 65001 để đọc đúng tiếng Việt):
     sqlcmd -S localhost\BB -d SmartCvDb -E -C -b -I -f 65001 -i database/update_seed_weights_30_70.sql

   Chạy thử không lưu: đổi @DryRun = 1 (kết quả được tính và kiểm tra, cuối cùng ROLLBACK).
   ===================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @DryRun bit = 0;   -- 1 = chạy thử rồi ROLLBACK, 0 = lưu thật

DECLARE @Db sysname = DB_NAME();
IF @Db <> N'SmartCvDb'
BEGIN
    RAISERROR(N'Sai database đích: %s. Script chỉ chạy trên SmartCvDb.', 16, 1, @Db);
    RETURN;
END;

/* ---------- Giá trị mong đợi (từ seed gốc 40/60 và công thức 30/70) ---------- */
DECLARE @Expect TABLE (
    EvaluationId  int          PRIMARY KEY,
    RunId         int          NOT NULL,
    ApplicationId int          NOT NULL,
    KeywordScore  decimal(5,2) NOT NULL,
    AiScore       decimal(5,2) NOT NULL,
    OldFinal      decimal(5,2) NOT NULL,
    NewFinal      decimal(5,2) NOT NULL
);
INSERT INTO @Expect VALUES
( 1,1, 1,100.00,88.00,92.80,91.60),( 2,1, 2, 80.00,58.00,66.80,64.60),
( 3,1, 3, 10.00,35.00,25.00,27.50),( 4,1, 4,100.00,90.00,94.00,93.00),
( 5,1, 5, 66.67,75.00,71.67,72.50),( 6,1, 6,100.00,92.00,95.20,94.40),
( 7,1, 7, 23.33,72.00,52.53,57.40),( 8,1, 8,100.00,82.00,89.20,87.40),
( 9,1, 9, 46.67,88.00,71.47,75.60),(10,1,10,100.00,89.00,93.40,92.30),
(11,2, 1,100.00,90.00,94.00,93.00),(12,2, 2, 80.00,60.00,68.00,66.00),
(13,2, 3, 10.00,30.00,22.00,24.00),(14,2, 4,100.00,92.00,95.20,94.40),
(15,2, 5, 66.67,75.00,71.67,72.50),(16,2, 6,100.00,94.00,96.40,95.80),
(17,2, 7, 23.33,45.00,36.33,38.50),(18,2, 8,100.00,85.00,91.00,89.50),
(19,2, 9, 46.67,66.00,58.27,60.20),(20,2,10,100.00,91.00,94.60,93.70),
(21,3, 1,100.00,90.00,94.00,93.00),(22,3, 2, 80.00,60.00,68.00,66.00),
(23,3, 3, 10.00,30.00,22.00,24.00),(24,3, 4,100.00,92.00,95.20,94.40),
(25,3, 5, 66.67,75.00,71.67,72.50),(26,3, 6,100.00,94.00,96.40,95.80),
(27,3, 7, 23.33,45.00,36.33,38.50),(28,3, 8,100.00,85.00,91.00,89.50),
(29,3, 9, 46.67,66.00,58.27,60.20),(30,3,10,100.00,91.00,94.60,93.70),
(32,3,12, 46.67,58.00,53.47,54.60),(34,4, 4,100.00,92.00,95.20,94.40);

DECLARE @RunIds TABLE (RunId int PRIMARY KEY);
INSERT INTO @RunIds VALUES (1),(2),(3),(4);

DECLARE @CountsBefore TABLE (Tbl sysname PRIMARY KEY, N bigint);
DECLARE @CountsAfter  TABLE (Tbl sysname PRIMARY KEY, N bigint);
DECLARE @nRuns int = 0, @nEval int = 0, @nAcc int = 0, @bad int;

BEGIN TRY
    BEGIN TRANSACTION;

    INSERT INTO @CountsBefore
    SELECT t.name, SUM(p.rows) FROM sys.tables t
    JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
    WHERE t.name <> N'__EFMigrationsHistory' GROUP BY t.name;

    /* ---------- 1. Kiểm tra trước ---------- */
    -- 1a. Đủ 4 lượt chấm, trọng số đang là 40/60 hoặc 30/70, ngưỡng 70
    SELECT @bad = 4 - COUNT(*) FROM EvaluationRuns r JOIN @RunIds x ON x.RunId = r.RunId
    WHERE r.PassThreshold = 70.00
      AND ((r.KeywordWeight = 0.40 AND r.AiWeight = 0.60) OR (r.KeywordWeight = 0.30 AND r.AiWeight = 0.70));
    IF @bad <> 0 THROW 50001, N'Kiểm tra trước thất bại: EvaluationRuns 1..4 không ở trạng thái 40/60 hoặc 30/70 (hoặc PassThreshold khác 70).', 1;

    -- 1b. 32 dòng mục tiêu tồn tại, đúng khóa và điểm thành phần, FinalScore là giá trị cũ hoặc mới
    SELECT @bad = 32 - COUNT(*) FROM Evaluations e JOIN @Expect x ON x.EvaluationId = e.EvaluationId
    WHERE e.RunId = x.RunId AND e.ApplicationId = x.ApplicationId
      AND e.ScoreSource = N'Combined' AND e.Status = N'Completed'
      AND e.KeywordScore = x.KeywordScore AND e.AiScore = x.AiScore
      AND e.FinalScore IN (x.OldFinal, x.NewFinal);
    IF @bad <> 0 THROW 50002, N'Kiểm tra trước thất bại: có Evaluations mục tiêu khác dữ liệu seed.', 1;

    -- 1c. Dòng KeywordOnly đúng giá trị dự phòng
    IF NOT EXISTS (SELECT 1 FROM Evaluations WHERE EvaluationId = 31 AND RunId = 3 AND ScoreSource = N'KeywordOnly'
                   AND KeywordScore = 56.67 AND AiScore IS NULL AND FinalScore = 56.67)
        THROW 50003, N'Kiểm tra trước thất bại: EvaluationId 31 (KeywordOnly) không còn là 56.67.', 1;

    /* ---------- 2. Đổi trọng số (cả hai cột trong 1 lệnh để không vi phạm CK_EvaluationRuns_Weights) ---------- */
    UPDATE r SET KeywordWeight = 0.30, AiWeight = 0.70
    FROM EvaluationRuns r JOIN @RunIds x ON x.RunId = r.RunId
    WHERE NOT (r.KeywordWeight = 0.30 AND r.AiWeight = 0.70);
    SET @nRuns = @@ROWCOUNT;

    /* ---------- 3. Tính lại FinalScore / FitLabel / PredictedPass cho 32 dòng Combined ---------- */
    ;WITH calc AS (
        SELECT e.EvaluationId, e.FinalScore, e.FitLabel, e.PredictedPass, s.NewScore,
               CASE WHEN s.NewScore >= 85 THEN N'Rất phù hợp'
                    WHEN s.NewScore >= 70 THEN N'Phù hợp'
                    WHEN s.NewScore >= 55 THEN N'Tiềm năng'
                    ELSE N'Chưa phù hợp' END AS NewLabel,
               CASE WHEN s.NewScore >= 70 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS NewPass
        FROM Evaluations e
        JOIN @Expect x ON x.EvaluationId = e.EvaluationId AND x.RunId = e.RunId AND x.ApplicationId = e.ApplicationId
        CROSS APPLY (SELECT CAST(ROUND(0.30 * e.KeywordScore + 0.70 * e.AiScore, 2) AS decimal(5,2)) AS NewScore) s
        WHERE e.ScoreSource = N'Combined' AND e.Status = N'Completed'
    )
    UPDATE calc SET FinalScore = NewScore, FitLabel = NewLabel, PredictedPass = NewPass
    WHERE FinalScore <> NewScore OR FitLabel <> NewLabel OR PredictedPass <> NewPass
       OR FitLabel IS NULL OR PredictedPass IS NULL;
    SET @nEval = @@ROWCOUNT;

    /* ---------- 4. Tính lại AccuracyPercent cho lượt PromptTest ---------- */
    ;WITH acc AS (
        SELECT r.RunId, r.AccuracyPercent,
               CAST(100.0 * SUM(CASE WHEN e.PredictedPass = h.IsPass THEN 1 ELSE 0 END) / COUNT(*) AS decimal(5,2)) AS NewAcc
        FROM EvaluationRuns r
        JOIN Evaluations e ON e.RunId = r.RunId AND e.Status = N'Completed'
        JOIN HrLabels h ON h.EvaluationSetId = r.EvaluationSetId AND h.ApplicationId = e.ApplicationId
        WHERE r.RunId IN (1, 2) AND r.RunType = N'PromptTest'
        GROUP BY r.RunId, r.AccuracyPercent
    )
    UPDATE r SET AccuracyPercent = a.NewAcc
    FROM EvaluationRuns r JOIN acc a ON a.RunId = r.RunId
    WHERE r.AccuracyPercent IS NULL OR r.AccuracyPercent <> a.NewAcc;
    SET @nAcc = @@ROWCOUNT;

    /* ---------- 5. Kiểm tra sau (sai -> ROLLBACK) ---------- */
    IF (SELECT COUNT(*) FROM EvaluationRuns r JOIN @RunIds x ON x.RunId = r.RunId
        WHERE r.KeywordWeight = 0.30 AND r.AiWeight = 0.70) <> 4
        THROW 50011, N'Kiểm tra sau thất bại: 4 EvaluationRuns chưa ở 0.30/0.70.', 1;

    IF (SELECT COUNT(*) FROM Evaluations e JOIN @Expect x ON x.EvaluationId = e.EvaluationId
        WHERE e.FinalScore = x.NewFinal
          AND e.FinalScore = CAST(ROUND(0.30 * e.KeywordScore + 0.70 * e.AiScore, 2) AS decimal(5,2))
          AND e.FitLabel = CASE WHEN e.FinalScore >= 85 THEN N'Rất phù hợp' WHEN e.FinalScore >= 70 THEN N'Phù hợp'
                                WHEN e.FinalScore >= 55 THEN N'Tiềm năng' ELSE N'Chưa phù hợp' END
          AND e.PredictedPass = CASE WHEN e.FinalScore >= 70 THEN 1 ELSE 0 END) <> 32
        THROW 50012, N'Kiểm tra sau thất bại: FinalScore/FitLabel/PredictedPass không khớp.', 1;

    IF NOT EXISTS (SELECT 1 FROM Evaluations WHERE EvaluationId = 31 AND FinalScore = 56.67 AND ScoreSource = N'KeywordOnly')
        THROW 50013, N'Kiểm tra sau thất bại: dòng KeywordOnly bị thay đổi.', 1;

    IF EXISTS (SELECT 1 FROM Evaluations WHERE EvaluationId IN (33, 35, 36) AND FinalScore IS NOT NULL)
        THROW 50014, N'Kiểm tra sau thất bại: dòng Failed/Pending bị tính điểm.', 1;

    IF (SELECT AccuracyPercent FROM EvaluationRuns WHERE RunId = 1) <> 70.00
    OR (SELECT AccuracyPercent FROM EvaluationRuns WHERE RunId = 2) <> 80.00
        THROW 50015, N'Kiểm tra sau thất bại: AccuracyPercent không phải 70.00 / 80.00.', 1;

    INSERT INTO @CountsAfter
    SELECT t.name, SUM(p.rows) FROM sys.tables t
    JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
    WHERE t.name <> N'__EFMigrationsHistory' GROUP BY t.name;
    IF EXISTS (SELECT 1 FROM @CountsBefore b FULL JOIN @CountsAfter a ON a.Tbl = b.Tbl WHERE ISNULL(a.N,-1) <> ISNULL(b.N,-1))
        THROW 50016, N'Kiểm tra sau thất bại: số dòng các bảng thay đổi.', 1;

    SELECT @nRuns AS EvaluationRunsUpdated, @nEval AS EvaluationsUpdated, @nAcc AS AccuracyUpdated,
           (SELECT SUM(N) FROM @CountsAfter) AS TotalRows12Tables,
           CASE WHEN @DryRun = 1 THEN N'DRY RUN - ROLLBACK' ELSE N'COMMIT' END AS Mode;

    IF @DryRun = 1 ROLLBACK TRANSACTION; ELSE COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

/* ---------- Kết quả sau khi chạy ---------- */
SELECT RunId, RunType, KeywordWeight, AiWeight, PassThreshold, AccuracyPercent FROM EvaluationRuns ORDER BY RunId;
SELECT EvaluationId, RunId, ApplicationId, ScoreSource, Status, KeywordScore, AiScore, FinalScore, FitLabel, PredictedPass
FROM Evaluations ORDER BY EvaluationId;
GO
