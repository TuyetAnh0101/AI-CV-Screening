using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartCv.Api.Endpoints;
using SmartCv.Api.Middleware;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

/// <summary>A29: incident fail-safe for the application catalog and A39 privacy lease outside Development.</summary>
public class A29SafetyTests
{
    [Theory]
    [InlineData("SmartCvDb")]
    [InlineData("smartcvdb")]
    [InlineData(" SMARTCVDB ")]
    public async Task Opening_the_application_catalog_is_refused_before_any_network_access(string catalog)
    {
        // A network attempt would surface SqlException; the guard must fail first with its own message.
        var target = new SqlConnectionStringBuilder { DataSource = "never-connect.invalid", InitialCatalog = catalog, IntegratedSecurity = true, ConnectTimeout = 1, Pooling = false };
        await using (var asyncConnection = new SqlConnection(target.ConnectionString))
            Assert.Equal(ApplicationDatabaseGuard.Message, (await Assert.ThrowsAsync<InvalidOperationException>(() => asyncConnection.OpenAsync())).Message);
        using var syncConnection = new SqlConnection(target.ConnectionString);
        Assert.Equal(ApplicationDatabaseGuard.Message, Assert.Throws<InvalidOperationException>(syncConnection.Open).Message);
    }

    [Theory]
    [InlineData("SmartCvDb", true)]
    [InlineData("smartcvdb", true)]
    [InlineData("SmartCv_A29_Test_0123456789abcdef0123456789abcdef", false)]
    [InlineData("SmartCv_Test_NotUsed", false)]
    [InlineData("SmartCvDb_ModelOnly", false)]
    [InlineData("master", false)]
    [InlineData(null, false)]
    public void Only_the_application_catalog_is_blocked(string? catalog, bool blocked)
    {
        Assert.Equal(blocked, ApplicationDatabaseGuard.IsApplicationCatalog(catalog));
        if (blocked) Assert.Throws<InvalidOperationException>(() => ApplicationDatabaseGuard.Check(catalog));
        else ApplicationDatabaseGuard.Check(catalog);
    }

    [Fact]
    public void Read_only_inspection_exception_is_explicit_and_ends_with_its_scope()
    {
        using (ApplicationDatabaseGuard.AllowReadOnlyApplicationInspection()) ApplicationDatabaseGuard.Check("SmartCvDb");
        Assert.Throws<InvalidOperationException>(() => ApplicationDatabaseGuard.Check("SmartCvDb"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Test_hosts_never_resolve_the_application_catalog_from_User_Secrets_or_appsettings(string environment)
    {
        using var factory = TestSupport.ApiFactory(environment);
        var configured = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("SmartCvDb");
        Assert.False(ApplicationDatabaseGuard.IsApplicationCatalog(new SqlConnectionStringBuilder(configured).InitialCatalog), configured);
        using var scope = factory.Services.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().Database.IsInMemory());
    }

    [Theory]
    [InlineData("/api/cvs")]
    [InlineData("/api/candidates/1")]
    [InlineData("/api/applications/1/events")]
    public async Task Privacy_read_lease_is_attempted_in_every_environment_and_fails_closed(string path)
    {
        var (http, called) = await InvokePrivacyMiddleware(path);
        Assert.False(called());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Non_personal_routes_skip_the_privacy_lease()
    {
        var (http, called) = await InvokePrivacyMiddleware("/api/jobs");
        Assert.True(called());
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
    }

    private static async Task<(DefaultHttpContext Http, Func<bool> Called)> InvokePrivacyMiddleware(string path)
    {
        var services = new ServiceCollection().AddLogging();
        // SQL Server provider (the lease only exists there), but opening is refused before any network access.
        services.AddDbContext<SmartCvDbContext>(o => o.UseSqlServer("Server=never-connect.invalid;Database=SmartCv_A29_Guard_NotUsed;Integrated Security=true;Connect Timeout=1;Pooling=false")
            .AddInterceptors(new RefuseOpen()));
        var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Path = path; http.Response.Body = new MemoryStream();
        var called = false;
        var middleware = new CvPrivacyReadMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(http, provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CvUploadEndpointOptions { Enabled = true }));
        return (http, () => called);
    }

    private sealed class RefuseOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("Synthetic: no connection in A29 privacy middleware test.");
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic: no connection in A29 privacy middleware test.");
    }
}
