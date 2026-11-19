using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Entities;
using SmartCv.Core.Scoring;

namespace SmartCv.Tests.Dashboard;

/// <summary>
/// SCApp v2 trên SQL Server thật (opt-in, giống A19): mỗi test tạo catalog MỚI SmartCv_A19_Test_&lt;guid&gt;, không đụng SmartCvDb.
/// Kiểm tra các truy vấn mới dịch được sang SQL: đếm thành viên, chỉ mục duy nhất của cờ đang mở, "Cần xem kỹ" trên Tổng quan,
/// biểu đồ xu hướng (CreatedAt + lịch sử giai đoạn).
/// </summary>
public class ScappWorkflowV2SqlServerTests
{
    [A19SqlFact]
    public async Task Members_flags_needs_review_and_trend_work_on_sql_server()
    {
        await using var env = await DashboardSqlEnvironment.CreateAsync();
        var created = new DateTime(2026, 11, 9, 16, 30, 0, DateTimeKind.Utc);   // 23:30 ngày 09/11 giờ Việt Nam
        int jobId, open, shortlisted, runId, promptId;
        await using (var db = env.Context())
        {
            var job = new Job { Title = "SCApp v2 SQL", CreatedAt = created };
            var prompt = new PromptVersion { VersionName = "SCApp v2 SQL", SystemPrompt = "synthetic", CreatedAt = created };
            Application App(int n, string stage) => new()
            {
                Job = job, CreatedAt = created, Stage = stage,
                Cv = new Cv
                {
                    CvId = Guid.NewGuid(), OriginalFileName = $"v2-{n}.pdf", StoredPath = "synthetic", ContentType = "application/pdf",
                    FileHash = new string((char)('a' + n), 64), FileSizeBytes = 10, ExtractionStatus = ExtractionStatuses.Completed,
                    ExtractedText = "Kinh nghiệm C# và SQL Server. " + new string('x', 600), UploadedAt = created
                }
            };
            var a = App(1, ApplicationStages.New);
            var b = App(2, ApplicationStages.Shortlisted);
            db.AddRange(a, b, prompt);
            await db.SaveChangesAsync();
            var run = new EvaluationRun
            {
                RunType = RunTypes.Screening, Status = RunStatuses.Completed, PromptVersionId = prompt.PromptVersionId, ModelName = "fixture",
                KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, TotalItems = 2, ProcessedItems = 2, CreatedAt = created,
                Evaluations = new[] { a, b }.Select(x => new Evaluation
                {
                    ApplicationId = x.ApplicationId, Status = EvaluationStatuses.Completed, KeywordScore = 80, AiScore = 82, FinalScore = 81,
                    ScoreSource = ScoreSources.Combined, FitLabel = FitLevels.Good, PredictedPass = true, CreatedAt = created
                }).ToList()
            };
            db.Add(run);
            await db.SaveChangesAsync();
            (jobId, open, shortlisted, runId, promptId) = (job.JobId, a.ApplicationId, b.ApplicationId, run.RunId, prompt.PromptVersionId);
        }
        var client = env.Client;

        // Bộ CV: tạo, thêm thành viên (có trùng), gán nhãn, danh sách và chi tiết
        using var createdSet = await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest { Name = "Bộ SQL", JobId = jobId, Purpose = "Kiểm tra SQL" });
        Assert.Equal(HttpStatusCode.Created, createdSet.StatusCode);
        var set = (await createdSet.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
        using var added = await client.PostAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/members", new AddEvaluationSetMembersRequest { ApplicationIds = [open, open, shortlisted], AddedBy = "HR" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(2, (await added.Content.ReadFromJsonAsync<AddEvaluationSetMembersResult>())!.Added);
        using (var save = await client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest { LabeledBy = "HR", Labels = [new() { ApplicationId = open, IsPass = true }] }))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var listed = (await client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!.Single();
        Assert.Equal((2, 1, 1, "SCApp v2 SQL"), (listed.MemberCount, listed.LabelCount, listed.UnlabeledCount, listed.JobTitle));
        var detail = (await client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{set.EvaluationSetId}"))!;
        Assert.Equal(2, detail.Members.Count);
        Assert.True(detail.Revision >= 3);
        var picker = (await client.GetFromJsonAsync<List<LabelCandidateDto>>($"/api/evaluation-sets/{set.EvaluationSetId}/applications?jobId={jobId}&from=2026-11-09&to=2026-11-09&notInSet=true"))!;
        Assert.Empty(picker);

        // Cờ HR: chỉ mục duy nhất có lọc chặn cờ mở thứ hai; giai đoạn không đổi
        foreach (var id in new[] { open, shortlisted })
            using (var flag = await client.PostAsJsonAsync($"/api/applications/{id}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.SecondOpinion, Actor = "HR" }))
                Assert.Equal(HttpStatusCode.Created, flag.StatusCode);
        using (var second = await client.PostAsJsonAsync($"/api/applications/{open}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.ScoreDoubt, Actor = "HR" }))
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var ranking = (await client.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking?jobId={jobId}"))!;
        Assert.Equal((2, 1), (ranking.ManualFlagCount, ranking.NeedsReviewCount));
        var summary = await env.SummaryAsync($"?jobId={jobId}");
        Assert.Equal((1, 1, jobId), (summary.FlaggedUnresolved, summary.ManualFlagsOpen, summary.FlaggedJobId!.Value));

        // Xu hướng: lịch Việt Nam, hồ sơ Qua vòng CV được tính vào đường tỷ lệ
        var trend = (await client.GetFromJsonAsync<DashboardTrendDto>($"/api/dashboard/trend?jobId={jobId}&from=2026-11-09&to=2026-11-10"))!;
        Assert.Equal((2, 1, 50m), (trend.Points[0].Applications, trend.Points[0].PassedCvRound, trend.Points[0].PassRatePercent));
        Assert.Equal(0, trend.Points[1].Applications);

        // Prompt Test ghi phiên bản bộ lúc tạo (chạy Gemini KHÔNG xảy ra: worker đã bị gỡ khỏi host test)
        using (var label = await client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest { LabeledBy = "HR", Labels = [new() { ApplicationId = shortlisted, IsPass = false }] }))
            Assert.Equal(HttpStatusCode.OK, label.StatusCode);
        using var start = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = promptId, EvaluationSetId = set.EvaluationSetId });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var queued = (await start.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        await using (var db = env.Context())
        {
            var revision = await db.EvaluationSets.Where(s => s.EvaluationSetId == set.EvaluationSetId).Select(s => s.Revision).SingleAsync();
            Assert.Equal(revision, queued.EvaluationSetRevision);
            Assert.Equal(ApplicationStages.New, await db.Applications.Where(x => x.ApplicationId == open).Select(x => x.Stage).SingleAsync());
        }
    }
}
