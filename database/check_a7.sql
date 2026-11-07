/* A7 – Kiểm tra kết quả đọc chữ CV. CHỈ ĐỌC (SELECT), không sửa dữ liệu.
   Chọn database cần xem trong SSMS (ô "Available Databases") hoặc chạy:
     sqlcmd -S localhost\BB -E -C -I -d SmartCvDb -f 65001 -i database/check_a7.sql
   Chạy lại vài lần sau khi tải CV: CV scan/ảnh cần vài giây mới đọc xong. */
SET NOCOUNT ON;

SELECT TOP 20
    OriginalFileName,
    ContentType,
    ExtractionStatus,            -- Pending -> Completed / Failed
    ExtractionMethod,            -- PdfText | PdfOcr | ImageOcr
    LEN(ExtractedText) AS SoKyTu,
    LEFT(REPLACE(REPLACE(ExtractedText, CHAR(10), ' | '), CHAR(13), ''), 120) AS DoanDau,
    ExtractionError,
    UploadedAt
FROM Cvs
WHERE CandidateId IS NULL        -- chỉ CV tự tải lên, bỏ qua 12 CV demo
ORDER BY UploadedAt DESC;

-- Tổng hợp theo trạng thái / cách đọc
SELECT ExtractionStatus, ExtractionMethod, COUNT(*) AS SoCv
FROM Cvs
WHERE CandidateId IS NULL
GROUP BY ExtractionStatus, ExtractionMethod
ORDER BY ExtractionStatus, ExtractionMethod;
