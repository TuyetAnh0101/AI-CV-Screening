using System.Data.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace SmartCv.Api.Auth;

internal static class AuthProblems
{
    internal const string UnavailableItem = "SCApp.Auth.DatabaseUnavailable";
    internal static bool IsDatabaseFailure(Exception error)
    {
        if (error is OperationCanceledException) return false;
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is DbException or DbUpdateException or TimeoutException) return true;
        return false;
    }
    internal static ProblemHttpResult Problem(int status, string code, string title, string detail) =>
        TypedResults.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code });
    internal static ProblemHttpResult Unavailable() => Problem(503, "auth_unavailable", "Dịch vụ đăng nhập tạm thời không khả dụng", "Không thể kiểm tra tài khoản lúc này. Vui lòng thử lại sau.");
}
