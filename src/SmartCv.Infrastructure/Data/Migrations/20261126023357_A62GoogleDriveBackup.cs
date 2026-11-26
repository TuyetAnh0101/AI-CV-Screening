using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartCv.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class A62GoogleDriveBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDriveConnections",
                columns: table => new
                {
                    BackupDriveConnectionId = table.Column<byte>(type: "tinyint", nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AccountEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    FolderId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    FolderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedByUserId = table.Column<int>(type: "int", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCheckOk = table.Column<bool>(type: "bit", nullable: true),
                    LastCheckError = table.Column<string>(type: "varchar(44)", unicode: false, maxLength: 44, nullable: true),
                    QuotaUsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    QuotaLimitBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDriveConnections", x => x.BackupDriveConnectionId);
                    table.CheckConstraint("CK_BackupDriveConnections_Single", "[BackupDriveConnectionId] = 1");
                });

            migrationBuilder.CreateTable(
                name: "BackupJobs",
                columns: table => new
                {
                    BackupJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: true),
                    NotBeforeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScheduledSlotUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    RetryOfJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Stage = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Progress = table.Column<byte>(type: "tinyint", nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(44)", unicode: false, maxLength: 44, nullable: true),
                    BackupPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StopAtServerTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TargetDatabase = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    DatabaseBytes = table.Column<long>(type: "bigint", nullable: true),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: true),
                    CvFiles = table.Column<int>(type: "int", nullable: true),
                    NewCvFiles = table.Column<int>(type: "int", nullable: true),
                    NewCvBytes = table.Column<long>(type: "bigint", nullable: true),
                    WorkerMachine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupJobs", x => x.BackupJobId);
                    table.CheckConstraint("CK_BackupJobs_Error", "[ErrorCode] IS NULL OR [ErrorCode] LIKE 'A62[_]%'");
                    table.CheckConstraint("CK_BackupJobs_Progress", "[Progress] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_BackupJobs_Status", "[Status] IN ('Queued','Running','Succeeded','Failed','Missed','Cancelled')");
                    table.CheckConstraint("CK_BackupJobs_Trigger", "[Trigger] IN ('scheduled','catch-up','retry','manual')");
                    table.CheckConstraint("CK_BackupJobs_Type", "[Type] IN ('full','log','verify','restore-drill','recovery','catalog-sync')");
                });

            migrationBuilder.CreateTable(
                name: "BackupObjects",
                columns: table => new
                {
                    ObjectKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    FileId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    PlainBytes = table.Column<long>(type: "bigint", nullable: false),
                    CipherBytes = table.Column<long>(type: "bigint", nullable: false),
                    CipherSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupObjects", x => x.ObjectKey);
                });

            migrationBuilder.CreateTable(
                name: "BackupPoints",
                columns: table => new
                {
                    BackupPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BackupJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstLsn = table.Column<decimal>(type: "decimal(25,0)", nullable: true),
                    LastLsn = table.Column<decimal>(type: "decimal(25,0)", nullable: true),
                    SqlFinishedServerTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    KeyId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ManifestFileId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    ManifestCipherBytes = table.Column<long>(type: "bigint", nullable: false),
                    PayloadFileId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    PayloadPlainBytes = table.Column<long>(type: "bigint", nullable: false),
                    PayloadCipherBytes = table.Column<long>(type: "bigint", nullable: false),
                    PayloadCipherSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    KeysFileId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    KeysCipherBytes = table.Column<long>(type: "bigint", nullable: true),
                    CvFiles = table.Column<int>(type: "int", nullable: false),
                    CvBytes = table.Column<long>(type: "bigint", nullable: false),
                    KnownMissingCvs = table.Column<int>(type: "int", nullable: false),
                    Consistency = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VerifyResult = table.Column<string>(type: "varchar(44)", unicode: false, maxLength: 44, nullable: true),
                    RestoreTestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RestoreTestResult = table.Column<string>(type: "varchar(44)", unicode: false, maxLength: 44, nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPoints", x => x.BackupPointId);
                    table.CheckConstraint("CK_BackupPoints_Kind", "[Kind] IN ('Full','Log')");
                    table.CheckConstraint("CK_BackupPoints_Status", "[Status] IN ('Available','Deleted','Damaged')");
                });

            migrationBuilder.CreateTable(
                name: "BackupSettings",
                columns: table => new
                {
                    BackupSettingsId = table.Column<byte>(type: "tinyint", nullable: false),
                    FullTimeLocal = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    LogIntervalHours = table.Column<int>(type: "int", nullable: false),
                    RetentionCount = table.Column<int>(type: "int", nullable: false),
                    Paused = table.Column<bool>(type: "bit", nullable: false),
                    WeeklyRestoreDrill = table.Column<bool>(type: "bit", nullable: false),
                    LastHandledFullSlotUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSettings", x => x.BackupSettingsId);
                    table.CheckConstraint("CK_BackupSettings_Limits", "[LogIntervalHours] BETWEEN 1 AND 24 AND [RetentionCount] BETWEEN 7 AND 90 AND [FullTimeLocal] LIKE '[0-2][0-9]:[0-5][0-9]'");
                    table.CheckConstraint("CK_BackupSettings_Single", "[BackupSettingsId] = 1");
                });

            migrationBuilder.CreateTable(
                name: "BackupWorkerHeartbeats",
                columns: table => new
                {
                    Role = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    HeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Ready = table.Column<bool>(type: "bit", nullable: false),
                    Problem = table.Column<string>(type: "varchar(44)", unicode: false, maxLength: 44, nullable: true),
                    KeyId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    KeyEscrowConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StagingFreeBytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupWorkerHeartbeats", x => x.Role);
                    table.CheckConstraint("CK_BackupWorkerHeartbeats_Role", "[Role] IN ('backup','restore')");
                });

            migrationBuilder.CreateTable(
                name: "BackupPointObjects",
                columns: table => new
                {
                    BackupPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectKey = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPointObjects", x => new { x.BackupPointId, x.ObjectKey });
                    table.ForeignKey(
                        name: "FK_BackupPointObjects_BackupObjects_ObjectKey",
                        column: x => x.ObjectKey,
                        principalTable: "BackupObjects",
                        principalColumn: "ObjectKey",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackupPointObjects_BackupPoints_BackupPointId",
                        column: x => x.BackupPointId,
                        principalTable: "BackupPoints",
                        principalColumn: "BackupPointId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "BackupSettings",
                columns: new[] { "BackupSettingsId", "FullTimeLocal", "LastHandledFullSlotUtc", "LogIntervalHours", "Paused", "RetentionCount", "UpdatedAtUtc", "UpdatedByUserId", "WeeklyRestoreDrill" },
                values: new object[] { (byte)1, "01:30", null, 4, false, 14, new DateTime(2026, 11, 26, 0, 0, 0, 0, DateTimeKind.Utc), null, true });

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_RequestedAtUtc",
                table: "BackupJobs",
                column: "RequestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_BackupJobs_ActiveType",
                table: "BackupJobs",
                column: "Type",
                unique: true,
                filter: "[Status] IN ('Queued','Running')");

            migrationBuilder.CreateIndex(
                name: "UX_BackupJobs_Slot",
                table: "BackupJobs",
                columns: new[] { "Type", "ScheduledSlotUtc" },
                unique: true,
                filter: "[ScheduledSlotUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPointObjects_ObjectKey",
                table: "BackupPointObjects",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPoints_Kind_Status",
                table: "BackupPoints",
                columns: new[] { "Kind", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_BackupPoints_Manifest",
                table: "BackupPoints",
                column: "ManifestFileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDriveConnections");

            migrationBuilder.DropTable(
                name: "BackupJobs");

            migrationBuilder.DropTable(
                name: "BackupPointObjects");

            migrationBuilder.DropTable(
                name: "BackupSettings");

            migrationBuilder.DropTable(
                name: "BackupWorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "BackupObjects");

            migrationBuilder.DropTable(
                name: "BackupPoints");
        }
    }
}
