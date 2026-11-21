using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Contracts;

namespace SmartCv.Tests;

public class WorkspaceIsolationAdminTests
{
    [Fact]
    public async Task Created_HR_has_only_internal_member_membership_and_safe_account_metadata()
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync();
        using var admin = f.Client(1, admin: true);
        using var response = await admin.PostAsJsonAsync("/api/users", new { email = "new-hr@example.invalid", fullName = "Synthetic internal HR", role = "HR", password = "Synthetic-Password9!" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var credential = (await response.Content.ReadFromJsonAsync<UserCredentialResponse>())!;
        await f.WithDb(1, async db =>
        {
            var membership = Assert.Single(await db.WorkspaceMembers.Where(m => m.UserId == credential.User.UserId).ToListAsync());
            Assert.Equal(1, membership.WorkspaceId); Assert.Equal("Member", membership.Role);
        });
        using var list = await admin.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var user = json.RootElement.EnumerateArray().Single(u => u.GetProperty("userId").GetInt32() == credential.User.UserId);
        Assert.Equal("SCApp nội bộ", user.GetProperty("workspaceName").GetString());
        Assert.True(user.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("Admin", user.GetProperty("signupSource").GetString());
        Assert.False(user.TryGetProperty("securityStamp", out _)); Assert.False(user.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task Admin_audit_is_system_scope_with_workspace_filter_and_metadata()
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync();
        using var admin = f.Client(1, admin: true);
        foreach (var w in new[] { 1, 2 })
        {
            using var response = await admin.GetAsync($"/api/admin/audit-events?workspaceId={w}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => { Assert.Equal(w, r.GetProperty("workspaceId").GetInt32()); Assert.False(string.IsNullOrEmpty(r.GetProperty("workspaceName").GetString())); });
            using var detail = await admin.GetAsync($"/api/admin/audit-events/{rows[0].GetProperty("auditEventId").GetInt64()}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal(w, detailJson.RootElement.GetProperty("workspaceId").GetInt32());
            Assert.DoesNotContain("extractedText", await detail.Content.ReadAsStringAsync());
        }
        foreach (var query in new[] { "workspaceId=0", "workspaceId=x", "workspaceId=1&workspaceId=2" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/audit-events?" + query)).StatusCode);
    }

    [Fact]
    public async Task Admin_export_logs_include_all_workspaces_and_workspace_metadata()
    {
        using var f = await WorkspaceIsolationFixture.CreateAsync();
        using var admin = f.Client(1, admin: true);
        using var response = await admin.GetAsync("/api/exports");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2 }, items.Select(x => x.GetProperty("workspaceId").GetInt32()).Order().ToArray());
        Assert.All(items, x => Assert.False(string.IsNullOrEmpty(x.GetProperty("workspaceName").GetString())));
        Assert.DoesNotContain("extractedText", body); Assert.DoesNotContain("storedPath", body);
    }
}
