using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Prompts;

namespace SmartCv.Infrastructure.Scoring;

/// <summary>
/// A11: chấm các hồ sơ của MỘT lượt: Queued → Running → chấm từng Evaluation đang Pending → Completed.
/// Mỗi hồ sơ được LƯU NGAY, kết quả + kỹ năng + tiến độ trong một lần SaveChanges (một giao dịch).
/// Tắt API giữa chừng: hồ sơ đã lưu giữ nguyên, lần chạy sau tiếp tục từ hồ sơ còn Pending.
/// Prompt, model, temperature, trọng số, ngưỡng lấy TỪ LƯỢT CHẤM, không đọc prompt đang kích hoạt.
/// Không ghi log tên file, nội dung CV hay JSON của AI.
/// </summary>
public sealed class RunScoringProcessor(
    SmartCvDbContext db,
    IAiEvaluator ai,
    IOptions<ScoringOptions> scoringOptions,
    ILogger<RunScoringProcessor> logger)
{
    private const string WorkerLockName = "SmartCv.RunScoringWorker";
    private const int MaxErrorLength = 1000;
    private const int MaxEvidenceLength = 500;

    // SQL Server từ chối vì dữ liệu của chính hồ sơ này: khóa ngoại / CHECK, trùng khóa, tràn số, cắt chuỗi
    private static readonly int[] PermanentDataErrors = [547, 2601, 2627, 8115, 8152, 2628];

    private readonly ScoringOptions _scoring = scoringOptions.Value;
    private readonly Dictionary<int, JobProfile> _jobCache = [];

    private sealed record RunSettings(int RunId, string ModelName, decimal Temperature, decimal KeywordWeight, decimal AiWeight, decimal PassThreshold, string SystemPrompt);

    /// <summary>
    /// Khóa để cả hệ thống chỉ một worker chấm tại một thời điểm, kể cả khi chạy nhiều instance API.
    /// Dùng khóa ứng dụng của SQL Server gắn với phiên: tiến trình chết thì khóa tự nhả, không cần cột mới.
    /// Trả null nếu instance khác đang giữ.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireWorkerLockAsync(CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return new NoLock();

        await db.Database.OpenConnectionAsync(ct);   // giữ kết nối mở suốt vòng: khóa gắn với phiên này
        var acquired = false;
        try
        {
            acquired = await CallAppLockAsync("sp_getapplock", ct, lockTimeoutMs: 0) >= 0;
            if (!acquired) logger.LogDebug("Bỏ qua vòng chấm: instance khác đang giữ khóa worker");
            return acquired ? new SqlWorkerLock(this) : null;
        }
        finally
        {
            if (!acquired) await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<int> CallAppLockAsync(string procedure, CancellationToken ct, int? lockTimeoutMs = null)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        AddParameter(command, "@Resource", WorkerLockName);
        AddParameter(command, "@LockOwner", "Session");
        if (lockTimeoutMs is { } timeout)
        {
            AddParameter(command, "@LockMode", "Exclusive");
            AddParameter(command, "@LockTimeout", timeout);
        }
        var result = command.CreateParameter();
        result.ParameterName = "@Result";
        result.DbType = DbType.Int32;
        result.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(result);

        await command.ExecuteNonQueryAsync(ct);
        return (int)result.Value!;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SqlWorkerLock(RunScoringProcessor owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await owner.ReleaseWorkerLockAsync();
    }

    private async Task ReleaseWorkerLockAsync()
    {
        try
        {
            // Phải nhả rõ ràng: kết nối về pool vẫn giữ phiên, khóa chưa tự mất
            await CallAppLockAsync("sp_releaseapplock", CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            logger.LogWarning("Không nhả được khóa worker; khóa sẽ mất khi phiên SQL đóng");
        }
        await db.Database.CloseConnectionAsync();
    }

    /// <summary>Các lượt chấm đang chờ hoặc đang chạy, cũ nhất trước.</summary>
    public Task<List<int>> FindActiveRunsAsync(CancellationToken ct) =>
        db.EvaluationRuns.AsNoTracking()
            .Where(r => ((r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running)
                        && (r.RunType == RunTypes.Screening || r.RunType == RunTypes.PromptTest))
                || (r.RunType == RunTypes.PromptTest && r.Status == RunStatuses.Completed
                    && r.AccuracyPercent == null
                    && r.Evaluations.Any(e => e.Application.HrLabels.Any(h => h.EvaluationSetId == r.EvaluationSetId))))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.RunId)
            .Select(r => r.RunId)
            .ToListAsync(ct);

    /// <summary>
    /// Chấm các hồ sơ còn Pending của lượt. Trả về số hồ sơ đã có kết quả trong lần gọi này.
    /// Hồ sơ có CV đang chờ đọc chữ (A7) được để lại; lượt chỉ Completed khi không còn hồ sơ Pending.
    /// </summary>
    public async Task<int> ProcessRunAsync(int runId, CancellationToken ct)
    {
        var run = await db.EvaluationRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return 0;
        // A16 restart recovery: completed CV results are never sent to AI again.
        if (run.RunType == RunTypes.PromptTest && run.Status == RunStatuses.Completed)
        {
            if (run.AccuracyPercent is null && run.EvaluationSetId is { } setId)
            {
                await using var transaction = await EvaluationSetWriteScope.BeginAsync(db, setId, ct);
                await SaveAccuracyAsync(run.RunId, ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
            }
            return 0;
        }
        if (run.Status is not (RunStatuses.Queued or RunStatuses.Running)) return 0;

        if (run.Status == RunStatuses.Queued)
        {
            run.Status = RunStatuses.Running;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Bắt đầu lượt chấm {RunId}: {Total} hồ sơ, model {Model}", run.RunId, run.TotalItems, run.ModelName);
        }

        var settings = await db.EvaluationRuns.AsNoTracking().Where(r => r.RunId == runId)
            .Select(r => new RunSettings(r.RunId, r.ModelName, r.Temperature, r.KeywordWeight, r.AiWeight, r.PassThreshold, r.PromptVersion.SystemPrompt))
            .FirstAsync(ct);

        var pendingIds = await db.Evaluations.AsNoTracking()
            .Where(e => e.RunId == runId && e.Status == EvaluationStatuses.Pending)
            .OrderBy(e => e.EvaluationId)
            .Select(e => e.EvaluationId)
            .ToListAsync(ct);

        var scored = 0;
        foreach (var evaluationId in pendingIds)
        {
            ct.ThrowIfCancellationRequested();
            if (await ScoreOneAsync(settings, evaluationId, ct)) scored++;
        }

        // Lượt không có hồ sơ cần chấm trong vòng này: vẫn đếm lại để hoàn tất nếu đã hết Pending
        db.ChangeTracker.Clear();
        await SaveWithProgressAsync(runId, current: null);
        return scored;
    }

    /// <summary>Chấm một hồ sơ. Trả false nếu chưa có kết quả (CV chưa đọc chữ xong, hoặc hồ sơ không còn Pending).</summary>
    private async Task<bool> ScoreOneAsync(RunSettings run, int evaluationId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();   // mỗi hồ sơ bắt đầu sạch, bộ nhớ không tăng theo số CV

        var evaluation = await db.Evaluations.FirstOrDefaultAsync(e => e.EvaluationId == evaluationId, ct);
        if (evaluation is null || evaluation.RunId != run.RunId || evaluation.Status != EvaluationStatuses.Pending) return false;

        var info = await db.Applications.AsNoTracking()
            .Where(a => a.ApplicationId == evaluation.ApplicationId)
            .Select(a => new
            {
                a.JobId, a.Cv.ExtractionStatus, a.Cv.ExtractedText,
                CandidateName = a.Cv.Candidate != null ? a.Cv.Candidate.FullName : null   // A37: để che họ tên trước khi gửi AI
            })
            .FirstOrDefaultAsync(ct);

        // Xét trạng thái trước: khi đọc lại (A7), CV về Pending nhưng vẫn còn chữ cũ
        if (info?.ExtractionStatus == ExtractionStatuses.Pending) return false;

        string? failure = info switch
        {
            null => "Không tìm thấy hồ sơ ứng tuyển.",
            { ExtractionStatus: ExtractionStatuses.Failed } => "CV chưa trích xuất được nội dung (ExtractionStatus = Failed).",
            { ExtractedText: var text } when string.IsNullOrWhiteSpace(text) => "CV đã trích xuất nhưng không có nội dung chữ.",
            _ => null
        };

        var job = failure is null ? await GetJobAsync(info!.JobId, ct) : null;
        if (failure is null && job is null) failure = "Không tìm thấy vị trí tuyển dụng của hồ sơ.";

        CombinedScore? result = null;
        AiEvaluationResult? aiResult = null;
        if (failure is null)
        {
            try
            {
                var keyword = KeywordScoringService.Score(job!, info!.ExtractedText, _scoring, DateOnly.FromDateTime(DateTime.Today));
                if (!keyword.HasApplicableCriteria)   // kiểm tra trước khi gọi AI để không tốn quota
                {
                    failure = "Vị trí không có tiêu chí nào áp dụng được nên không tính được điểm.";
                }
                else
                {
                    // Keyword (ở trên) dùng nội dung gốc; bản gửi Gemini được che trong GeminiEvaluationService (A37).
                    aiResult = await ai.EvaluateAsync(new AiEvaluationRequest(run.ModelName, run.Temperature, run.SystemPrompt, job!,
                        info.ExtractedText!, info.CandidateName), ct);
                    result = ScoreCombiner.Combine(keyword, aiResult, run.KeywordWeight, run.AiWeight, run.PassThreshold, _scoring.MissingMustHaveCap);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Lỗi ở một hồ sơ: ghi lỗi, chấm tiếp hồ sơ khác. Chỉ ghi loại lỗi vì nội dung có thể chứa dữ liệu CV.
                logger.LogError("Lỗi khi chấm hồ sơ {EvaluationId} của lượt {RunId}: {ErrorType}", evaluationId, run.RunId, ex.GetType().Name);
                failure = $"Lỗi khi chấm hồ sơ ({ex.GetType().Name}).";
            }
        }

        if (failure is not null)
        {
            MarkFailed(evaluation, failure);
            await MergeSkillsAsync(evaluationId, info?.JobId ?? 0, [], ct);
        }
        else
        {
            Apply(evaluation, result!, aiResult!);
            await MergeSkillsAsync(evaluationId, job!.JobId, result!.Skills, ct);
        }

        try
        {
            await SaveWithProgressAsync(run.RunId, evaluation);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && PermanentDataErrors.Contains(sql.Number))
        {
            // Dữ liệu của hồ sơ này không lưu được: thử lại cũng vậy và sẽ tốn thêm quota, nên ghi Failed
            logger.LogError("Không lưu được kết quả hồ sơ {EvaluationId} của lượt {RunId}: lỗi SQL {SqlNumber}", evaluationId, run.RunId, ((SqlException)ex.InnerException).Number);
            db.ChangeTracker.Clear();
            evaluation = await db.Evaluations.FirstAsync(e => e.EvaluationId == evaluationId, CancellationToken.None);
            MarkFailed(evaluation, "Kết quả chấm không lưu được do vi phạm ràng buộc dữ liệu.");
            await MergeSkillsAsync(evaluationId, info?.JobId ?? 0, [], CancellationToken.None);
            result = null;
            await SaveWithProgressAsync(run.RunId, evaluation);
        }

        if (evaluation.Status == EvaluationStatuses.Failed)
            logger.LogWarning("Hồ sơ {EvaluationId} của lượt {RunId} lỗi", evaluationId, run.RunId);
        else
            logger.LogInformation("Lượt {RunId}, hồ sơ {EvaluationId}: keyword {Keyword}, AI {Ai}, cuối {Final} ({Source}, giả lập: {Simulated})",
                run.RunId, evaluationId, result!.KeywordScore, result.AiScore?.ToString() ?? "-", result.FinalScore, result.ScoreSource, result.IsSimulated);

        return true;
    }

    /// <summary>
    /// Lưu kết quả cùng tiến độ. Dùng CancellationToken.None: kết quả AI đã có (đã tốn quota) thì lưu cho xong
    /// kể cả khi API đang tắt, phần chưa gọi AI đã dừng theo token từ trước.
    /// </summary>
    private async Task SaveWithProgressAsync(int runId, Evaluation? current)
    {
        var run = await db.EvaluationRuns.FirstAsync(r => r.RunId == runId, CancellationToken.None);
        // A16: same lock order as HR label writes. AI computation has already finished outside this lock.
        await using var transaction = run.RunType == RunTypes.PromptTest && run.EvaluationSetId is { } setId
            ? await EvaluationSetWriteScope.BeginAsync(db, setId, CancellationToken.None) : null;
        await RefreshProgressAsync(run, current, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        if (run.RunType == RunTypes.PromptTest && run.Status == RunStatuses.Completed)
            await SaveAccuracyAsync(runId, CancellationToken.None);
        if (transaction is not null) await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task SaveAccuracyAsync(int runId, CancellationToken ct)
    {
        var result = await db.SaveCompletedAccuracyAsync(runId, ct);
        if (result is null) return;
        logger.LogInformation(
            "Lượt PromptTest {RunId}: đúng {Correct}/{Total}, accuracy {Accuracy}, bỏ sót {Missed}, chọn nhầm {WrongPick}, chưa chấm được {Unscored}, chỉ keyword {KeywordOnly}",
            runId, result.Correct, result.Total, result.AccuracyPercent, result.Missed, result.WrongPick,
            result.Unscored, result.KeywordOnly);
    }

    private static void MarkFailed(Evaluation e, string message)
    {
        // Pending rows may contain stale data from an external/manual reset.
        // A failed extraction/persistence must never retain a previous score as its result.
        e.KeywordScore = null;
        e.AiScore = null;
        e.FinalScore = null;
        e.ScoreSource = null;
        e.FitLabel = null;
        e.PredictedPass = null;
        e.YearsExperience = null;
        e.Reasoning = null;
        e.AiRawResponse = null;
        e.Status = EvaluationStatuses.Failed;
        e.ErrorMessage = Truncate(message, MaxErrorLength);
    }

    private static void Apply(Evaluation e, CombinedScore r, AiEvaluationResult ai)
    {
        e.KeywordScore = r.KeywordScore;
        e.AiScore = r.AiScore;
        e.FinalScore = r.FinalScore;
        e.ScoreSource = r.ScoreSource;
        e.FitLabel = r.FitLabel;
        e.PredictedPass = r.PredictedPass;
        e.YearsExperience = r.YearsExperience;
        e.Reasoning = r.Reasoning;
        e.AiRawResponse = ai.RawResponse;
        e.Status = EvaluationStatuses.Completed;   // KeywordOnly vẫn là Completed, ScoreSource và ErrorMessage ghi rõ
        e.ErrorMessage = r.ScoreSource == ScoreSources.KeywordOnly ? Truncate(r.ErrorMessage, MaxErrorLength) : null;
    }

    /// <summary>Mỗi kỹ năng của job một dòng. Dòng cũ (nếu có) được cập nhật tại chỗ, không tạo trùng khóa.</summary>
    private async Task MergeSkillsAsync(int evaluationId, int jobId, IReadOnlyList<SkillMatch> skills, CancellationToken ct)
    {
        var rows = await db.EvaluationSkills.Where(s => s.EvaluationId == evaluationId).ToListAsync(ct);
        // JobId is part of the primary key: a stale row from another job must be removed,
        // never reused just because its SkillCode matches.
        db.EvaluationSkills.RemoveRange(rows.Where(s => s.JobId != jobId));
        var existing = rows.Where(s => s.JobId == jobId).ToDictionary(s => s.SkillCode);

        foreach (var skill in skills)
        {
            if (!existing.Remove(skill.SkillCode, out var row))
            {
                row = new EvaluationSkill { EvaluationId = evaluationId, JobId = jobId, SkillCode = skill.SkillCode };
                db.EvaluationSkills.Add(row);
            }
            row.IsMatched = skill.IsMatched;
            row.Evidence = Truncate(skill.Evidence, MaxEvidenceLength);
        }
        db.EvaluationSkills.RemoveRange(existing.Values);
    }

    /// <summary>
    /// Đếm lại tiến độ từ database (không cộng dồn nên không lệch). <paramref name="current"/> là hồ sơ đang chờ lưu cùng giao dịch.
    /// Chỉ Completed khi không còn Pending và đã có đủ số hồ sơ của lượt.
    /// </summary>
    private async Task RefreshProgressAsync(EvaluationRun run, Evaluation? current, CancellationToken ct)
    {
        var currentId = current?.EvaluationId;
        var statuses = await db.Evaluations.AsNoTracking()
            .Where(e => e.RunId == run.RunId && e.EvaluationId != currentId)
            .Select(e => e.Status)
            .ToListAsync(ct);
        if (current is not null) statuses.Add(current.Status);

        var processed = statuses.Count(s => s != EvaluationStatuses.Pending);
        var changed = processed != run.ProcessedItems;
        run.ProcessedItems = processed;
        run.FailedItems = statuses.Count(s => s == EvaluationStatuses.Failed);

        if (changed)
            logger.LogInformation("Lượt {RunId}: {Processed}/{Total} ({Percent}%), lỗi {Failed}",
                run.RunId, run.ProcessedItems, run.TotalItems, ProgressPercent(run.ProcessedItems, run.TotalItems), run.FailedItems);

        var finished = statuses.Count >= run.TotalItems && processed == statuses.Count;
        if (finished && run.Status != RunStatuses.Completed)
        {
            run.Status = RunStatuses.Completed;
            run.CompletedAt = DateTime.UtcNow;
            logger.LogInformation("Xong lượt chấm {RunId}: {Done}/{Total}, lỗi {Failed}", run.RunId, run.ProcessedItems, run.TotalItems, run.FailedItems);
        }
    }

    /// <summary>Phần trăm tiến độ, trong khoảng 0–100. Lượt không có hồ sơ: 100.</summary>
    public static decimal ProgressPercent(int processed, int total) =>
        total <= 0 ? 100m : Math.Min(100m, Math.Round(processed * 100m / total, 2, MidpointRounding.AwayFromZero));

    private async Task<JobProfile?> GetJobAsync(int jobId, CancellationToken ct)
    {
        if (_jobCache.TryGetValue(jobId, out var cached)) return cached;
        var profile = await db.LoadJobProfileAsync(jobId, ct);
        if (profile is not null) _jobCache[jobId] = profile;
        return profile;
    }

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..(max - 1)] + "…";
}
