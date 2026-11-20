-- =====================================================================================
-- B43 – Áp dụng migration 20261120043759_AddAuditEvents (bảng Nhật ký hoạt động).
--
-- Vì sao cần: code B43 ghi 1 dòng AuditEvents trong CÙNG transaction đăng nhập. Database
-- chưa có bảng này → SQL 208 "Invalid object name 'AuditEvents'" → DbUpdateException →
-- POST /api/auth/login trả 503 auth_unavailable cho MỌI lần đăng nhập đúng mật khẩu.
--
-- Script chỉ TẠO bảng dbo.AuditEvents + 6 index + trigger TR_AuditEvents_NoChange và ghi
-- 1 dòng __EFMigrationsHistory. Không sửa/xóa dữ liệu nào khác, không đụng tài khoản.
-- Idempotent: chạy lại khi đã áp dụng thì không làm gì.
--
-- Cách chạy (sau khi đã backup và được phê duyệt):
--   1. Sửa @ExpectedDatabase bên dưới thành đúng tên database đích (ví dụ SmartCvDb).
--   2. sqlcmd -S "localhost\BB" -E -I -b -f 65001 -d <TênDatabase> -i database\b43_add_audit_events.sql
--      (-I bắt buộc: QUOTED_IDENTIFIER ON; -b: dừng ngay khi lỗi)
-- Tương đương: dotnet ef database update 20261120043759_AddAuditEvents (cùng database đích).
-- Nội dung giữa hai dòng "EF generated" do `dotnet ef migrations script 20261119110208_AddUsers
-- 20261120043759_AddAuditEvents --idempotent` sinh ra, không sửa tay.
-- =====================================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';
IF DB_NAME() <> @ExpectedDatabase
    THROW 51044, N'B43: sai database. Sua @ExpectedDatabase thanh dung ten database dich roi chay lai.', 1;
IF SESSIONPROPERTY('QUOTED_IDENTIFIER') <> 1
    THROW 51044, N'B43: QUOTED_IDENTIFIER OFF. Chay sqlcmd voi tham so -I.', 1;
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
   OR NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261119110208_AddUsers')
    THROW 51044, N'B43: database chua co migration 20261119110208_AddUsers (A27). Ap dung cac migration truoc do truoc.', 1;
IF OBJECT_ID(N'dbo.AuditEvents', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261120043759_AddAuditEvents')
    THROW 51044, N'B43: da co bang AuditEvents nhung khong co dong lich su migration. Dung lai va kiem tra thu cong.', 1;

-- ---------------------------- EF generated (begin) ----------------------------
BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE TABLE [AuditEvents] (
        [AuditEventId] bigint NOT NULL IDENTITY,
        [OccurredAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
        [ActorUserId] int NULL,
        [ActorNameSnapshot] nvarchar(150) NULL,
        [ActorRoleSnapshot] nvarchar(20) NULL,
        [ActionCode] varchar(80) NOT NULL,
        [Category] varchar(32) NOT NULL,
        [ResourceType] varchar(40) NOT NULL,
        [ResourceId] varchar(80) NULL,
        [Outcome] varchar(20) NOT NULL,
        [RequestId] varchar(64) NOT NULL,
        [SafeSummary] nvarchar(300) NOT NULL,
        [FailureCode] varchar(64) NULL,
        CONSTRAINT [PK_AuditEvents] PRIMARY KEY ([AuditEventId]),
        CONSTRAINT [CK_AuditEvents_Actor] CHECK (([ActorUserId] IS NULL AND [ActorNameSnapshot] IS NULL AND [ActorRoleSnapshot] IS NULL) OR ([ActorUserId] IS NOT NULL AND [ActorUserId] > 0 AND [ActorNameSnapshot] IS NOT NULL AND LEN([ActorNameSnapshot]) > 0 AND [ActorRoleSnapshot] IS NOT NULL AND [ActorRoleSnapshot] COLLATE Latin1_General_100_BIN2 IN (N'Admin',N'HR'))),
        CONSTRAINT [CK_AuditEvents_Outcome] CHECK ([Outcome] COLLATE Latin1_General_100_BIN2 IN ('Succeeded','Denied','Failed','Partial','NoChange','AccessGranted')),
        CONSTRAINT [FK_AuditEvents_Users] FOREIGN KEY ([ActorUserId]) REFERENCES [Users] ([UserId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Action_Time] ON [AuditEvents] ([ActionCode], [OccurredAtUtc] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Actor_Time] ON [AuditEvents] ([ActorUserId], [OccurredAtUtc] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Category_Outcome_Time] ON [AuditEvents] ([Category], [Outcome], [OccurredAtUtc] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Request] ON [AuditEvents] ([RequestId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Resource_Time] ON [AuditEvents] ([ResourceType], [ResourceId], [OccurredAtUtc] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    CREATE INDEX [IX_AuditEvents_Time] ON [AuditEvents] ([OccurredAtUtc] DESC, [AuditEventId] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    EXEC(N'CREATE TRIGGER [dbo].[TR_AuditEvents_NoChange]
    ON [dbo].[AuditEvents] AFTER UPDATE, DELETE AS
    BEGIN
        SET NOCOUNT ON;
        IF EXISTS (SELECT 1 FROM deleted)
            THROW 51043, N''Audit events are append-only.'', 1;
    END');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261120043759_AddAuditEvents'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261120043759_AddAuditEvents', N'10.0.15');
END;

COMMIT;

-- ----------------------------- EF generated (end) -----------------------------
GO

-- Kiểm tra sau khi chạy: mong đợi 1 | 1 | 1 | 6
SELECT
    CASE WHEN OBJECT_ID(N'dbo.AuditEvents', N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasAuditEvents,
    (SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_AuditEvents_NoChange' AND parent_id = OBJECT_ID(N'dbo.AuditEvents') AND is_disabled = 0) AS HasTrigger,
    (SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20261120043759_AddAuditEvents') AS HistoryRows,
    (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AuditEvents') AND name LIKE N'IX[_]AuditEvents[_]%') AS IndexCount;
GO
