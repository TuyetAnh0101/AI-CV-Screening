using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Tests;

/// <summary>
/// A37: payload HTTP THẬT SỰ gửi Gemini (GeminiEvaluationService + HttpMessageHandler giả, không gọi mạng):
/// thông tin cá nhân đã thay bằng placeholder, che trước khi cắt bớt, tắt che thì giữ nguyên hành vi cũ, log chỉ có số lượng.
/// </summary>
public class GeminiPayloadMaskingTests
{
    private const string ApiKey = "test-secret-key-a37-000";
    private const string SystemPrompt = "PROMPT-HE-THONG-A37";
    private const string CandidateName = "Trần Minh Khoa";

    private static readonly JobProfile Job = new(1, "Backend .NET", "Phát triển API", 2.5m, 0.5m,
    [
        new SkillProfile("csharp", "C#", SkillTypes.MustHave, ["C Sharp"]),
        new SkillProfile("sql_server", "SQL Server", SkillTypes.NiceToHave, [])
    ]);

    private static readonly string Cv = """
        SƠ YẾU LÝ LỊCH
        TRẦN MINH KHOA
        Ngày sinh: 15/08/1999 | Giới tính: Nam
        Email: khoa.tran@example.com | Điện thoại: 0900 123 456 | linkedin.com/in/tran-minh-khoa
        HỌC VẤN: Đại học Công nghệ (2019 - 2023), GPA 3.6/4
        KINH NGHIỆM: Công ty ABC (06/2023 - nay): API ASP.NET Core trên .NET 8, SQL Server 2022, CI/CD bằng GitHub Actions.
        Tôi là Trần Minh Khoa.
        """.ReplaceLineEndings("\n");

    private static readonly string[] RawPersonalData =
        ["TRẦN MINH KHOA", "Trần Minh Khoa", "15/08/1999", "khoa.tran@example.com", "0900 123 456", "linkedin.com/in/tran-minh-khoa"];

    private static readonly string[] Professional =
        ["(2019 - 2023)", "GPA 3.6/4", "(06/2023 - nay)", "ASP.NET Core", ".NET 8", "SQL Server 2022", "GitHub Actions"];

    private static (GeminiEvaluationService Service, GeminiPayloadCapture Handler, CapturingLogger<GeminiEvaluationService> Logger) Create(
        bool? mask = null, int maxChars = 12000, Func<int, HttpResponseMessage>? respond = null)
    {
        var settings = new GeminiOptions { ApiKey = ApiKey, MinDelayBetweenCallsMs = 0, MaxCvCharacters = maxChars };
        if (mask is not null) settings.MaskPersonalData = mask.Value;   // null: giữ giá trị mặc định
        var options = Options.Create(settings);
        var handler = new GeminiPayloadCapture(respond);
        var logger = new CapturingLogger<GeminiEvaluationService>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/v1beta/") };
        var service = new GeminiEvaluationService(http, new GeminiRateLimiter(options), options, logger)
        {
            DelayAsync = (_, _) => Task.CompletedTask,
            NextJitter = () => 0
        };
        return (service, handler, logger);
    }

    private static AiEvaluationRequest Request(string cv = "", string? candidate = CandidateName) =>
        new("model-a37", 0.2m, SystemPrompt, Job, cv.Length == 0 ? Cv : cv, candidate);

    // ---------- Mặc định: che ----------

    [Fact]
    public void Masking_is_enabled_by_default_in_options_and_appsettings()
    {
        Assert.True(new GeminiOptions().MaskPersonalData);

        var appsettings = JsonNode.Parse(File.ReadAllText(TestSupport.RepoFile("src/SmartCv.Api/appsettings.json")),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        Assert.True(appsettings["Gemini"]!["MaskPersonalData"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Default_payload_replaces_personal_data_and_keeps_professional_content()
    {
        var (service, handler, _) = Create();

        var result = await service.EvaluateAsync(Request());

        Assert.True(result.IsSuccess);
        var body = Assert.Single(handler.Bodies);
        var user = GeminiPayloadCapture.UserText(body);
        foreach (var raw in RawPersonalData) Assert.DoesNotContain(raw, user, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("khoa", user, StringComparison.OrdinalIgnoreCase);
        foreach (var token in new[] { "[CANDIDATE]", "[EMAIL]", "[PHONE]", "[LINK]", "[DOB]", "[GENDER]" }) Assert.Contains(token, user);
        foreach (var keep in Professional) Assert.Contains(keep, user);
        Assert.Contains(AiPromptBuilder.MaskedDataRule, user);

        // System prompt của phiên bản prompt (HR quản lý, lưu trong database) gửi nguyên văn, không bị sửa.
        Assert.Equal(SystemPrompt, GeminiPayloadCapture.SystemText(body));
        // Payload thô (JSON escape tiếng Việt) cũng không chứa email, số điện thoại, đường dẫn hay API key.
        foreach (var raw in new[] { "example.com", "0900", "tran-minh-khoa", ApiKey }) Assert.DoesNotContain(raw, body);
    }

    [Fact]
    public async Task Candidate_name_from_request_is_masked_even_when_not_on_first_line()
    {
        var (service, handler, _) = Create();
        const string cv = "Backend Developer\nTôi là Trần Minh Khoa, 2 năm C# và SQL Server.";

        await service.EvaluateAsync(Request(cv));

        var user = GeminiPayloadCapture.UserText(Assert.Single(handler.Bodies));
        Assert.DoesNotContain("Trần Minh Khoa", user);
        Assert.Contains("Backend Developer\nTôi là [CANDIDATE], 2 năm C# và SQL Server.", user);
    }

    [Fact]
    public async Task Empty_cv_text_is_sent_without_error()
    {
        var (service, handler, _) = Create();

        var result = await service.EvaluateAsync(new AiEvaluationRequest("model-a37", 0.2m, SystemPrompt, Job, string.Empty));

        Assert.True(result.IsSuccess);
        Assert.Contains("<<<CV\n\nCV>>>", GeminiPayloadCapture.UserText(Assert.Single(handler.Bodies)).ReplaceLineEndings("\n"));
    }

    // ---------- Tắt che: tương thích phiên bản trước ----------

    [Fact]
    public async Task Disabled_masking_sends_exactly_the_pre_A37_prompt()
    {
        var (service, handler, logger) = Create(mask: false);

        await service.EvaluateAsync(Request());

        var user = GeminiPayloadCapture.UserText(Assert.Single(handler.Bodies));
        Assert.Equal(AiPromptBuilder.BuildUserPrompt(Job, Cv, 12000), user);   // lời gọi 3 tham số = prompt trước A37
        Assert.Contains("khoa.tran@example.com", user);
        Assert.Contains(CandidateName, user);
        Assert.DoesNotContain(AiPromptBuilder.MaskedDataRule, user);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Đã che"));
    }

    // ---------- Che TRƯỚC khi cắt CV dài ----------

    [Fact]
    public async Task Masking_happens_before_truncation_so_no_fragment_leaks()
    {
        // Email nằm vắt qua giới hạn 1000 ký tự: cắt trước rồi mới che sẽ để lộ "an.nguyen..."
        var cv = new string('a', 990) + " an.nguyen99@example.com tiếp tục mô tả dự án ASP.NET Core.";

        var (masked, maskedHandler, _) = Create(mask: true, maxChars: 1000);
        await masked.EvaluateAsync(Request(cv, candidate: null));
        var maskedUser = GeminiPayloadCapture.UserText(Assert.Single(maskedHandler.Bodies));
        Assert.Contains("[EMAIL]", maskedUser);
        Assert.DoesNotContain("an.ngu", maskedUser);
        Assert.DoesNotContain("example.com", maskedUser);

        // Đối chứng: không che thì đoạn đầu email nằm trong phần được gửi.
        var (plain, plainHandler, _) = Create(mask: false, maxChars: 1000);
        await plain.EvaluateAsync(Request(cv, candidate: null));
        Assert.Contains("an.ngu", GeminiPayloadCapture.UserText(Assert.Single(plainHandler.Bodies)));
    }

    [Fact]
    public async Task Truncation_flag_and_note_follow_the_text_actually_sent()
    {
        // Bản gốc 1006 ký tự (> 1000) nhưng bản đã che chỉ 968 ký tự: AI đọc được toàn bộ CV.
        var cv = new string('a', 960) + " nguyen.van.an.backend.dotnet.2023@example.com";
        Assert.True(cv.Length > 1000);

        var (masked, maskedHandler, _) = Create(mask: true, maxChars: 1000);
        var maskedResult = await masked.EvaluateAsync(Request(cv, candidate: null));
        Assert.False(maskedResult.CvTruncated);
        Assert.DoesNotContain("CV dài nên chỉ có", GeminiPayloadCapture.UserText(Assert.Single(maskedHandler.Bodies)));

        var (plain, plainHandler, _) = Create(mask: false, maxChars: 1000);
        var plainResult = await plain.EvaluateAsync(Request(cv, candidate: null));
        Assert.True(plainResult.CvTruncated);
        Assert.Contains("CV dài nên chỉ có", GeminiPayloadCapture.UserText(Assert.Single(plainHandler.Bodies)));
    }

    // ---------- Thử lại: mọi lần gửi đều là bản đã che ----------

    [Fact]
    public async Task Retries_resend_the_same_masked_payload()
    {
        var (service, handler, _) = Create(respond: n => n == 1
            ? GeminiPayloadCapture.Json(HttpStatusCode.ServiceUnavailable, "{}")
            : GeminiPayloadCapture.Ok());

        var result = await service.EvaluateAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.DoesNotContain("khoa.tran@example.com", GeminiPayloadCapture.UserText(handler.Bodies[0]));
    }

    // ---------- Log: chỉ số lượng ----------

    [Fact]
    public async Task Logs_only_counts_never_personal_data_or_key()
    {
        var (service, _, logger) = Create();

        await service.EvaluateAsync(Request());

        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Đã che"));
        Assert.Equal("Đã che 7 thông tin cá nhân trước khi gửi Gemini (email 1, điện thoại 1, link 1, họ tên 2, ngày sinh 1, giới tính 1)", debug.Message);
        AssertNoPersonalData(logger);
    }

    [Fact]
    public async Task Error_logs_do_not_contain_personal_data()
    {
        var (service, _, logger) = Create(respond: _ =>
            GeminiPayloadCapture.Json(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid argument"}}"""));

        var result = await service.EvaluateAsync(Request());

        Assert.False(result.IsSuccess);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        AssertNoPersonalData(logger);
    }

    private static void AssertNoPersonalData(CapturingLogger<GeminiEvaluationService> logger)
    {
        foreach (var (_, message) in logger.Entries)
        {
            foreach (var raw in RawPersonalData) Assert.DoesNotContain(raw, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, message);
            Assert.DoesNotContain("GitHub Actions", message);   // không ghi nội dung CV
        }
    }
}
