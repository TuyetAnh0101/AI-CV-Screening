using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests;

/// <summary>Injects a real fixture JWT before the standard Bearer handler reads the request.</summary>
internal sealed class RegressionAuthenticationHandlerProvider(IAuthenticationSchemeProvider schemes) : IAuthenticationHandlerProvider
{
    private readonly AuthenticationHandlerProvider inner = new(schemes);
    public async Task<IAuthenticationHandler?> GetHandlerAsync(HttpContext http, string name)
    {
        if (name == "Bearer" && !http.Request.Headers.ContainsKey("Authorization"))
            http.Request.Headers.Authorization = "Bearer " + await http.RequestServices.GetRequiredService<RegressionIdentity>().Token(http.RequestServices);
        return await inner.GetHandlerAsync(http, name);
    }
}

internal sealed class RegressionIdentity
{
    private readonly SemaphoreSlim gate = new(1);
    private string? token;
    public async Task<string> Token(IServiceProvider services)
    {
        await gate.WaitAsync();
        try
        {
            if (token is not null) return token;
            var db = services.GetRequiredService<SmartCvDbContext>();
            ValidateTarget(db);
            // Preserve this host's store while omitting fault interceptors used by a business test.
#pragma warning disable EF1001 // Test-only access to the current InMemory root, never another configured database.
            var options = new DbContextOptionsBuilder<SmartCvDbContext>();
            if (db.Database.IsInMemory())
            {
                var memory = db.GetService<IDbContextOptions>().FindExtension<Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal.InMemoryOptionsExtension>()!;
                var root = memory.DatabaseRoot ?? db.GetService<Microsoft.EntityFrameworkCore.InMemory.Storage.Internal.IInMemoryDatabaseRootCache>().SharedRoot;
                options.UseInMemoryDatabase(memory.StoreName!, root);
            }
            else options.UseSqlServer(db.Database.GetConnectionString());
#pragma warning restore EF1001
            await using var identityDb = new SmartCvDbContext(options.Options);
            if (identityDb.Database.IsInMemory()) await identityDb.Database.EnsureCreatedAsync();
            await identityDb.EnsureInternalWorkspaceAsync();
            var user = new User
            {
                Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = StartupGatedApiFactory.RegressionActor,
                Role = "Admin", IsActive = true, CreatedAt = DateTime.UtcNow,
                PasswordHash = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48))
            };
            // A52: người dùng nghiệp vụ của fixture là thành viên workspace nội bộ (WorkspaceId = 1, có sẵn qua migration/HasData);
            // token mang claim smartcv_ws và được UserSessionValidator xác nhận với database như mọi request thật.
            identityDb.Users.Add(user);
            identityDb.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = 1, User = user, Role = "Member", JoinedAt = DateTime.UtcNow });
            await identityDb.SaveChangesAsync();
            return token = new JwtTokenService(services.GetRequiredService<IOptions<JwtOptions>>(), TimeProvider.System).Create(user, 1).Token;
        }
        finally { gate.Release(); }
    }

    internal static void ValidateTarget(SmartCvDbContext db)
    {
        if (db.Database.IsInMemory()) return;
        if (db.Database.IsSqlServer())
        {
            var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.Database.GetConnectionString());
            // Opt-in SQL fixtures in this suite create fresh catalogs with exactly this shape.
            if (string.IsNullOrWhiteSpace(target.AttachDBFilename)
                && System.Text.RegularExpressions.Regex.IsMatch(target.InitialCatalog, "^SmartCv_[A-Za-z0-9]+_Test_[a-f0-9]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return;
        }
        throw new InvalidOperationException("Regression identity requires InMemory or a designated GUID test catalog. No connection opened.");
    }
}
