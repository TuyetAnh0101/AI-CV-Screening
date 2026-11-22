using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;

namespace SmartCv.Api.Endpoints;

/// <summary>Chỉ bốn endpoint public của A56; không đụng UI đăng ký B53.</summary>
public static class RegistrationEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/registration-settings", SettingsAsync)
            .AllowAnonymous().WithName("RegistrationSettings");
        group.MapPost("/register", RegisterAsync)
            .AllowAnonymous().RequireRateLimiting(AuthSetup.PublicRegisterLimit)
            .WithName("PublicRegister").Produces(202).ProducesValidationProblem();
        group.MapPost("/verify-email", VerifyAsync)
            .AllowAnonymous().RequireRateLimiting(AuthSetup.PublicVerifyLimit)
            .WithName("VerifyEmail").Produces<LoginResponse>();
        group.MapPost("/resend-verification", ResendAsync)
            .AllowAnonymous().RequireRateLimiting(AuthSetup.PublicResendLimit)
            .WithName("ResendVerification").Produces(202);
    }

    private static IResult SettingsAsync(HttpContext http, IOptions<RegistrationOptions> opts, IConfiguration config)
    {
        http.Response.Headers.CacheControl = "public, max-age=60";
        var o = opts.Value;
        return Results.Ok(new RegistrationSettingsResponse(o.Enabled,
            o.Enabled && o.EmailPasswordEnabled,
            [new PublicProviderSetting("google", config.GetValue<bool>("Authentication:Google:Enabled"))],
            new PublicPasswordRules(PasswordRules.MinLength, PasswordRules.MaxLength, true, true),
            o.TermsVersion));
    }

    private static IResult Disabled() => AuthProblems.Problem(404, "registration_disabled",
        "Đăng ký chưa được mở", "Chức năng đăng ký đang tạm thời tắt.");

    private static async Task<IResult> RegisterAsync(HttpContext http,
        RegistrationService service, IOptions<RegistrationOptions> options,
        ILogger<RegisterRequest> logger, CancellationToken ct)
    {
        if (!options.Value.Enabled || !options.Value.EmailPasswordEnabled) return Disabled();
        var (body, failure) = await ReadJsonAsync<RegisterRequest>(http, ct);
        if (failure is not null) return failure;
        var (input, errors) = RegistrationRules.Validate(body?.FullName, body?.Email, body?.Password,
            body?.AcceptTerms == true);
        if (input is null)
            return TypedResults.ValidationProblem(errors,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });
        try { return Results.Accepted(value: await service.RegisterAsync(input, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            logger.LogWarning("Public registration unavailable: {ErrorType}", e.GetType().Name);
            return AuthProblems.Problem(503, "registration_unavailable", "Dịch vụ chưa sẵn sàng",
                "Chưa thể xử lý đăng ký, vui lòng thử lại sau.");
        }
    }

    private static async Task<IResult> ResendAsync(HttpContext http,
        RegistrationService service, IOptions<RegistrationOptions> options,
        ILogger<ResendVerificationRequest> logger, CancellationToken ct)
    {
        if (!options.Value.Enabled || !options.Value.EmailPasswordEnabled) return Disabled();
        var (body, failure) = await ReadJsonAsync<ResendVerificationRequest>(http, ct);
        if (failure is not null) return failure;
        try { return Results.Accepted(value: await service.ResendAsync(body?.Email, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            logger.LogWarning("Resend verification unavailable: {ErrorType}", e.GetType().Name);
            return AuthProblems.Problem(503, "registration_unavailable", "Dịch vụ chưa sẵn sàng",
                "Chưa thể xử lý yêu cầu, vui lòng thử lại sau.");
        }
    }

    private static async Task<IResult> VerifyAsync(HttpContext http, RegistrationService service,
        ILogger<VerifyEmailRequest> logger, CancellationToken ct)
    {
        // Flag tắt không vô hiệu hóa link xác minh đã gửi trước đó.
        var (body, failure) = await ReadJsonAsync<VerifyEmailRequest>(http, ct);
        if (failure is not null) return failure;
        try
        {
            var (login, error) = await service.VerifyAsync(body?.Token, ct);
            if (login is not null) return Results.Ok(login);
            return AuthProblems.Problem(400, error ?? "token_invalid", "Liên kết không hợp lệ",
                error == "token_expired" ? "Liên kết đã hết hạn." : "Liên kết không hợp lệ hoặc đã được sử dụng.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            logger.LogWarning("Email verification unavailable: {ErrorType}", e.GetType().Name);
            return AuthProblems.Problem(503, "workspace_unavailable", "Dịch vụ chưa sẵn sàng",
                "Chưa thể hoàn tất xác minh email. Vui lòng thử lại.");
        }
    }

    private static async Task<(T? Value, IResult? Failure)> ReadJsonAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
            return (default, AuthProblems.Problem(415, "invalid_content_type", "Sai định dạng",
                "Yêu cầu cần application/json."));
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || (!string.IsNullOrEmpty(contentType.CharSet)
                && !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            return (default, AuthProblems.Problem(415, "invalid_content_type", "Sai mã hóa",
                "Yêu cầu cần JSON UTF-8."));
        if (http.Request.ContentLength > 8192)
            return (default, AuthProblems.Problem(413, "request_too_large", "Yêu cầu quá lớn",
                "Dữ liệu gửi lên tối đa 8 KiB."));
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 8192;
        try
        {
            var body = await http.Request.ReadFromJsonAsync<T>(cancellationToken: ct);
            return body is null
                ? (default, AuthProblems.Problem(400, "invalid_request", "Dữ liệu rỗng", "Cần gửi JSON object."))
                : (body, null);
        }
        catch (JsonException)
        {
            return (default, AuthProblems.Problem(400, "invalid_request", "JSON không hợp lệ", "Cần gửi JSON hợp lệ."));
        }
        catch (BadHttpRequestException err) when (err.StatusCode is 400 or 413)
        {
            return (default, AuthProblems.Problem(err.StatusCode, "invalid_request", "Yêu cầu không hợp lệ", "Không đọc được dữ liệu gửi lên."));
        }
    }
}
