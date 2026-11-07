/* =====================================================================
   test_a5_trung_database.sql – Thử A5: file trùng với CV ĐÃ CÓ trong database.

   A5 chưa lưu file (A6 mới lưu), nên ta thêm tạm MỘT dòng vào bảng Cvs mang mã hash
   của test-files/cv-mau-3.pdf, rồi tải file đó lên: API phải trả Duplicate.

   ⚠️ CHỈ CHẠY TRÊN DATABASE THỬ NGHIỆM RIÊNG, KHÔNG chạy trên SmartCvDb đang dùng chung/đã nghiệm thu.
      Script tự dừng nếu đang kết nối SmartCvDb.

   1) Tạo database thử (12 bảng + 5 job nền, không có dữ liệu demo):
        dotnet ef database update --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api ^
          --context SmartCv.Infrastructure.Data.SmartCvDbContext ^
          --connection "Server=TEN_SERVER;Database=SmartCvDb_A5Test;Trusted_Connection=True;TrustServerCertificate=True"
   2) Chạy PHẦN 1 của script này vào database thử:
        sqlcmd -S TEN_SERVER -d SmartCvDb_A5Test -E -C -b -I -f 65001 -i database/test_a5_trung_database.sql
   3) Chạy API trỏ vào database thử (PowerShell):
        $env:ConnectionStrings__SmartCvDb = "Server=TEN_SERVER;Database=SmartCvDb_A5Test;Trusted_Connection=True;TrustServerCertificate=True"
        dotnet run --project src/SmartCv.Api --launch-profile https
      rồi gửi test-files/cv-mau-3.pdf -> Duplicate "Trùng với CV đã tải trước đó: "CV_ThuTrung_A5.pdf" ..."
   4) Chạy PHẦN 2 (bỏ dấu chú thích) để xóa dòng thử, hoặc xóa luôn database thử.

   Mã hash là của test-files/cv-mau-3.pdf (1408 byte). Kiểm tra trên máy:
       (Get-FileHash test-files\cv-mau-3.pdf -Algorithm SHA256).Hash
   Khác giá trị @Hash thì file đã bị đổi (ví dụ đổi xuống dòng khi checkout); xem .gitattributes.
   ===================================================================== */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() = N'SmartCvDb'
BEGIN
    RAISERROR(N'Đang kết nối SmartCvDb. Script này chỉ chạy trên database thử nghiệm riêng (ví dụ SmartCvDb_A5Test).', 16, 1);
    SET NOEXEC ON;
END
GO

DECLARE @Hash CHAR(64) = '810917C59EF11CDC4D9200469A7812546BEC30E26C23FBFC6E1C629351BF1CB5';

/* ---------- PHẦN 1: thêm dòng thử (chạy trước khi gửi file) ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.Cvs WHERE FileHash = @Hash)
    INSERT INTO dbo.Cvs (CvId, CandidateId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash, ExtractionStatus, UploadedAt)
    VALUES ('A5A5A5A5-0000-0000-0000-000000000001', NULL, N'CV_ThuTrung_A5.pdf', N'(chỉ để thử A5, không có file thật)',
            N'application/pdf', 1408, @Hash, N'Pending', '2026-11-05 03:00:00');

SELECT DB_NAME() AS DatabaseThu, CvId, OriginalFileName, FileHash, UploadedAt FROM dbo.Cvs WHERE FileHash = @Hash;
GO

/* ---------- PHẦN 2: XÓA dòng thử (bỏ chú thích rồi chạy SAU KHI thử xong) ----------
DELETE FROM dbo.Cvs
WHERE CvId = 'A5A5A5A5-0000-0000-0000-000000000001'
  AND FileHash = '810917C59EF11CDC4D9200469A7812546BEC30E26C23FBFC6E1C629351BF1CB5'
  AND OriginalFileName = N'CV_ThuTrung_A5.pdf';
SELECT COUNT(*) AS ConLai FROM dbo.Cvs WHERE CvId = 'A5A5A5A5-0000-0000-0000-000000000001';  -- phải ra 0
*/
GO
SET NOEXEC OFF;
GO
