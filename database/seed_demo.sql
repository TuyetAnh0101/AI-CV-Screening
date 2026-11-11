/* =====================================================================
   seed_demo.sql - Dữ liệu DEMO cho SmartCvDb (9 bảng, 282 dòng)
     Candidates 12, Cvs 12, Applications 13, PromptVersions 2, EvaluationSets 1,
     HrLabels 10, EvaluationRuns 4, Evaluations 36, EvaluationSkills 192

   Dữ liệu NỀN (5 Jobs, 29 JobSkills, 52 JobSkillAliases) KHÔNG nằm ở đây:
   chúng được migration SeedJobs (A3) nạp tự động khi chạy "dotnet ef database update".
   Tổng sau khi chạy cả hai: 86 + 282 = 368 dòng.

   Thứ tự:  dotnet ef database update  ->  (tùy chọn) seed_demo.sql

   SNAPSHOT DEMO LỊCH SỬ: các điểm dưới đây được tạo trước quy tắc cap MustHave = 69.
   Không dùng 70%/80% làm kết quả kỳ vọng khi chấm mới bằng thuật toán hiện tại.
   Replay có thể tái tạo (đóng băng điểm/skill flags cũ, không gọi Gemini):
     dotnet test tests/SmartCv.Tests --filter FullyQualifiedName~HistoricalDemoReplayTests
   Xem docs/scoring-data-consistency.md để phân biệt replay với chấm lại CV/Gemini.

   Trọng số của snapshot lịch sử: 30% Keyword + 70% AI, CHƯA áp dụng cap
              FinalScore = ROUND(0.30 * KeywordScore + 0.70 * AiScore, 2)
              FitLabel: >= 85 Rất phù hợp, >= 70 Phù hợp, >= 55 Tiềm năng, < 55 Chưa phù hợp
              PredictedPass = FinalScore >= PassThreshold (70)

   Cách chạy:
     sqlcmd -S localhost\SQLEXPRESS -d SmartCvDb -E -C -b -I -f 65001 -i database/seed_demo.sql
       -I       : bật QUOTED_IDENTIFIER (bắt buộc vì PromptVersions có filtered index)
       -f 65001 : đọc file UTF-8 để giữ đúng tiếng Việt

   An toàn:
     - Chưa có đủ 5 job nền -> dừng, yêu cầu chạy "dotnet ef database update" trước.
     - Bất kỳ bảng demo nào đã có dữ liệu -> dừng, không chèn gì (không chạy lại lần hai).
     - Toàn bộ dữ liệu chèn trong 1 transaction, lỗi thì ROLLBACK.
   LƯU Ý: 12 CV mẫu chỉ có bản ghi trong database, không có file thật trong thư mục Uploads.
   ===================================================================== */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

USE SmartCvDb;
GO

IF (SELECT COUNT(*) FROM Jobs WHERE JobId BETWEEN 1 AND 5) < 5
BEGIN
    RAISERROR(N'Chưa có 5 job nền. Hãy chạy "dotnet ef database update" trước.', 16, 1);
    SET NOEXEC ON;
END
GO

IF EXISTS (SELECT 1 FROM Candidates) OR EXISTS (SELECT 1 FROM Cvs) OR EXISTS (SELECT 1 FROM Applications)
OR EXISTS (SELECT 1 FROM PromptVersions) OR EXISTS (SELECT 1 FROM EvaluationSets) OR EXISTS (SELECT 1 FROM HrLabels)
OR EXISTS (SELECT 1 FROM EvaluationRuns) OR EXISTS (SELECT 1 FROM Evaluations) OR EXISTS (SELECT 1 FROM EvaluationSkills)
BEGIN
    RAISERROR(N'CSDL đã có dữ liệu demo (một trong các bảng demo không trống), bỏ qua seed_demo.sql.', 16, 1);
    SET NOEXEC ON;
END
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

/* ---------- 4. Candidates (12 ứng viên) ---------- */
SET IDENTITY_INSERT Candidates ON;
INSERT INTO Candidates (CandidateId, FullName, Email, PhoneNumber) VALUES
(1, N'Nguyễn Văn An', N'an.nguyen@gmail.com', '0901234561'),
(2, N'Trần Thị Bình', N'binh.tran@gmail.com', '0912345672'),
(3, N'Lê Hoàng Cường', N'cuong.le@gmail.com', '0923456783'),
(4, N'Phạm Minh Dũng', N'dung.pham@gmail.com', '0934567894'),
(5, N'Hoàng Thị Em', N'em.hoang@gmail.com', '0945678905'),
(6, N'Võ Quốc Phong', N'phong.vo@gmail.com', '0356789016'),
(7, N'Đặng Thu Giang', N'giang.dang@gmail.com', '0367890127'),
(8, N'Bùi Thanh Hải', N'hai.bui@gmail.com', '0378901238'),
(9, N'Đỗ Ngọc Hân', N'han.do@gmail.com', '0389012349'),
(10, N'Ngô Gia Khánh', N'khanh.ngo@gmail.com', '0390123450'),
(11, N'Dương Mỹ Linh', N'linh.duong@gmail.com', NULL),
(12, N'Lý Văn Minh', NULL, '0798765432');
SET IDENTITY_INSERT Candidates OFF;

/* ---------- 5. Cvs (12 file: PDF có chữ, PDF scan, ảnh, 1 file lỗi) ---------- */
INSERT INTO Cvs (CvId, CandidateId, OriginalFileName, StoredPath, ContentType, FileSizeBytes, FileHash, ExtractedText, ExtractionMethod, ExtractionStatus, ExtractionError, UploadedAt) VALUES
('C7BA1810-54F6-5D77-A22F-DAF0023A079D', 1, N'CV_NguyenVanAn.pdf', N'Uploads/CV/2026/11/C7BA1810-54F6-5D77-A22F-DAF0023A079D.pdf', N'application/pdf', 248331, '221BD3561C9671CFD1B6E91DB2A6F78220C21F2DF158A195FA50DC6B284BD290', N'Sinh viên năm cuối CNTT, GPA 3.20. 1.5 năm làm Backend với C#, ASP.NET Core Web API, SQL Server, EF Core. Dùng Git và Docker trong dự án nhóm. Có tìm hiểu Python và Gemini API.', N'PdfText', N'Completed', NULL, '2026-11-03 09:07:00'),
('0F2BF90D-9D52-5D3B-B5C9-64EFCBCD2ED7', 2, N'CV_TranThiBinh.pdf', N'Uploads/CV/2026/11/0F2BF90D-9D52-5D3B-B5C9-64EFCBCD2ED7.pdf', N'application/pdf', 198214, '9DB39A284FC09EFA0F649227B24451343B23524FA4A26915989A54EF5F214540', N'GPA 2.90. 6 tháng thực tập lập trình C# và ASP.NET, viết truy vấn T-SQL. Dùng GitHub quản lý mã nguồn. Có kinh nghiệm viết test case.', N'PdfText', N'Completed', NULL, '2026-11-04 10:14:00'),
('8779BBF9-1E4D-5321-9337-C42A324773F2', 3, N'CV_LeHoangCuong.pdf', N'Uploads/CV/2026/11/8779BBF9-1E4D-5321-9337-C42A324773F2.pdf', N'application/pdf', 176502, '5BCFABB01F26E708EAAB6AD2F8B4E5233FD34A9CC4B58ACB2B56AF8CFDBC70F5', N'GPA 2.60. Lập trình Java, Spring Boot, MySQL. Đã làm đồ án web bán hàng. Dùng GitHub.', N'PdfText', N'Completed', NULL, '2026-11-05 11:21:00'),
('E7421830-8B15-5151-BBF1-F9D98274AD99', 4, N'CV_PhamMinhDung.pdf', N'Uploads/CV/2026/11/E7421830-8B15-5151-BBF1-F9D98274AD99.pdf', N'application/pdf', 305118, '71C0227707FB26615FD237C26BE837DD7CA541BF1F0D1CCD01927D5161E75885', N'GPA 3.40. 1 năm làm Frontend với Blazor WebAssembly, Razor, HTML5, CSS3, Bootstrap 5. Lập trình C#, có thiết kế wireframe trên Figma, biết JavaScript.', N'PdfText', N'Completed', NULL, '2026-11-02 12:28:00'),
('B4DBE125-2E0C-5D04-B603-711D91B54940', 5, N'CV_HoangThiEm.png', N'Uploads/CV/2026/11/B4DBE125-2E0C-5D04-B603-711D91B54940.png', N'image/png', 1204773, '4787584997A0226F317BE60B49337661F164284090D38BBBA74FD17DD777733B', N'GPA 2.70. Thành thạo HTML/CSS, JavaScript, Bootstrap. Đang tự học C# và Razor. Có làm landing page cá nhân.', N'ImageOcr', N'Completed', NULL, '2026-11-03 13:35:00'),
('9FC710E9-EC14-51D3-9ABE-9347D627A147', 6, N'CV_VoQuocPhong.pdf', N'Uploads/CV/2026/11/9FC710E9-EC14-51D3-9ABE-9347D627A147.pdf', N'application/pdf', 412907, '2F5B35D5040ED01074EFC7C9575EFEFA479AFCB9F14968EB5802CAF03CBB4258', N'GPA 3.60. Nghiên cứu Machine Learning và NLP bằng Python. Xây dựng chatbot dùng Gemini API, viết prompt và đánh giá kết quả. Biết SQL.', N'PdfText', N'Completed', NULL, '2026-11-04 14:42:00'),
('A541BC02-099E-5DF6-98B4-D72D4C8F14E2', 7, N'CV_DangThuGiang.jpg', N'Uploads/CV/2026/11/A541BC02-099E-5DF6-98B4-D72D4C8F14E2.jpg', N'image/jpeg', 986420, 'B7F23E7F0558CE62FE564942D3EC7104E54D468E715FBD3463D1823D376A8B39', N'GPA 3.10. Lập trình Python cơ bản, phân tích dữ liệu bằng Excel. Có tham gia CLB lập trình.', N'ImageOcr', N'Completed', NULL, '2026-11-05 15:49:00'),
('6601E62F-7977-535E-99E4-857437192366', 8, N'CV_BuiThanhHai.pdf', N'Uploads/CV/2026/11/6601E62F-7977-535E-99E4-857437192366.pdf', N'application/pdf', 2310457, 'C589E2B9901E04E91294A2D41487AA4D2159AD5D8EE5BBB3F69078C3D32BD4FA', N'GPA 2.80. 1 năm làm Tester: viết test case, kiểm thử thủ công, ghi lỗi trên Jira, kiểm thử API bằng Postman, viết truy vấn SQL. Biết Selenium cơ bản.', N'PdfOcr', N'Completed', NULL, '2026-11-02 16:56:00'),
('C0E668F3-AB0A-5C9E-932F-D619895FE6BD', 9, N'CV_DoNgocHan.pdf', N'Uploads/CV/2026/11/C0E668F3-AB0A-5C9E-932F-D619895FE6BD.pdf', N'application/pdf', 154209, '0F06FEFEAE3531B7E165E99B72DBFA78735BFAAB480CA4D7E977D7A5C65DEA65', N'GPA 3.00. Thực tập 3 tháng kiểm thử thủ công cho ứng dụng di động, viết test case. Có báo lỗi trên GitHub Issues.', N'PdfText', N'Completed', NULL, '2026-11-03 08:03:00'),
('13A513E4-1972-5C93-8C13-6F624770F864', 10, N'CV_NgoGiaKhanh.pdf', N'Uploads/CV/2026/11/13A513E4-1972-5C93-8C13-6F624770F864.pdf', N'application/pdf', 287640, 'D8C3AAB93BAA3BF18E13775CDB6614DF09DF4244F790F5402CA84E21068BAA29', N'GPA 3.30. 2 năm làm Business Analyst: viết BRD, phân tích yêu cầu, vẽ Use case và BPMN. Thuyết trình với khách hàng. Dùng SQL, Figma vẽ mockup. IELTS 7.0.', N'PdfText', N'Completed', NULL, '2026-11-04 09:10:00'),
('F65DCDA8-3A1B-50C5-A010-726C29FA1030', 11, N'CV_DuongMyLinh.pdf', N'Uploads/CV/2026/11/F65DCDA8-3A1B-50C5-A010-726C29FA1030.pdf', N'application/pdf', 132877, '3B329142456E21F555BD6ED7DCA90567B2E326C78EB6524400787A1F6D29D3BC', N'GPA 2.85. Thực tập hành chính 6 tháng, kỹ năng giao tiếp tốt, TOEIC 750. Có soạn tài liệu Requirement đơn giản.', N'PdfText', N'Completed', NULL, '2026-11-05 10:17:00'),
('E461F5AB-0EFB-598A-9C87-5C203F175EC9', 12, N'CV_LyVanMinh.pdf', N'Uploads/CV/2026/11/E461F5AB-0EFB-598A-9C87-5C203F175EC9.pdf', N'application/pdf', 88120, '04AB019529C5BA5A836A8D8A0939009EEF51F209F9770E2F3101756425F4269F', NULL, N'PdfOcr', N'Failed', N'Không đọc được nội dung: file PDF bị hỏng hoặc có mật khẩu.', '2026-11-02 11:24:00');

/* ---------- 6. Applications (gắn CV với job) ---------- */
SET IDENTITY_INSERT Applications ON;
INSERT INTO Applications (ApplicationId, CvId, JobId) VALUES
(1, 'C7BA1810-54F6-5D77-A22F-DAF0023A079D', 1),
(2, '0F2BF90D-9D52-5D3B-B5C9-64EFCBCD2ED7', 1),
(3, '8779BBF9-1E4D-5321-9337-C42A324773F2', 1),
(4, 'E7421830-8B15-5151-BBF1-F9D98274AD99', 2),
(5, 'B4DBE125-2E0C-5D04-B603-711D91B54940', 2),
(6, '9FC710E9-EC14-51D3-9ABE-9347D627A147', 3),
(7, 'A541BC02-099E-5DF6-98B4-D72D4C8F14E2', 3),
(8, '6601E62F-7977-535E-99E4-857437192366', 4),
(9, 'C0E668F3-AB0A-5C9E-932F-D619895FE6BD', 4),
(10, '13A513E4-1972-5C93-8C13-6F624770F864', 5),
(11, 'F65DCDA8-3A1B-50C5-A010-726C29FA1030', 5),
(12, 'C7BA1810-54F6-5D77-A22F-DAF0023A079D', 3),
(13, 'E461F5AB-0EFB-598A-9C87-5C203F175EC9', 1);
SET IDENTITY_INSERT Applications OFF;

/* ---------- 7. PromptVersions (chỉ 1 bản IsActive = 1) ---------- */
SET IDENTITY_INSERT PromptVersions ON;
INSERT INTO PromptVersions (PromptVersionId, VersionName, SystemPrompt, ChangeNote, IsActive, CreatedAt) VALUES
(1, N'v1-co-ban', N'Bạn là chuyên viên tuyển dụng. Đọc CV và mô tả công việc, chấm điểm mức phù hợp từ 0 đến 100. Trả về JSON gồm: score, matchedSkills, missingSkills, yearsExperience, reasoning.', N'Phiên bản đầu tiên, yêu cầu ngắn gọn.', 0, '2026-11-01 09:00:00'),
(2, N'v2-tieu-chi-ro-rang', N'Bạn là chuyên viên tuyển dụng IT. Nhiệm vụ: đánh giá CV so với job.
Quy tắc:
1. Chỉ dựa vào thông tin có trong CV, không suy đoán.
2. Kỹ năng MustHave quan trọng hơn NiceToHave; thiếu MustHave thì điểm không quá 60.
3. Kinh nghiệm thực tế (đi làm, thực tập, dự án) quan trọng hơn kỹ năng tự học.
4. Nếu CV không ghi GPA hoặc kinh nghiệm thì không trừ điểm, ghi rõ trong reasoning.
Trả về DUY NHẤT JSON: {"score": 0-100, "matchedSkills": [], "missingSkills": [], "yearsExperience": số, "reasoning": "tiếng Việt, tối đa 3 câu"}', N'Thêm quy tắc MustHave, kinh nghiệm thực tế và xử lý thiếu GPA.', 1, '2026-11-03 14:00:00');
SET IDENTITY_INSERT PromptVersions OFF;

/* ---------- 8. EvaluationSets ---------- */
SET IDENTITY_INSERT EvaluationSets ON;
INSERT INTO EvaluationSets (EvaluationSetId, Name, Description) VALUES
(1, N'Bộ CV đánh giá đợt 1', N'Demo lịch sử trước quy tắc giới hạn MustHave 69: 10 CV thuộc 5 job; 70%/80% là snapshot, không phải kết quả kỳ vọng khi chấm mới.');
SET IDENTITY_INSERT EvaluationSets OFF;

/* ---------- 9. HrLabels (6 Đạt, 4 Không đạt) ---------- */
INSERT INTO HrLabels (EvaluationSetId, ApplicationId, IsPass, Note, LabeledBy, LabeledAt) VALUES
(1, 1, 1, N'Đủ kỹ năng, mời phỏng vấn.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 2, 1, N'Còn ít kinh nghiệm nhưng nền tảng tốt, nên phỏng vấn.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 3, 0, N'Không đúng công nghệ.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 4, 1, N'Rất phù hợp.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 5, 0, N'Chưa đủ C#/Blazor.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 6, 1, N'Rất phù hợp.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 7, 0, N'Thiếu Machine Learning và LLM.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 8, 1, N'Phù hợp.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 9, 0, N'Thiếu SQL, kinh nghiệm ngắn.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00'),
(1, 10, 1, N'Rất phù hợp.', N'HR - Trần Thu Hà', '2026-11-04 10:00:00');

/* ---------- 10. EvaluationRuns ----------
   Run 1, 2: snapshot lịch sử v1 (70%) và v2 (80%); cùng bộ CV/model/trọng số/ngưỡng được lưu.
             Không có snapshot cap/thuật toán, không chứng minh tái lập với cấu hình hiện tại.
   Run 3   : sàng lọc toàn bộ CV bằng prompt đang dùng (v2)
   Run 4   : đợt sàng lọc đang chạy, dùng để hiển thị thanh tiến độ
   Cả 4 lượt: KeywordWeight = 0.30, AiWeight = 0.70, PassThreshold = 70 */
SET IDENTITY_INSERT EvaluationRuns ON;
INSERT INTO EvaluationRuns (RunId, RunType, PromptVersionId, EvaluationSetId, ModelName, Temperature, KeywordWeight, AiWeight, PassThreshold, Status, TotalItems, ProcessedItems, FailedItems, AccuracyPercent, CreatedAt, CompletedAt) VALUES
(1, N'PromptTest', 1, 1, N'gemini-2.5-flash', 0.20, 0.30, 0.70, 70.00, N'Completed', 10, 10, 0, 70.00, '2026-11-04 14:00:00', '2026-11-04 14:06:00'),
(2, N'PromptTest', 2, 1, N'gemini-2.5-flash', 0.20, 0.30, 0.70, 70.00, N'Completed', 10, 10, 0, 80.00, '2026-11-04 15:00:00', '2026-11-04 15:05:30'),
(3, N'Screening', 2, NULL, N'gemini-2.5-flash', 0.20, 0.30, 0.70, 70.00, N'Completed', 13, 13, 1, NULL, '2026-11-05 09:00:00', '2026-11-05 09:12:00'),
(4, N'Screening', 2, NULL, N'gemini-2.5-flash', 0.20, 0.30, 0.70, 70.00, N'Running', 3, 1, 0, NULL, '2026-11-06 08:30:00', NULL);
SET IDENTITY_INSERT EvaluationRuns OFF;

/* ---------- 11. Evaluations ---------- */
SET IDENTITY_INSERT Evaluations ON;
INSERT INTO Evaluations (EvaluationId, RunId, ApplicationId, KeywordScore, AiScore, FinalScore, ScoreSource, FitLabel, PredictedPass, YearsExperience, Reasoning, AiRawResponse, Status, ErrorMessage) VALUES
(1, 1, 1, 100.00, 88.00, 91.60, N'Combined', N'Rất phù hợp', 1, 1.5, N'Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng.', N'{"score": 88, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Git", "Docker"], "missingSkills": [], "yearsExperience": 1.5, "reasoning": "Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng."}', N'Completed', NULL),
(2, 1, 2, 80.00, 58.00, 64.60, N'Combined', N'Tiềm năng', 0, 0.5, N'Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core.', N'{"score": 58, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Git"], "missingSkills": ["Entity Framework Core", "Docker"], "yearsExperience": 0.5, "reasoning": "Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core."}', N'Completed', NULL),
(3, 1, 3, 10.00, 35.00, 27.50, N'Combined', N'Chưa phù hợp', 0, 0.0, N'Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu.', N'{"score": 35, "matchedSkills": ["Git"], "missingSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Docker"], "yearsExperience": 0.0, "reasoning": "Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu."}', N'Completed', NULL),
(4, 1, 4, 100.00, 90.00, 93.00, N'Combined', N'Rất phù hợp', 1, 1.0, N'Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript.', N'{"score": 90, "matchedSkills": ["Blazor", "HTML/CSS", "C#", "Bootstrap", "Figma", "JavaScript"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript."}', N'Completed', NULL),
(5, 1, 5, 66.67, 75.00, 72.50, N'Combined', N'Phù hợp', 1, 0.0, N'Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn.', N'{"score": 75, "matchedSkills": ["Blazor", "HTML/CSS", "Bootstrap", "JavaScript"], "missingSkills": ["C#", "Figma"], "yearsExperience": 0.0, "reasoning": "Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn."}', N'Completed', NULL),
(6, 1, 6, 100.00, 92.00, 94.40, N'Combined', N'Rất phù hợp', 1, 0.5, N'Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt.', N'{"score": 92, "matchedSkills": ["Python", "Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "missingSkills": [], "yearsExperience": 0.5, "reasoning": "Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt."}', N'Completed', NULL),
(7, 1, 7, 23.33, 72.00, 57.40, N'Combined', N'Tiềm năng', 0, 0.0, N'Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API.', N'{"score": 72, "matchedSkills": ["Python"], "missingSkills": ["Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "yearsExperience": 0.0, "reasoning": "Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API."}', N'Completed', NULL),
(8, 1, 8, 100.00, 82.00, 87.40, N'Combined', N'Rất phù hợp', 1, 1.0, N'Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL.', N'{"score": 82, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi", "SQL", "Kiểm thử tự động", "Kiểm thử API"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL."}', N'Completed', NULL),
(9, 1, 9, 46.67, 88.00, 75.60, N'Combined', N'Phù hợp', 1, 0.3, N'Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn.', N'{"score": 88, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi"], "missingSkills": ["SQL", "Kiểm thử tự động", "Kiểm thử API"], "yearsExperience": 0.3, "reasoning": "Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn."}', N'Completed', NULL),
(10, 1, 10, 100.00, 89.00, 92.30, N'Combined', N'Rất phù hợp', 1, 2.0, N'2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt.', N'{"score": 89, "matchedSkills": ["Phân tích yêu cầu", "UML", "Giao tiếp", "SQL", "Figma", "Tiếng Anh"], "missingSkills": [], "yearsExperience": 2.0, "reasoning": "2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt."}', N'Completed', NULL),
(11, 2, 1, 100.00, 90.00, 93.00, N'Combined', N'Rất phù hợp', 1, 1.5, N'Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng.', N'{"score": 90, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Git", "Docker"], "missingSkills": [], "yearsExperience": 1.5, "reasoning": "Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng."}', N'Completed', NULL),
(12, 2, 2, 80.00, 60.00, 66.00, N'Combined', N'Tiềm năng', 0, 0.5, N'Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core.', N'{"score": 60, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Git"], "missingSkills": ["Entity Framework Core", "Docker"], "yearsExperience": 0.5, "reasoning": "Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core."}', N'Completed', NULL),
(13, 2, 3, 10.00, 30.00, 24.00, N'Combined', N'Chưa phù hợp', 0, 0.0, N'Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu.', N'{"score": 30, "matchedSkills": ["Git"], "missingSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Docker"], "yearsExperience": 0.0, "reasoning": "Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu."}', N'Completed', NULL),
(14, 2, 4, 100.00, 92.00, 94.40, N'Combined', N'Rất phù hợp', 1, 1.0, N'Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript.', N'{"score": 92, "matchedSkills": ["Blazor", "HTML/CSS", "C#", "Bootstrap", "Figma", "JavaScript"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript."}', N'Completed', NULL),
(15, 2, 5, 66.67, 75.00, 72.50, N'Combined', N'Phù hợp', 1, 0.0, N'Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn.', N'{"score": 75, "matchedSkills": ["Blazor", "HTML/CSS", "Bootstrap", "JavaScript"], "missingSkills": ["C#", "Figma"], "yearsExperience": 0.0, "reasoning": "Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn."}', N'Completed', NULL),
(16, 2, 6, 100.00, 94.00, 95.80, N'Combined', N'Rất phù hợp', 1, 0.5, N'Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt.', N'{"score": 94, "matchedSkills": ["Python", "Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "missingSkills": [], "yearsExperience": 0.5, "reasoning": "Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt."}', N'Completed', NULL),
(17, 2, 7, 23.33, 45.00, 38.50, N'Combined', N'Chưa phù hợp', 0, 0.0, N'Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API.', N'{"score": 45, "matchedSkills": ["Python"], "missingSkills": ["Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "yearsExperience": 0.0, "reasoning": "Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API."}', N'Completed', NULL),
(18, 2, 8, 100.00, 85.00, 89.50, N'Combined', N'Rất phù hợp', 1, 1.0, N'Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL.', N'{"score": 85, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi", "SQL", "Kiểm thử tự động", "Kiểm thử API"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL."}', N'Completed', NULL),
(19, 2, 9, 46.67, 66.00, 60.20, N'Combined', N'Tiềm năng', 0, 0.3, N'Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn.', N'{"score": 66, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi"], "missingSkills": ["SQL", "Kiểm thử tự động", "Kiểm thử API"], "yearsExperience": 0.3, "reasoning": "Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn."}', N'Completed', NULL),
(20, 2, 10, 100.00, 91.00, 93.70, N'Combined', N'Rất phù hợp', 1, 2.0, N'2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt.', N'{"score": 91, "matchedSkills": ["Phân tích yêu cầu", "UML", "Giao tiếp", "SQL", "Figma", "Tiếng Anh"], "missingSkills": [], "yearsExperience": 2.0, "reasoning": "2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt."}', N'Completed', NULL),
(21, 3, 1, 100.00, 90.00, 93.00, N'Combined', N'Rất phù hợp', 1, 1.5, N'Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng.', N'{"score": 90, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Git", "Docker"], "missingSkills": [], "yearsExperience": 1.5, "reasoning": "Đáp ứng đầy đủ kỹ năng bắt buộc, có kinh nghiệm thực tế với ASP.NET Core và SQL Server, biết Docker là điểm cộng."}', N'Completed', NULL),
(22, 3, 2, 80.00, 60.00, 66.00, N'Combined', N'Tiềm năng', 0, 0.5, N'Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core.', N'{"score": 60, "matchedSkills": ["C#", "ASP.NET Core", "SQL Server", "Git"], "missingSkills": ["Entity Framework Core", "Docker"], "yearsExperience": 0.5, "reasoning": "Có nền tảng C# và ASP.NET, kinh nghiệm còn ít nhưng đúng hướng; chưa thấy Entity Framework Core."}', N'Completed', NULL),
(23, 3, 3, 10.00, 30.00, 24.00, N'Combined', N'Chưa phù hợp', 0, 0.0, N'Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu.', N'{"score": 30, "matchedSkills": ["Git"], "missingSkills": ["C#", "ASP.NET Core", "SQL Server", "Entity Framework Core", "Docker"], "yearsExperience": 0.0, "reasoning": "Kinh nghiệm chủ yếu với Java/Spring Boot, chưa có C# và SQL Server theo yêu cầu."}', N'Completed', NULL),
(24, 3, 4, 100.00, 92.00, 94.40, N'Combined', N'Rất phù hợp', 1, 1.0, N'Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript.', N'{"score": 92, "matchedSkills": ["Blazor", "HTML/CSS", "C#", "Bootstrap", "Figma", "JavaScript"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript."}', N'Completed', NULL),
(25, 3, 5, 66.67, 75.00, 72.50, N'Combined', N'Phù hợp', 1, 0.0, N'Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn.', N'{"score": 75, "matchedSkills": ["Blazor", "HTML/CSS", "Bootstrap", "JavaScript"], "missingSkills": ["C#", "Figma"], "yearsExperience": 0.0, "reasoning": "Mạnh HTML/CSS, JavaScript, Bootstrap; đã có tìm hiểu C# và Razor nên có thể đáp ứng sau thời gian ngắn."}', N'Completed', NULL),
(26, 3, 6, 100.00, 94.00, 95.80, N'Combined', N'Rất phù hợp', 1, 0.5, N'Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt.', N'{"score": 94, "matchedSkills": ["Python", "Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "missingSkills": [], "yearsExperience": 0.5, "reasoning": "Nền tảng Machine Learning và NLP tốt, đã tích hợp Gemini API và có kinh nghiệm viết prompt."}', N'Completed', NULL),
(27, 3, 7, 23.33, 45.00, 38.50, N'Combined', N'Chưa phù hợp', 0, 0.0, N'Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API.', N'{"score": 45, "matchedSkills": ["Python"], "missingSkills": ["Machine Learning", "LLM API", "Prompt Engineering", "SQL", "NLP"], "yearsExperience": 0.0, "reasoning": "Mới biết Python cơ bản, chưa có Machine Learning và kinh nghiệm với LLM API."}', N'Completed', NULL),
(28, 3, 8, 100.00, 85.00, 89.50, N'Combined', N'Rất phù hợp', 1, 1.0, N'Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL.', N'{"score": 85, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi", "SQL", "Kiểm thử tự động", "Kiểm thử API"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Có kinh nghiệm kiểm thử thủ công, quản lý lỗi trên Jira, kiểm thử API và SQL."}', N'Completed', NULL),
(29, 3, 9, 46.67, 66.00, 60.20, N'Combined', N'Tiềm năng', 0, 0.3, N'Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn.', N'{"score": 66, "matchedSkills": ["Kiểm thử thủ công", "Quản lý lỗi"], "missingSkills": ["SQL", "Kiểm thử tự động", "Kiểm thử API"], "yearsExperience": 0.3, "reasoning": "Đã viết test case và báo lỗi, nhưng chưa thấy SQL và kinh nghiệm còn ngắn."}', N'Completed', NULL),
(30, 3, 10, 100.00, 91.00, 93.70, N'Combined', N'Rất phù hợp', 1, 2.0, N'2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt.', N'{"score": 91, "matchedSkills": ["Phân tích yêu cầu", "UML", "Giao tiếp", "SQL", "Figma", "Tiếng Anh"], "missingSkills": [], "yearsExperience": 2.0, "reasoning": "2 năm làm BA, thành thạo phân tích yêu cầu, UML/BPMN, giao tiếp tốt và tiếng Anh tốt."}', N'Completed', NULL),
(31, 3, 11, 56.67, NULL, 56.67, N'KeywordOnly', N'Tiềm năng', 0, 0.5, N'Gemini vượt hạn mức sau 3 lần thử lại; điểm chỉ tính theo từ khóa.', NULL, N'Completed', NULL),
(32, 3, 12, 46.67, 58.00, 54.60, N'Combined', N'Chưa phù hợp', 0, 1.5, N'Có Python và đã dùng Gemini API, nhưng chưa có nền tảng Machine Learning.', N'{"score": 58, "matchedSkills": ["Python", "LLM API"], "missingSkills": ["Machine Learning", "Prompt Engineering", "SQL", "NLP"], "yearsExperience": 1.5, "reasoning": "Có Python và đã dùng Gemini API, nhưng chưa có nền tảng Machine Learning."}', N'Completed', NULL),
(33, 3, 13, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'Failed', N'CV chưa trích xuất được nội dung (ExtractionStatus = Failed).'),
(34, 4, 4, 100.00, 92.00, 94.40, N'Combined', N'Rất phù hợp', 1, 1.0, N'Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript.', N'{"score": 92, "matchedSkills": ["Blazor", "HTML/CSS", "C#", "Bootstrap", "Figma", "JavaScript"], "missingSkills": [], "yearsExperience": 1.0, "reasoning": "Kinh nghiệm Blazor thực tế, đủ kỹ năng bắt buộc và có cả Figma, JavaScript."}', N'Completed', NULL),
(35, 4, 6, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'Pending', NULL),
(36, 4, 8, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'Pending', NULL);
SET IDENTITY_INSERT Evaluations OFF;

/* ---------- 12. EvaluationSkills (kỹ năng đạt / thiếu của từng lần chấm) ---------- */
INSERT INTO EvaluationSkills (EvaluationId, JobId, SkillCode, IsMatched, Evidence) VALUES
(1, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(1, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(1, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(1, 1, N'efcore', 1, N'CV có nhắc đến Entity Framework Core.'),
(1, 1, N'git', 1, N'CV có nhắc đến Git.'),
(1, 1, N'docker', 1, N'CV có nhắc đến Docker.'),
(2, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(2, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(2, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(2, 1, N'efcore', 0, NULL),
(2, 1, N'git', 1, N'CV có nhắc đến Git.'),
(2, 1, N'docker', 0, NULL),
(3, 1, N'csharp', 0, NULL),
(3, 1, N'aspnetcore', 0, NULL),
(3, 1, N'sqlserver', 0, NULL),
(3, 1, N'efcore', 0, NULL),
(3, 1, N'git', 1, N'CV có nhắc đến Git.'),
(3, 1, N'docker', 0, NULL),
(4, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(4, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(4, 2, N'csharp', 1, N'CV có nhắc đến C#.'),
(4, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(4, 2, N'figma', 1, N'CV có nhắc đến Figma.'),
(4, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(5, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(5, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(5, 2, N'csharp', 0, NULL),
(5, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(5, 2, N'figma', 0, NULL),
(5, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(6, 3, N'python', 1, N'CV có nhắc đến Python.'),
(6, 3, N'machine_learning', 1, N'CV có nhắc đến Machine Learning.'),
(6, 3, N'llm_api', 1, N'CV có nhắc đến LLM API.'),
(6, 3, N'prompt_engineering', 1, N'CV có nhắc đến Prompt Engineering.'),
(6, 3, N'sql', 1, N'CV có nhắc đến SQL.'),
(6, 3, N'nlp', 1, N'CV có nhắc đến NLP.'),
(7, 3, N'python', 1, N'CV có nhắc đến Python.'),
(7, 3, N'machine_learning', 0, NULL),
(7, 3, N'llm_api', 0, NULL),
(7, 3, N'prompt_engineering', 0, NULL),
(7, 3, N'sql', 0, NULL),
(7, 3, N'nlp', 0, NULL),
(8, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(8, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(8, 4, N'sql', 1, N'CV có nhắc đến SQL.'),
(8, 4, N'automation_testing', 1, N'CV có nhắc đến Kiểm thử tự động.'),
(8, 4, N'api_testing', 1, N'CV có nhắc đến Kiểm thử API.'),
(9, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(9, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(9, 4, N'sql', 0, NULL),
(9, 4, N'automation_testing', 0, NULL),
(9, 4, N'api_testing', 0, NULL),
(10, 5, N'requirement_analysis', 1, N'CV có nhắc đến Phân tích yêu cầu.'),
(10, 5, N'uml', 1, N'CV có nhắc đến UML.'),
(10, 5, N'communication', 1, N'CV có nhắc đến Giao tiếp.'),
(10, 5, N'sql', 1, N'CV có nhắc đến SQL.'),
(10, 5, N'figma', 1, N'CV có nhắc đến Figma.'),
(10, 5, N'english', 1, N'CV có nhắc đến Tiếng Anh.'),
(11, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(11, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(11, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(11, 1, N'efcore', 1, N'CV có nhắc đến Entity Framework Core.'),
(11, 1, N'git', 1, N'CV có nhắc đến Git.'),
(11, 1, N'docker', 1, N'CV có nhắc đến Docker.'),
(12, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(12, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(12, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(12, 1, N'efcore', 0, NULL),
(12, 1, N'git', 1, N'CV có nhắc đến Git.'),
(12, 1, N'docker', 0, NULL),
(13, 1, N'csharp', 0, NULL),
(13, 1, N'aspnetcore', 0, NULL),
(13, 1, N'sqlserver', 0, NULL),
(13, 1, N'efcore', 0, NULL),
(13, 1, N'git', 1, N'CV có nhắc đến Git.'),
(13, 1, N'docker', 0, NULL),
(14, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(14, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(14, 2, N'csharp', 1, N'CV có nhắc đến C#.'),
(14, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(14, 2, N'figma', 1, N'CV có nhắc đến Figma.'),
(14, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(15, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(15, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(15, 2, N'csharp', 0, NULL),
(15, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(15, 2, N'figma', 0, NULL),
(15, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(16, 3, N'python', 1, N'CV có nhắc đến Python.'),
(16, 3, N'machine_learning', 1, N'CV có nhắc đến Machine Learning.'),
(16, 3, N'llm_api', 1, N'CV có nhắc đến LLM API.'),
(16, 3, N'prompt_engineering', 1, N'CV có nhắc đến Prompt Engineering.'),
(16, 3, N'sql', 1, N'CV có nhắc đến SQL.'),
(16, 3, N'nlp', 1, N'CV có nhắc đến NLP.'),
(17, 3, N'python', 1, N'CV có nhắc đến Python.'),
(17, 3, N'machine_learning', 0, NULL),
(17, 3, N'llm_api', 0, NULL),
(17, 3, N'prompt_engineering', 0, NULL),
(17, 3, N'sql', 0, NULL),
(17, 3, N'nlp', 0, NULL),
(18, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(18, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(18, 4, N'sql', 1, N'CV có nhắc đến SQL.'),
(18, 4, N'automation_testing', 1, N'CV có nhắc đến Kiểm thử tự động.'),
(18, 4, N'api_testing', 1, N'CV có nhắc đến Kiểm thử API.'),
(19, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(19, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(19, 4, N'sql', 0, NULL),
(19, 4, N'automation_testing', 0, NULL),
(19, 4, N'api_testing', 0, NULL),
(20, 5, N'requirement_analysis', 1, N'CV có nhắc đến Phân tích yêu cầu.'),
(20, 5, N'uml', 1, N'CV có nhắc đến UML.'),
(20, 5, N'communication', 1, N'CV có nhắc đến Giao tiếp.'),
(20, 5, N'sql', 1, N'CV có nhắc đến SQL.'),
(20, 5, N'figma', 1, N'CV có nhắc đến Figma.'),
(20, 5, N'english', 1, N'CV có nhắc đến Tiếng Anh.'),
(21, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(21, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(21, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(21, 1, N'efcore', 1, N'CV có nhắc đến Entity Framework Core.'),
(21, 1, N'git', 1, N'CV có nhắc đến Git.'),
(21, 1, N'docker', 1, N'CV có nhắc đến Docker.'),
(22, 1, N'csharp', 1, N'CV có nhắc đến C#.'),
(22, 1, N'aspnetcore', 1, N'CV có nhắc đến ASP.NET Core.'),
(22, 1, N'sqlserver', 1, N'CV có nhắc đến SQL Server.'),
(22, 1, N'efcore', 0, NULL),
(22, 1, N'git', 1, N'CV có nhắc đến Git.'),
(22, 1, N'docker', 0, NULL),
(23, 1, N'csharp', 0, NULL),
(23, 1, N'aspnetcore', 0, NULL),
(23, 1, N'sqlserver', 0, NULL),
(23, 1, N'efcore', 0, NULL),
(23, 1, N'git', 1, N'CV có nhắc đến Git.'),
(23, 1, N'docker', 0, NULL),
(24, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(24, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(24, 2, N'csharp', 1, N'CV có nhắc đến C#.'),
(24, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(24, 2, N'figma', 1, N'CV có nhắc đến Figma.'),
(24, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(25, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(25, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(25, 2, N'csharp', 0, NULL),
(25, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(25, 2, N'figma', 0, NULL),
(25, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.'),
(26, 3, N'python', 1, N'CV có nhắc đến Python.'),
(26, 3, N'machine_learning', 1, N'CV có nhắc đến Machine Learning.'),
(26, 3, N'llm_api', 1, N'CV có nhắc đến LLM API.'),
(26, 3, N'prompt_engineering', 1, N'CV có nhắc đến Prompt Engineering.'),
(26, 3, N'sql', 1, N'CV có nhắc đến SQL.'),
(26, 3, N'nlp', 1, N'CV có nhắc đến NLP.'),
(27, 3, N'python', 1, N'CV có nhắc đến Python.'),
(27, 3, N'machine_learning', 0, NULL),
(27, 3, N'llm_api', 0, NULL),
(27, 3, N'prompt_engineering', 0, NULL),
(27, 3, N'sql', 0, NULL),
(27, 3, N'nlp', 0, NULL),
(28, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(28, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(28, 4, N'sql', 1, N'CV có nhắc đến SQL.'),
(28, 4, N'automation_testing', 1, N'CV có nhắc đến Kiểm thử tự động.'),
(28, 4, N'api_testing', 1, N'CV có nhắc đến Kiểm thử API.'),
(29, 4, N'manual_testing', 1, N'CV có nhắc đến Kiểm thử thủ công.'),
(29, 4, N'bug_tracking', 1, N'CV có nhắc đến Quản lý lỗi.'),
(29, 4, N'sql', 0, NULL),
(29, 4, N'automation_testing', 0, NULL),
(29, 4, N'api_testing', 0, NULL),
(30, 5, N'requirement_analysis', 1, N'CV có nhắc đến Phân tích yêu cầu.'),
(30, 5, N'uml', 1, N'CV có nhắc đến UML.'),
(30, 5, N'communication', 1, N'CV có nhắc đến Giao tiếp.'),
(30, 5, N'sql', 1, N'CV có nhắc đến SQL.'),
(30, 5, N'figma', 1, N'CV có nhắc đến Figma.'),
(30, 5, N'english', 1, N'CV có nhắc đến Tiếng Anh.'),
(31, 5, N'requirement_analysis', 1, N'CV có nhắc đến Phân tích yêu cầu.'),
(31, 5, N'uml', 0, NULL),
(31, 5, N'communication', 1, N'CV có nhắc đến Giao tiếp.'),
(31, 5, N'sql', 0, NULL),
(31, 5, N'figma', 0, NULL),
(31, 5, N'english', 1, N'CV có nhắc đến Tiếng Anh.'),
(32, 3, N'python', 1, N'CV có nhắc đến Python.'),
(32, 3, N'machine_learning', 0, NULL),
(32, 3, N'llm_api', 1, N'CV có nhắc đến LLM API.'),
(32, 3, N'prompt_engineering', 0, NULL),
(32, 3, N'sql', 0, NULL),
(32, 3, N'nlp', 0, NULL),
(34, 2, N'blazor', 1, N'CV có nhắc đến Blazor.'),
(34, 2, N'html_css', 1, N'CV có nhắc đến HTML/CSS.'),
(34, 2, N'csharp', 1, N'CV có nhắc đến C#.'),
(34, 2, N'bootstrap', 1, N'CV có nhắc đến Bootstrap.'),
(34, 2, N'figma', 1, N'CV có nhắc đến Figma.'),
(34, 2, N'javascript', 1, N'CV có nhắc đến JavaScript.');

COMMIT TRANSACTION;
GO
SET NOEXEC OFF;
GO

/* ---------- Kiểm tra nhanh ---------- */
SELECT 'Jobs' AS Bang, COUNT(*) AS SoDong FROM Jobs
UNION ALL SELECT 'JobSkills', COUNT(*) FROM JobSkills
UNION ALL SELECT 'JobSkillAliases', COUNT(*) FROM JobSkillAliases
UNION ALL SELECT 'Candidates', COUNT(*) FROM Candidates
UNION ALL SELECT 'Cvs', COUNT(*) FROM Cvs
UNION ALL SELECT 'Applications', COUNT(*) FROM Applications
UNION ALL SELECT 'PromptVersions', COUNT(*) FROM PromptVersions
UNION ALL SELECT 'EvaluationSets', COUNT(*) FROM EvaluationSets
UNION ALL SELECT 'HrLabels', COUNT(*) FROM HrLabels
UNION ALL SELECT 'EvaluationRuns', COUNT(*) FROM EvaluationRuns
UNION ALL SELECT 'Evaluations', COUNT(*) FROM Evaluations
UNION ALL SELECT 'EvaluationSkills', COUNT(*) FROM EvaluationSkills;

-- Bảng xếp hạng theo job của đợt sàng lọc 3
SELECT j.Title, RANK() OVER (PARTITION BY j.JobId ORDER BY e.FinalScore DESC) AS Hang,
       c.OriginalFileName, e.FinalScore, e.FitLabel
FROM Evaluations e
JOIN Applications a ON a.ApplicationId = e.ApplicationId
JOIN Cvs c ON c.CvId = a.CvId
JOIN Jobs j ON j.JobId = a.JobId
WHERE e.RunId = 3 AND e.Status = N'Completed'
ORDER BY j.JobId, Hang;

-- So sánh tỷ lệ đúng giữa các phiên bản prompt
SELECT p.VersionName, r.AccuracyPercent,
       SUM(CASE WHEN h.IsPass = 1 AND e.PredictedPass = 0 THEN 1 ELSE 0 END) AS PhuHopBiBoSot,
       SUM(CASE WHEN h.IsPass = 0 AND e.PredictedPass = 1 THEN 1 ELSE 0 END) AS KhongPhuHopBiChonNham
FROM EvaluationRuns r
JOIN PromptVersions p ON p.PromptVersionId = r.PromptVersionId
JOIN Evaluations e ON e.RunId = r.RunId
JOIN HrLabels h ON h.EvaluationSetId = r.EvaluationSetId AND h.ApplicationId = e.ApplicationId
WHERE r.RunType = N'PromptTest'
GROUP BY p.VersionName, r.AccuracyPercent;
GO
