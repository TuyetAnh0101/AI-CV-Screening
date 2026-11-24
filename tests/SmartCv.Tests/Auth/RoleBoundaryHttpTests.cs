using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Workspaces;

namespace SmartCv.Tests.Auth;

/// <summary>Test HTTP thật với JWT + database InMemory của fixture A52.</summary>
public sealed class RoleBoundaryHttpTests
{
    [Fact]
    public async Task Admin_can_manage_accounts_but_cannot_open_recruitment_data()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var admin = fixture.Client(1, admin: true);
        using var accounts = await admin.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);

        foreach (var endpoint in new[] { "/api/jobs", "/api/cvs", "/api/runs", "/api/prompts", "/api/workspace/" })
        {
            using var response = await admin.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("A52 job", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Admin_without_workspace_claim_still_gets_system_administration()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", A28TestSupport.Token(fixture.Admin, workspaceId: null));
        using var accounts = await admin.GetAsync("/api/users");
        using var audit = await admin.GetAsync("/api/admin/audit-events");
        using var jobs = await admin.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, jobs.StatusCode);
    }

    [Fact]
    public async Task Hr_legacy_token_without_workspace_can_manage_self_but_not_recruit()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var hr = fixture.Factory.CreateClient();
        hr.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", A28TestSupport.Token(fixture.HrA, workspaceId: null));
        using var me = await hr.GetAsync("/api/auth/me");
        using var jobs = await hr.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, jobs.StatusCode);
    }

    [Fact]
    public async Task Hr_internal_and_personal_owner_can_operate_their_own_workspace()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        foreach (var workspaceId in new[] { 1, 2 })
        {
            using var hr = fixture.Client(workspaceId);
            using var jobs = await hr.GetAsync("/api/jobs");
            Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
            Assert.Contains($"A52 job {workspaceId}", await jobs.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Personal_member_is_not_an_owner_even_with_a_valid_workspace_token()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        await fixture.WithDb(2, async db =>
        {
            var membership = await db.WorkspaceMembers.SingleAsync(m =>
                m.UserId == fixture.OwnerB.UserId && m.WorkspaceId == 2);
            membership.Role = WorkspaceRoles.Member;
            await db.SaveChangesAsync();
        });
        using var hr = fixture.Client(2);
        using var jobs = await hr.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, jobs.StatusCode);
        using var sample = await hr.PostAsync("/api/workspace/sample-job", null);
        Assert.Equal(HttpStatusCode.Forbidden, sample.StatusCode);
    }

    [Fact]
    public async Task Personal_owner_id_mismatch_blocks_business_access()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        await fixture.WithDb(2, async db =>
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.WorkspaceId == 2);
            workspace.OwnerUserId = fixture.HrA.UserId;
            await db.SaveChangesAsync();
        });
        using var hr = fixture.Client(2);
        using var jobs = await hr.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, jobs.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_use_system_admin_endpoints()
    {
        using var fixture = await WorkspaceIsolationFixture.CreateAsync();
        using var owner = fixture.Client(2);
        using var accounts = await owner.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, accounts.StatusCode);
        using var audit = await owner.GetAsync("/api/admin/audit-events");
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
    }
}
