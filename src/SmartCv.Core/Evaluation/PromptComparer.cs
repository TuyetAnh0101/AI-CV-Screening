using System.Globalization;
using SmartCv.Core.Constants;

namespace SmartCv.Core.Evaluation;

/// <summary>Cài đặt đã chép vào một lượt PromptTest (EvaluationRuns). So sánh công bằng = mọi cài đặt giống nhau, chỉ khác prompt.</summary>
public sealed record RunSettings(
    int? EvaluationSetId,
    string ModelName,
    decimal Temperature,
    decimal KeywordWeight,
    decimal AiWeight,
    decimal PassThreshold);

/// <summary>Một CV thay đổi thế nào giữa bản gốc và bản thử.</summary>
public enum CompareChange
{
    Fixed,       // bản gốc sai, bản thử đúng
    Broken,      // bản gốc đúng, bản thử sai
    Unchanged,   // cùng đúng hoặc cùng sai
    Unscored     // ít nhất một lượt không chấm được CV này
}

public static class CompareVerdicts
{
    public const string Better = "Better";
    public const string Worse = "Worse";
    public const string Same = "Same";
    public const string NotComparable = "NotComparable";
}

/// <summary>
/// Một bên của phép so sánh: lượt PromptTest đã lưu và dự đoán của từng CV đối chiếu với nhãn HR hiện tại
/// trong bộ đánh giá của chính lượt đó. Chỉ truyền CV còn nhãn HR (cùng phạm vi với A16).
/// </summary>
public sealed record CompareRun(
    int RunId,
    string PromptName,
    string Status,
    RunSettings Settings,
    IReadOnlyCollection<LabeledPrediction> Predictions);

public sealed record CompareItem(
    int ApplicationId,
    bool HrIsPass,
    LabeledPrediction Base,
    LabeledPrediction Candidate,
    PredictionOutcome BaseOutcome,
    PredictionOutcome CandidateOutcome,
    CompareChange Change);

/// <summary>Kết quả so sánh. Mọi số liệu tính trên các CV có mặt ở CẢ HAI lượt (cùng mẫu số).</summary>
public sealed record PromptComparison(
    AccuracyResult Base,
    AccuracyResult Candidate,
    decimal? AccuracyDelta,
    int CorrectDelta,
    int MissedDelta,
    int WrongPickDelta,
    int FixedCount,
    int BrokenCount,
    int UnchangedCount,
    int UnscoredCount,
    int KeywordOnlyAffectedCount,
    string Verdict,
    string Summary,
    bool IsFairComparison,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompareItem> Items)
{
    /// <summary>
    /// A41: true chỉ khi Verdict là Better/Worse VÀ hai khoảng tin cậy 95% (Wilson) không chồng nhau.
    /// Verdict giữ nguyên (B16): Better với Conclusive = false nghĩa là chênh lệch mới là kết quả quan sát, chưa đủ cơ sở kết luận.
    /// </summary>
    public bool Conclusive { get; init; }

    /// <summary>A41: câu giải thích mức độ kết luận cho giao diện (tiếng Việt).</summary>
    public string ConclusionNote { get; init; } = string.Empty;
}

/// <summary>
/// A17: so sánh hai lượt đánh giá prompt. Hàm thuần: không đọc database, không gọi Gemini.
/// <list type="bullet">
/// <item>Ghép CV theo ApplicationId, không theo vị trí trong danh sách.</item>
/// <item>Tỷ lệ đúng dùng đúng công thức A16 (<see cref="PromptAccuracyCalculator"/>): CV không chấm được vẫn ở mẫu số.</item>
/// <item>Công bằng khi: cả hai Completed, cùng bộ CV, cùng model/temperature/trọng số/ngưỡng,
/// cùng danh sách ApplicationId có nhãn và cùng nhãn HR cho từng CV.</item>
/// <item>Không kết luận Better/Worse/Same khi chênh lệch đến từ CV chỉ chấm bằng từ khóa hoặc không chấm được:
/// kết luận phải đứng vững trên các CV Gemini chấm được ở cả hai lượt, và các CV đó phải chiếm ít nhất một nửa.</item>
/// </list>
/// </summary>
public static class PromptComparer
{
    // Dấu phẩy thập phân kiểu Việt Nam; tự tạo để không phụ thuộc gói ngôn ngữ của máy chủ
    private static readonly NumberFormatInfo Vi = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };
    private const int MaxListedApplications = 10;

    public static CompareChange ClassifyChange(PredictionOutcome baseOutcome, PredictionOutcome candidateOutcome)
    {
        if (baseOutcome == PredictionOutcome.Unscored || candidateOutcome == PredictionOutcome.Unscored)
            return CompareChange.Unscored;

        var baseOk = baseOutcome == PredictionOutcome.Correct;
        var candidateOk = candidateOutcome == PredictionOutcome.Correct;
        if (!baseOk && candidateOk) return CompareChange.Fixed;
        if (baseOk && !candidateOk) return CompareChange.Broken;
        return CompareChange.Unchanged;
    }

    /// <summary>Liệt kê các cài đặt khác nhau giữa hai lượt chạy. Rỗng = cùng điều kiện chạy.</summary>
    public static List<string> SettingDifferences(RunSettings a, RunSettings b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var diffs = new List<string>();
        if (a.EvaluationSetId != b.EvaluationSetId)
            diffs.Add($"Khác bộ CV đánh giá (#{a.EvaluationSetId} và #{b.EvaluationSetId}).");
        if (!string.Equals(a.ModelName?.Trim(), b.ModelName?.Trim(), StringComparison.OrdinalIgnoreCase))
            diffs.Add($"Khác model ({a.ModelName} và {b.ModelName}).");
        if (a.Temperature != b.Temperature)
            diffs.Add($"Khác temperature ({F(a.Temperature)} và {F(b.Temperature)}).");
        if (a.KeywordWeight != b.KeywordWeight || a.AiWeight != b.AiWeight)
            diffs.Add($"Khác trọng số từ khóa/AI ({F(a.KeywordWeight)}/{F(a.AiWeight)} và {F(b.KeywordWeight)}/{F(b.AiWeight)}).");
        if (a.PassThreshold != b.PassThreshold)
            diffs.Add($"Khác ngưỡng Đạt ({F(a.PassThreshold)} và {F(b.PassThreshold)}).");
        return diffs;
    }

    /// <summary>Better / Worse / Same theo tỷ lệ đúng; NotComparable nếu không công bằng hoặc thiếu tỷ lệ.</summary>
    public static string Verdict(decimal? baseAccuracy, decimal? candidateAccuracy, bool isFair)
    {
        if (!isFair || baseAccuracy is null || candidateAccuracy is null) return CompareVerdicts.NotComparable;
        if (candidateAccuracy > baseAccuracy) return CompareVerdicts.Better;
        if (candidateAccuracy < baseAccuracy) return CompareVerdicts.Worse;
        return CompareVerdicts.Same;
    }

    /// <param name="describe">Tên hiển thị của một hồ sơ trong cảnh báo (mặc định "hồ sơ #id").</param>
    public static PromptComparison Compare(CompareRun baseRun, CompareRun candidateRun, Func<int, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(baseRun);
        ArgumentNullException.ThrowIfNull(candidateRun);
        describe ??= id => $"hồ sơ #{id}";

        var baseById = ById(baseRun);
        var candidateById = ById(candidateRun);

        var warnings = new List<string>();
        var settingDiffs = SettingDifferences(baseRun.Settings, candidateRun.Settings);
        warnings.AddRange(settingDiffs);
        var bothCompleted = baseRun.Status == RunStatuses.Completed && candidateRun.Status == RunStatuses.Completed;
        foreach (var run in new[] { baseRun, candidateRun }.Where(r => r.Status != RunStatuses.Completed))
            warnings.Add($"Lượt #{run.RunId} ({run.PromptName}) chưa hoàn thành (trạng thái {run.Status}).");

        // Danh sách CV: chỉ so trên CV có ở cả hai lượt, cùng nhãn HR
        var onlyBase = baseById.Keys.Where(id => !candidateById.ContainsKey(id)).Order().ToList();
        var onlyCandidate = candidateById.Keys.Where(id => !baseById.ContainsKey(id)).Order().ToList();
        var labelConflicts = baseById.Keys.Where(id => candidateById.TryGetValue(id, out var c) && c.HrIsPass != baseById[id].HrIsPass)
            .Order().ToList();
        var common = baseById.Keys.Where(id => candidateById.ContainsKey(id) && !labelConflicts.Contains(id)).Order().ToList();

        if (onlyBase.Count > 0 || onlyCandidate.Count > 0)
        {
            var parts = new List<string>();
            if (onlyBase.Count > 0)
                parts.Add($"{onlyBase.Count} CV chỉ có trong lượt #{baseRun.RunId} ({baseRun.PromptName}): {List(onlyBase, describe)}");
            if (onlyCandidate.Count > 0)
                parts.Add($"{onlyCandidate.Count} CV chỉ có trong lượt #{candidateRun.RunId} ({candidateRun.PromptName}): {List(onlyCandidate, describe)}");
            warnings.Add($"Hai lượt không cùng danh sách CV có nhãn HR: {string.Join("; ", parts)}. " +
                         $"Số liệu chỉ tính trên {common.Count} CV chung để tham khảo; hãy chạy lại cả hai phiên bản trên cùng bộ CV.");
        }
        if (labelConflicts.Count > 0)
            warnings.Add($"{labelConflicts.Count} CV có nhãn HR khác nhau giữa hai bộ đánh giá nên không được so sánh: {List(labelConflicts, describe)}.");
        if (common.Count == 0)
            warnings.Add("Hai lượt không có CV chung nào còn nhãn HR để so sánh.");

        var items = common.Select(id =>
        {
            var b = baseById[id];
            var c = candidateById[id];
            var bo = PromptAccuracyCalculator.Classify(b);
            var co = PromptAccuracyCalculator.Classify(c);
            return new CompareItem(id, b.HrIsPass, b, c, bo, co, ClassifyChange(bo, co));
        }).ToList();

        var accBase = PromptAccuracyCalculator.Calculate(items.Select(i => i.Base));
        var accCandidate = PromptAccuracyCalculator.Calculate(items.Select(i => i.Candidate));
        var fixedCount = items.Count(i => i.Change == CompareChange.Fixed);
        var brokenCount = items.Count(i => i.Change == CompareChange.Broken);
        var unchangedCount = items.Count(i => i.Change == CompareChange.Unchanged);
        var unscoredCount = items.Count(i => i.Change == CompareChange.Unscored);

        // CV mà ít nhất một lượt chỉ có điểm từ khóa: kết quả không phản ánh system prompt
        var keywordAffected = items.Count(i => IsKeywordOnly(i.Base) || IsKeywordOnly(i.Candidate));
        if (keywordAffected > 0)
            warnings.Add($"Có {keywordAffected} CV chỉ được chấm bằng từ khóa ở ít nhất một lượt " +
                         $"(bản gốc {accBase.KeywordOnly}, bản thử {accCandidate.KeywordOnly}) do Gemini lỗi hoặc hết hạn mức. " +
                         "Kết quả các CV này không phản ánh tác động của system prompt; xem lý do ở chi tiết từng CV và nên chạy lại khi Gemini ổn định.");
        if (unscoredCount > 0)
            warnings.Add($"Có {unscoredCount} CV không chấm được ở ít nhất một lượt (lỗi đọc file hoặc lỗi chấm). " +
                         "Theo quy tắc A16, CV này vẫn nằm trong mẫu số như một dự đoán sai và không được tính là sửa đúng hay làm sai.");

        var isFair = bothCompleted && settingDiffs.Count == 0 && onlyBase.Count == 0 && onlyCandidate.Count == 0
                     && labelConflicts.Count == 0 && common.Count > 0;
        var verdict = Verdict(accBase.AccuracyPercent, accCandidate.AccuracyPercent, isFair);

        // Kết luận phải đứng vững trên các CV Gemini thật sự chấm ở cả hai lượt
        var reliabilityIssue = false;
        if (verdict != CompareVerdicts.NotComparable)
        {
            var clean = items.Where(i => IsAiScored(i.Base) && IsAiScored(i.Candidate)).ToList();
            var cleanDelta = clean.Count(i => i.Change == CompareChange.Fixed) - clean.Count(i => i.Change == CompareChange.Broken);
            var totalDelta = accCandidate.Correct - accBase.Correct;
            if (clean.Count * 2 < items.Count)
            {
                reliabilityIssue = true;
                warnings.Add($"Chưa đủ cơ sở kết luận về prompt: chỉ {clean.Count}/{items.Count} CV được Gemini chấm ở cả hai lượt " +
                             "(cần ít nhất một nửa số CV).");
            }
            else if (Math.Sign(cleanDelta) != Math.Sign(totalDelta))
            {
                reliabilityIssue = true;
                warnings.Add($"Chưa đủ cơ sở kết luận về prompt: trên {clean.Count} CV được Gemini chấm ở cả hai lượt, kết quả không cùng chiều " +
                             "với chênh lệch tổng; chênh lệch đến từ CV chỉ chấm bằng từ khóa hoặc không chấm được.");
            }
            if (reliabilityIssue) verdict = CompareVerdicts.NotComparable;
        }

        var summary = Summary(baseRun.PromptName, accBase, candidateRun.PromptName, accCandidate, fixedCount, brokenCount,
            verdict, reliabilityIssue, keywordAffected, unscoredCount);
        var (conclusive, conclusionNote) = Conclusion(verdict, accBase, accCandidate);

        return new PromptComparison(
            accBase, accCandidate,
            accBase.AccuracyPercent is { } x && accCandidate.AccuracyPercent is { } y ? y - x : null,
            accCandidate.Correct - accBase.Correct,
            accCandidate.Missed - accBase.Missed,
            accCandidate.WrongPick - accBase.WrongPick,
            fixedCount, brokenCount, unchangedCount, unscoredCount, keywordAffected,
            verdict, summary, isFair, warnings, items)
        {
            Conclusive = conclusive,
            ConclusionNote = conclusionNote
        };
    }

    /// <summary>A41: câu bắt buộc khi hai khoảng tin cậy chồng nhau.</summary>
    public const string InconclusiveText = "Chưa đủ dữ liệu để kết luận bản nào tốt hơn, cần thêm CV có nhãn.";

    /// <summary>
    /// A41: hai khoảng tin cậy 95% KHÔNG chồng nhau (chạm nhau cũng tính là chồng). Thiếu một khoảng → false.
    /// Đây là tiêu chí thận trọng, dễ giải thích cho HR; không phải kiểm định ý nghĩa thống kê chính thức.
    /// </summary>
    public static bool IsConclusive((decimal Low, decimal High)? a, (decimal Low, decimal High)? b) =>
        a is { } x && b is { } y && (x.High < y.Low || y.High < x.Low);

    /// <summary>
    /// A41: mức độ kết luận, tách khỏi Verdict (quan sát). Không bao giờ nói bản mới "chắc chắn" tốt hơn:
    /// NotComparable → chưa kết luận; khoảng tin cậy chồng nhau → chưa đủ dữ liệu; không chồng → chênh lệch rõ trên bộ CV này.
    /// </summary>
    public static (bool Conclusive, string Note) Conclusion(string verdict, AccuracyResult baseResult, AccuracyResult candidateResult)
    {
        ArgumentNullException.ThrowIfNull(baseResult);
        ArgumentNullException.ThrowIfNull(candidateResult);

        if (verdict == CompareVerdicts.NotComparable)
            return (false, "Chưa thể kết luận bản nào tốt hơn: hai lượt chạy chưa so sánh được với nhau " +
                           "(chưa hoàn thành, khác điều kiện, khác danh sách CV hoặc phụ thuộc CV không chấm được). Xem phần cảnh báo.");

        var a = baseResult.Confidence95;
        var b = candidateResult.Confidence95;
        if (!IsConclusive(a, b))
        {
            if (verdict is not (CompareVerdicts.Better or CompareVerdicts.Worse)) return (false, InconclusiveText);
            var delta = candidateResult.AccuracyPercent!.Value - baseResult.AccuracyPercent!.Value;
            return (false, $"{InconclusiveText} Chênh lệch {F(Math.Abs(delta))} điểm phần trăm trên {candidateResult.Total} CV " +
                           $"chỉ là kết quả quan sát, chưa đủ cơ sở kết luận (khoảng tin cậy 95%: {Range(a)} và {Range(b)} chồng nhau).");
        }

        return (true, $"Hai khoảng tin cậy 95% không chồng nhau ({Range(a)} và {Range(b)}): chênh lệch đủ rõ trên bộ CV này. " +
                      "Đây là tiêu chí thận trọng để đọc kết quả, không phải kiểm định thống kê chính thức; " +
                      "nên kiểm tra lại khi có thêm CV có nhãn.");
    }

    private static string Range((decimal Low, decimal High)? interval) =>
        interval is { } x ? $"{F(x.Low)}–{F(x.High)}%" : "—";

    /// <summary>
    /// Một câu tiếng Việt cho quản lý tuyển dụng, ví dụ:
    /// "v2 đúng 80% (8/10 CV), cao hơn v1 10 điểm phần trăm (70%, 7/10 CV). Sửa đúng thêm 1 CV, không làm sai thêm CV nào.
    ///  Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1)."
    /// </summary>
    public static string Summary(
        string baseName, AccuracyResult baseResult,
        string candidateName, AccuracyResult candidateResult,
        int fixedCount, int brokenCount, string verdict,
        bool reliabilityIssue = false, int keywordAffected = 0, int unscoredCount = 0)
    {
        ArgumentNullException.ThrowIfNull(baseResult);
        ArgumentNullException.ThrowIfNull(candidateResult);

        if (verdict == CompareVerdicts.NotComparable)
            return reliabilityIssue
                ? $"Chưa thể kết luận {candidateName} tốt hơn hay kém hơn {baseName}: kết quả phụ thuộc vào CV chỉ chấm bằng từ khóa " +
                  "hoặc không chấm được, chưa phản ánh tác động của prompt. Hãy chạy lại khi Gemini ổn định."
                : $"Chưa thể so sánh {candidateName} với {baseName}: hai lượt chạy chưa hoàn thành hoặc không cùng điều kiện " +
                  "(bộ CV, model, temperature, trọng số, ngưỡng). Xem phần cảnh báo.";

        var delta = candidateResult.AccuracyPercent!.Value - baseResult.AccuracyPercent!.Value;
        var head = $"{candidateName} đúng {P(candidateResult.AccuracyPercent)} ({candidateResult.Correct}/{candidateResult.Total} CV)";
        var cmp = verdict switch
        {
            CompareVerdicts.Better => $"cao hơn {baseName} {F(delta)} điểm phần trăm ({P(baseResult.AccuracyPercent)}, {baseResult.Correct}/{baseResult.Total} CV)",
            CompareVerdicts.Worse => $"thấp hơn {baseName} {F(-delta)} điểm phần trăm ({P(baseResult.AccuracyPercent)}, {baseResult.Correct}/{baseResult.Total} CV)",
            _ => $"bằng {baseName}"
        };
        var changes = $"{(fixedCount > 0 ? $"Sửa đúng thêm {fixedCount} CV" : "Không sửa đúng thêm CV nào")}, " +
                      $"{(brokenCount > 0 ? $"làm sai thêm {brokenCount} CV" : "không làm sai thêm CV nào")}.";
        var detail = $"{Trend("Bỏ sót", baseResult.Missed, candidateResult.Missed)}, " +
                     $"{Trend("chọn nhầm", baseResult.WrongPick, candidateResult.WrongPick)}.";
        var text = $"{head}, {cmp}. {changes} {detail}";

        var notes = new List<string>();
        if (keywordAffected > 0) notes.Add($"{keywordAffected} CV chỉ chấm bằng từ khóa");
        if (unscoredCount > 0) notes.Add($"{unscoredCount} CV không chấm được");
        if (notes.Count > 0) text += $" Lưu ý: có {string.Join(" và ", notes)}, xem phần cảnh báo.";
        return text;
    }

    private static Dictionary<int, LabeledPrediction> ById(CompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run.Predictions);
        var map = new Dictionary<int, LabeledPrediction>(run.Predictions.Count);
        foreach (var p in run.Predictions)
            if (!map.TryAdd(p.ApplicationId, p))
                throw new ArgumentException($"Lượt #{run.RunId} có hai kết quả cho hồ sơ #{p.ApplicationId}.", nameof(run));
        return map;
    }

    private static bool IsKeywordOnly(LabeledPrediction p) =>
        p.Status == EvaluationStatuses.Completed && p.ScoreSource == ScoreSources.KeywordOnly;

    private static bool IsAiScored(LabeledPrediction p) =>
        PromptAccuracyCalculator.Classify(p) != PredictionOutcome.Unscored && !IsKeywordOnly(p);

    private static string Trend(string label, int before, int after) => (after - before) switch
    {
        < 0 => $"{label} giảm {before - after} ({before} → {after})",
        > 0 => $"{label} tăng {after - before} ({before} → {after})",
        _ => $"{label} giữ nguyên ({before} → {after})"
    };

    private static string List(IReadOnlyList<int> ids, Func<int, string> describe)
    {
        var shown = string.Join(", ", ids.Take(MaxListedApplications).Select(describe));
        return ids.Count > MaxListedApplications ? $"{shown} và {ids.Count - MaxListedApplications} CV khác" : shown;
    }

    private static string F(decimal v) => v.ToString("0.##", Vi);
    private static string P(decimal? v) => v is null ? "—" : F(v.Value) + "%";
}
