/* A27: chỉ đọc. Xác nhận đúng server/catalog trước khi áp dụng migration đã được duyệt.
   Không xuất PasswordHash, SecurityStamp, mật khẩu hoặc dữ liệu CV. */
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS CatalogName;

SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;

SELECT c.name AS ColumnName, t.name AS SqlType, c.max_length AS MaxLengthBytes,
       c.is_nullable, c.is_identity
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.Users')
ORDER BY c.column_id;

SELECT name, definition FROM sys.check_constraints
WHERE parent_object_id IN (OBJECT_ID(N'dbo.Users'), OBJECT_ID(N'dbo.ApplicationEvents'));

SELECT name, is_unique FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.Users') OR
      (object_id = OBJECT_ID(N'dbo.ApplicationEvents') AND name = N'IX_ApplicationEvents_UserId');

SELECT name, delete_referential_action_desc FROM sys.foreign_keys
WHERE name = N'FK_ApplicationEvents_Users';

SELECT name, is_disabled FROM sys.triggers
WHERE parent_id = OBJECT_ID(N'dbo.ApplicationEvents');

SELECT COUNT(*) AS ActiveAdminCount FROM dbo.Users WHERE Role = N'Admin' AND IsActive = 1;
SELECT UserId, Role, IsActive, MustChangePassword, CreatedAt, LastLoginAt,
       LEN(PasswordHash) AS HashLength
FROM dbo.Users ORDER BY UserId;

-- A27 không backfill UserId. Mọi lịch sử trước A27 phải còn nguyên và có UserId NULL.
SELECT COUNT_BIG(*) AS HistoryCount,
       SUM(CASE WHEN UserId IS NULL THEN CONVERT(bigint, 1) ELSE 0 END) AS HistoryWithoutAccount
FROM dbo.ApplicationEvents;
