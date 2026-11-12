using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Core.Privacy;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Scoring;

namespace SmartCv.Tests;

/// <summary>
/// A37 ở tiến trình chấm chính thức (A11, dùng chung cho lượt Screening và PromptTest): tên ứng viên lấy từ bảng Candidates
/// truyền vào yêu cầu AI; payload Gemini đã che; keyword và database vẫn dùng nội dung gốc.
/// Phần partial của RunScoringProcessorTests để dùng lại Env, StubAi, AddRunAsync, EvalOf.
/// </summary>
public partial class RunScoringProcessorTests
{
    private const string A37Candidate = "Trần Minh Khoa";

    // Email và đường dẫn chứa từ khóa kỹ năng: chấm keyword trên bản đã che sẽ ra điểm khác → chứng minh keyword dùng bản gốc.
    private const string A37Cv =
        "Họ và tên: Trần Minh Khoa\n" +
        "Email: python.ml@example.com | github.com/khoa/machine-learning-python\n" +
        "GPA 3.5. LLM API, Prompt Engineering, SQL, NLP.";

    private static async Task<int> AddA37ItemAsync(Env env, int runId, string text, string? candidateName)
    {
        Candidate? candidate = candidateName is null ? null : new Candidate { FullName = candidateName, CreatedAt = DateTime.UtcNow };
        if (candidate is not null) env.Db.Candidates.Add(candidate);
        var cvId = Guid.NewGuid();
        env.Db.Cvs.Add(new Cv
        {
            CvId = cvId, Candidate = candidate, OriginalFileName = "cv.pdf", StoredPath = $"Uploads/CV/{cvId}.pdf", ContentType = "application/pdf",
            FileSizeBytes = 1, FileHash = cvId.ToString("N").PadRight(64, '0').ToUpperInvariant(), ExtractionStatus = ExtractionStatuses.Completed,
            ExtractedText = text, UploadedAt = DateTime.UtcNow
        });
        var application = new Application { CvId = cvId, JobId = AiJob, CreatedAt = DateTime.UtcNow };
        env.Db.Applications.Add(application);
        await env.Db.SaveChangesAsync();
        var evaluation = new Evaluation { RunId = runId, ApplicationId = application.ApplicationId, Status = EvaluationStatuses.Pending, CreatedAt = DateTime.UtcNow };
        env.Db.Evaluations.Add(evaluation);
        await env.Db.SaveChangesAsync();
        return evaluation.EvaluationId;
    }

    [Fact]
    public async Task A37_worker_passes_candidate_name_from_database_and_original_cv_text()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 2);
        await AddA37ItemAsync(env, run, A37Cv, A37Candidate);
        await AddA37ItemAsync(env, run, GoodCv, candidateName: null);
        var ai = new StubAi();

        await env.Processor(ai).ProcessRunAsync(run, default);

        Assert.Equal(2, ai.Calls.Count);
        var known = Assert.Single(ai.Calls, c => c.CvText == A37Cv);      // nội dung gốc: che là việc của GeminiEvaluationService
        Assert.Equal(A37Candidate, known.CandidateName);
        Assert.Null(Assert.Single(ai.Calls, c => c.CvText == GoodCv).CandidateName);
    }

    [Fact]
    public async Task A37_worker_with_gemini_sends_masked_payload_keeps_database_and_keyword_on_original_text()
    {
        using var env = await CreateAsync();
        var run = await AddRunAsync(env, 1);
        var evaluationId = await AddA37ItemAsync(env, run, A37Cv, A37Candidate);
        var handler = new GeminiPayloadCapture();
        var options = Options.Create(new GeminiOptions { ApiKey = "test-key-a37", MinDelayBetweenCallsMs = 0 });
        var gemini = new GeminiEvaluationService(new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/v1beta/") },
            new GeminiRateLimiter(options), options, NullLogger<GeminiEvaluationService>.Instance);

        await env.Processor(gemini).ProcessRunAsync(run, default);

        var user = GeminiPayloadCapture.UserText(Assert.Single(handler.Bodies));
        foreach (var raw in new[] { A37Candidate, "python.ml@example.com", "github.com/khoa" }) Assert.DoesNotContain(raw, user);
        Assert.Contains("Họ và tên: [CANDIDATE]\nEmail: [EMAIL] | [LINK]\nGPA 3.5. LLM API, Prompt Engineering, SQL, NLP.", user);

        var evaluation = await EvalOf(env, evaluationId);
        Assert.Equal(EvaluationStatuses.Completed, evaluation.Status);
        Assert.Equal(75m, evaluation.AiScore);

        await using var db = env.Fresh();
        Assert.Equal(A37Cv, await db.Applications.Where(a => a.ApplicationId == evaluation.ApplicationId).Select(a => a.Cv.ExtractedText).SingleAsync());

        var job = (await db.LoadJobProfileAsync(AiJob, default))!;
        var scoring = new ScoringOptions { MissingMustHaveCap = 69m };
        var today = DateOnly.FromDateTime(DateTime.Today);
        var onOriginal = KeywordScoringService.Score(job, A37Cv, scoring, today).Score;
        var onMasked = KeywordScoringService.Score(job, PersonalDataMasker.Mask(A37Cv, A37Candidate).Text, scoring, today).Score;
        Assert.NotEqual(onOriginal, onMasked);                // điều kiện của phép thử: bản che làm keyword khác đi
        Assert.Equal(onOriginal, evaluation.KeywordScore);    // keyword đã lưu tính trên nội dung gốc
    }
}
