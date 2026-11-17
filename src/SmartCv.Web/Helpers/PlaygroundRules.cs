using System.Globalization;
using System.Text.RegularExpressions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Privacy;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B39 "Thử với hồ sơ…": quy tắc thuần (không gọi API) để test được.
/// Nguồn prompt, chữ lượt thử, chênh điểm, hồ sơ đủ điều kiện, mô tả lỗi A41 và dữ liệu hai thẻ kết quả.
/// Chỉ đọc số liệu API trả về: không tự suy điểm, không thay null bằng 0, không tự ghép nội dung gửi Gemini.
/// </summary>
public static class PlaygroundRules
{
    /// <summary>A41 kiểm tra độ dài prompt nháp SAU khi bỏ khoảng trắng đầu/cuối (PromptToolSupport.ValidateSource).</summary>
    public const int MaxDraftLength = PromptDraft.MaxPromptLength;
    public const int LowQuotaThreshold = 3;
    public const string InvalidSourceMessage = "Chỉ chọn một nguồn prompt: bản nháp hoặc một phiên bản đã lưu.";

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    // ---------------- Nguồn prompt: bản nháp hay phiên bản đã lưu ----------------

    /// <summary>
    /// Cách so khớp nội dung: đổi CRLF/CR thành LF rồi bỏ khoảng trắng đầu/cuối — đúng như backend xử lý prompt nháp
    /// (Trim) và cách trình duyệt chuẩn hóa xuống dòng của textarea. Khoảng trắng GIỮA các dòng vẫn được tính là thay đổi
    /// vì Gemini sẽ nhận đúng các ký tự đó.
    /// </summary>
    public static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Nội dung đang soạn khác bản nguồn → nháp. Chưa có bản nguồn (trang chưa có phiên bản nào) → luôn là nháp.</summary>
    public static bool IsDraft(string? content, PromptVersionDto? source) =>
        source is null || !string.Equals(Normalize(content), Normalize(source.SystemPrompt), StringComparison.Ordinal);

    /// <summary>Kế hoạch gửi cho một lần chấm thử / xem nội dung gửi Gemini, tính từ nội dung THỰC SỰ trong ô soạn.</summary>
    public sealed record SourcePlan(bool IsDraft, string Label, string Signature, string? SystemPrompt, int? PromptVersionId, string? BlockedReason)
    {
        public bool CanSend => BlockedReason is null;
        public string ActionText => IsDraft ? "Chấm thử bản nháp" : $"Chấm thử {Label}";
    }

    public static SourcePlan Plan(string? content, PromptVersionDto? source)
    {
        if (!IsDraft(content, source))
            return new(false, source!.VersionName, $"version:{source.PromptVersionId}", null, source.PromptVersionId, null);

        var normalized = Normalize(content);
        string? blocked = normalized.Length == 0
            ? "Nội dung prompt đang trống. Hãy soạn prompt trước khi chấm thử."
            : normalized.Length > MaxDraftLength
                ? $"Prompt nháp dài {normalized.Length.ToString("N0", Vi)} ký tự, vượt giới hạn {MaxDraftLength.ToString("N0", Vi)} ký tự của API."
                : null;
        // Gửi đúng chữ đang soạn; backend tự bỏ khoảng trắng đầu/cuối (không lưu).
        return new(true, "Bản nháp", "draft:" + normalized, blocked is null ? content : null, null, blocked);
    }

    /// <summary>Đúng một nguồn: nháp (systemPrompt không trống) HOẶC phiên bản (promptVersionId &gt; 0). Ngược lại null: không gửi.</summary>
    public static AiPreviewRequest? SingleSource(string? systemPrompt, int? promptVersionId) => (systemPrompt, promptVersionId) switch
    {
        ({ } draft, null) when !string.IsNullOrWhiteSpace(draft) => new AiPreviewRequest { SystemPrompt = draft },
        (null, > 0) => new AiPreviewRequest { PromptVersionId = promptVersionId },
        _ => null
    };

    public static AiPreviewRequest? PreviewRequest(SourcePlan plan) =>
        plan.CanSend ? SingleSource(plan.SystemPrompt, plan.PromptVersionId) : null;

    public static AssembledPreviewRequest? AssembledRequest(int applicationId, SourcePlan plan) =>
        applicationId > 0 && PreviewRequest(plan) is { } source
            ? new AssembledPreviewRequest { ApplicationId = applicationId, SystemPrompt = source.SystemPrompt, PromptVersionId = source.PromptVersionId }
            : null;

    // ---------------- Lượt thử ----------------

    public static string WindowText(int minutes) => $"{minutes.ToString(Vi)} phút";

    /// <summary>"Còn 27/30 lượt thử trong 60 phút" — mọi số lấy từ API.</summary>
    public static string QuotaText(int remaining, int limit, int windowMinutes) => remaining <= 0
        ? $"Đã hết {limit.ToString(Vi)} lượt thử trong {WindowText(windowMinutes)}"
        : $"Còn {remaining.ToString(Vi)}/{limit.ToString(Vi)} lượt thử trong {WindowText(windowMinutes)}";

    /// <summary>ok | low (còn ≤ 3) | empty (hết lượt).</summary>
    public static string QuotaTone(int remaining) => remaining <= 0 ? "empty" : remaining <= LowQuotaThreshold ? "low" : "ok";

    /// <summary>Giờ Việt Nam của thời điểm UTC, làm tròn LÊN phút (cùng quy ước thông báo 429 của A41).</summary>
    public static string VnClock(DateTime utc, DateTime? nowUtc = null)
    {
        var value = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime();
        var local = new DateTimeOffset(value).ToOffset(VietnamOffset);
        var remainder = local.Ticks % TimeSpan.TicksPerMinute;
        if (remainder != 0) local = local.AddTicks(TimeSpan.TicksPerMinute - remainder);
        var today = new DateTimeOffset(nowUtc ?? DateTime.UtcNow).ToOffset(VietnamOffset).Date;
        return local.Date == today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm 'ngày' dd/MM", CultureInfo.InvariantCulture);
    }

    public static string? ResetText(DateTime? resetsAtUtc, DateTime? nowUtc = null) =>
        resetsAtUtc is { } at ? $"Lượt sớm nhất được trả lại lúc {VnClock(at, nowUtc)} (UTC+7)" : null;

    /// <summary>Cập nhật số lượt từ kết quả chấm thử; giữ cửa sổ/giới hạn chạy cùng lúc của lần đọc quota trước.</summary>
    public static PreviewQuotaDto QuotaAfterPreview(PreviewQuotaDto? before, AiPreviewDto result) => new()
    {
        Remaining = result.RemainingTries,
        Limit = result.TryLimit,
        WindowMinutes = before?.WindowMinutes ?? 0,
        ResetsAt = result.QuotaResetsAt,
        InFlight = 0,
        MaxConcurrent = before?.MaxConcurrent ?? 0,
        AiSimulated = result.AiSimulated
    };

    // ---------------- So điểm với kết quả đã lưu ----------------

    /// <summary>Chênh điểm cuối; null nếu một trong hai điểm không có (không coi null là 0).</summary>
    public static decimal? ScoreDelta(decimal? trial, decimal? saved) =>
        trial is { } t && saved is { } s ? Math.Round(t - s, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>"+6,5" / "−3" / "0" (dấu trừ Unicode, dấu phẩy thập phân).</summary>
    public static string SignedScore(decimal delta)
    {
        var text = Math.Abs(delta).ToString("0.##", Vi);
        return delta > 0 ? "+" + text : delta < 0 ? "−" + text : "0";
    }

    public static string DeltaText(decimal delta) => delta == 0 ? "Bằng điểm đã lưu" : $"{SignedScore(delta)} điểm so với bản đã lưu";

    /// <summary>
    /// Có so được điểm cuối hay không. Không so khi: chưa có kết quả lưu, kết quả lưu không có điểm,
    /// hoặc hai bên khác nguồn điểm (một bên chỉ có điểm từ khóa vì AI lỗi) → so sẽ gây kết luận sai.
    /// </summary>
    public static (decimal? Delta, string? Reason) Compare(AiPreviewDto trial, CurrentEvaluationDto? saved)
    {
        if (saved is null) return (null, "Hồ sơ chưa có kết quả đã lưu để so.");
        if (saved.FinalScore is null) return (null, "Kết quả đã lưu chưa có điểm cuối nên không so điểm.");
        if (saved.ScoreSource is { } source && !string.Equals(source, trial.ScoreSource, StringComparison.OrdinalIgnoreCase))
            return (null, "Không so điểm trực tiếp: một bên chỉ có điểm từ khóa (AI chưa chấm được).");
        return (ScoreDelta(trial.FinalScore, saved.FinalScore), null);
    }

    // ---------------- Hồ sơ đủ điều kiện ----------------

    public static bool IsTombstone(LabelCandidateDto c) =>
        string.IsNullOrWhiteSpace(c.CandidateName) && c.FileName == RetentionRules.AnonymizedFileName;

    /// <summary>null: chấm thử được. Ngược lại là lý do khóa (cùng điều kiện A41 LoadReadyApplicationAsync).</summary>
    public static string? Unavailable(LabelCandidateDto c)
    {
        if (c.ApplicationId <= 0) return "thiếu mã hồ sơ";
        if (IsTombstone(c)) return "đã ẩn danh hóa theo hạn lưu";
        return c.ExtractionStatus switch
        {
            ExtractionStatuses.Pending => "CV đang được đọc",
            ExtractionStatuses.Failed => "đọc CV lỗi",
            ExtractionStatuses.Completed when !c.HasExtractedText => "CV không có chữ để chấm",
            ExtractionStatuses.Completed => null,
            _ => "CV chưa sẵn sàng"
        };
    }

    public static string LabelText(bool? label) => label switch { true => "HR: Đạt", false => "HR: Không đạt", _ => "" };

    /// <summary>Tên gọn để chọn hồ sơ. Ẩn danh (B36) hoặc hồ sơ đã ẩn danh hóa: không hiện tên/file.</summary>
    public static string CandidateText(LabelCandidateDto c, bool anonymous)
    {
        var id = $"HS{c.ApplicationId.ToString(CultureInfo.InvariantCulture)}";
        if (IsTombstone(c)) return $"Hồ sơ đã ẩn danh · {id}";
        if (anonymous) return $"Ứng viên #{c.ApplicationId.ToString(CultureInfo.InvariantCulture)} · {id}";
        var name = !string.IsNullOrWhiteSpace(c.CandidateName) ? c.CandidateName!.Trim()
            : !string.IsNullOrWhiteSpace(c.FileName) ? c.FileName.Trim() : "Chưa rõ tên";
        return $"{name} · {id}";
    }

    public static string OptionText(LabelCandidateDto c, bool anonymous)
    {
        var parts = new List<string> { CandidateText(c, anonymous) };
        if (!IsTombstone(c) && LabelText(c.CurrentLabel) is { Length: > 0 } label) parts.Add(label);
        var text = string.Join(" · ", parts);
        return Unavailable(c) is { } reason ? $"{text} — {reason}" : text;
    }

    public sealed record JobGroup(int JobId, string JobTitle, int Total, int Ready);

    /// <summary>Nhóm theo JobId (không theo tên), sắp xếp theo tên rồi mã.</summary>
    public static List<JobGroup> Jobs(IEnumerable<LabelCandidateDto> candidates) => candidates
        .Where(c => c.JobId > 0)
        .GroupBy(c => c.JobId)
        .Select(g => new JobGroup(g.Key, g.Select(c => c.JobTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? $"Công việc #{g.Key}",
            g.Count(), g.Count(c => Unavailable(c) is null)))
        .OrderBy(j => j.JobTitle, StringComparer.Create(Vi, ignoreCase: true)).ThenBy(j => j.JobId)
        .ToList();

    public static string JobOptionText(JobGroup job) =>
        $"{job.JobTitle} · {job.Ready.ToString(Vi)}/{job.Total.ToString(Vi)} sẵn sàng";

    // ---------------- Lỗi API A41 ----------------

    public sealed record PlaygroundError(string Title, string Message, bool CanRetry, bool ReloadCandidates = false, bool QuotaExhausted = false);

    /// <summary>
    /// Lỗi của POST ai-preview. Thông báo chi tiết là ProblemDetails tiếng Việt của API (không chứa stack trace / khóa / nội dung CV).
    /// Không tự thử lại: mọi lần chấm thử lại đều do người dùng bấm.
    /// </summary>
    public static PlaygroundError DescribePreviewError(int? status, string? code, string? message, TimeSpan? retryAfter = null)
    {
        var detail = string.IsNullOrWhiteSpace(message) ? "Chưa nhận được kết quả chấm thử." : message!;
        return (status, code) switch
        {
            (429, "PreviewQuotaExhausted") => new("Hết lượt chấm thử", detail, false, QuotaExhausted: true),
            (429, "PreviewBusy") => new("Đang có lượt chấm thử khác", detail + RetryHint(retryAfter), true),
            (429, _) => new("Quá nhiều yêu cầu chấm thử", detail + RetryHint(retryAfter), true),
            (409, _) => new("Hồ sơ chưa thể chấm thử", detail + " Danh sách hồ sơ đã được tải lại.", false, ReloadCandidates: true),
            (404, _) => new("Không tìm thấy dữ liệu", detail + " Danh sách hồ sơ đã được tải lại.", false, ReloadCandidates: true),
            (400 or 413 or 415, _) => new("Yêu cầu chấm thử không hợp lệ", detail, false),
            (null or 408 or 502 or 503 or 504, _) => new("Máy chủ chưa phản hồi",
                detail + " Lượt thử có thể đã được tính nếu máy chủ đã gửi yêu cầu tới AI; hãy xem số lượt rồi thử lại khi sẵn sàng.", true),
            _ => new("Chấm thử chưa thành công", detail, true)
        };
    }

    /// <summary>Lỗi của POST assembled-preview (không tốn lượt nên luôn cho thử lại thủ công, trừ khi máy chủ tắt che dữ liệu).</summary>
    public static PlaygroundError DescribeAssembledError(int? status, string? message) => status switch
    {
        409 or 404 => new("Không xem được nội dung gửi Gemini", Fallback(message) + " Danh sách hồ sơ đã được tải lại.", true, ReloadCandidates: true),
        400 or 413 or 415 => new("Yêu cầu xem trước không hợp lệ", Fallback(message), false),
        _ => new("Không tải được nội dung gửi Gemini", Fallback(message), true)
    };

    public const string MaskingDisabledCode = "PersonalDataMaskingDisabled";

    private static string Fallback(string? message) => string.IsNullOrWhiteSpace(message) ? "API không trả nội dung." : message!;

    private static string RetryHint(TimeSpan? retryAfter) => retryAfter is { } wait && wait > TimeSpan.Zero
        ? $" (máy chủ đề nghị chờ khoảng {Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(Vi)} giây)"
        : string.Empty;

    // ---------------- Nội dung gửi Gemini (đã che) ----------------

    public static string MaskSummary(AssembledPromptDto a)
    {
        var parts = new List<string>();
        void Add(int n, string what) { if (n > 0) parts.Add($"{n.ToString(Vi)} {what}"); }
        Add(a.MaskedNames, "họ tên");
        Add(a.MaskedEmails, "email");
        Add(a.MaskedPhones, "số điện thoại");
        Add(a.MaskedLinks, "liên kết");
        Add(a.MaskedBirthDates, "ngày sinh");
        Add(a.MaskedGenders, "giới tính");
        return parts.Count == 0 ? "không phát hiện thông tin cá nhân cần che" : "đã che " + string.Join(", ", parts);
    }

    public static IReadOnlyList<(string Label, int Count)> MaskTable(AssembledPromptDto a) =>
    [
        ("Họ tên", a.MaskedNames), ("Email", a.MaskedEmails), ("Điện thoại", a.MaskedPhones),
        ("Liên kết", a.MaskedLinks), ("Ngày sinh", a.MaskedBirthDates), ("Giới tính", a.MaskedGenders)
    ];

    private static readonly Regex MaskToken = new(@"\[(?:CANDIDATE|EMAIL|PHONE|LINK|DOB|GENDER)\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Tách văn bản thành đoạn thường / nhãn đã che để tô nhẹ khi hiển thị (vẫn là text, Razor tự escape).</summary>
    public static IEnumerable<(string Text, bool IsMask)> SplitMasked(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var at = 0;
        foreach (Match m in MaskToken.Matches(text))
        {
            if (m.Index > at) yield return (text[at..m.Index], false);
            yield return (m.Value, true);
            at = m.Index + m.Length;
        }
        if (at < text.Length) yield return (text[at..], false);
    }

    // ---------------- Hai thẻ kết quả ----------------

    public sealed record SkillChip(string Code, string Name, bool Matched, bool MustHave);

    public sealed record ScoreView(
        decimal? FinalScore, string? FitLabel, string? ScoreSource, bool? PredictedPass, decimal? KeywordScore, decimal? AiScore,
        IReadOnlyList<SkillChip> Skills, string? Reasoning, IReadOnlyList<string> Notes);

    /// <summary>Kỹ năng của job theo kết quả chấm thử (đã gộp keyword + AI), có tên hiển thị.</summary>
    public static ScoreView TrialView(AiPreviewDto r)
    {
        var notes = new List<string>();
        if (r.AiSimulated) notes.Add("AI giả lập (Gemini:UseFake): điểm AI chỉ để thử giao diện, không phản ánh Gemini thật và không tốn hạn mức Gemini.");
        if (!r.AiSucceeded || string.Equals(r.ScoreSource, ScoreSources.KeywordOnly, StringComparison.OrdinalIgnoreCase))
            notes.Add($"AI chưa chấm được ({AiErrorText(r.AiErrorKind)}): điểm cuối chỉ tính từ từ khóa, không phải điểm chắc chắn.");
        if (r.AiCvTruncated) notes.Add("CV dài nên đã được cắt bớt trước khi gửi AI.");
        if (r.AiInvalidSkillCodes is { Count: > 0 } invalid) notes.Add($"AI trả {invalid.Count.ToString(Vi)} mã kỹ năng không thuộc job (đã bỏ qua).");
        var skills = (r.Skills ?? [])
            .Select(s => new SkillChip(s.SkillCode, string.IsNullOrWhiteSpace(s.DisplayName) ? s.SkillCode : s.DisplayName, s.IsMatched,
                string.Equals(s.SkillType, SkillTypes.MustHave, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new(r.FinalScore, r.FitLabel, r.ScoreSource, r.PredictedPass, r.KeywordScore, r.AiScore, skills, r.Reasoning, notes);
    }

    /// <summary>Kết quả đã lưu chỉ có mã kỹ năng: lấy tên/bắt buộc từ danh sách kỹ năng của job trong kết quả chấm thử.</summary>
    public static ScoreView SavedView(CurrentEvaluationDto e, IReadOnlyList<SkillChip> jobSkills)
    {
        var byCode = jobSkills.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        SkillChip Chip(string code, bool matched) => byCode.TryGetValue(code, out var s)
            ? s with { Matched = matched } : new SkillChip(code, code, matched, false);
        var skills = (e.MatchedSkills ?? []).Select(c => Chip(c, true)).Concat((e.MissingSkills ?? []).Select(c => Chip(c, false))).ToList();
        var notes = new List<string>();
        if (string.Equals(e.ScoreSource, ScoreSources.KeywordOnly, StringComparison.OrdinalIgnoreCase))
            notes.Add("Kết quả đã lưu chỉ có điểm từ khóa (AI chưa chấm được ở lượt đó).");
        return new(e.FinalScore, e.FitLabel, e.ScoreSource, e.PredictedPass, e.KeywordScore, e.AiScore, skills, e.Reasoning, notes);
    }

    /// <summary>
    /// Kỹ năng đạt thêm / không còn đạt so với kết quả đã lưu, CHỈ xét mã có mặt ở cả hai bên
    /// (mã không có trong kết quả đã lưu là "chưa biết", không được coi là "chưa đạt").
    /// </summary>
    public static (IReadOnlyList<string> Gained, IReadOnlyList<string> Lost) SkillChanges(ScoreView trial, ScoreView saved)
    {
        var before = saved.Skills.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(s => s.Matched), StringComparer.OrdinalIgnoreCase);
        var both = trial.Skills.Where(s => before.ContainsKey(s.Code)).GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Name, Now: g.Any(s => s.Matched), Before: before[g.Key])).ToList();
        var order = StringComparer.Create(Vi, true);
        return (both.Where(s => s.Now && !s.Before).Select(s => s.Name).Order(order).ToList(),
                both.Where(s => !s.Now && s.Before).Select(s => s.Name).Order(order).ToList());
    }

    public static string PassText(bool? pass) => pass switch { true => "Đạt", false => "Không đạt", _ => "Chưa có dự đoán" };

    public static string RunTypeText(string? runType) => runType switch
    {
        RunTypes.Screening => "lượt sàng lọc",
        RunTypes.PromptTest => "lượt đánh giá prompt",
        _ => "lượt chấm"
    };

    public static string AiErrorText(string? kind) => kind switch
    {
        "QuotaExceeded" => "Gemini hết hạn mức",
        "Network" => "lỗi mạng",
        "InvalidResponse" => "AI trả dữ liệu không hợp lệ",
        "Blocked" => "nội dung bị chặn",
        "Configuration" => "thiếu cấu hình AI",
        "InvalidRequest" => "yêu cầu không hợp lệ",
        "Overloaded" => "Gemini đang quá tải",
        "Timeout" => "hết thời gian chờ",
        "ModelUnavailable" => "model không khả dụng",
        _ => "lỗi không xác định"
    };
}

/// <summary>
/// B39: lựa chọn KHÔNG nhạy cảm của khung thử (mã công việc, mã hồ sơ, thẻ đang xem), giữ trong bộ nhớ của ô soạn
/// để đóng/mở lại khung không phải chọn lại. Kết quả chấm thử và nội dung gửi Gemini KHÔNG được giữ ở đây.
/// </summary>
public sealed class PlaygroundSelection
{
    public int JobId { get; set; }
    public int ApplicationId { get; set; }
    public int Tab { get; set; }
}
