using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Auth;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Email;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Registration;

/// <summary>
/// A57 SQL end-to-end. Requires separate, PRE-CREATED, EMPTY GUID catalog, explicit opt-in.
/// Never writes to SmartCvDb; never creates, drops or resets a catalog.
/// </summary>
public sealed class A57SqlFactAttribute : FactAttribute
{
    internal const string Connection = "SMARTCV_A57_TEST_SQL_CONNECTION";
    internal const string Approval = "SMARTCV_A57_SQL_APPROVED";
    public A57SqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Approval) != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Connection)))
        { Skip = "A57 SQL opt-in not configured; provide a pre-created EMPTY GUID test catalog."; return; }
        try { Validate(new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Connection)!)); }
        catch (ArgumentException) { Skip = "A57 SQL catalog must be SmartCv_A57_Test_<32 hexadecimal GUID>."; }
    }
    internal static void Validate(SqlConnectionStringBuilder settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DataSource) ||
            !string.IsNullOrEmpty(settings.AttachDBFilename) ||
            !Regex.IsMatch(settings.InitialCatalog, "^SmartCv_A57_Test_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Refusing any application/system/non-GUID database.");
    }
}

public sealed class A57SqlIntegrationTests
{
    [A57SqlFact]
    public async Task FullReset_RevokeOldJwt_ConcurrentTokenUse_AndPasswordSetup()
    {
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(A57SqlFactAttribute.Connection)!);
        A57SqlFactAttribute.Validate(connection);
        await using (var check = new SqlConnection(connection.ConnectionString))
        {
            await check.OpenAsync();
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0", check);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
        }
        var options = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using (var migrate = new SmartCvDbContext(options)) await migrate.Database.MigrateAsync();

        var initialPassword = "A57Old9Password";
        var user = new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic A57",
            Role = "HR", IsActive = true, SignupSource = "Admin", MustChangePassword = false,
            EmailVerifiedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, initialPassword);
        var locked = new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic locked",
            Role = "HR", IsActive = false, SignupSource = "Admin", PasswordHash = "synthetic", CreatedAt = DateTime.UtcNow };
        var onlyProvider = new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Synthetic Google",
            Role = "HR", IsActive = true, SignupSource = "google", PasswordHash = "",
            EmailVerifiedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        await using (var seed = new SmartCvDbContext(options))
        {
            seed.Users.AddRange(user, locked, onlyProvider);
            await seed.SaveChangesAsync();
        }

        WebApplicationFactory<Program> Factory() => A28TestSupport.Factory(new()
        {
            ["Email:WorkerEnabled"] = "false", ["App:WebBaseUrl"] = "https://localhost:7001",
            ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false"
        }).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer(connection.ConnectionString));
        }));

        using var factory = Factory();
        using var client = factory.CreateClient();
        async Task<string> Forgot(string? email)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest { Email = email });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        var unknownResult = await Forgot(Guid.NewGuid().ToString("N") + "@example.invalid");
        Assert.Equal(unknownResult, await Forgot(locked.Email));
        Assert.Equal(unknownResult, await Forgot(user.Email));
        await Forgot(user.Email);
        await Forgot(user.Email);
        await Forgot(user.Email); // 4th call is still 202; SQL limit preserves <=3 outbox rows.
        await using (var inspect = new SmartCvDbContext(options))
        {
            Assert.Equal(3, await inspect.EmailOutbox.CountAsync(e => e.UserId == user.UserId && e.Template == "ResetPassword"));
            Assert.Equal(0, await inspect.EmailOutbox.CountAsync(e => e.UserId == locked.UserId));
        }

        string resetToken;
        {
            string payload;
            await using (var read = new SmartCvDbContext(options))
            {
                payload = (await read.EmailOutbox.Where(e => e.UserId == user.UserId && e.Template == "ResetPassword")
                    .OrderByDescending(e => e.EmailOutboxId).FirstAsync()).ProtectedPayload!;
            }
            var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
            var json = provider.CreateProtector(EmailQueue.Purpose).Unprotect(payload);
            var rendered = JsonSerializer.Deserialize<EmailMessage>(json)!;
            resetToken = Regex.Match(rendered.Text, @"token=([A-Za-z0-9_-]{43})").Groups[1].Value;
            Assert.Equal(43, resetToken.Length);
        }

        var originalToken = A28TestSupport.Token(user, workspaceId: null);
        using var existingSession = factory.CreateClient();
        existingSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", originalToken);
        using (var before = await existingSession.GetAsync("/api/auth/me/profile")) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // EmailVerification token may never be consumed by PasswordReset endpoint.
        var verificationOnly = UserTokenService.CreateRandomToken();
        var expiredOnly = UserTokenService.CreateRandomToken();
        await using (var prepare = new SmartCvDbContext(options))
        {
            prepare.UserTokens.AddRange(
                new UserToken { UserId = user.UserId, Purpose = TokenPurposes.EmailVerification,
                    TokenHash = UserTokenService.HashToken(verificationOnly)!, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30) },
                new UserToken { UserId = user.UserId, Purpose = TokenPurposes.PasswordReset,
                    TokenHash = UserTokenService.HashToken(expiredOnly)!, CreatedAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
            await prepare.SaveChangesAsync();
        }
        async Task<HttpResponseMessage> Reset(string raw, string password) =>
            await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest { Token = raw, NewPassword = password });
        using (var wrongPurpose = await Reset(verificationOnly, "Fresh9Password")) Assert.Equal(HttpStatusCode.BadRequest, wrongPurpose.StatusCode);
        using (var expired = await Reset(expiredOnly, "Fresh9Password"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
            Assert.Contains("token_expired", await expired.Content.ReadAsStringAsync());
        }
        using (var weak = await Reset(resetToken, "weak")) Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        // Concurrent requests for the SAME token: 1 winner; 1 rejected (not two resets).
        var answers = await Task.WhenAll(Reset(resetToken, "Fresh9Password"), Reset(resetToken, "Other9Password"));
        try
        {
            Assert.Equal(1, answers.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, answers.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        }
        finally { foreach (var response in answers) response.Dispose(); }

        Assert.Equal(HttpStatusCode.Unauthorized, (await existingSession.GetAsync("/api/auth/me/profile")).StatusCode);
        using (var oldPasswordLogin = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Email = user.Email, Password = initialPassword }))
            Assert.Equal(HttpStatusCode.Unauthorized, oldPasswordLogin.StatusCode);
        await using (var verify = new SmartCvDbContext(options))
        {
            var saved = await verify.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
            Assert.NotEqual(user.SecurityStamp, saved.SecurityStamp);
            Assert.False(saved.MustChangePassword);
            Assert.True(saved.EmailVerifiedAt is not null);
            Assert.Equal(1, await verify.EmailOutbox.CountAsync(e => e.UserId == user.UserId && e.Template == "PasswordChanged"));
        }
        using (var providerSession = factory.CreateClient())
        {
            providerSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", A28TestSupport.Token(onlyProvider, workspaceId: null));
            using var setup = await providerSession.PostAsJsonAsync("/api/auth/me/password-setup", new { });
            Assert.Equal(HttpStatusCode.Accepted, setup.StatusCode);
        }
        // Retain synthetic GUID test catalog for review; never drop/reset the user's SQL database.
    }

    [Theory]
    [InlineData("Server=nohost;Database=SmartCvDb;Trusted_Connection=True")]
    [InlineData("Server=nohost;Database=master;Trusted_Connection=True")]
    public void SQL_guard_rejects_real_or_system_catalog(string connection) =>
        Assert.Throws<ArgumentException>(() => A57SqlFactAttribute.Validate(new SqlConnectionStringBuilder(connection)));
}
