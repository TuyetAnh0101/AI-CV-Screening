extern alias web;

using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

/// <summary>B39: quy tắc thuần của "Thử với hồ sơ…" (không gọi API, không Gemini, không database).</summary>
public class B39PlaygroundRulesTests
{
    private static PromptVersionDto Version(int id = 2, string name = "v2-tieu-chi", string prompt = "Dòng 1\nDòng 2") =>
        new() { PromptVersionId = id, VersionName = name, SystemPrompt = prompt, IsActive = true };

    private static LabelCandidateDto Candidate(int id, string status = "Completed", bool text = true, string? name = "Nguyễn Văn An",
        string file = "cv-an.pdf", bool? label = null, int jobId = 1, string job = "Backend Developer (.NET)") => new()
    {
        ApplicationId = id, CvId = Guid.NewGuid(), HasExtractedText = text, CandidateName = name, FileName = file,
        JobId = jobId, JobTitle = job, ExtractionStatus = status, CurrentLabel = label
    };

    private static AiPreviewDto Trial(decimal final = 78.5m, string source = "Combined", bool ok = true, bool simulated = false,
        CurrentEvaluationDto? saved = null) =>
        new("Nháp (chưa lưu)", "gemini-test", 60m, 86m, final, source, "Phù hợp", true, 1.5m, "Lý do chấm thử.",
            ok, ok ? "None" : "Timeout", null, 1, simulated, false, ["csharp"], ["docker"], [],
            [new("csharp", "C#", "MustHave", true, "Keyword", "C# 3 năm"), new("docker", "Docker", "MustHave", false, null, null),
             new("git", "Git", "NiceToHave", true, "AI", null)])
        {
            IsDraft = true, PromptSource = "Draft", RemainingTries = 27, TryLimit = 30, TryCounted = true, CurrentEvaluation = saved,
            KeywordWeight = .3m, AiWeight = .7m, PassThreshold = 70
        };

    private static CurrentEvaluationDto Saved(decimal? final = 72m, string? source = "Combined") => new()
    {
        EvaluationId = 9, RunId = 4, RunType = "Screening", PromptVersionId = 2, PromptVersionName = "v2-tieu-chi", ModelName = "gemini-test",
        KeywordScore = 60, AiScore = 77, FinalScore = final, ScoreSource = source, FitLabel = "Phù hợp", PredictedPass = true,
        MatchedSkills = ["csharp"], MissingSkills = ["docker", "git"], CreatedAt = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc)
    };

    // ---------- Bản nháp hay phiên bản ----------

    [Theory]
    [InlineData("Dòng 1\r\nDòng 2", false)]          // CRLF của Windows = LF của trình duyệt
    [InlineData("Dòng 1\rDòng 2", false)]
    [InlineData("  Dòng 1\nDòng 2\n\n", false)]       // backend bỏ khoảng trắng đầu/cuối của bản nháp
    [InlineData("Dòng 1\n Dòng 2", true)]             // khoảng trắng GIỮA nội dung: Gemini nhận khác
    [InlineData("Dòng 1\nDòng 2\nQuy tắc mới", true)]
    [InlineData("", true)]
    public void Draft_detection_normalizes_line_endings_and_outer_whitespace_only(string content, bool draft) =>
        Assert.Equal(draft, PlaygroundRules.IsDraft(content, Version()));

    [Fact]
    public void Without_source_version_everything_is_a_draft()
    {
        Assert.True(PlaygroundRules.IsDraft("Bạn là HR.", null));
        var plan = PlaygroundRules.Plan("Bạn là HR.", null);
        Assert.True(plan.IsDraft);
        Assert.Equal("Chấm thử bản nháp", plan.ActionText);
        Assert.Equal("Bạn là HR.", PlaygroundRules.PreviewRequest(plan)!.SystemPrompt);
    }

    [Fact]
    public void Unchanged_content_sends_only_the_version_id_and_uses_its_name()
    {
        var plan = PlaygroundRules.Plan("Dòng 1\r\nDòng 2\n", Version());
        var request = PlaygroundRules.PreviewRequest(plan)!;
        Assert.False(plan.IsDraft);
        Assert.Equal("Chấm thử v2-tieu-chi", plan.ActionText);
        Assert.Equal(2, request.PromptVersionId);
        Assert.Null(request.SystemPrompt);
        Assert.Equal("version:2", plan.Signature);
    }

    [Fact]
    public void Edited_content_sends_only_the_draft_text_never_both_sources()
    {
        const string edited = "Dòng 1\nDòng 2\nThiếu kỹ năng bắt buộc thì điểm không quá 50.";
        var plan = PlaygroundRules.Plan(edited, Version());
        var request = PlaygroundRules.PreviewRequest(plan)!;
        Assert.True(plan.IsDraft);
        Assert.Equal(edited, request.SystemPrompt);
        Assert.Null(request.PromptVersionId);
        var assembled = PlaygroundRules.AssembledRequest(5, plan)!;
        Assert.Equal((5, edited, (int?)null), (assembled.ApplicationId, assembled.SystemPrompt, assembled.PromptVersionId));
    }

    [Fact]
    public void Signature_changes_with_content_so_old_results_can_be_marked_stale()
    {
        var a = PlaygroundRules.Plan("Dòng 1\nDòng 2\nA", Version());
        var b = PlaygroundRules.Plan("Dòng 1\nDòng 2\nB", Version());
        var aAgain = PlaygroundRules.Plan("Dòng 1\r\nDòng 2\r\nA\r\n", Version());
        Assert.NotEqual(a.Signature, b.Signature);
        Assert.Equal(a.Signature, aAgain.Signature);
        // Hai nội dung khác nhau nhưng cùng hash .NET không thể gây nhầm: chữ ký là toàn bộ nội dung đã chuẩn hóa.
        Assert.StartsWith("draft:", a.Signature);
    }

    [Fact]
    public void Empty_or_oversized_drafts_are_blocked_with_a_reason_and_no_request()
    {
        var empty = PlaygroundRules.Plan("   \n ", Version());
        Assert.False(empty.CanSend);
        Assert.Contains("trống", empty.BlockedReason);
        Assert.Null(PlaygroundRules.PreviewRequest(empty));
        Assert.Null(PlaygroundRules.AssembledRequest(5, empty));

        var tooLong = PlaygroundRules.Plan(new string('a', PlaygroundRules.MaxDraftLength + 1), Version());
        Assert.False(tooLong.CanSend);
        Assert.Contains("20.000", tooLong.BlockedReason);
        // Đúng giới hạn sau khi bỏ khoảng trắng đầu/cuối vẫn hợp lệ (cùng quy tắc A41).
        Assert.True(PlaygroundRules.Plan("  " + new string('a', PlaygroundRules.MaxDraftLength) + "\n", Version()).CanSend);
    }

    [Theory]
    [InlineData("nháp", null, true)]
    [InlineData(null, 3, true)]
    [InlineData("nháp", 3, false)]   // không gửi cả hai
    [InlineData(null, null, false)]  // không rơi về bản đang kích hoạt
    [InlineData("  ", null, false)]
    [InlineData(null, 0, false)]
    public void Single_source_accepts_exactly_one_prompt_source(string? draft, int? versionId, bool valid) =>
        Assert.Equal(valid, PlaygroundRules.SingleSource(draft, versionId) is not null);

    // ---------- Lượt thử ----------

    [Fact]
    public void Quota_text_uses_api_numbers_and_tones()
    {
        Assert.Equal("Còn 27/30 lượt thử trong 60 phút", PlaygroundRules.QuotaText(27, 30, 60));
        Assert.Equal("Còn 4/5 lượt thử trong 15 phút", PlaygroundRules.QuotaText(4, 5, 15));
        Assert.Equal("Đã hết 30 lượt thử trong 60 phút", PlaygroundRules.QuotaText(0, 30, 60));
        Assert.Equal("ok", PlaygroundRules.QuotaTone(4));
        Assert.Equal("low", PlaygroundRules.QuotaTone(3));
        Assert.Equal("low", PlaygroundRules.QuotaTone(1));
        Assert.Equal("empty", PlaygroundRules.QuotaTone(0));
    }

    [Fact]
    public void Reset_time_is_vietnam_time_rounded_up_to_the_minute()
    {
        var now = new DateTime(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal("14:06", PlaygroundRules.VnClock(new DateTime(2026, 11, 17, 7, 5, 20, DateTimeKind.Utc), now));
        Assert.Equal("14:05", PlaygroundRules.VnClock(new DateTime(2026, 11, 17, 7, 5, 0, DateTimeKind.Utc), now));
        Assert.Equal("07:30 ngày 18/11", PlaygroundRules.VnClock(new DateTime(2026, 11, 18, 0, 30, 0, DateTimeKind.Utc), now));
        Assert.Null(PlaygroundRules.ResetText(null));
        Assert.Contains("UTC+7", PlaygroundRules.ResetText(new DateTime(2026, 11, 17, 7, 5, 0, DateTimeKind.Utc), now));
    }

    [Fact]
    public void Quota_after_preview_comes_from_the_response_and_keeps_the_window()
    {
        var before = new PreviewQuotaDto { Remaining = 28, Limit = 30, WindowMinutes = 60, MaxConcurrent = 2 };
        var after = PlaygroundRules.QuotaAfterPreview(before, Trial());
        Assert.Equal((27, 30, 60, 2), (after.Remaining, after.Limit, after.WindowMinutes, after.MaxConcurrent));
    }

    // ---------- So điểm ----------

    [Theory]
    [InlineData(78.5, 72.0, "+6,5")]
    [InlineData(60, 63, "−3")]
    [InlineData(70.25, 70.25, "0")]
    [InlineData(70.255, 70, "+0,26")]
    public void Signed_delta_uses_vietnamese_decimal_comma_and_unicode_minus(double trial, double saved, string expected) =>
        Assert.Equal(expected, PlaygroundRules.SignedScore(PlaygroundRules.ScoreDelta((decimal)trial, (decimal)saved)!.Value));

    [Fact]
    public void Missing_scores_are_never_treated_as_zero()
    {
        Assert.Null(PlaygroundRules.ScoreDelta(70, null));
        Assert.Null(PlaygroundRules.ScoreDelta(null, 70));
        Assert.Equal("Bằng điểm đã lưu", PlaygroundRules.DeltaText(0));
        Assert.Equal("+6,5 điểm so với bản đã lưu", PlaygroundRules.DeltaText(6.5m));
    }

    [Fact]
    public void Compare_explains_why_scores_are_not_compared()
    {
        Assert.Equal("Hồ sơ chưa có kết quả đã lưu để so.", PlaygroundRules.Compare(Trial(), null).Reason);
        Assert.Null(PlaygroundRules.Compare(Trial(), Saved(final: null)).Delta);
        var mixed = PlaygroundRules.Compare(Trial(source: "KeywordOnly", ok: false), Saved());
        Assert.Null(mixed.Delta);
        Assert.Contains("từ khóa", mixed.Reason);
        var ok = PlaygroundRules.Compare(Trial(78.5m), Saved(72m));
        Assert.Equal((6.5m, (string?)null), (ok.Delta, ok.Reason));
    }

    // ---------- Hồ sơ đủ điều kiện, nhãn hiển thị ----------

    [Fact]
    public void Unavailable_candidates_have_a_reason_matching_a41()
    {
        Assert.Null(PlaygroundRules.Unavailable(Candidate(1)));
        Assert.Equal("CV đang được đọc", PlaygroundRules.Unavailable(Candidate(2, "Pending")));
        Assert.Equal("đọc CV lỗi", PlaygroundRules.Unavailable(Candidate(3, "Failed")));
        Assert.Equal("CV không có chữ để chấm", PlaygroundRules.Unavailable(Candidate(4, text: false)));
        Assert.Equal("đã ẩn danh hóa theo hạn lưu",
            PlaygroundRules.Unavailable(Candidate(5, text: false, name: null, file: RetentionRules.AnonymizedFileName)));
        Assert.Equal("CV chưa sẵn sàng", PlaygroundRules.Unavailable(Candidate(6, "Unknown")));
    }

    [Fact]
    public void Option_text_shows_hr_label_and_never_shows_tombstone_or_anonymous_identity()
    {
        Assert.Equal("Nguyễn Văn An · HS1 · HR: Đạt", PlaygroundRules.OptionText(Candidate(1, label: true), anonymous: false));
        Assert.Equal("Ứng viên #1 · HS1 · HR: Không đạt", PlaygroundRules.OptionText(Candidate(1, label: false), anonymous: true));
        Assert.Equal("cv-x.pdf · HS2", PlaygroundRules.OptionText(Candidate(2, name: null, file: "cv-x.pdf"), anonymous: false));
        var tombstone = PlaygroundRules.OptionText(Candidate(5, text: false, name: null, file: RetentionRules.AnonymizedFileName, label: true), false);
        Assert.Equal("Hồ sơ đã ẩn danh · HS5 — đã ẩn danh hóa theo hạn lưu", tombstone);
        Assert.Contains("— đọc CV lỗi", PlaygroundRules.OptionText(Candidate(3, "Failed"), false));
    }

    [Fact]
    public void Jobs_are_grouped_by_id_not_title()
    {
        var jobs = PlaygroundRules.Jobs([
            Candidate(1, jobId: 2, job: "Tester"), Candidate(2, "Failed", jobId: 2, job: "Tester"),
            Candidate(3, jobId: 1, job: "Backend"), Candidate(4, jobId: 7, job: "Backend")]);
        Assert.Equal([1, 7, 2], jobs.Select(j => j.JobId));
        Assert.Equal((2, 1), (jobs[2].Total, jobs[2].Ready));
        Assert.Equal("Tester · 1/2 sẵn sàng", PlaygroundRules.JobOptionText(jobs[2]));
    }

    // ---------- Lỗi A41 ----------

    [Fact]
    public void Quota_exhausted_429_is_not_retryable_and_busy_429_is_manual_retry_only()
    {
        var exhausted = PlaygroundRules.DescribePreviewError(429, "PreviewQuotaExhausted", "Đã dùng hết 30 lượt… Thử lại sau 14:06.");
        Assert.True(exhausted.QuotaExhausted);
        Assert.False(exhausted.CanRetry);
        Assert.Contains("14:06", exhausted.Message);

        var busy = PlaygroundRules.DescribePreviewError(429, "PreviewBusy", "Đang chạy 2/2 lượt.", TimeSpan.FromSeconds(5));
        Assert.True(busy.CanRetry);
        Assert.Contains("5 giây", busy.Message);
        Assert.True(PlaygroundRules.DescribePreviewError(429, null, "Quá nhiều yêu cầu").CanRetry);
    }

    [Theory]
    [InlineData(409, false, true)]
    [InlineData(404, false, true)]
    [InlineData(400, false, false)]
    [InlineData(413, false, false)]
    [InlineData(415, false, false)]
    [InlineData(503, true, false)]
    [InlineData(null, true, false)]
    [InlineData(500, true, false)]
    public void Preview_errors_map_to_safe_states(int? status, bool retry, bool reload)
    {
        var e = PlaygroundRules.DescribePreviewError(status, null, "Chi tiết từ API.");
        Assert.Equal((retry, reload), (e.CanRetry, e.ReloadCandidates));
        Assert.StartsWith("Chi tiết từ API.", e.Message);
    }

    [Fact]
    public void Server_unavailable_message_warns_that_a_try_may_have_been_counted()
    {
        var e = PlaygroundRules.DescribePreviewError(503, null, "Máy chủ API phản hồi quá lâu.");
        Assert.Contains("có thể đã được tính", e.Message);
    }

    // ---------- Nội dung đã che ----------

    [Fact]
    public void Mask_summary_and_table_only_use_api_counts()
    {
        var a = new AssembledPromptDto { MaskedNames = 1, MaskedEmails = 1, MaskedPhones = 2, PersonalDataMasked = true };
        Assert.Equal("đã che 1 họ tên, 1 email, 2 số điện thoại", PlaygroundRules.MaskSummary(a));
        Assert.Equal("không phát hiện thông tin cá nhân cần che", PlaygroundRules.MaskSummary(new AssembledPromptDto()));
        Assert.Equal([1, 1, 2, 0, 0, 0], PlaygroundRules.MaskTable(a).Select(m => m.Count));
    }

    [Fact]
    public void Split_masked_keeps_every_character_and_marks_only_known_tokens()
    {
        const string text = "Ứng viên [CANDIDATE], email [EMAIL], [PHONE]; [KHÁC] <script>";
        var parts = PlaygroundRules.SplitMasked(text).ToList();
        Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
        Assert.Equal(["[CANDIDATE]", "[EMAIL]", "[PHONE]"], parts.Where(p => p.IsMask).Select(p => p.Text));
        Assert.Empty(PlaygroundRules.SplitMasked(null));
    }

    // ---------- Hai thẻ kết quả ----------

    [Fact]
    public void Trial_view_warns_about_simulated_or_keyword_only_scores()
    {
        var simulated = PlaygroundRules.TrialView(Trial(simulated: true));
        Assert.Contains(simulated.Notes, n => n.Contains("AI giả lập"));
        var failed = PlaygroundRules.TrialView(Trial(source: "KeywordOnly", ok: false));
        Assert.Contains(failed.Notes, n => n.Contains("chỉ tính từ từ khóa") && n.Contains("hết thời gian chờ"));
        Assert.Equal(["C#", "Docker", "Git"], simulated.Skills.Select(s => s.Name));
        Assert.True(simulated.Skills.Single(s => s.Code == "docker").MustHave);
    }

    [Fact]
    public void Saved_view_uses_its_own_skill_codes_with_job_names_and_skill_changes_are_listed()
    {
        var trial = PlaygroundRules.TrialView(Trial());
        var saved = PlaygroundRules.SavedView(Saved(), trial.Skills);
        Assert.Equal([("C#", true), ("Docker", false), ("Git", false)], saved.Skills.Select(s => (s.Name, s.Matched)));
        var (gained, lost) = PlaygroundRules.SkillChanges(trial, saved);
        Assert.Equal(["Git"], gained);
        Assert.Empty(lost);
        Assert.Equal(72m, saved.FinalScore);
    }

    [Fact]
    public void Skill_changes_ignore_skills_missing_from_the_saved_record()
    {
        var trial = PlaygroundRules.TrialView(Trial());   // csharp ✓, docker ✗, git ✓
        var partial = PlaygroundRules.SavedView(new CurrentEvaluationDto { MatchedSkills = [], MissingSkills = ["csharp"], FinalScore = 60 }, trial.Skills);
        var (gained, lost) = PlaygroundRules.SkillChanges(trial, partial);
        Assert.Equal(["C#"], gained);   // git không có trong kết quả đã lưu → không kết luận "đạt thêm"
        Assert.Empty(lost);
        var reverse = PlaygroundRules.SavedView(new CurrentEvaluationDto { MatchedSkills = ["docker"], FinalScore = 60 }, trial.Skills);
        Assert.Equal(["Docker"], PlaygroundRules.SkillChanges(trial, reverse).Lost);
    }
}
