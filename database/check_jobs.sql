/* Kiểm tra A3: 5 job, kỹ năng và từ khóa đã được migration SeedJobs nạp đúng.
   Chạy sau "dotnet ef database update" (chưa cần chạy seed_demo.sql). */
USE SmartCvDb;
GO

-- 1. Migration đã chạy: phải thấy ..._InitialCreate và ..._SeedJobs
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;

-- 2. Số dòng: Jobs 5, JobSkills 29, JobSkillAliases 52
SELECT N'Jobs' AS Bang, COUNT(*) AS ThucTe, 5 AS MongDoi FROM Jobs
UNION ALL SELECT N'JobSkills', COUNT(*), 29 FROM JobSkills
UNION ALL SELECT N'JobSkillAliases', COUNT(*), 52 FROM JobSkillAliases;

-- 3. Mỗi job: 3 MustHave; NiceToHave = 3 (riêng Tester là 2); job nào cũng đang mở
SELECT j.JobId, j.Title, j.IsActive, j.MinGpa, j.MinYearsExperience,
       SUM(CASE WHEN s.SkillType = N'MustHave'   THEN 1 ELSE 0 END) AS MustHave,
       SUM(CASE WHEN s.SkillType = N'NiceToHave' THEN 1 ELSE 0 END) AS NiceToHave,
       (SELECT COUNT(*) FROM JobSkillAliases a WHERE a.JobId = j.JobId) AS SoTuKhoa
FROM Jobs j
LEFT JOIN JobSkills s ON s.JobId = j.JobId
GROUP BY j.JobId, j.Title, j.IsActive, j.MinGpa, j.MinYearsExperience
ORDER BY j.JobId;

-- 4. Kỹ năng không có từ khóa tương đương: phải trả về 0 dòng
SELECT s.JobId, s.SkillCode, s.DisplayName
FROM JobSkills s
WHERE NOT EXISTS (SELECT 1 FROM JobSkillAliases a WHERE a.JobId = s.JobId AND a.SkillCode = s.SkillCode);

-- 5. Tiếng Việt hiển thị đúng (không bị dấu ?): phải thấy "Kiểm thử thủ công", "Học máy"...
SELECT TOP 5 s.JobId, s.DisplayName, a.Alias
FROM JobSkills s JOIN JobSkillAliases a ON a.JobId = s.JobId AND a.SkillCode = s.SkillCode
WHERE s.JobId IN (3, 4) ORDER BY s.JobId;
GO
