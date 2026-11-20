using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Jobs;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Endpoints;

public static class JobSkillEndpoints
{
    private const long MaxBodyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.Strict };

    public static IEndpointRouteBuilder MapJobSkillEndpoints(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<IOptions<CvUploadEndpointOptions>>().Value.Enabled)
            return app;
        var group = app.MapGroup("/api/jobs/{id:int}/skills").WithTags("Jobs").RequireAuthorization().AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapPost("/", AddAsync).WithName("AddJobSkill").Produces<JobSkillDto>(201).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPut("/{code}", UpdateAsync).WithName("UpdateJobSkill").Produces<JobSkillDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapDelete("/{code}", DeleteAsync).WithName("DeleteJobSkill").Produces(204).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapPut("/{code}/aliases", ReplaceAliasesAsync).WithName("ReplaceJobSkillAliases").Produces<JobSkillDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        return app;
    }

    private static async Task<IResult> AddAsync(int id, HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        if (id <= 0) return Invalid(new() { ["id"] = ["Mã Job phải là số nguyên dương."] });
        if (!await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId == id, ct)) return JobMissing();
        var (input, problem) = await ReadAsync<JobSkillInput>(http, ct);
        if (problem is not null) return problem;
        var (normalized, errors) = JobSkillRules.ValidateNew(input);
        if (normalized is null) return Invalid(errors);
        if (await Skills(db, id, normalized.SkillCode!).AnyAsync(ct)) return Duplicate();
        var skill = Entity(normalized); skill.JobId = id;
        db.JobSkills.Add(skill);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627 && e.Message.Contains("PK_JobSkills", StringComparison.Ordinal))) { return Duplicate(); }
        catch (DbUpdateException ex) when (AliasConflict(ex)) { return AliasDuplicate(); }
        catch (DbUpdateException ex) when (SqlError(ex, 2601, 2627)) { return Duplicate(); }
        catch (DbUpdateException ex) when (SqlError(ex, 547)) { return Changed(); }
        return TypedResults.Created($"/api/jobs/{id}/skills/{skill.SkillCode}", await LoadAsync(db, id, skill.SkillCode, ct));
    }

    private static async Task<IResult> UpdateAsync(int id, string code, HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        var (skill, missing) = await FindAsync(db, id, code, ct);
        if (missing is not null) return missing;
        var (request, problem) = await ReadAsync<UpdateJobSkillRequest>(http, ct);
        if (problem is not null) return problem;
        var errors = JobSkillRules.Validate(request!.DisplayName, request.SkillType);
        if (errors.Count > 0) return Invalid(errors);
        skill!.DisplayName = request.DisplayName!.Trim(); skill.SkillType = request.SkillType!;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return TypedResults.Ok(await LoadAsync(db, id, skill.SkillCode, ct));
    }

    private static async Task<IResult> DeleteAsync(int id, string code, SmartCvDbContext db, CancellationToken ct)
    {
        var (skill, missing) = await FindAsync(db, id, code, ct);
        if (missing is not null) return missing;
        if (await db.EvaluationSkills.AsNoTracking().AnyAsync(e => e.JobId == id && e.SkillCode == skill!.SkillCode, ct))
            return Used();
        db.JobSkillAliases.RemoveRange(skill!.Aliases); db.JobSkills.Remove(skill);
        try { await db.SaveChangesAsync(ct); }
        // The FK remains authoritative if an evaluation is inserted after the pre-check.
        // The same SaveChanges transaction rolls back alias deletes as well.
        catch (DbUpdateException ex) when (SqlError(ex, 547)) { return Used(); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ReplaceAliasesAsync(int id, string code, HttpContext http, SmartCvDbContext db, CancellationToken ct)
    {
        var (request, problem) = await ReadAsync<ReplaceAliasesRequest>(http, ct);
        if (problem is not null) return problem;
        if (request!.Aliases is null) return Invalid(new() { ["aliases"] = ["Cần gửi danh sách từ khóa; dùng [] để xóa toàn bộ."] });
        var (aliases, error) = JobSkillRules.NormalizeAliases(request.Aliases);
        if (error is not null) return Invalid(new() { ["aliases"] = [error] });
        // Protect the read/diff/write range, including an initially empty list. Concurrent
        // replacements must not silently merge two different complete lists.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        try
        {
            var (skill, missing) = await FindAsync(db, id, code, ct);
            if (missing is not null) return missing;
            var keep = aliases.ToHashSet(StringComparer.Ordinal);
            db.JobSkillAliases.RemoveRange(skill!.Aliases.Where(a => !keep.Contains(a.Alias)));
            var existing = skill.Aliases.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
            foreach (var alias in aliases.Where(a => !existing.Contains(a)))
                db.JobSkillAliases.Add(new JobSkillAlias { JobId = id, SkillCode = skill.SkillCode, Alias = alias });
            await db.SaveChangesAsync(ct);
            var dto = await LoadAsync(db, id, skill.SkillCode, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return TypedResults.Ok(dto);
        }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        catch (DbUpdateException ex) when (SqlError(ex, 2601, 2627, 547, 1205)) { return Changed("aliases"); }
        catch (SqlException ex) when (ex.Number == 1205) { return Changed("aliases"); }
        // EF's non-retrying SQL execution strategy can wrap deadlocks as transient failures.
        catch (InvalidOperationException ex) when (SqlError(ex, 1205)) { return Changed("aliases"); }
    }

    internal static JobSkill Entity(JobSkillInput skill) => new()
    {
        SkillCode = skill.SkillCode!, DisplayName = skill.DisplayName!, SkillType = skill.SkillType!,
        Aliases = (skill.Aliases ?? []).Select(a => new JobSkillAlias { SkillCode = skill.SkillCode!, Alias = a! }).ToList()
    };

    // Keep the stored key (including legacy lowercase seed codes) intact. Job-scoped lookup
    // supports case-insensitive routes on both SQL Server and the HTTP test provider.
    private static IQueryable<JobSkill> Skills(SmartCvDbContext db, int id, string code)
    {
        var canonical = code.ToUpperInvariant();
        return db.JobSkills.Where(s => s.JobId == id && s.SkillCode.ToUpper() == canonical);
    }

    private static async Task<(JobSkill? Skill, ProblemHttpResult? Problem)> FindAsync(SmartCvDbContext db, int id, string code, CancellationToken ct)
    {
        if (id <= 0) return (null, Invalid(new() { ["id"] = ["Mã Job phải là số nguyên dương."] }));
        if (!JobSkillRules.IsValidCode(code.ToUpperInvariant()))
            return (null, Invalid(new() { ["skillCode"] = ["Mã kỹ năng trên đường dẫn không hợp lệ."] }));
        if (!await db.Jobs.AsNoTracking().AnyAsync(j => j.JobId == id, ct)) return (null, JobMissing());
        var skill = await Skills(db, id, code).Include(s => s.Aliases).SingleOrDefaultAsync(ct);
        return skill is null ? (null, TypedResults.Problem(statusCode: 404, title: "Không tìm thấy kỹ năng", detail: "Job này không có kỹ năng được yêu cầu.")) : (skill, null);
    }

    private static async Task<JobSkillDto?> LoadAsync(SmartCvDbContext db, int id, string code, CancellationToken ct) =>
        await db.JobSkills.AsNoTracking().Where(s => s.JobId == id && s.SkillCode == code).Select(s => new JobSkillDto
        {
            SkillCode = s.SkillCode, DisplayName = s.DisplayName, SkillType = s.SkillType,
            Aliases = s.Aliases.OrderBy(a => a.Alias).Select(a => a.Alias).ToList()
        }).SingleOrDefaultAsync(ct);

    private static async Task<(T? Body, ProblemHttpResult? Problem)> ReadAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (http.Request.ContentLength == 0 || http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false)
            return (null, Invalid(new() { ["body"] = ["Cần gửi nội dung JSON."] }));
        if (http.Request.ContentLength > MaxBodyBytes) return (null, TooLarge());
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;
        if (!http.Request.HasJsonContentType())
            return (null, TypedResults.Problem(statusCode: 415, title: "Nội dung yêu cầu phải là JSON", detail: "Gửi Content-Type: application/json và body UTF-8."));
        try
        {
            var body = await http.Request.ReadFromJsonAsync<T>(RequestJson, ct);
            return body is null ? (null, Invalid(new() { ["body"] = ["Cần gửi nội dung JSON."] })) : (body, null);
        }
        catch (JsonException ex)
        {
            var field = new[] { "skillCode", "displayName", "skillType", "aliases" }
                .FirstOrDefault(f => ex.Path?.Equals("$." + f, StringComparison.OrdinalIgnoreCase) == true || ex.Path?.StartsWith("$." + f + "[", StringComparison.OrdinalIgnoreCase) == true) ?? "body";
            return (null, Invalid(new() { [field] = ["JSON không hợp lệ. Kiểm tra tên trường và kiểu dữ liệu."] }));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return (null, TooLarge()); }
    }

    private static bool SqlError(Exception ex, params int[] numbers)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => numbers.Contains(e.Number))) return true;
        return false;
    }
    internal static bool AliasConflict(DbUpdateException ex) => ex.InnerException is SqlException sql
        && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627 && e.Message.Contains("PK_JobSkillAliases", StringComparison.Ordinal));
    internal static ProblemHttpResult AliasDuplicate() => TypedResults.Problem(statusCode: 409, title: "Trùng từ khóa",
        detail: "Có từ khóa tương đương theo quy tắc so sánh của database. Hãy kiểm tra lại danh sách.",
        extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { ["aliases"] = ["Từ khóa bị trùng theo quy tắc so sánh của database."] } });
    private static ProblemHttpResult TooLarge() => TypedResults.Problem(statusCode: 413, title: "Nội dung yêu cầu quá lớn", detail: "Body tối đa 256 KiB.");
    private static ProblemHttpResult Invalid(Dictionary<string, string[]> errors) => TypedResults.Problem(statusCode: 400, title: "Dữ liệu chưa hợp lệ",
        detail: string.Join(" ", errors.Values.SelectMany(e => e)), extensions: new Dictionary<string, object?> { ["errors"] = errors });
    private static ProblemHttpResult JobMissing() => TypedResults.Problem(statusCode: 404, title: "Không tìm thấy Job", detail: "Job được yêu cầu không tồn tại.");
    private static ProblemHttpResult Duplicate() => TypedResults.Problem(statusCode: 409, title: "Trùng mã kỹ năng", detail: "Job này đã có mã kỹ năng đó. Hãy chọn mã khác.",
        extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { ["skillCode"] = ["Mã kỹ năng đã được sử dụng trong Job này."] } });
    private static ProblemHttpResult Used() => TypedResults.Problem(statusCode: 409, title: "Kỹ năng đang được dùng", detail: "Kỹ năng đã có kết quả chấm nên không thể xóa. Có thể sửa tên, loại hoặc từ khóa; lịch sử điểm được giữ nguyên.");
    private static ProblemHttpResult Changed(string field = "body") => TypedResults.Problem(statusCode: 409, title: "Dữ liệu đã thay đổi", detail: "Dữ liệu vừa được thay đổi bởi thao tác khác. Hãy tải lại trước khi lưu.",
        extensions: new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [field] = ["Hãy tải lại dữ liệu và thử lại."] } });
}
