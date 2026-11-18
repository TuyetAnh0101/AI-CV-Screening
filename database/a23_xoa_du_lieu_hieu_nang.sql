/* A23: xóa dữ liệu giả PERF_ (chạy sau khi đo xong). Thứ tự: Applications → Cvs → Candidates (khóa ngoại). */
SET NOCOUNT ON;
BEGIN TRAN;
DELETE a FROM Applications a JOIN Cvs v ON v.CvId = a.CvId WHERE v.OriginalFileName LIKE N'PERF[_]%';
DECLARE @c TABLE (CandidateId INT);
INSERT @c SELECT DISTINCT CandidateId FROM Cvs WHERE OriginalFileName LIKE N'PERF[_]%' AND CandidateId IS NOT NULL;
DELETE FROM Cvs WHERE OriginalFileName LIKE N'PERF[_]%';
DELETE FROM Candidates WHERE CandidateId IN (SELECT CandidateId FROM @c) OR Email LIKE 'perf[_]%@example.com';
COMMIT;
SELECT (SELECT COUNT(*) FROM Cvs WHERE OriginalFileName LIKE N'PERF[_]%') AS CvConLai,
       (SELECT COUNT(*) FROM Candidates WHERE Email LIKE 'perf[_]%@example.com') AS UngVienConLai;
