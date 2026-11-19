using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;

namespace SmartCv.Core.Evaluation;

/// <summary>
/// SCApp v2: quy tắc của bộ CV đánh giá khi thành viên tách khỏi nhãn HR.
/// Không bao giờ lấy dự đoán AI làm nhãn HR.
/// </summary>
public static class EvaluationSetRules
{
    public const int MaxMembersPerRequest = 500;
    public const int MaxPurposeLength = 300;

    /// <summary>null = sẵn sàng chạy PromptTest; ngược lại là lý do tiếng Việt.</summary>
    public static string? NotReadyReason(string extractionStatus, bool hasExtractedText, bool anonymized)
    {
        if (anonymized) return "CV đã được ẩn danh (hết hạn lưu hoặc ứng viên yêu cầu xóa); không thể dùng để đánh giá.";
        if (extractionStatus == ExtractionStatuses.Failed) return "Trích xuất nội dung CV thất bại. Hãy đọc lại CV ở Kho CV.";
        if (extractionStatus != ExtractionStatuses.Completed) return "CV đang chờ trích xuất nội dung.";
        if (!hasExtractedText) return "CV không có nội dung chữ (cần OCR hoặc file khác).";
        return null;
    }

    public sealed record MemberState(int ApplicationId, bool? IsPass, bool IsReady);

    /// <summary>
    /// Bước 4 — kiểm tra bộ. Lượt chạy chỉ gồm thành viên ĐÃ gán nhãn; thành viên chưa gán nhãn không chặn chạy
    /// nhưng được cảnh báo rõ (bộ chưa hoàn thiện vẫn lưu được để làm tiếp).
    /// </summary>
    public static EvaluationSetReadinessDto Readiness(IReadOnlyCollection<MemberState> members)
    {
        var labeled = members.Where(m => m.IsPass is not null).ToList();
        var pass = labeled.Count(m => m.IsPass == true);
        var fail = labeled.Count - pass;
        var labeledNotReady = labeled.Count(m => !m.IsReady);
        var unlabeled = members.Count - labeled.Count;
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (members.Count == 0) blockers.Add("Bộ chưa có hồ sơ. Hãy chọn CV đã quét để thêm vào bộ.");
        else if (labeled.Count == 0) blockers.Add("Chưa có hồ sơ nào được HR gán nhãn Đạt / Không đạt.");
        else if (pass == 0 || fail == 0) blockers.Add("Bộ cần có cả hồ sơ Đạt và Không đạt để đo được độ chính xác.");
        if (labeledNotReady > 0)
            blockers.Add($"{labeledNotReady} hồ sơ đã gán nhãn nhưng CV chưa sẵn sàng (chưa trích xuất, lỗi, không có chữ hoặc đã ẩn danh). Hãy xử lý CV hoặc bỏ hồ sơ khỏi bộ.");
        if (unlabeled > 0)
            warnings.Add($"{unlabeled} hồ sơ chưa gán nhãn sẽ KHÔNG được đưa vào lượt chạy.");
        if (labeled.Count is > 0 and < 10)
            warnings.Add("Bộ có ít hơn 10 hồ sơ có nhãn: tỷ lệ đúng sẽ dao động lớn, chưa đủ để kết luận.");
        return new EvaluationSetReadinessDto
        {
            MemberCount = members.Count, LabeledCount = labeled.Count, PassCount = pass, FailCount = fail,
            UnlabeledCount = unlabeled, LabeledNotReadyCount = labeledNotReady,
            NotReadyCount = members.Count(m => !m.IsReady),
            CanRun = blockers.Count == 0, Blockers = blockers, Warnings = warnings
        };
    }

    /// <summary>Tên người thao tác: bắt buộc, tối đa 100 ký tự (giống LabeledBy / Actor).</summary>
    public static string? ValidateActor(string? actor, out string trimmed)
    {
        trimmed = (actor ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Ghi tên người thao tác, ví dụ \"HR - Trần Thu Hà\".";
        if (trimmed.Length > 100) return "Tên người thao tác tối đa 100 ký tự.";
        return null;
    }
}

/// <summary>SCApp v2: một định nghĩa "Cần xem kỹ" cho Tổng quan và Kết quả.</summary>
public static class ReviewAttention
{
    /// <summary>Chỉ hồ sơ chưa có quyết định HR (Mới / Đang xem) mới "cần xem kỹ".</summary>
    public static bool IsOpenStage(string? stage) => stage is ApplicationStages.New or ApplicationStages.Reviewing;

    public static bool NeedsReview(string? stage, bool hasAutomaticWarning, bool hasOpenManualFlag) =>
        IsOpenStage(stage) && (hasAutomaticWarning || hasOpenManualFlag);
}

/// <summary>SCApp v2: kiểm tra dữ liệu cờ thủ công.</summary>
public static class ManualFlagRules
{
    public const int MaxNoteLength = 500;

    public static string? ValidateCreate(string? reason, string? note, string? actor)
    {
        if (!ManualFlagReasons.IsValid(reason?.Trim())) return "Hãy chọn lý do cần kiểm tra.";
        if (note is { Length: > MaxNoteLength }) return $"Ghi chú tối đa {MaxNoteLength} ký tự.";
        if (reason!.Trim() == ManualFlagReasons.Other && string.IsNullOrWhiteSpace(note))
            return "Lý do \"Khác\" cần có ghi chú để người xử lý hiểu cần kiểm tra gì.";
        return EvaluationSetRules.ValidateActor(actor, out _);
    }

    public static string? ValidateChange(string? note, string? actor)
    {
        if (note is { Length: > MaxNoteLength }) return $"Ghi chú tối đa {MaxNoteLength} ký tự.";
        return EvaluationSetRules.ValidateActor(actor, out _);
    }

    public static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}
