/* A43: kiểm tra sau khi xóa dữ liệu theo yêu cầu ứng viên. Đổi @CandidateId, @Email rồi chạy. */
DECLARE @CandidateId INT = 0;                 -- id ứng viên đã xóa
DECLARE @Email NVARCHAR(254) = N'';           -- email của ứng viên đó (ghi lại TRƯỚC khi xóa)

IF @CandidateId <= 0 OR NULLIF(LTRIM(RTRIM(@Email)), N'') IS NULL THROW 51043, N'Nhap CandidateId va Email cua du lieu thu cach ly truoc khi chay.', 1;

-- 1) Không còn dòng ứng viên, không còn email ở đâu  → mong đợi: 0, 0, 0
SELECT (SELECT COUNT(*) FROM Candidates WHERE CandidateId = @CandidateId) AS UngVienConLai,
       (SELECT COUNT(*) FROM Candidates WHERE Email = @Email) AS EmailTrongCandidates,
       (SELECT COUNT(*) FROM Cvs WHERE CHARINDEX(@Email, ExtractedText) > 0) AS EmailTrongChuCv;

-- 2) Nhật ký: có dòng Erase, CvIds liệt kê các CV đã xử lý
SELECT * FROM CandidateDataRequests WHERE CandidateId = @CandidateId ORDER BY CreatedAt;

-- 3) Các CV đó: đã ẩn danh, không còn chữ, không gắn ai; điểm và giai đoạn GIỮ
DECLARE @CvIds NVARCHAR(MAX) = (SELECT TOP 1 CvIds FROM CandidateDataRequests
                                 WHERE CandidateId = @CandidateId AND RequestType = 'Erase' ORDER BY CreatedAt DESC, RequestId DESC);
SELECT v.CvId, v.OriginalFileName, v.CandidateId, v.AnonymizedAt, LEN(v.ExtractedText) AS DoDaiChu,
       a.Stage, a.RejectReason,
       (SELECT TOP 1 e.FinalScore FROM Evaluations e WHERE e.ApplicationId = a.ApplicationId ORDER BY e.CreatedAt DESC) AS DiemGiuLai
FROM Cvs v
LEFT JOIN Applications a ON a.CvId = v.CvId
WHERE v.CvId IN (SELECT TRY_CAST(value AS UNIQUEIDENTIFIER) FROM STRING_SPLIT(@CvIds, ','));

-- 4) Lịch sử: hồ sơ bị rút có dòng "Theo yêu cầu ứng viên"
SELECT e.ApplicationId, e.FromStage, e.ToStage, e.RejectReason, e.Note, e.Actor, e.CreatedAt
FROM ApplicationEvents e
JOIN Applications a ON a.ApplicationId = e.ApplicationId
WHERE a.CvId IN (SELECT TRY_CAST(value AS UNIQUEIDENTIFIER) FROM STRING_SPLIT(@CvIds, ','))
  AND e.Note LIKE N'Theo yêu cầu ứng viên%';
