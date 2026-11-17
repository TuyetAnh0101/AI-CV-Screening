-- A20 verification. ONLY run on a generated SmartCv_A20_Test_<32 hex GUID> catalog.
-- This script never commits a rename or duplicate attempt and refuses nested transactions.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF LEFT(DB_NAME(), LEN(N'SmartCv_A20_Test_')) <> N'SmartCv_A20_Test_'
   OR LEN(DB_NAME()) <> LEN(N'SmartCv_A20_Test_') + 32
   OR RIGHT(DB_NAME(),32) COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-f]%'
    THROW 52020, N'A20: chỉ chạy trên catalog thử nghiệm mới có GUID.', 1;
IF @@TRANCOUNT <> 0
    THROW 52021, N'A20: cần phiên kiểm thử không có transaction đang mở.', 1;
IF (SELECT COUNT(*) FROM dbo.Jobs) < 2
    THROW 52022, N'A20: cần ít nhất hai Job khác tên.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Jobs')
    AND name=N'UQ_Jobs_Title' AND is_unique=1 AND is_unique_constraint=0)
    THROW 52023, N'A20: UQ_Jobs_Title chưa phải UNIQUE INDEX.', 1;

SELECT name,type_desc,is_unique,is_unique_constraint FROM sys.indexes
WHERE object_id=OBJECT_ID(N'dbo.Jobs') AND name=N'UQ_Jobs_Title';

DECLARE @First int = (SELECT MIN(JobId) FROM dbo.Jobs);
DECLARE @Second int = (SELECT MIN(JobId) FROM dbo.Jobs WHERE JobId > @First);
DECLARE @Original nvarchar(150) = (SELECT Title FROM dbo.Jobs WHERE JobId=@First);
DECLARE @SecondOriginal nvarchar(150) = (SELECT Title FROM dbo.Jobs WHERE JobId=@Second);
DECLARE @TestTitle nvarchar(150) = N'A20 rename ' + CONVERT(nvarchar(36),NEWID());
DECLARE @JobCount int = (SELECT COUNT(*) FROM dbo.Jobs);
DECLARE @DuplicateError int = 0;

BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.Jobs SET Title=@TestTitle WHERE JobId=@First;
    IF @@ROWCOUNT <> 1 OR NOT EXISTS (SELECT 1 FROM dbo.Jobs WHERE JobId=@First AND Title=@TestTitle)
        THROW 52024, N'A20: đổi tên không thành công.', 1;
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.Jobs SET Title=@Original WHERE JobId=@Second;
    ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    SET @DuplicateError=ERROR_NUMBER();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
END CATCH;
IF @DuplicateError <> 2601
    THROW 52025, N'A20: tên trùng phải bị UNIQUE INDEX chặn bằng lỗi 2601.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.Jobs WHERE JobId=@First AND Title COLLATE Latin1_General_100_BIN2=@Original COLLATE Latin1_General_100_BIN2)
   OR NOT EXISTS (SELECT 1 FROM dbo.Jobs WHERE JobId=@Second AND Title COLLATE Latin1_General_100_BIN2=@SecondOriginal COLLATE Latin1_General_100_BIN2)
   OR (SELECT COUNT(*) FROM dbo.Jobs) <> @JobCount
    THROW 52026, N'A20: dữ liệu Job sau ROLLBACK không khớp.', 1;
SELECT DB_NAME() AS Catalog, CAST(1 AS bit) AS RenameRolledBack, @DuplicateError AS DuplicateError,
    @JobCount AS JobsPreserved;
