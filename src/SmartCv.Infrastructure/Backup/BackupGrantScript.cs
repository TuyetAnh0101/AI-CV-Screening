using System.Security.Cryptography;
using System.Text;

namespace SmartCv.Infrastructure.Backup;

/// <summary>
/// Least-privilege SQL for the two worker accounts, as text for review. Batches are separated by a line "GO".
///
/// Restore claim: a database restored by an account that only holds CREATE ANY DATABASE keeps the source's dbo,
/// so that account cannot check or drop what it just restored. The optional procedure below makes the caller owner
/// of exactly such a database: its name must be SmartCv_Restore_{32 hex} and the caller must be the login SQL
/// Server recorded as the one who restored it. The procedure is signed with a certificate whose private key is
/// removed immediately, so nothing else can ever be signed with it.
/// </summary>
public static class BackupGrantScript
{
    public const string ClaimProcedure = "SCApp_ClaimRestoredDatabase";
    private const string Certificate = "SCAppRestoreClaimCert";
    private const string Signer = "SCAppRestoreClaimSigner";

    private static string Id(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string Lit(string name) => "N'" + name.Replace("'", "''") + "'";

    public static string Build(string database, string backupAccount, string restoreAccount, bool restoreClaim)
    {
        var sql = new StringBuilder();
        void Line(string text) => sql.Append(text).Append('\n');
        void Go() => Line("GO");
        var accounts = new[] { backupAccount, restoreAccount }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tables = new[] { "BackupJobs", "BackupPoints", "BackupObjects", "BackupPointObjects", "BackupSettings", "BackupWorkerHeartbeats", "BackupDriveConnections" };

        foreach (var account in accounts)
        {
            Line("USE master;");
            Line($"IF SUSER_ID({Lit(account)}) IS NULL CREATE LOGIN {Id(account)} FROM WINDOWS;");
            Go();
            Line($"USE {Id(database)};");
            Line($"IF USER_ID({Lit(account)}) IS NULL CREATE USER {Id(account)} FOR LOGIN {Id(account)};");
            Go();
        }
        Line($"USE {Id(database)};");
        foreach (var table in tables)
            foreach (var account in accounts) Line($"GRANT SELECT ON dbo.{table} TO {Id(account)};");
        foreach (var table in tables.Except(["BackupDriveConnections", "BackupSettings"]))
            Line($"GRANT INSERT, UPDATE ON dbo.{table} TO {Id(backupAccount)};");
        Line($"GRANT UPDATE ON dbo.BackupSettings TO {Id(backupAccount)};");
        Line($"GRANT UPDATE ON dbo.BackupDriveConnections TO {Id(backupAccount)};");
        Line($"GRANT INSERT, UPDATE ON dbo.BackupJobs TO {Id(restoreAccount)};");
        Line($"GRANT UPDATE ON dbo.BackupPoints TO {Id(restoreAccount)};");
        Line($"GRANT INSERT, UPDATE ON dbo.BackupWorkerHeartbeats TO {Id(restoreAccount)};");
        foreach (var account in accounts)
        {
            Line($"GRANT INSERT ON dbo.AuditEvents TO {Id(account)};");
            Line($"GRANT SELECT ON dbo.AuditEvents (AuditEventId) TO {Id(account)};");
        }
        Line($"ALTER ROLE db_backupoperator ADD MEMBER {Id(backupAccount)};");
        Line($"GRANT SELECT ON dbo.Cvs (CvId, StoredPath, FileHash, FileSizeBytes, AnonymizedAt) TO {Id(backupAccount)};");
        Line($"GRANT SELECT ON dbo.__EFMigrationsHistory TO {Id(backupAccount)};");
        Go();
        Line("USE msdb;");
        Line($"IF USER_ID({Lit(backupAccount)}) IS NULL CREATE USER {Id(backupAccount)} FOR LOGIN {Id(backupAccount)};");
        Line($"GRANT SELECT ON dbo.backupset TO {Id(backupAccount)};");
        Line($"GRANT SELECT ON dbo.backupmediafamily TO {Id(backupAccount)};");
        Go();
        Line("USE master;");
        Line($"GRANT CREATE ANY DATABASE TO {Id(restoreAccount)};");
        Go();
        if (restoreClaim) AppendClaim(sql, restoreAccount);
        return sql.ToString();
    }

    private static void AppendClaim(StringBuilder sql, string restoreAccount)
    {
        void Line(string text) => sql.Append(text).Append('\n');
        void Go() => Line("GO");
        // Only protects the private key between CREATE and REMOVE PRIVATE KEY below.
        var password = "Sc!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a9";
        Line("USE master;");
        Line($"IF OBJECT_ID(N'dbo.{ClaimProcedure}', N'P') IS NOT NULL DROP PROCEDURE dbo.{ClaimProcedure};");
        Line($"IF SUSER_ID(N'{Signer}') IS NOT NULL DROP LOGIN {Signer};");
        Line($"IF CERT_ID(N'{Certificate}') IS NOT NULL DROP CERTIFICATE {Certificate};");
        Line($"CREATE CERTIFICATE {Certificate} ENCRYPTION BY PASSWORD = N'{password}' WITH SUBJECT = N'SCApp restore ownership claim', EXPIRY_DATE = '20990101';");
        Line($"CREATE LOGIN {Signer} FROM CERTIFICATE {Certificate};");
        Line($"GRANT CONTROL SERVER TO {Signer};");
        Go();
        Line("USE master;");
        Go();
        Line($"""
            CREATE PROCEDURE dbo.{ClaimProcedure} @name sysname
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @name IS NULL OR DATALENGTH(@name) <> 96 OR @name COLLATE Latin1_General_BIN2 NOT LIKE N'SmartCv[_]Restore[_]%'
                    OR SUBSTRING(@name, 17, 32) COLLATE Latin1_General_BIN2 LIKE N'%[^0-9a-f]%'
                    THROW 50001, N'Only a SmartCv_Restore database can be claimed.', 1;
                IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND owner_sid = SUSER_SID(ORIGINAL_LOGIN()))
                    THROW 50002, N'Only a database this account restored can be claimed.', 1;
                -- The restored copy usually carries the account's own user from the source database, which prevents it
                -- from becoming the owner. That user (it owns nothing) is removed from this copy only.
                DECLARE @user sysname, @sid varbinary(85) = SUSER_SID(ORIGINAL_LOGIN());
                DECLARE @sql nvarchar(max) = N'SELECT @u = name FROM ' + QUOTENAME(@name) + N'.sys.database_principals WHERE sid = @s AND principal_id > 1;';
                EXEC sys.sp_executesql @sql, N'@s varbinary(85), @u sysname OUTPUT', @s = @sid, @u = @user OUTPUT;
                IF @user IS NOT NULL
                BEGIN
                    SET @sql = N'USE ' + QUOTENAME(@name) + N'; DROP USER ' + QUOTENAME(@user) + N';';
                    EXEC (@sql);
                END
                SET @sql = N'ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@name) + N' TO ' + QUOTENAME(ORIGINAL_LOGIN()) + N';';
                EXEC (@sql);
            END
            """.Replace("\r\n", "\n"));
        Go();
        Line($"ADD SIGNATURE TO dbo.{ClaimProcedure} BY CERTIFICATE {Certificate} WITH PASSWORD = N'{password}';");
        Line($"ALTER CERTIFICATE {Certificate} REMOVE PRIVATE KEY;");
        Line($"IF USER_ID({Lit(restoreAccount)}) IS NULL CREATE USER {Id(restoreAccount)} FOR LOGIN {Id(restoreAccount)};");
        Line($"GRANT EXECUTE ON dbo.{ClaimProcedure} TO {Id(restoreAccount)};");
        Go();
    }

    public static IEnumerable<string> Batches(string script) =>
        System.Text.RegularExpressions.Regex.Split(script, @"^GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Where(batch => batch.Trim().Length > 0);
}
