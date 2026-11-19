extern alias web;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Dashboard;
using SmartCv.Core.Entities;
using SmartCv.Core.Evaluation;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Data;
using WebHelpers = web::SmartCv.Web.Helpers;
using WebServices = web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// SCApp v2: thành viên bộ CV tách khỏi nhãn HR, phiên bản bộ, cờ HR thủ công, "Cần xem kỹ" thống nhất, xu hướng Tổng quan.
/// Chạy trên InMemory (không cần SQL Server, không gọi Gemini).
/// </summary>
public class ScappWorkflowV2Tests
{
    private static WebApplicationFactory<Program> Factory() => TestSupport.ApiFactory("Development", new()
    {
        ["CvUpload:Endpoint:Enabled"] = "true", ["Scoring:WorkerEnabled"] = "false", ["Gemini:UseFake"] = "true"
    });

    private sealed record Seed(int JobId, int OtherJobId, int Ready1, int Ready2, int Ready3, int Pending, int Anonymized, int Prompt);

    private static async Task<Seed> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        await db.Database.EnsureCreatedAsync();
        var jobs = await db.Jobs.OrderBy(j => j.JobId).Take(2).ToListAsync();
        var n = 0;
        Cv Cv(string status = "Completed", string? text = null, DateTime? anonymized = null) => new()
        {
            CvId = Guid.NewGuid(), OriginalFileName = $"cv-{++n}.pdf", StoredPath = "synthetic", ContentType = "application/pdf",
            FileHash = new string((char)('a' + n), 64), FileSizeBytes = 10, ExtractionStatus = status, ExtractedText = text,
            AnonymizedAt = anonymized, UploadedAt = DateTime.UtcNow
        };
        // Đủ dài để không sinh cảnh báo tự động ShortText (ngưỡng mặc định 500 ký tự).
        var longText = "Kinh nghiệm C# và SQL Server. " + new string('x', 600);
        var a1 = new Application { Cv = Cv(text: longText), JobId = jobs[0].JobId, CreatedAt = DateTime.UtcNow };
        var a2 = new Application { Cv = Cv(text: longText), JobId = jobs[0].JobId, CreatedAt = DateTime.UtcNow };
        var a3 = new Application { Cv = Cv(text: longText), JobId = jobs[1].JobId, CreatedAt = DateTime.UtcNow };
        var pending = new Application { Cv = Cv("Pending", null), JobId = jobs[0].JobId, CreatedAt = DateTime.UtcNow };
        var anon = new Application { Cv = Cv(text: longText, anonymized: DateTime.UtcNow), JobId = jobs[0].JobId, CreatedAt = DateTime.UtcNow };
        var prompt = new PromptVersion { VersionName = "v-test", SystemPrompt = "TEST", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.AddRange(a1, a2, a3, pending, anon, prompt);
        await db.SaveChangesAsync();
        return new(jobs[0].JobId, jobs[1].JobId, a1.ApplicationId, a2.ApplicationId, a3.ApplicationId, pending.ApplicationId, anon.ApplicationId, prompt.PromptVersionId);
    }

    private static async Task<EvaluationSetDto> CreateSetAsync(HttpClient client, string name, int? jobId = null)
    {
        using var response = await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest { Name = name, Purpose = "Mục tiêu " + name, JobId = jobId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EvaluationSetDto>())!;
    }

    private static async Task<AddEvaluationSetMembersResult> AddAsync(HttpClient client, int setId, params int[] ids)
    {
        using var response = await client.PostAsJsonAsync($"/api/evaluation-sets/{setId}/members", new AddEvaluationSetMembersRequest { ApplicationIds = [.. ids], AddedBy = "HR - Thử" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AddEvaluationSetMembersResult>())!;
    }

    private static Task<EvaluationSetDetailDto?> DetailAsync(HttpClient client, int setId) =>
        client.GetFromJsonAsync<EvaluationSetDetailDto>($"/api/evaluation-sets/{setId}");

    // ===================== Bộ CV: tạo nhiều bộ, thành viên, chống trùng =====================

    [Fact]
    public async Task Multiple_sets_are_created_with_purpose_and_job_and_listed_with_member_counts()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var first = await CreateSetAsync(client, "Bộ A", seed.JobId);
        var second = await CreateSetAsync(client, "Bộ B");
        Assert.Equal(seed.JobId, first.JobId); Assert.NotNull(first.JobTitle); Assert.Equal("Mục tiêu Bộ A", first.Purpose); Assert.Equal(1, first.Revision);
        await AddAsync(client, first.EvaluationSetId, seed.Ready1, seed.Ready2);
        var list = (await client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!;
        Assert.Equal(2, list.Count);
        var a = list.Single(s => s.EvaluationSetId == first.EvaluationSetId);
        Assert.Equal((2, 0, 2), (a.MemberCount, a.LabelCount, a.UnlabeledCount));
        Assert.Equal(0, list.Single(s => s.EvaluationSetId == second.EvaluationSetId).MemberCount);

        using var badJob = await client.PostAsJsonAsync("/api/evaluation-sets", new CreateEvaluationSetRequest { Name = "Bộ C", JobId = 999_999 });
        Assert.Equal(HttpStatusCode.BadRequest, badJob.StatusCode);
    }

    [Fact]
    public async Task Adding_members_skips_duplicates_missing_and_anonymized_and_bumps_revision_once_per_change()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var set = await CreateSetAsync(client, "Bộ trùng");
        var first = await AddAsync(client, set.EvaluationSetId, seed.Ready1, seed.Ready1, seed.Pending, seed.Anonymized, 987_654);
        Assert.Equal(2, first.Added);
        Assert.Equal(new[] { seed.Ready1, seed.Pending }, first.AddedApplicationIds);
        Assert.Contains(first.Skipped, s => s.ApplicationId == seed.Anonymized && s.Reason == SkippedMemberReasons.Anonymized);
        Assert.Contains(first.Skipped, s => s.ApplicationId == 987_654 && s.Reason == SkippedMemberReasons.NotFound);
        Assert.Contains(first.Warnings, w => w.Contains("chưa sẵn sàng"));
        Assert.Equal(2, first.Revision);

        var again = await AddAsync(client, set.EvaluationSetId, seed.Ready1);
        Assert.Equal(0, again.Added);
        Assert.Equal(SkippedMemberReasons.AlreadyMember, Assert.Single(again.Skipped).Reason);
        Assert.Equal(2, again.Revision);                    // không có thay đổi → không tăng phiên bản

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().EvaluationSetMembers.CountAsync(m => m.EvaluationSetId == set.EvaluationSetId));

        var detail = (await DetailAsync(client, set.EvaluationSetId))!;
        Assert.Equal(2, detail.Members.Count);
        var pending = detail.Members.Single(m => m.ApplicationId == seed.Pending);
        Assert.False(pending.IsReady); Assert.Null(pending.IsPass); Assert.Contains("chờ trích xuất", pending.NotReadyReason);
    }

    [Fact]
    public async Task Candidate_picker_filters_by_job_and_hides_current_members()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var set = await CreateSetAsync(client, "Bộ lọc");
        await AddAsync(client, set.EvaluationSetId, seed.Ready1);
        var all = (await client.GetFromJsonAsync<List<LabelCandidateDto>>($"/api/evaluation-sets/{set.EvaluationSetId}/applications"))!;
        Assert.True(all.Single(c => c.ApplicationId == seed.Ready1).IsMember);
        Assert.True(all.Single(c => c.ApplicationId == seed.Anonymized).IsAnonymized);
        var available = (await client.GetFromJsonAsync<List<LabelCandidateDto>>($"/api/evaluation-sets/{set.EvaluationSetId}/applications?jobId={seed.JobId}&notInSet=true"))!;
        Assert.DoesNotContain(available, c => c.ApplicationId == seed.Ready1);
        Assert.DoesNotContain(available, c => c.ApplicationId == seed.Ready3);   // Job khác
        Assert.Contains(available, c => c.ApplicationId == seed.Ready2);
        Assert.All(available, c => Assert.Equal(DateTimeKind.Utc, c.AppliedAt.Kind));
        using var badStage = await client.GetAsync($"/api/evaluation-sets/{set.EvaluationSetId}/applications?stage=Unknown");
        Assert.Equal(HttpStatusCode.BadRequest, badStage.StatusCode);
    }

    [Fact]
    public async Task Labels_are_separate_from_membership_clear_keeps_member_and_remove_drops_both()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var set = await CreateSetAsync(client, "Bộ nhãn");
        await AddAsync(client, set.EvaluationSetId, seed.Ready1, seed.Ready2);
        // Ready3 chưa là thành viên: lưu nhãn vẫn được (hành vi A15 cũ) và tự thêm làm thành viên.
        using (var save = await client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest
        {
            LabeledBy = "HR - Thử", Labels = [new() { ApplicationId = seed.Ready1, IsPass = true, Note = "Đủ kinh nghiệm" }, new() { ApplicationId = seed.Ready3, IsPass = false }]
        })) Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var detail = (await DetailAsync(client, set.EvaluationSetId))!;
        Assert.Equal(3, detail.Members.Count);
        Assert.Equal((3, 2, 1, 1, 1), (detail.Readiness.MemberCount, detail.Readiness.LabeledCount, detail.Readiness.PassCount, detail.Readiness.FailCount, detail.Readiness.UnlabeledCount));
        Assert.True(detail.Readiness.CanRun);
        Assert.Contains(detail.Readiness.Warnings, w => w.Contains("chưa gán nhãn"));
        var revision = detail.Revision;

        // "Chưa đánh giá": bỏ nhãn, vẫn là thành viên
        using (var clear = await client.DeleteAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels/{seed.Ready3}")) Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        detail = (await DetailAsync(client, set.EvaluationSetId))!;
        Assert.Null(detail.Members.Single(m => m.ApplicationId == seed.Ready3).IsPass);
        Assert.False(detail.Readiness.CanRun);               // chỉ còn một loại nhãn
        Assert.True(detail.Revision > revision);

        using var removed = await client.DeleteAsync($"/api/evaluation-sets/{set.EvaluationSetId}/members/{seed.Ready1}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var result = (await removed.Content.ReadFromJsonAsync<RemoveEvaluationSetMemberResult>())!;
        Assert.True(result.RemovedLabel); Assert.Equal(2, result.MemberCount);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<SmartCvDbContext>().HrLabels.AnyAsync(h => h.EvaluationSetId == set.EvaluationSetId && h.ApplicationId == seed.Ready1));
        using var missing = await client.DeleteAsync($"/api/evaluation-sets/{set.EvaluationSetId}/members/{seed.Ready1}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Legacy_labels_without_member_rows_still_count_as_members()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        int setId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var set = new EvaluationSet { Name = "Bộ cũ (trước v2)", CreatedAt = DateTime.UtcNow };
            db.Add(set); await db.SaveChangesAsync(); setId = set.EvaluationSetId;
            db.HrLabels.Add(new HrLabel { EvaluationSetId = setId, ApplicationId = seed.Ready1, IsPass = true, LabeledBy = "HR cũ", LabeledAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var detail = (await DetailAsync(client, setId))!;
        var member = Assert.Single(detail.Members);
        Assert.Equal((seed.Ready1, true, "HR cũ"), (member.ApplicationId, member.IsPass, member.AddedBy));
        Assert.Equal(1, (await client.GetFromJsonAsync<List<EvaluationSetDto>>("/api/evaluation-sets"))!.Single(s => s.EvaluationSetId == setId).MemberCount);
        var again = await AddAsync(client, setId, seed.Ready1);
        Assert.Equal(SkippedMemberReasons.AlreadyMember, Assert.Single(again.Skipped).Reason);
    }

    [Fact]
    public async Task Prompt_test_stores_set_revision_keeps_scores_and_reports_later_set_changes()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        var set = await CreateSetAsync(client, "Bộ chạy");
        using (var save = await client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest
        { LabeledBy = "HR", Labels = [new() { ApplicationId = seed.Ready1, IsPass = true }, new() { ApplicationId = seed.Ready2, IsPass = false }] }))
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        await AddAsync(client, set.EvaluationSetId, seed.Ready3);   // chưa gán nhãn: không được đưa vào lượt chạy
        var revision = (await DetailAsync(client, set.EvaluationSetId))!.Revision;

        using var start = await client.PostAsJsonAsync("/api/prompt-tests", new StartPromptTestRequest { PromptVersionId = seed.Prompt, EvaluationSetId = set.EvaluationSetId });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var run = (await start.Content.ReadFromJsonAsync<PromptTestRunDto>())!;
        Assert.Equal(revision, run.EvaluationSetRevision);
        Assert.False(run.SetChangedSinceRun);
        Assert.Equal(2, run.TotalItems);

        using (var change = await client.PutAsJsonAsync($"/api/evaluation-sets/{set.EvaluationSetId}/labels", new SaveHrLabelsRequest
        { LabeledBy = "HR", Labels = [new() { ApplicationId = seed.Ready3, IsPass = true }] }))
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var after = (await client.GetFromJsonAsync<PromptTestProgressDto>($"/api/prompt-tests/{run.RunId}"))!;
        Assert.True(after.SetChangedSinceRun);
        Assert.Equal(2, after.Items.Count);                         // danh sách hồ sơ của lượt cũ giữ nguyên
        Assert.DoesNotContain(after.Items, i => i.ApplicationId == seed.Ready3);
    }

    // ===================== Cờ HR thủ công =====================

    [Fact]
    public async Task Manual_flag_create_resolve_reopen_with_history_and_409_concurrency_without_stage_change()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using var other = await client.PostAsJsonAsync($"/api/applications/{seed.Ready1}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.Other, Actor = "HR - A" });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);  // "Khác" bắt buộc ghi chú

        using var created = await client.PostAsJsonAsync($"/api/applications/{seed.Ready1}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.ScoreDoubt, Note = "Điểm AI thấp bất thường", Actor = "HR - A" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var flag = (await created.Content.ReadFromJsonAsync<ManualFlagDto>())!;
        Assert.Equal((ManualFlagStatuses.Open, StartupGatedApiFactory.RegressionActor), (flag.Status, flag.CreatedBy));

        using var duplicate = await client.PostAsJsonAsync($"/api/applications/{seed.Ready1}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.SecondOpinion, Actor = "HR - B" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var stale = await client.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/resolve", new ChangeManualFlagRequest { Actor = "HR - B", ExpectedVersion = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var noVersion = await client.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/resolve", new ChangeManualFlagRequest { Actor = "HR - B" });
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);

        using var resolved = await client.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/resolve", new ChangeManualFlagRequest { Actor = "HR - B", Note = "Đã đối chiếu CV", ExpectedVersion = flag.Version });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var done = (await resolved.Content.ReadFromJsonAsync<ManualFlagDto>())!;
        Assert.Equal((ManualFlagStatuses.Resolved, StartupGatedApiFactory.RegressionActor), (done.Status, done.ResolvedBy));
        Assert.NotEqual(flag.Version, done.Version);

        // Bấm "Đã xử lý" lần hai với phiên bản cũ → 409, không ghi đè
        using var twice = await client.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/resolve", new ChangeManualFlagRequest { Actor = "HR - A", ExpectedVersion = flag.Version });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        using var reopened = await client.PostAsJsonAsync($"/api/applications/flags/{flag.FlagId}/reopen", new ChangeManualFlagRequest { Actor = "HR - A", ExpectedVersion = done.Version });
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);

        var flags = (await client.GetFromJsonAsync<ApplicationFlagsResponse>($"/api/applications/{seed.Ready1}/flags"))!;
        Assert.NotNull(flags.Current);
        Assert.Equal(new[] { ManualFlagActions.Reopened, ManualFlagActions.Resolved, ManualFlagActions.Opened }, flags.Events.Select(e => e.Action));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var app = await db.Applications.AsNoTracking().SingleAsync(a => a.ApplicationId == seed.Ready1);
        Assert.Equal(ApplicationStages.New, app.Stage);
        Assert.False(await db.ApplicationEvents.AnyAsync(e => e.ApplicationId == seed.Ready1));   // không có quyết định HR nào bị tạo
    }

    [Fact]
    public async Task Anonymized_application_cannot_be_flagged()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using var response = await client.PostAsJsonAsync($"/api/applications/{seed.Anonymized}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.ScoreDoubt, Actor = "HR" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var missing = await client.GetAsync("/api/applications/987654/flags");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Ranking_and_dashboard_share_one_needs_review_definition()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        int runId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            // Ready2 qua vòng CV: cờ HR vẫn hiện nhưng không "cần xem kỹ" nữa.
            var shortlisted = await db.Applications.SingleAsync(a => a.ApplicationId == seed.Ready2);
            shortlisted.Stage = ApplicationStages.Shortlisted;
            var run = new EvaluationRun
            {
                RunType = RunTypes.Screening, Status = RunStatuses.Completed, PromptVersionId = seed.Prompt, ModelName = "fixture",
                KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70, TotalItems = 2, ProcessedItems = 2, CreatedAt = DateTime.UtcNow,
                Evaluations = new[] { seed.Ready1, seed.Ready2 }.Select(id => new Evaluation
                {
                    ApplicationId = id, Status = EvaluationStatuses.Completed, KeywordScore = 80, AiScore = 82, FinalScore = 81,
                    ScoreSource = ScoreSources.Combined, FitLabel = FitLevels.Good, PredictedPass = true, CreatedAt = DateTime.UtcNow
                }).ToList()
            };
            db.Add(run); await db.SaveChangesAsync(); runId = run.RunId;
        }
        foreach (var id in new[] { seed.Ready1, seed.Ready2 })
            using (var r = await client.PostAsJsonAsync($"/api/applications/{id}/flags", new CreateManualFlagRequest { Reason = ManualFlagReasons.SecondOpinion, Actor = "HR" }))
                Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var ranking = (await client.GetFromJsonAsync<RankingResponse>($"/api/runs/{runId}/ranking?jobId={seed.JobId}"))!;
        Assert.Equal(2, ranking.ManualFlagCount);
        Assert.Equal(1, ranking.NeedsReviewCount);
        Assert.True(ranking.Items.Single(i => i.ApplicationId == seed.Ready1).NeedsReview);
        Assert.False(ranking.Items.Single(i => i.ApplicationId == seed.Ready2).NeedsReview);
        Assert.Equal(0, ranking.FlaggedCount);                       // cảnh báo tự động tách riêng: không có

        // Phần Tổng quan (truy vấn SQL thô cho thời gian sàng lọc) được kiểm tra ở ScappWorkflowV2SqlServerTests.
    }

    // ===================== Tổng quan: xu hướng =====================

    [Fact]
    public async Task Trend_counts_each_application_once_per_vietnam_day_and_uses_cv_round_rule()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var seed = await SeedAsync(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
            var apps = await db.Applications.ToListAsync();
            foreach (var a in apps) a.CreatedAt = new DateTime(2026, 11, 9, 16, 30, 0, DateTimeKind.Utc);    // 23:30 ngày 09/11 giờ VN
            apps.Single(a => a.ApplicationId == seed.Ready1).CreatedAt = new DateTime(2026, 11, 9, 17, 30, 0, DateTimeKind.Utc); // 00:30 ngày 10/11 VN
            apps.Single(a => a.ApplicationId == seed.Ready1).Stage = ApplicationStages.Interview;
            var rejectedAfterShortlist = apps.Single(a => a.ApplicationId == seed.Ready2);
            rejectedAfterShortlist.Stage = ApplicationStages.Rejected; rejectedAfterShortlist.RejectReason = RejectReasons.MissingSkills;
            db.ApplicationEvents.Add(new ApplicationEvent { ApplicationId = seed.Ready2, FromStage = ApplicationStages.New, ToStage = ApplicationStages.Shortlisted, Actor = "HR", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var trend = (await client.GetFromJsonAsync<DashboardTrendDto>($"/api/dashboard/trend?jobId={seed.JobId}&from=2026-11-09&to=2026-11-10"))!;
        Assert.Equal("day", trend.Bucket);
        Assert.Equal(2, trend.Points.Count);
        var day9 = trend.Points[0]; var day10 = trend.Points[1];
        Assert.Equal((3, 1), (day9.Applications, day9.PassedCvRound));   // Ready2 (đã từng qua vòng CV), Pending, Anonymized
        Assert.Equal((1, 1, 100m), (day10.Applications, day10.PassedCvRound, day10.PassRatePercent));
        Assert.Equal(4, trend.TotalApplications);                        // Ready3 thuộc Job khác: không tính

        var week = (await client.GetFromJsonAsync<DashboardTrendDto>("/api/dashboard/trend?from=2026-09-01&to=2026-11-10"))!;
        Assert.Equal("week", week.Bucket);
        Assert.Equal(DayOfWeek.Monday, week.Points[1].Start.DayOfWeek);
        Assert.Equal(5, week.TotalApplications);
        using var bad = await client.GetAsync("/api/dashboard/trend?bucket=month");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var tooLong = await client.GetAsync("/api/dashboard/trend?from=2020-01-01&to=2026-11-10&bucket=day");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    // ===================== Quy tắc thuần (Core) =====================

    [Fact]
    public void Readiness_blocks_one_sided_or_unready_labels_and_warns_about_unlabeled_members()
    {
        static EvaluationSetRules.MemberState M(int id, bool? pass, bool ready = true) => new(id, pass, ready);
        Assert.False(EvaluationSetRules.Readiness([]).CanRun);
        var oneSided = EvaluationSetRules.Readiness([M(1, true), M(2, true)]);
        Assert.False(oneSided.CanRun); Assert.Contains(oneSided.Blockers, b => b.Contains("cả hồ sơ Đạt và Không đạt"));
        var unready = EvaluationSetRules.Readiness([M(1, true), M(2, false, ready: false)]);
        Assert.False(unready.CanRun); Assert.Equal(1, unready.LabeledNotReadyCount);
        var ok = EvaluationSetRules.Readiness([M(1, true), M(2, false), M(3, null, ready: false)]);
        Assert.True(ok.CanRun); Assert.Equal((1, 1), (ok.UnlabeledCount, ok.NotReadyCount));
        Assert.Contains(ok.Warnings, w => w.Contains("KHÔNG được đưa vào lượt chạy"));
        Assert.NotNull(EvaluationSetRules.NotReadyReason("Completed", true, anonymized: true));
        Assert.NotNull(EvaluationSetRules.NotReadyReason("Failed", true, false));
        Assert.Null(EvaluationSetRules.NotReadyReason("Completed", true, false));
    }

    [Theory]
    [InlineData(ApplicationStages.New, true, false, true)]
    [InlineData(ApplicationStages.Reviewing, false, true, true)]
    [InlineData(ApplicationStages.Shortlisted, true, true, false)]
    [InlineData(ApplicationStages.Rejected, false, true, false)]
    [InlineData(ApplicationStages.New, false, false, false)]
    public void Needs_review_requires_an_open_stage_and_any_flag(string stage, bool auto, bool manual, bool expected) =>
        Assert.Equal(expected, ReviewAttention.NeedsReview(stage, auto, manual));

    [Fact]
    public void Manual_flag_rules_validate_reason_note_and_actor()
    {
        Assert.NotNull(ManualFlagRules.ValidateCreate("Bogus", null, "HR"));
        Assert.NotNull(ManualFlagRules.ValidateCreate(ManualFlagReasons.Other, "  ", "HR"));
        Assert.NotNull(ManualFlagRules.ValidateCreate(ManualFlagReasons.ScoreDoubt, null, " "));
        Assert.NotNull(ManualFlagRules.ValidateCreate(ManualFlagReasons.ScoreDoubt, new string('x', 501), "HR"));
        Assert.Null(ManualFlagRules.ValidateCreate(ManualFlagReasons.ScoreDoubt, null, "HR"));
        Assert.Null(ManualFlagRules.ValidateChange(null, "HR"));
    }

    [Fact]
    public void Trend_calculator_uses_utc_plus_7_days_monday_weeks_and_null_rates_for_empty_buckets()
    {
        var rows = new[]
        {
            new TrendCalculator.Row(new DateTime(2026, 11, 15, 16, 59, 0, DateTimeKind.Utc), true),   // 23:59 Chủ nhật 15/11 VN
            new TrendCalculator.Row(new DateTime(2026, 11, 15, 17, 0, 0, DateTimeKind.Utc), false),   // 00:00 Thứ Hai 16/11 VN
            new TrendCalculator.Row(new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc), true)      // ngoài khoảng
        };
        var day = TrendCalculator.Build(null, new(2026, 11, 14), new(2026, 11, 17), TrendCalculator.Day, rows);
        Assert.Equal(new[] { 0, 1, 1, 0 }, day.Points.Select(p => p.Applications));
        Assert.Null(day.Points[0].PassRatePercent);
        Assert.Equal(50m, day.PassRatePercent);
        var week = TrendCalculator.Build(null, new(2026, 11, 14), new(2026, 11, 17), TrendCalculator.Week, rows);
        Assert.Equal(2, week.Points.Count);
        Assert.Equal((new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 15)), (week.Points[0].Start, week.Points[0].End));
        Assert.Equal(new DateOnly(2026, 11, 16), week.Points[1].Start);
        Assert.Equal(TrendCalculator.Day, TrendCalculator.ResolveBucket("auto", new(2026, 11, 1), new(2026, 11, 30)));
        Assert.Equal(TrendCalculator.Week, TrendCalculator.ResolveBucket(null, new(2026, 8, 1), new(2026, 11, 30)));
        var (from, to) = TrendCalculator.DefaultRange(null, null, new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc));
        Assert.Equal((new DateOnly(2026, 10, 22), new DateOnly(2026, 11, 20)), (from, to));
    }

    // ===================== Web: trạng thái bất đồng bộ của bước chọn CV / gán nhãn =====================

    private sealed class FakeApi : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public bool FailSave;
        public List<EvaluationSetMemberDto> Members =
        [
            new() { ApplicationId = 1, IsReady = true, IsPass = true, Note = "cũ", FileName = "a.pdf", JobTitle = "Backend" },
            new() { ApplicationId = 2, IsReady = true, FileName = "b.pdf", JobTitle = "Backend" },
            new() { ApplicationId = 3, IsReady = false, NotReadyReason = "CV đang chờ trích xuất nội dung.", FileName = "c.pdf", JobTitle = "Backend" }
        ];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Put)
                return FailSave ? Json(new { detail = "Lỗi máy chủ giả lập" }, HttpStatusCode.ServiceUnavailable) : Json(new SaveHrLabelsResult { LabelCount = 2, PassCount = 1 });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Post)
            {
                var body = (await request.Content!.ReadFromJsonAsync<AddEvaluationSetMembersRequest>(ct))!;
                return Json(new AddEvaluationSetMembersResult { Added = body.ApplicationIds.Count, AddedApplicationIds = body.ApplicationIds, MemberCount = 3 + body.ApplicationIds.Count, Revision = 5 });
            }
            if (path.Contains("/applications"))
                return Json(new List<LabelCandidateDto>
                {
                    new() { ApplicationId = 1, IsMember = true, ExtractionStatus = "Completed", HasExtractedText = true },
                    new() { ApplicationId = 7, ExtractionStatus = "Completed", HasExtractedText = true, JobTitle = "Backend", FileName = "moi.pdf" },
                    new() { ApplicationId = 8, IsAnonymized = true, ExtractionStatus = "Completed" }
                });
            return Json(new EvaluationSetDetailDto { EvaluationSetId = 10, Name = "Bộ", Members = Members });
        }
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }

    private static WebServices.PromptApiClient Client(FakeApi api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("https://fixture.test/") }, NullLogger<WebServices.PromptApiClient>.Instance);

    [Fact]
    public async Task Picker_sends_only_selected_non_members_and_uses_filters()
    {
        var api = new FakeApi();
        var ws = new WebHelpers.SetMembersWorkspace(Client(api));
        await ws.LoadAsync(10, default);
        ws.Filter = new WebHelpers.CandidatePickerFilter(JobId: 3, Stage: ApplicationStages.New, From: new(2026, 11, 1));
        await ws.LoadCandidatesAsync(default);
        Assert.Contains(api.Calls, c => c.Contains("applications?jobId=3&stage=New&from=2026-11-01&notInSet=true"));
        Assert.Equal(new[] { 7, 8 }, ws.VisibleCandidates(c => c.CandidateName ?? "").Select(c => c.ApplicationId));   // thành viên bị ẩn
        ws.Toggle(1, true); ws.Toggle(8, true); ws.Toggle(7, true);   // thành viên & hồ sơ ẩn danh không chọn được
        Assert.Equal(new[] { 7 }, ws.Selected);
        Assert.False(await ws.AddSelectedAsync(" ", default));        // thiếu tên người thao tác: không gửi
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("POST"));
        Assert.True(await ws.AddSelectedAsync("HR", default));
        Assert.Single(api.Calls, c => c.StartsWith("POST /api/evaluation-sets/10/members"));
        Assert.Empty(ws.Selected);
    }

    [Fact]
    public async Task Label_drafts_survive_failed_save_and_clearing_uses_delete_not_ai_prediction()
    {
        var api = new FakeApi { FailSave = true };
        var ws = new WebHelpers.SetMembersWorkspace(Client(api));
        await ws.LoadAsync(10, default);
        Assert.Equal(new[] { "pass", "", "" }, ws.Rows.Select(r => r.Choice));   // không có nhãn mặc định cho thành viên mới
        ws.Rows[1].Choice = "fail"; ws.Rows[1].Note = "Thiếu SQL";
        ws.Rows[2].Choice = "pass";                                               // hồ sơ chưa sẵn sàng
        Assert.False(await ws.SaveLabelsAsync("HR", default));
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("PUT"));
        ws.Rows[2].Choice = "";
        Assert.False(await ws.SaveLabelsAsync("HR", default));                    // máy chủ lỗi
        Assert.Equal(("fail", "Thiếu SQL"), (ws.Rows[1].Choice, ws.Rows[1].Note));
        Assert.True(ws.Rows[1].IsDirty);

        api.FailSave = false;
        ws.Rows[0].Choice = "";                                                   // Đạt → Chưa đánh giá
        Assert.True(await ws.SaveLabelsAsync("HR", default));
        Assert.Contains(api.Calls, c => c == "DELETE /api/evaluation-sets/10/labels/1");
        Assert.Contains(api.Calls, c => c == "PUT /api/evaluation-sets/10/labels");
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 4)]
    [InlineData(7, 8)]
    [InlineData(37, 40)]
    [InlineData(101, 200)]
    public void Trend_axis_maximum_is_round_and_divisible_into_four_gridlines(int value, int expected)
    {
        var max = web::SmartCv.Web.Components.Dashboard.TrendChart.NiceMax(value);
        Assert.Equal(expected, max);
        Assert.Equal(0, max % 4);
        Assert.True(max >= value);
    }

    [Fact]
    public void Dashboard_quick_range_supports_90_days()
    {
        var filter = new WebHelpers.DashboardFilter().Quick(90, new DateTime(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal((new DateOnly(2026, 8, 22), new DateOnly(2026, 11, 19)), (filter.From!.Value, filter.To!.Value));
    }

    [Fact]
    public void Combined_trend_curve_breaks_on_missing_rates_and_never_overshoots_real_values()
    {
        // 0 → 100 → 100 → 0, rồi ngắt (mốc không có hồ sơ), rồi một điểm lẻ.
        var path = web::SmartCv.Web.Components.Dashboard.TrendChart.Smooth(new (double, double)?[] { (50, 100), (150, 0), (250, 0), (350, 100), null, (550, 40) });
        Assert.Equal(2, path.Count(c => c == 'M'));                       // hai đoạn: đường bị ngắt, không nối qua mốc trống
        var numbers = path.Split(' ', 'M', 'C', 'L').Where(s => s.Contains(','))
            .Select(s => double.Parse(s.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.All(numbers, y => Assert.InRange(y, 0, 100));             // không vượt 0–100% (không có đỉnh/đáy giả)
        Assert.Equal("", web::SmartCv.Web.Components.Dashboard.TrendChart.Smooth(new (double, double)?[] { null, null }));
    }
}
