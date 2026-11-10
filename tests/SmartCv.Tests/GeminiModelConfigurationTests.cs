extern alias web;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using RunFlow = web::SmartCv.Web.Helpers.RunFlow;

namespace SmartCv.Tests;

/// <summary>
/// Model Gemini: một nguồn duy nhất (Scoring:ModelName). Trước đây "Scoring:Model" (sai tên) bị bỏ qua và
/// mặc định trong code là gemini-2.5-flash, model đã bị gỡ → 404; /api/health/gemini lại kiểm tra Gemini:Model (model khác).
/// </summary>
public class GeminiModelConfigurationTests
{
    private static JsonElement ApiSettings() => JsonDocument.Parse(File.ReadAllText(
        TestSupport.RepoFile(Path.Combine("src", "SmartCv.Api", "appsettings.json")))).RootElement;

    [Fact]
    public void Model_is_configured_once_under_scoring_model_name()
    {
        var root = ApiSettings();

        Assert.Equal("gemini-3.1-flash-lite", root.GetProperty("Scoring").GetProperty("ModelName").GetString());
        Assert.False(root.GetProperty("Scoring").TryGetProperty("Model", out _));   // tên sai, binder bỏ qua
        Assert.False(root.GetProperty("Gemini").TryGetProperty("Model", out _));    // không còn model thứ hai
    }

    [Fact]
    public void Code_default_matches_appsettings_and_is_not_a_retired_or_overloaded_model()
    {
        var configured = ApiSettings().GetProperty("Scoring").GetProperty("ModelName").GetString();

        Assert.Equal(configured, new ScoringOptions().ModelName);   // hai nơi duy nhất có tên model phải trùng nhau
        Assert.DoesNotContain(new ScoringOptions().ModelName, new[] { "gemini-2.5-flash", "gemini-3.8-flash" });
    }

    [Fact]
    public void Bound_options_use_the_configured_model()
    {
        using var factory = TestSupport.ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal("gemini-3.1-flash-lite", factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value.ModelName);
    }

    /// <summary>
    /// Biến môi trường .NET đi qua cấu hình với "__" thay cho ":": Scoring__ModelName = khóa "Scoring:ModelName".
    /// GEMINI_MODEL (tên dùng trong notebook Python) KHÔNG được đọc: đặt nó không đổi model của SCApp.
    /// </summary>
    [Theory]
    [InlineData("Scoring:ModelName", "gemini-3.5-flash-lite", "gemini-3.5-flash-lite")]   // = biến môi trường Scoring__ModelName
    [InlineData("GEMINI_MODEL", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite")]        // bị bỏ qua, giữ appsettings
    public void Only_scoring_model_name_overrides_the_model(string key, string value, string expected)
    {
        using var factory = TestSupport.ApiFactory(settings: new() { [key] = value });
        using var client = factory.CreateClient();
        Assert.Equal(expected, factory.Services.GetRequiredService<IOptions<ScoringOptions>>().Value.ModelName);
    }

    /// <summary>Health check hỏi đúng model dùng để chấm, và nói rõ chỉ kiểm tra thông tin model.</summary>
    [Fact]
    public async Task Health_check_asks_google_about_the_scoring_model()
    {
        var urls = new List<string>();
        var handler = new Recorder(urls);
        // Model KHÁC mặc định: chứng minh health check đọc đúng Scoring:ModelName đang cấu hình
        using var factory = TestSupport.ApiFactory(settings: new() { ["Scoring:ModelName"] = "gemini-3.5-flash-lite" })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient("Gemini").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health/gemini");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["/v1beta/models/gemini-3.5-flash-lite"], urls);   // Gemini:BaseUrl mặc định .../v1beta/
        Assert.Contains("gemini-3.5-flash-lite", body);
        Assert.Contains("Chưa chứng minh generateContent", body);
        Assert.DoesNotContain("test-not-a-real-key", body);   // không trả API key đầy đủ
    }

    [Fact]
    public async Task Health_check_uses_the_configured_gemini_base_url()
    {
        var urls = new List<string>();
        var handler = new Recorder(urls, absolute: true);
        using var factory = TestSupport.ApiFactory(settings: new() { ["Gemini:BaseUrl"] = "https://gemini-proxy.test/v9/" })
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient("Gemini").ConfigurePrimaryHttpMessageHandler(() => handler)));
        using var client = factory.CreateClient();

        using var _ = await client.GetAsync("/api/health/gemini");

        Assert.Equal(["https://gemini-proxy.test/v9/models/gemini-3.1-flash-lite"], urls);
    }

    private sealed class Recorder(List<string> urls, bool absolute = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            urls.Add(absolute ? request.RequestUri!.ToString() : request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    // ---------- Giao diện: KeywordOnly nói rõ vì sao AI không chấm được ----------

    [Theory]
    // ErrorMessage thật trong SmartCvDb (Evaluation #45, #50, #51)
    [InlineData("Không tìm thấy model, kiểm tra Scoring:ModelName (HTTP 404: This model models/gemini-2.5-flash is no longer available to new users.)",
        "model Gemini không khả dụng, cần kiểm tra cấu hình model")]
    [InlineData("Gemini lỗi sau 3 lần thử (Máy chủ Gemini lỗi tạm thời (HTTP 503: This model is currently experiencing high demand.))",
        "Gemini đang quá tải, có thể thử lại sau")]
    [InlineData("Gemini lỗi sau 3 lần thử (Không kết nối được Gemini)", "không kết nối được Gemini")]
    // Thông báo mới của GeminiEvaluationService
    [InlineData("Gemini lỗi sau 3 lần thử (Gemini đang quá tải (HTTP 503: high demand))", "Gemini đang quá tải, có thể thử lại sau")]
    [InlineData("Không tìm thấy model hoặc model không còn khả dụng, kiểm tra Scoring:ModelName (HTTP 404)", "model Gemini không khả dụng, cần kiểm tra cấu hình model")]
    [InlineData("Gemini vượt hạn mức sau 3 lần thử lại", "đã vượt hạn mức gọi Gemini")]
    [InlineData("Gemini lỗi sau 3 lần thử (Gemini không phản hồi sau 90 giây)", "Gemini không phản hồi kịp")]
    [InlineData("Gemini lỗi sau 3 lần thử (JSON không hợp lệ hoặc sai kiểu dữ liệu)", "Gemini trả kết quả không hợp lệ")]
    [InlineData("Không có quyền sử dụng Gemini, kiểm tra API key (HTTP 403)", "lỗi cấu hình API key Gemini")]
    [InlineData("Gemini từ chối nội dung (SAFETY)", "Gemini từ chối xử lý nội dung CV")]
    public void Keyword_only_items_show_the_classified_ai_failure_reason(string stored, string reason)
    {
        var item = new RunItemDto { Status = EvaluationStatuses.Completed, ScoreSource = ScoreSources.KeywordOnly, ErrorMessage = stored };

        var text = RunFlow.FriendlyItemError(item);

        Assert.Equal($"AI chưa chấm được ({reason}), điểm chỉ tính theo từ khóa.", text);
        Assert.DoesNotContain("high demand", text);   // không đưa chi tiết kỹ thuật tiếng Anh của Google ra giao diện
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lỗi lạ không phân loại được")]
    public void Unknown_reason_keeps_the_generic_keyword_only_text(string? stored) =>
        Assert.Equal("AI chưa chấm được, điểm chỉ tính theo từ khóa.", RunFlow.FriendlyItemError(new RunItemDto
        {
            Status = EvaluationStatuses.Completed, ScoreSource = ScoreSources.KeywordOnly, ErrorMessage = stored
        }));

    [Fact]
    public void Combined_item_never_shows_an_ai_failure_note() =>
        Assert.Null(RunFlow.FriendlyItemError(new RunItemDto
        {
            Status = EvaluationStatuses.Completed, ScoreSource = ScoreSources.Combined, ErrorMessage = "HTTP 503"
        }));
}
