using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Auth;

/// <summary>Serialize mọi request dùng cùng email, kể cả khi chạy nhiều API instances.</summary>
public static class RegistrationEmailLock
{
    public static async Task AcquireAsync(SmartCvDbContext db, string normalizedEmail, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            throw new NotSupportedException("Public registration requires SQL Server transactional locks.");
        var tx = db.Database.CurrentTransaction ??
            throw new InvalidOperationException("Registration email lock requires an existing transaction.");
        var name = "SCApp:A56:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = tx.GetDbTransaction();
        cmd.CommandText = "DECLARE @rc INT; EXEC @rc = sys.sp_getapplock @Resource=@resource, " +
            "@LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; SELECT @rc;";
        var arg = cmd.CreateParameter();
        arg.ParameterName = "@resource";
        arg.DbType = DbType.String;
        arg.Value = name;
        cmd.Parameters.Add(arg);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not int resultCode || resultCode < 0)
            throw new TimeoutException("registration_lock_unavailable");
    }
}
