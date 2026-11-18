/* =====================================================================================
   A23: tạo ~500 CV GIẢ để đo tốc độ GET /api/cvs (tìm theo nội dung CV).
   - Chỉ chạy trên database của MÁY MÌNH (SmartCvDb local), KHÔNG chạy trên database chung.
   - Mọi dữ liệu giả có tên file bắt đầu bằng PERF_ và email perf_xxxxx@example.com
     → xóa sạch bằng a23_xoa_du_lieu_hieu_nang.sql.
   - Không có file thật trên ổ đĩa: bấm "Xem file" của CV giả sẽ báo không có file (bình thường).
   - Mỗi CV ~3.000–4.000 ký tự (cỡ CV thật 1–2 trang), kỹ năng xoay vòng để tìm "React", "SQL Server"... ra một phần CV.
   ===================================================================================== */
SET NOCOUNT ON;
DECLARE @Count INT = 500;                                   -- đổi thành 2000, 5000 để thử kho lớn hơn
DECLARE @JobId INT = (SELECT TOP 1 JobId FROM Jobs WHERE IsActive = 1 ORDER BY JobId);

IF EXISTS (SELECT 1 FROM Cvs WHERE OriginalFileName LIKE N'PERF[_]%')
BEGIN
    RAISERROR(N'Đã có dữ liệu PERF_. Chạy a23_xoa_du_lieu_hieu_nang.sql trước.', 16, 1);
    RETURN;
END;

BEGIN TRAN;

DECLARE @Skills TABLE (I INT PRIMARY KEY, Name NVARCHAR(50));
INSERT @Skills VALUES (0, N'React'), (1, N'Angular'), (2, N'SQL Server'), (3, N'PostgreSQL'), (4, N'Docker'),
                      (5, N'Kubernetes'), (6, N'Python'), (7, N'Java Spring Boot'), (8, N'ASP.NET Core'), (9, N'Figma');

DECLARE @Filler NVARCHAR(MAX) = REPLICATE(CAST(
    N'Tham gia phát triển hệ thống quản lý bán hàng cho khách hàng doanh nghiệp, làm việc theo Scrum, '
  + N'viết tài liệu kỹ thuật, review code cho thành viên mới, phối hợp với đội kiểm thử để sửa lỗi trước khi phát hành. '
  AS NVARCHAR(MAX)), 14);

;WITH n AS (
    SELECT TOP (@Count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
SELECT i,
       NEWID() AS CvId,
       N'Ứng viên Hiệu Năng ' + RIGHT('00000' + CAST(i AS VARCHAR(5)), 5) AS FullName,
       'perf_' + RIGHT('00000' + CAST(i AS VARCHAR(5)), 5) + '@example.com' AS Email,
       '09' + RIGHT('00000000' + CAST(i AS VARCHAR(8)), 8) AS Phone
INTO #p FROM n;

INSERT Candidates (FullName, Email, PhoneNumber, CreatedAt)
SELECT FullName, Email, Phone, SYSUTCDATETIME() FROM #p ORDER BY i;

INSERT Cvs (CvId, CandidateId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash,
            ExtractedText, ExtractionMethod, ExtractionStatus, UploadedAt, SourceChannel, ConsentConfirmedAt)
SELECT p.CvId, c.CandidateId,
       N'PERF_' + RIGHT('00000' + CAST(p.i AS VARCHAR(5)), 5) + N'.pdf',
       N'perf/' + CAST(p.CvId AS NVARCHAR(36)) + N'.pdf',
       'application/pdf', 120000,
       LEFT(CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', CAST(p.CvId AS VARCHAR(36))), 2), 64),
       UPPER(p.FullName) + NCHAR(10) + p.Email + N' | ' + p.Phone + NCHAR(10)
         + N'KỸ NĂNG: ' + s1.Name + N', ' + s2.Name + N', Git, Tiếng Anh giao tiếp' + NCHAR(10)
         + N'KINH NGHIỆM: ' + CAST(p.i % 6 AS NVARCHAR(2)) + N' năm' + NCHAR(10) + @Filler,
       'PdfText', 'Completed',
       DATEADD(MINUTE, -p.i * 37, SYSUTCDATETIME()),
       CASE p.i % 5 WHEN 0 THEN 'Email' WHEN 1 THEN 'Website' WHEN 2 THEN 'JobBoard' WHEN 3 THEN 'Referral' ELSE 'Other' END,
       DATEADD(MINUTE, -p.i * 37, SYSUTCDATETIME())
FROM #p p
JOIN Candidates c ON c.Email = p.Email
JOIN @Skills s1 ON s1.I = p.i % 10
JOIN @Skills s2 ON s2.I = (p.i * 3 + 1) % 10;

-- Gán vào job đầu tiên đang tuyển, giai đoạn xoay vòng (để thử lọc jobId + stage)
IF @JobId IS NOT NULL
    INSERT Applications (CvId, JobId, CreatedAt, Stage, RejectReason)
    SELECT p.CvId, @JobId, SYSUTCDATETIME(),
           CASE p.i % 4 WHEN 0 THEN 'New' WHEN 1 THEN 'Reviewing' WHEN 2 THEN 'Rejected' ELSE 'OnHold' END,
           CASE p.i % 4 WHEN 2 THEN 'MissingSkills' ELSE NULL END
    FROM #p p;

COMMIT;
DROP TABLE #p;

SELECT COUNT(*) AS SoCvGia,
       SUM(CASE WHEN ExtractedText LIKE N'%React%' THEN 1 ELSE 0 END) AS CoReact,
       SUM(CASE WHEN ExtractedText LIKE N'%SQL Server%' THEN 1 ELSE 0 END) AS CoSqlServer,
       SUM(DATALENGTH(ExtractedText)) / 1024 / 1024.0 AS TongChuMB
FROM Cvs WHERE OriginalFileName LIKE N'PERF[_]%';
