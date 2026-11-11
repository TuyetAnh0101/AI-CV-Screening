extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Scoring;
using web::SmartCv.Web.Components.Results;
using web::SmartCv.Web.Components.Shared;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

/// <summary>
/// B8: 4 mức đánh giá (FitLevel), nhãn kết quả (FitBadge), thanh điểm (ScoreBar) và cột kỹ năng bắt buộc của bảng xếp hạng.
/// Phần (1) logic thuần; phần (2) dựng component thật bằng renderer kiểm thử (không cần API, không có dữ liệu giả trong ứng dụng).
/// </summary>
#pragma warning disable BL0006 // RenderTree APIs chỉ dùng trong renderer kiểm thử.
public class FitLevelUiTests
{
    // ================= (1) FitLevel: phân loại 4 mức =================

    public static TheoryData<decimal?, string> Boundaries => new()
    {
        { null, "" },
        { 0m, FitLevel.Poor },
        { 54.99m, FitLevel.Poor },
        { 54.999m, FitLevel.Poor },      // không làm tròn lên 55
        { 55m, FitLevel.Potential },
        { 69.99m, FitLevel.Potential },
        { 70m, FitLevel.Good },
        { 84.99m, FitLevel.Good },
        { 85m, FitLevel.Excellent },
        { 100m, FitLevel.Excellent },
        { -5m, FitLevel.Poor },          // ngoài phạm vi: vẫn phân loại an toàn
        { 120m, FitLevel.Excellent },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Score_boundaries_map_to_the_four_levels(decimal? score, string expected) =>
        Assert.Equal(expected, FitLevel.FromScore(score));

    [Fact]
    public void Web_levels_use_the_same_rule_and_names_as_backend()
    {
        Assert.Equal([FitLevels.Excellent, FitLevels.Good, FitLevels.Potential, FitLevels.Poor], FitLevel.All);
        for (var score = -2m; score <= 102m; score += 0.01m)
            Assert.Equal(FitLevels.FromScore(score), FitLevel.FromScore(score));
    }

    [Theory]
    [InlineData(FitLevel.Excellent, "fit-excellent", "85–100")]
    [InlineData(FitLevel.Good, "fit-good", "70–84")]
    [InlineData(FitLevel.Potential, "fit-potential", "55–69")]
    [InlineData(FitLevel.Poor, "fit-poor", "0–54")]
    [InlineData("Xuất sắc", "fit-none", "")]
    [InlineData(null, "fit-none", "")]
    public void Css_class_and_range_per_level(string? label, string css, string range)
    {
        Assert.Equal(css, FitLevel.CssClass(label));
        Assert.Equal(range, FitLevel.Range(label));
    }

    [Fact]
    public void Api_label_is_kept_and_only_flagged_when_it_disagrees_with_score()
    {
        Assert.Equal(new FitDisplay(FitLevel.Good, "fit-good", FitLabelIssue.None), FitLevel.Resolve(FitLevel.Good, 76m));

        var mismatch = FitLevel.Resolve(FitLevel.Excellent, 60m)!;
        Assert.Equal(FitLevel.Excellent, mismatch.Label);        // KHÔNG âm thầm đổi nhãn của API
        Assert.Equal(FitLabelIssue.Mismatch, mismatch.Issue);
        Assert.Equal(FitLevel.Potential, mismatch.ExpectedLabel);
        Assert.Contains("Dữ liệu cần kiểm tra", FitLevel.IssueText(mismatch, FitLevel.Excellent));

        var unknown = FitLevel.Resolve("Xuất sắc", 90m)!;
        Assert.Equal(("Mức không xác định", "fit-none", FitLabelIssue.Unknown), (unknown.Label, unknown.CssClass, unknown.Issue));
        Assert.True(unknown.HasIssue);

        var derived = FitLevel.Resolve(null, 72m)!;
        Assert.Equal((FitLevel.Good, FitLabelIssue.DerivedFromScore), (derived.Label, derived.Issue));
        Assert.False(derived.HasIssue);

        Assert.Null(FitLevel.Resolve(null, null));                // không điểm, không nhãn → không có mức (không coi là 0)
        Assert.Equal(FitLabelIssue.None, FitLevel.Resolve(" Tiềm năng ", 60m)!.Issue);
        Assert.Equal(FitLabelIssue.None, FitLevel.Resolve(FitLevel.Potential, null)!.Issue);
    }

    // ================= ScoreBar: phần trăm, tông màu, mô tả =================

    [Theory]
    [InlineData(null, null)]
    [InlineData(-10.0, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(54.99, 54.99)]
    [InlineData(100.0, 100.0)]
    [InlineData(150.0, 100.0)]
    public void Bar_position_is_clamped_only_for_drawing(double? input, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected, FitLevel.BarPercent(input is null ? null : (decimal)input));

    [Theory]
    [InlineData(null, ScoreSources.Combined, "tone-none")]
    [InlineData(72.0, ScoreSources.KeywordOnly, "tone-keyword")]
    [InlineData(85.0, ScoreSources.Combined, "tone-excellent")]
    [InlineData(84.99, ScoreSources.Combined, "tone-good")]
    [InlineData(70.0, ScoreSources.Combined, "tone-good")]
    [InlineData(55.0, ScoreSources.Combined, "tone-potential")]
    [InlineData(54.99, ScoreSources.Combined, "tone-poor")]
    public void Bar_tone_follows_level_and_keyword_only_is_neutral(double? final, string source, string tone) =>
        Assert.Equal(tone, FitLevel.ScoreTone(final is null ? null : (decimal)final, source));

    [Fact]
    public void Bar_label_is_understandable_without_colour()
    {
        Assert.Equal("Điểm cuối 76 (Phù hợp), điểm keyword 85, ngưỡng Đạt 70: đạt ngưỡng",
            FitLevel.ScoreBarLabel(85, 76, 70, ScoreSources.Combined));
        Assert.Equal("Điểm cuối 66 (Tiềm năng), điểm keyword 80, ngưỡng Đạt 70: chưa đạt ngưỡng",
            FitLevel.ScoreBarLabel(80, 66, 70, ScoreSources.Combined));
        Assert.Equal("Điểm cuối 72 (Phù hợp), điểm keyword 72, chỉ chấm keyword, AI chưa chấm được, ngưỡng Đạt 70: đạt ngưỡng",
            FitLevel.ScoreBarLabel(72, 72, 70, ScoreSources.KeywordOnly));
        Assert.Equal("Chưa có điểm", FitLevel.ScoreBarLabel(null, null, 70, null));
        Assert.DoesNotContain("ngưỡng", FitLevel.ScoreBarLabel(50, 50, null, ScoreSources.Combined));
    }

    // ================= Cột kỹ năng bắt buộc =================

    [Fact]
    public void Must_have_column_uses_api_counts()
    {
        Assert.Null(MustHaveDisplay.From(0, 3, scored: false));   // đang chấm/lỗi: không suy đoán
        var ok = MustHaveDisplay.From(3, 3, scored: true)!;
        Assert.Equal(("3/3", "must-ok", "Đủ kỹ năng bắt buộc", true), (ok.Text, ok.CssClass, ok.Detail, ok.ShowDots));
        var missing = MustHaveDisplay.From(1, 3, scored: true)!;
        Assert.Equal(("1/3", "must-missing", "Thiếu 2 kỹ năng bắt buộc"), (missing.Text, missing.CssClass, missing.Detail));
        Assert.Equal("must-none", MustHaveDisplay.From(0, 0, scored: true)!.CssClass);
        Assert.False(MustHaveDisplay.From(5, 12, scored: true)!.ShowDots);   // quá nhiều kỹ năng: chỉ hiện chữ
    }

    // ================= (2) Component thật =================

    private sealed class ComponentRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public Task<Rendered> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            Dispatcher.InvokeAsync(async () =>
            {
                var id = AssignRootComponentId(InstantiateComponent(typeof(T)));
                await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));
                return new Rendered(Flat(id));
            });

        private List<RenderTreeFrame> Flat(int id)
        {
            var range = GetCurrentRenderTreeFrames(id);
            return range.Array.Take(range.Count).SelectMany(f => f.FrameType == RenderTreeFrameType.Component
                ? new[] { f }.Concat(Flat(f.ComponentId)) : [f]).ToList();
        }
    }

    private sealed record Rendered(List<RenderTreeFrame> Frames)
    {
        public string Text => System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(string.Concat(Frames.Select(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => System.Text.RegularExpressions.Regex.Replace(f.MarkupContent, "<[^>]+>", " "),
            _ => " "
        }))), @"\s+", " ").Trim();

        public List<string> Attr(string name) => Frames
            .Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == name)
            .Select(f => f.AttributeValue?.ToString() ?? string.Empty).ToList();

        /// <summary>style của phần tử có class cho trước (vd "final" → "width:76%").</summary>
        public List<string> StyleOf(string cssClass)
        {
            var result = new List<string>();
            for (var i = 0; i < Frames.Count; i++)
            {
                if (Frames[i].FrameType != RenderTreeFrameType.Element) continue;
                var attrs = Frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToList();
                if (attrs.Any(a => a.AttributeName == "class" && (a.AttributeValue as string)?.Split(' ').Contains(cssClass) == true))
                    result.Add(attrs.FirstOrDefault(a => a.AttributeName == "style").AttributeValue as string ?? string.Empty);
            }
            return result;
        }

        public bool HasClass(string cssClass) =>
            Attr("class").Any(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cssClass))
            || Frames.Any(f => f.FrameType == RenderTreeFrameType.Markup && f.MarkupContent.Contains(cssClass));
    }

    private static async Task<Rendered> Bar(decimal? keyword, decimal? final, decimal? threshold, string? source = ScoreSources.Combined)
    {
        await using var r = new ComponentRenderer();
        return await r.RenderAsync<ScoreBar>(new()
        {
            [nameof(ScoreBar.Keyword)] = keyword, [nameof(ScoreBar.Final)] = final,
            [nameof(ScoreBar.Threshold)] = threshold, [nameof(ScoreBar.ScoreSource)] = source
        });
    }

    [Fact]
    public async Task ScoreBar_draws_keyword_final_and_threshold_from_data()
    {
        var bar = await Bar(85, 76, 70);
        Assert.Equal(["width:85%"], bar.StyleOf("kw"));
        Assert.Equal(["width:76%"], bar.StyleOf("final"));
        Assert.Equal(["left:70%"], bar.StyleOf("pass-line"));
        Assert.Contains("tone-good", string.Join(' ', bar.Attr("class")));
        Assert.Equal(["Điểm cuối 76 (Phù hợp), điểm keyword 85, ngưỡng Đạt 70: đạt ngưỡng"], bar.Attr("aria-label"));
        Assert.Equal(["img"], bar.Attr("role"));

        // Ngưỡng lấy từ lượt chấm, không viết cứng 70
        Assert.Equal(["left:62.5%"], (await Bar(85, 76, 62.5m)).StyleOf("pass-line"));
    }

    [Fact]
    public async Task ScoreBar_never_draws_fake_bars_for_missing_scores()
    {
        var pending = await Bar(null, null, 70, null);
        Assert.Empty(pending.StyleOf("kw"));
        Assert.Empty(pending.StyleOf("final"));
        Assert.Equal(["left:70%"], pending.StyleOf("pass-line"));
        Assert.Equal(["Chưa có điểm"], pending.Attr("aria-label"));
        Assert.Contains("tone-none", string.Join(' ', pending.Attr("class")));

        var zero = await Bar(0, 0, 70);                  // 0 là điểm thật: vẫn vẽ (độ rộng 0), khác với null
        Assert.Equal(["width:0%"], zero.StyleOf("final"));

        var noThreshold = await Bar(50, 50, null);
        Assert.Empty(noThreshold.StyleOf("pass-line"));
    }

    [Fact]
    public async Task ScoreBar_clamps_out_of_range_values_but_reports_original_score()
    {
        var bar = await Bar(130, 120, 70);
        Assert.Equal(["width:100%"], bar.StyleOf("final"));
        Assert.Equal(["width:100%"], bar.StyleOf("kw"));
        Assert.Contains("Điểm cuối 120", bar.Attr("aria-label").Single());

        var negative = await Bar(-5, -5, 70);
        Assert.Equal(["width:0%"], negative.StyleOf("final"));
        Assert.Contains("tone-poor", string.Join(' ', negative.Attr("class")));
    }

    [Fact]
    public async Task ScoreBar_keyword_only_is_neutral_and_says_so()
    {
        var bar = await Bar(72, 72, 70, ScoreSources.KeywordOnly);
        Assert.Contains("tone-keyword", string.Join(' ', bar.Attr("class")));
        Assert.Contains("chỉ chấm keyword, AI chưa chấm được", bar.Attr("aria-label").Single());
    }

    private static async Task<Rendered> Badge(string? label, decimal? final, string status, string? source = ScoreSources.Combined, string? message = null)
    {
        await using var r = new ComponentRenderer();
        return await r.RenderAsync<FitBadge>(new()
        {
            [nameof(FitBadge.FitLabel)] = label, [nameof(FitBadge.FinalScore)] = final, [nameof(FitBadge.Status)] = status,
            [nameof(FitBadge.ScoreSource)] = source, [nameof(FitBadge.Message)] = message
        });
    }

    [Theory]
    [InlineData(FitLevel.Excellent, 93.0, "fit-excellent")]
    [InlineData(FitLevel.Good, 76.0, "fit-good")]
    [InlineData(FitLevel.Potential, 66.0, "fit-potential")]
    [InlineData(FitLevel.Poor, 24.0, "fit-poor")]
    public async Task FitBadge_shows_each_level_with_its_class(string label, double final, string css)
    {
        var badge = await Badge(label, (decimal)final, EvaluationStatuses.Completed);
        Assert.Equal(label, badge.Text);
        Assert.True(badge.HasClass(css));
        Assert.True(badge.HasClass("fit-badge"));
    }

    [Fact]
    public async Task FitBadge_separates_status_level_and_score_source()
    {
        var failed = await Badge(null, null, EvaluationStatuses.Failed, null, "CV chưa trích xuất được nội dung.");
        Assert.Equal("Lỗi xử lý", failed.Text);
        Assert.True(failed.HasClass("is-error"));
        Assert.Contains("CV chưa trích xuất được nội dung.", failed.Attr("title"));

        var pending = await Badge(null, null, EvaluationStatuses.Pending, null);
        Assert.Equal("Đang chấm", pending.Text);
        Assert.True(pending.HasClass("is-pending"));

        // AI lỗi nhưng đã chấm dự phòng bằng keyword: GIỮ mức, thêm nhãn phụ; không phải "Lỗi xử lý"
        var keywordOnly = await Badge(FitLevel.Good, 72, EvaluationStatuses.Completed, ScoreSources.KeywordOnly);
        Assert.Equal("Phù hợp Chỉ keyword", keywordOnly.Text);
        Assert.True(keywordOnly.HasClass("fit-good"));
        Assert.True(keywordOnly.HasClass("is-keyword"));
        Assert.False(keywordOnly.HasClass("is-error"));

        var empty = await Badge(null, null, EvaluationStatuses.Completed);
        Assert.Equal("Chưa có kết quả", empty.Text);
        Assert.True(empty.HasClass("fit-none"));
    }

    [Fact]
    public async Task FitBadge_flags_label_that_disagrees_with_score_without_changing_it()
    {
        var badge = await Badge(FitLevel.Excellent, 60, EvaluationStatuses.Completed);
        Assert.StartsWith("Rất phù hợp !", badge.Text);
        Assert.Contains("(dữ liệu cần kiểm tra)", badge.Text);
        Assert.True(badge.HasClass("fit-excellent"));
        Assert.Contains(badge.Attr("title"), t => t.Contains("điểm cuối thuộc mức \"Tiềm năng\""));
    }

    // ================= Bảng xếp hạng =================

    private static RankingItemDto Row(int id, string name, decimal? final, string? label, int matched, int total,
        string status = EvaluationStatuses.Completed, string? source = ScoreSources.Combined, decimal? keyword = 80, int? rank = null) => new()
    {
        EvaluationId = id, Rank = rank ?? (status == EvaluationStatuses.Completed ? id : null), FileName = $"CV_{id}.pdf",
        CandidateName = name, FinalScore = final, KeywordScore = status == EvaluationStatuses.Completed ? keyword : null,
        AiScore = source == ScoreSources.KeywordOnly || final is null ? null : final, FitLabel = label, ScoreSource = source,
        PredictedPass = final >= 65, Status = status, MatchedMustHave = matched, TotalMustHave = total
    };

    private static async Task<Rendered> Table(decimal threshold, params RankingItemDto[] items)
    {
        await using var r = new ComponentRenderer();
        return await r.RenderAsync<RankingTable>(new()
        {
            [nameof(RankingTable.Items)] = items.ToList(), [nameof(RankingTable.PassThreshold)] = threshold
        });
    }

    [Fact]
    public async Task RankingTable_shows_levels_bars_must_have_and_threshold_from_api()
    {
        var table = await Table(65m,
            Row(1, "Nguyễn Văn An", 93, FitLevel.Excellent, 3, 3),
            Row(2, "Trần Thị Bình", 66, FitLevel.Potential, 2, 3),
            Row(3, "Dương Mỹ Linh", 72, FitLevel.Good, 3, 3, source: ScoreSources.KeywordOnly, keyword: 72),
            Row(4, "Lý Văn Minh", null, null, 0, 3, status: EvaluationStatuses.Failed));

        var text = table.Text;
        Assert.Contains("Ngưỡng Đạt 65", text);                               // chú giải lấy ngưỡng của lượt chấm
        Assert.Equal(4, table.StyleOf("pass-line").Count(s => s == "left:65%"));
        Assert.True(table.HasClass("fit-excellent") && table.HasClass("fit-potential") && table.HasClass("fit-good"));
        Assert.Contains("Phù hợp Chỉ keyword", text);
        Assert.Contains("KW 72 · AI —", text);
        Assert.Contains("Lỗi xử lý Chưa có điểm", text);
        Assert.True(table.HasClass("must-ok"));
        Assert.True(table.HasClass("must-missing"));
        Assert.Contains("Thiếu 1 kỹ năng bắt buộc", text);
        Assert.Contains("Đủ kỹ năng bắt buộc", text);
        Assert.DoesNotContain("0/3", text);                                  // CV lỗi: không hiện số kỹ năng suy đoán
        Assert.DoesNotContain("cần kiểm tra dữ liệu", text);                  // dữ liệu hợp lệ → không cảnh báo
        AssertOrderIn(text, "Nguyễn Văn An", "Dương Mỹ Linh", "Trần Thị Bình", "Lý Văn Minh");   // B7: vẫn sắp đúng
    }

    [Fact]
    public async Task RankingTable_reports_inconsistent_api_labels()
    {
        var table = await Table(70m,
            Row(1, "Nguyễn Văn An", 60, FitLevel.Excellent, 3, 3),
            Row(2, "Trần Thị Bình", 66, "Xuất sắc", 2, 3));

        Assert.Contains("2 CV có nhãn mức từ API không khớp quy tắc điểm hoặc không hợp lệ", table.Text);
        Assert.Contains("Rất phù hợp", table.Text);       // vẫn hiện nhãn của API
        Assert.Contains("Mức không xác định", table.Text);
    }

    private static void AssertOrderIn(string text, params string[] names)
    {
        var positions = names.Select(n => text.IndexOf(n, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }
}
