using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartCv.Core.Ops;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Ops;
using SmartCv.Tests.Auth;

namespace SmartCv.Tests.Ops;

public class A44HealthTests
{
    internal static (WebApplicationFactory<Program> Factory, string OcrRoot) Factory(string environment = "Development")
    {
        var root = TestSupport.TempRoot();
        File.WriteAllBytes(Path.Combine(root, "eng.traineddata"), [1]); File.WriteAllBytes(Path.Combine(root, "vie.traineddata"), [1]);
        return (TestSupport.ApiFactory(environment, new() { ["Scoring:WorkerEnabled"] = "false", ["Retention:AutoAnonymize"] = "false",
            ["TextExtraction:TessdataPath"] = root, ["Logging:File:Enabled"] = "false" }, authenticated: false), root);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Existing_public_health_checks_all_dependencies_without_writing_business_or_audit_data(string environment)
    {
        var (factory, ocr) = Factory(environment); using (factory)
        using (var http = factory.CreateClient())
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => http.GetAsync("/api/health")));
            foreach (var response in results)
            {
                using (response)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
                    var report = (await response.Content.ReadFromJsonAsync<HealthReportDto>())!;
                    Assert.Equal("ok", report.Status); Assert.Equal(new[] { "database", "uploads", "ocr", "email" }, report.Checks.Select(c => c.Name));
                    Assert.All(report.Checks, c => Assert.True(c.Ok));
                    Assert.DoesNotContain(ocr, await response.Content.ReadAsStringAsync());
                }
            }
            await A28TestSupport.WithDb(factory, async db =>
            {
                Assert.Equal("Microsoft.EntityFrameworkCore.InMemory", db.Database.ProviderName);
                Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.AuditEvents.ToListAsync()); Assert.Empty(await db.ExportLogs.ToListAsync());
                Assert.Empty(await db.Applications.ToListAsync());
            });
            Assert.Equal(environment == "Development" ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await http.GetAsync("/openapi/v1.json")).StatusCode);
        }
    }

    [Fact]
    public async Task Removing_test_OCR_data_returns_503_and_refresh_recovers_without_exposing_paths()
    {
        var (factory, root) = Factory(); using (factory) using (var http = factory.CreateClient())
        {
            var file = Path.Combine(root, "vie.traineddata"); File.Delete(file);
            using var response = await http.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<HealthReportDto>())!; Assert.False(report.Checks.Single(c => c.Name == "ocr").Ok);
            Assert.DoesNotContain(root, await response.Content.ReadAsStringAsync());
            File.WriteAllBytes(file, [1]); Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/health")).StatusCode);
        }
    }

    [Fact]
    public async Task Storage_failure_is_503_safe_and_does_not_create_a_second_audit_record()
    {
        var (parent, _) = Factory(); using (parent)
        using (var factory = parent.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<ICvFileStorage>(); s.AddSingleton<ICvFileStorage>(new BrokenStorage()); })))
        using (var http = factory.CreateClient())
        {
            using var response = await http.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<HealthReportDto>())!; Assert.False(report.Checks.Single(c => c.Name == "uploads").Ok);
            Assert.DoesNotContain("PRIVATE_STORAGE", await response.Content.ReadAsStringAsync());
            await A28TestSupport.WithDb(factory, async db => Assert.Empty(await db.AuditEvents.ToListAsync()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_connection_failure_or_timeout_returns_readiness_error_without_SQL_details(bool timeout)
    {
        var (parent, _) = Factory(); using (parent)
        using (var factory = parent.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<DbContextOptions<SmartCvDbContext>>(); s.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
            if (timeout) s.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()).ReplaceService<IDatabaseCreator, SlowDatabase>());
            else s.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()).ReplaceService<IDatabaseCreator, BrokenDatabase>());
        })))
        using (var http = factory.CreateClient())
        {
            using var response = await http.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<HealthReportDto>())!; Assert.False(report.Checks.Single(c => c.Name == "database").Ok);
            Assert.DoesNotContain("PRIVATE_SQL", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_instead_of_becoming_a_false_health_result()
    {
        var (factory, _) = Factory(); using (factory) using (var scope = factory.Services.CreateScope())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ApplicationReadiness>().CheckAsync(cancelled.Token));
        }
    }

    [Fact]
    public async Task Production_unexpected_exception_returns_safe_500_and_HTTPS_response_has_HSTS()
    {
        var (parent, _) = Factory("Production"); using (parent)
        using (var factory = parent.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(new BrokenClock()); })))
        using (var http = factory.CreateClient(new() { BaseAddress = new Uri("https://scapp.example.invalid"), AllowAutoRedirect = false }))
        {
            using var response = await http.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(); Assert.Contains("server_error", text); Assert.Contains("requestId", text);
            Assert.DoesNotContain("PRIVATE_UNEXPECTED", text); Assert.DoesNotContain("InvalidOperationException", text);
            Assert.True(response.Headers.Contains("Strict-Transport-Security"));
        }
    }

    [Fact]
    public async Task Production_HTTP_redirects_to_HTTPS_before_health_or_authentication()
    {
        var (parent, _) = Factory("Production"); using (parent)
        using (var factory = parent.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.Configure<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>(o => o.HttpsPort = 7443))))
        using (var http = factory.CreateClient(new() { BaseAddress = new Uri("http://scapp.example.invalid"), AllowAutoRedirect = false }))
        {
            using var response = await http.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal("https://scapp.example.invalid:7443/api/health", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Production_business_profile_preserves_HR_Admin_anonymous_and_temporary_password_boundaries()
    {
        var (factory, _) = Factory("Production"); using (factory)
        {
            using var anonymous = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/runs")).StatusCode);
            foreach (var role in new[] { "HR", "Admin" })
            {
                var actor = await A28TestSupport.Seed(factory, A28TestSupport.Password(), role);
                using var http = A29AuthorizationTests.Client(factory, actor);
                // Since #114 the System Admin is not a business operator: business API is HR-only, admin API Admin-only.
                Assert.Equal(role == "Admin" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, (await http.GetAsync("/api/runs")).StatusCode);
                Assert.Equal(role == "Admin" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await http.GetAsync("/api/admin/audit-events")).StatusCode);
            }
            var temporary = await A28TestSupport.Seed(factory, A28TestSupport.Password(), "HR", mustChange: true);
            using var blocked = A29AuthorizationTests.Client(factory, temporary);
            Assert.Equal(HttpStatusCode.Forbidden, (await blocked.GetAsync("/api/runs")).StatusCode);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../private")]
    [InlineData("eng++vie")]
    public void Invalid_OCR_languages_cannot_escape_the_configured_directory(string text) => Assert.Throws<ArgumentException>(() => HealthRules.OcrFiles(text));

    private sealed class BrokenStorage : ICvFileStorage, ICvStorageProbe
    {
        public Task CheckWritableAsync(CancellationToken ct) => throw new IOException("PRIVATE_STORAGE private@example.invalid C:\\private");
        public Task<string> SaveAsync(Guid id, string extension, Stream data, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Stream OpenRead(string path) => throw new NotSupportedException();
        public bool Exists(string path) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
    }
    private sealed class BrokenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("PRIVATE_UNEXPECTED internal exception");
    }
    public class BrokenDatabase : IDatabaseCreator
    {
        public virtual Task<bool> CanConnectAsync(CancellationToken ct = default) => throw new IOException("PRIVATE_SQL password=synthetic");
        public bool CanConnect() => throw new NotSupportedException();
        public bool EnsureCreated() => throw new NotSupportedException();
        public Task<bool> EnsureCreatedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public bool EnsureDeleted() => throw new NotSupportedException();
        public Task<bool> EnsureDeletedAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    public sealed class SlowDatabase : BrokenDatabase
    {
        public override async Task<bool> CanConnectAsync(CancellationToken ct = default) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return false; }
    }
}
