using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SmartCv.Api.Auth;

internal static class AccountApiSupport
{
    internal const int MaxBodyBytes = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<IResult> RunAsync(HttpContext http, ILogger logger, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AccountFailure error)
        {
            if (error.Status == 401) http.Response.Headers.WWWAuthenticate = "Bearer";
            var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
            if (error.Errors is not null) extensions["errors"] = error.Errors;
            return Results.Problem(statusCode: error.Status, title: error.Message, extensions: extensions);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Failure(413, "request_too_large", "Nội dung tối đa 8 KiB."); }
        catch (DbUpdateConcurrencyException) { return Failure(409, "account_conflict", "Tài khoản vừa thay đổi. Hãy tải lại."); }
        catch (Exception error) when (error is SqlException { Number: 1205 } || error.InnerException is SqlException { Number: 1205 })
        { return Failure(409, "account_conflict", "Có thao tác tài khoản đồng thời. Hãy tải lại."); }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error))
        {
            logger.LogWarning("Account operation temporarily unavailable.");
            return Failure(503, "account_unavailable", "Dịch vụ tài khoản tạm không khả dụng. Kiểm tra trạng thái trước khi thử lại.");
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogError("Account operation failed ({Type}).", error.GetType().Name);
            return Failure(500, "account_error", "Không xử lý được yêu cầu tài khoản.");
        }
    }
    private static IResult Failure(int status, string code, string detail) => AuthProblems.Problem(status, code, detail, detail);

    internal static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType() || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || (!string.IsNullOrEmpty(contentType.CharSet) && !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new AccountFailure(415, "invalid_content_type", "Gửi JSON UTF-8.");
        if (request.ContentLength > MaxBodyBytes) throw new AccountFailure(413, "request_too_large", "Nội dung tối đa 8 KiB.");
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = MaxBodyBytes;
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxBodyBytes) throw new AccountFailure(413, "request_too_large", "Nội dung tối đa 8 KiB.");
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => !keys.Add(p.Name))) throw new JsonException();
            return document.RootElement.Deserialize<T>(Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw AccountFailure.Invalid(new() { ["body"] = ["Cần gửi một đối tượng JSON hợp lệ, không có trường trùng."] }); }
    }

    internal static string? ExpectedVersion(HttpRequest request, string? bodyVersion = null)
    {
        var values = request.Headers.IfMatch;
        if (values.Count > 1) throw AccountFailure.Invalid(new() { ["version"] = ["Chỉ gửi một If-Match."] });
        var header = values.Count == 0 ? null : values[0]?.Trim().Trim('"');
        if (header is not null && bodyVersion is not null && header != bodyVersion)
            throw AccountFailure.Invalid(new() { ["version"] = ["If-Match và ExpectedVersion phải giống nhau."] });
        var version = header ?? bodyVersion;
        if (version is not null && (version.Length != 64 || !version.All(Uri.IsHexDigit)))
            throw AccountFailure.Invalid(new() { ["version"] = ["Phiên bản tài khoản không hợp lệ."] });
        return version;
    }
}