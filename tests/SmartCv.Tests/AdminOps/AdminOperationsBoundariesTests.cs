using System.Text.RegularExpressions;
using SmartCv.Api.Auth;

namespace SmartCv.Tests.AdminOps;

/// <summary>
/// Static safety checks: explicit Admin policy, dedicated route group, and no leaked keys
/// in the bounded DTO. HTTP integration still needs SQL Server in local environment.
/// </summary>
public sealed class AdminOperationsBoundariesTests
{
    private static string Root => A51SchemaTests.FindRepositoryRoot();
    private static string Read(string file) => File.ReadAllText(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Admin_routes_are_not_nested_inside_the_business_group()
    {
        var api = Read("src/SmartCv.Api/SmartCvApi.cs");
        var admin = api.IndexOf("app.MapAdminOperationsEndpoints();", StringComparison.Ordinal);
        var business = api.IndexOf("var api = app.MapGroup", StringComparison.Ordinal);
        Assert.True(admin >= 0 && business > admin, "Admin route registration must precede business route group.");
        var endpoints = Read("src/SmartCv.Api/Endpoints/AdminOperationsEndpoints.cs");
        Assert.Contains("RequireAuthorization(AuthPolicies.Admin)", endpoints);
        Assert.DoesNotContain("IgnoreQueryFilters", endpoints);
        Assert.DoesNotContain("BeginSystem", endpoints);
    }

    [Fact]
    public void No_operational_contract_contains_private_CV_or_provider_secrets()
    {
        var contracts = Read("src/SmartCv.Core/Contracts/AdminOperationsContracts.cs");
        Assert.DoesNotContain("CandidateEmail", contracts);
        Assert.DoesNotContain("ExtractedText", contracts);
        Assert.DoesNotContain("StoredPath", contracts);
        Assert.DoesNotContain("string ApiKey", contracts);
        Assert.DoesNotContain("string ClientSecret", contracts);
        Assert.Contains("HasTokenTelemetry", contracts);
        Assert.Contains("AdminQuotaChangeRequest", contracts);
    }

    [Fact]
    public void Admin_dashboard_routes_to_independent_operations_API()
    {
        var page = Read("src/SmartCv.Web/Components/Pages/AdminOperationsDashboard.razor");
        Assert.Contains("@page \"/admin/dashboard\"", page);
        Assert.Contains("AccessRules.AdminPolicy", page);
        Assert.Contains("GetOverviewAsync", page);
        Assert.DoesNotContain("GetSummaryAsync", page);
        Assert.DoesNotContain("RunApiClient", page);
    }
}
