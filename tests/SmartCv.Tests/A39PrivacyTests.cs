using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartCv.Core.Privacy;
using SmartCv.Infrastructure.Storage;

namespace SmartCv.Tests;

public class A39PrivacyTests
{
    [Fact]
    public void Hash_is_unique_non_SHA_namespace_and_64_characters()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var hashes = ids.Select(SmartCv.Infrastructure.Privacy.CvAnonymizer.AnonymizedHash).ToArray();
        Assert.Equal(100, hashes.Distinct().Count());
        Assert.All(hashes, h =>
        {
            Assert.Equal(64, h.Length);
            Assert.StartsWith("ANON", h);
        });
        Assert.Equal(hashes[0], SmartCv.Infrastructure.Privacy.CvAnonymizer.AnonymizedHash(ids[0]));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Due_boundary_includes_exact_deadline(int days, bool due)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(due, RetentionRules.IsDueForAnonymization(now.AddDays(days), null, ["Rejected", "OnHold"], now));
        Assert.False(RetentionRules.IsDueForAnonymization(now.AddDays(-1), null, ["Rejected", "Reviewing"], now));
        Assert.False(RetentionRules.IsDueForAnonymization(now.AddDays(-1), null, ["New"], now));
    }

    [Fact]
    public async Task File_erasure_restores_on_rollback_and_purges_recovery_on_completion()
    {
        var root = TestSupport.TempRoot();
        var files = TestSupport.Storage(root);
        var id = Guid.NewGuid();
        var original = TestSupport.Pdf("synthetic-private-marker");
        using var input = new MemoryStream(original);
        var path = await files.SaveAsync(id, ".pdf", input, DateTime.UtcNow);
        Assert.True(files.PrepareErasure(id, path));
        Assert.False(files.Exists(path));
        files.RollbackErasure(id);
        Assert.True(files.Exists(path));
        using (var stream = files.OpenRead(path))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(original, buffer.ToArray());
        }

        Assert.True(files.PrepareErasure(id, path));
        files.CompleteErasure(id);
        Assert.False(files.Exists(path));
        files.CompleteErasure(id); // idempotent cleanup after a crash/retry
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "Uploads/CV/.a39-recovery")));
    }

    [Fact]
    public void Invalid_storage_path_is_not_treated_as_missing_file()
    {
        var files = TestSupport.Storage(TestSupport.TempRoot());
        Assert.Throws<InvalidOperationException>(() => files.PrepareErasure(Guid.NewGuid(), "../outside.pdf"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Retention_endpoints_are_absent_when_CV_gate_is_off(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment, new() { ["CvUpload:Endpoint:Enabled"] = "false", ["Scoring:WorkerEnabled"] = "false" });
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/retention/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/retention/run?dryRun=false", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/cvs/{Guid.NewGuid()}/anonymize", null)).StatusCode);
    }

    [Fact]
    public async Task Worker_is_off_by_default_and_settings_do_not_start_a_scan()
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" });
        using var http = factory.CreateClient();
        using var response = await http.GetAsync("/api/retention/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"autoAnonymize\":false", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("bad", true)]
    [InlineData("2999-01-01", false)]
    [InlineData("2027-01-01T00:00:00+07:00", true)]
    public async Task Invalid_asOf_or_future_real_run_is_400(string value, bool dryRun)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" });
        using var http = factory.CreateClient();
        using var response = await http.PostAsync($"/api/retention/run?dryRun={dryRun}&asOf={Uri.EscapeDataString(value)}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Viewer", 403)]
    [InlineData("HR", 200)]
    [InlineData("Admin", 200)]
    public async Task Configured_authentication_requires_authenticated_HR_or_Admin(string? role, int status)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false" }).WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddAuthentication("A39Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("A39Test", _ =>
        {
        })));
        using var http = factory.CreateClient();
        if (role is not null)
            http.DefaultRequestHeaders.Add("X-Test-Role", role);
        Assert.Equal(status, (int)(await http.GetAsync("/api/retention/settings")).StatusCode);
        if (status != 200)
        {
            Assert.Equal(status, (int)(await http.PostAsync("/api/retention/run?dryRun=false", null)).StatusCode);
            Assert.Equal(status, (int)(await http.PostAsync($"/api/cvs/{Guid.NewGuid()}/anonymize", null)).StatusCode);
        }
    }

    [Theory]
    [InlineData("Retention:BatchSize", "0")]
    [InlineData("Retention:BatchSize", "201")]
    [InlineData("Retention:CheckIntervalHours", "0")]
    [InlineData("Retention:CheckIntervalHours", "721")]
    public void Unsafe_batch_or_timer_configuration_stops_startup(string key, string value)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:WorkerEnabled"] = "false", [key] = value });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(role) ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "synthetic-operator"), new Claim(ClaimTypes.Role, role)], Scheme.Name)), Scheme.Name)));
        }
    }
}
