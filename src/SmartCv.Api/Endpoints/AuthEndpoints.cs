using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartCv.Api.Auth;
using SmartCv.Core.Auth;
using SmartCv.Core.Candidates;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static class AuthEndpoints
{
    public const string InvalidCredentials = "Email hoặc mật khẩu không đúng.";
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginRateLimit)
            // Login parses and validates content type itself. Accepts metadata would select a synthetic
            // 415 endpoint without AllowAnonymous, causing the A29 fallback to turn this client error into 401.
            .Produces<LoginResponse>().ProducesValidationProblem()
            .WithName("Login").WithSummary("Đăng nhập bằng email và mật khẩu");
        group.MapGet("/me", MeAsync).RequireAuthorization(AuthPolicies.SignedIn)
            .WithName("Me").WithSummary("Thông tin tài khoản hiện tại");
        return app;
    }

    private static async Task<IResult> LoginAsync(HttpRequest http, SmartCvDbContext db, IPasswordHasher<User> hasher,
        LoginDummyHash dummy, JwtTokenService tokens, TimeProvider time, ILogger<LoginRequest> logger, CancellationToken ct)
    {
        if (!http.HasJsonContentType()) return AuthProblems.Problem(415, "invalid_content_type", "Định dạng không hỗ trợ", "Gửi thông tin đăng nhập bằng application/json.");
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.ContentType, out var contentType)
            || (!string.IsNullOrEmpty(contentType.CharSet) && !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            return AuthProblems.Problem(415, "invalid_content_type", "Định dạng không hỗ trợ", "Thông tin đăng nhập cần dùng JSON UTF-8.");
        if (http.ContentLength > 4096) return AuthProblems.Problem(413, "request_too_large", "Yêu cầu quá lớn", "Thông tin đăng nhập tối đa 4 KiB.");
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 4096;
        LoginRequest? request;
        try { request = await http.ReadFromJsonAsync<LoginRequest>(ct); }
        catch (JsonException) { return AuthProblems.Problem(400, "invalid_request", "Yêu cầu không hợp lệ", "Nội dung JSON không hợp lệ."); }
        catch (BadHttpRequestException error) when (error.StatusCode is 400 or 413)
        { return AuthProblems.Problem(error.StatusCode, "invalid_request", "Yêu cầu không hợp lệ", "Không đọc được thông tin đăng nhập hợp lệ."); }
        if (request is null) return AuthProblems.Problem(400, "invalid_request", "Yêu cầu không hợp lệ", "Thông tin đăng nhập là bắt buộc.");
        var email = CandidateRules.NormalizeEmail(request.Email);
        var errors = new Dictionary<string, string[]>();
        if (email is null) errors["email"] = ["Email là bắt buộc, đúng định dạng và tối đa 254 ký tự."];
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > PasswordRules.MaxLength)
            errors["password"] = ["Mật khẩu là bắt buộc và tối đa 128 ký tự."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

        try
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
            PasswordVerificationResult verified;
            try { verified = hasher.VerifyHashedPassword(user ?? new User(), user?.PasswordHash ?? dummy.Value, request.Password!); }
            catch (FormatException) { verified = PasswordVerificationResult.Failed; }
            if (user is null || verified == PasswordVerificationResult.Failed)
            {
                logger.LogWarning("Đăng nhập không thành công.");
                return AuthProblems.Problem(401, "invalid_credentials", "Đăng nhập không thành công", InvalidCredentials);
            }
            if (!user.IsActive) return AuthProblems.Problem(403, "account_locked", "Tài khoản đã bị khóa", "Tài khoản này đã bị khóa. Liên hệ Admin nếu cần mở lại.");
            if (!UserRoles.IsValid(user.Role) || string.IsNullOrWhiteSpace(user.SecurityStamp)) return AuthProblems.Unavailable();
            var expectedHash = user.PasswordHash;
            var expectedStamp = user.SecurityStamp;
            var newHash = verified == PasswordVerificationResult.SuccessRehashNeeded ? hasher.HashPassword(user, request.Password!) : expectedHash;
            var loggedInAt = time.GetUtcNow().UtcDateTime;
            if (db.Database.IsSqlServer())
            {
                // Không ghi đè mật khẩu vừa bị thay đổi/khóa bởi tiến trình khác trong lúc VerifyHash chạy.
                var updated = await db.Users.Where(u => u.UserId == user.UserId && u.IsActive
                    && EF.Functions.Collate(u.PasswordHash, "Latin1_General_100_BIN2") == expectedHash
                    && EF.Functions.Collate(u.SecurityStamp, "Latin1_General_100_BIN2") == expectedStamp)
                    .ExecuteUpdateAsync(set => set.SetProperty(u => u.LastLoginAt, loggedInAt).SetProperty(u => u.PasswordHash, newHash), ct);
                if (updated != 1) return AuthProblems.Problem(401, "invalid_credentials", "Đăng nhập không thành công", InvalidCredentials);
            }
            user.PasswordHash = newHash;
            user.LastLoginAt = loggedInAt;
            if (!db.Database.IsSqlServer()) await db.SaveChangesAsync(ct);
            var (token, expires) = tokens.Create(user);
            logger.LogInformation("Tài khoản #{UserId} đăng nhập.", user.UserId);
            return TypedResults.Ok(new LoginResponse { Token = token, ExpiresAt = expires, User = AuthMapping.ToMe(user) });
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error))
        {
            logger.LogWarning("Dịch vụ đăng nhập tạm thời không khả dụng.");
            return AuthProblems.Unavailable();
        }
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, SmartCvDbContext db, CancellationToken ct)
    {
        try
        {
            var id = principal.GetUserId();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == id, ct);
            if (user is null || SessionRules.Check(new(user.IsActive, user.SecurityStamp), principal.FindFirstValue(SmartCvClaims.Stamp)) is not null)
                return AuthProblems.Problem(401, "authentication_required", "Chưa đăng nhập", "Tài khoản hoặc phiên đăng nhập không còn hoạt động.");
            return TypedResults.Ok(AuthMapping.ToMe(user));
        }
        catch (Exception error) when (AuthProblems.IsDatabaseFailure(error)) { return AuthProblems.Unavailable(); }
    }
}
