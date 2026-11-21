using Microsoft.AspNetCore.Http;
using SmartCv.Api.Auth;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Tests;

/// <summary>A52: WorkspaceScope (AsyncLocal) và HttpWorkspaceContext.</summary>
public class A52WorkspaceScopeTests
{
    private static IWorkspaceContext Ambient => WorkspaceScope.Ambient;

    [Fact]
    public void Without_a_scope_there_is_no_workspace_and_no_system_scope()
    {
        Assert.False(WorkspaceScope.IsActive);
        Assert.Null(Ambient.WorkspaceId); Assert.Null(Ambient.Role); Assert.Null(Ambient.Kind); Assert.False(Ambient.IsSystemScope);
        Assert.Null(NullWorkspaceContext.Instance.WorkspaceId); Assert.False(NullWorkspaceContext.Instance.IsSystemScope);
    }

    [Fact]
    public void Nested_scopes_restore_the_previous_state_on_dispose()
    {
        using (WorkspaceScope.Begin(1, "Member", "Internal"))
        {
            Assert.Equal((1, "Member", "Internal"), (Ambient.WorkspaceId, Ambient.Role, Ambient.Kind));
            using (WorkspaceScope.Begin(2))
            {
                Assert.Equal(2, Ambient.WorkspaceId); Assert.Null(Ambient.Role);
                using (WorkspaceScope.BeginSystem("test: lồng"))
                {
                    Assert.True(Ambient.IsSystemScope); Assert.Null(Ambient.WorkspaceId);
                }
                Assert.False(Ambient.IsSystemScope); Assert.Equal(2, Ambient.WorkspaceId);
            }
            Assert.Equal(1, Ambient.WorkspaceId);
        }
        Assert.False(WorkspaceScope.IsActive);
    }

    [Fact]
    public void Disposing_twice_or_out_of_order_never_overwrites_another_scope()
    {
        var outer = WorkspaceScope.Begin(1);
        var inner = WorkspaceScope.Begin(2);
        outer.Dispose();                       // lệch thứ tự: scope trong (2) vẫn là hiện hành
        Assert.Equal(2, Ambient.WorkspaceId);
        inner.Dispose(); inner.Dispose();      // Dispose hai lần không ảnh hưởng gì
        Assert.False(WorkspaceScope.IsActive); // scope ngoài đã đóng thì không sống lại khi scope trong đóng
        using (WorkspaceScope.Begin(3)) Assert.Equal(3, Ambient.WorkspaceId);
        Assert.False(WorkspaceScope.IsActive);
    }

    [Fact]
    public void Scope_arguments_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceScope.Begin(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceScope.Begin(-1));
        Assert.Throws<ArgumentException>(() => WorkspaceScope.BeginSystem(""));
        Assert.Throws<ArgumentException>(() => WorkspaceScope.BeginSystem("   "));
    }

    [Fact]
    public async Task Scope_follows_awaits_but_never_leaks_into_other_flows()
    {
        async Task<(int?, bool)> Flow(int id)
        {
            using var scope = id == 0 ? WorkspaceScope.BeginSystem("test: song song") : WorkspaceScope.Begin(id);
            await Task.Delay(id % 5);
            await Task.Yield();
            return (Ambient.WorkspaceId, Ambient.IsSystemScope);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => Flow(i % 3))));
        for (var i = 0; i < results.Length; i++)
            Assert.Equal(i % 3 == 0 ? ((int?)null, true) : (i % 3, false), results[i]);
        Assert.False(WorkspaceScope.IsActive);   // luồng gọi không bị ảnh hưởng
    }

    [Fact]
    public async Task A_scope_opened_in_a_child_task_is_invisible_to_the_parent_and_a_following_request()
    {
        await Task.Run(() => { WorkspaceScope.BeginSystem("test: không được rò"); });   // cố ý không Dispose
        await Task.Delay(10);
        Assert.False(WorkspaceScope.IsActive);
        Assert.False(Ambient.IsSystemScope);
    }

    // ---------- HttpWorkspaceContext ----------

    private static HttpWorkspaceContext Http(HttpContext? http) => new(new HttpContextAccessor { HttpContext = http });

    [Fact]
    public void Http_context_ignores_headers_query_route_and_forms_and_trusts_only_the_validated_item()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Workspace-Id"] = "2"; http.Request.Headers["smartcv_ws"] = "2";
        http.Request.QueryString = new QueryString("?workspaceId=2&ws=2");
        http.Request.RouteValues["workspaceId"] = "2";
        var context = Http(http);
        Assert.Null(context.WorkspaceId); Assert.False(context.IsSystemScope);
        Assert.False(HttpWorkspaceContext.HasValidatedWorkspace(http));

        // Chỉ giá trị đúng kiểu do validator đặt mới được dùng; chuỗi/số tự đặt không có hiệu lực.
        http.Items[HttpWorkspaceContext.ItemKey] = 2;
        http.Items[HttpWorkspaceContext.ItemKey + "x"] = "2";
        Assert.Null(context.WorkspaceId);

        http.Items[HttpWorkspaceContext.ItemKey] = new WorkspaceSessionInfo(1, "SCApp nội bộ", "Internal", "Member");
        Assert.Equal((1, "Member", "Internal", false), (context.WorkspaceId, context.Role, context.Kind, context.IsSystemScope));
    }

    [Fact]
    public void Http_context_without_a_request_has_no_workspace_and_an_explicit_scope_takes_priority()
    {
        var context = Http(null);   // ví dụ: worker
        Assert.Null(context.WorkspaceId);
        var http = new DefaultHttpContext();
        http.Items[HttpWorkspaceContext.ItemKey] = new WorkspaceSessionInfo(1, "WS", "Internal", "Member");
        var request = Http(http);
        using (WorkspaceScope.Begin(5)) Assert.Equal(5, request.WorkspaceId);
        using (WorkspaceScope.BeginSystem("test: ưu tiên")) { Assert.True(request.IsSystemScope); Assert.Null(request.WorkspaceId); }
        Assert.Equal(1, request.WorkspaceId);
        Assert.False(request.IsSystemScope);
    }
}
