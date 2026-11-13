using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Core.Constants;
using SmartCv.Core.Privacy;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure.Ai;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Tests;

/// <summary>
/// A41: AiPromptAssembler là MỘT nguồn sự thật cho nội dung gửi AI. Dữ liệu là CV GIẢ chép từ hai ảnh thử OCR của gói A41
/// (test-files/cv-anh-ro.png và cv-anh-mo.png: họ tên, email @example.com, số điện thoại đều giả). Không gọi OCR, không gọi Gemini.
/// </summary>
public class AiPromptAssemblerTests
{
    private const string CandidateName = "Nguyễn Thị Lan Anh";
    private const string SystemPrompt = "Bạn là chuyên viên tuyển dụng. Chấm CV theo tiêu chí của vị trí.";

    private static readonly JobProfile Job = new(1, "Backend Developer (.NET)", "Phát triển API", 2.5m, 0.5m,
    [
        new SkillProfile("csharp", "C#", SkillTypes.MustHave, ["C Sharp"]),
        new SkillProfile("aspnet_core", "ASP.NET Core", SkillTypes.MustHave, []),
        new SkillProfile("docker", "Docker", SkillTypes.NiceToHave, [])
    ]);

    /// <summary>Chữ đọc được từ ảnh rõ (cv-anh-ro.png).</summary>
    internal static readonly string ClearImageCv = """
        NGUYỄN THỊ LAN ANH
        Lập trình viên Backend (.NET)
        Email: lananh.nguyen@example.com · ĐT: 0938 214 657

        KINH NGHIỆM
        2023 – nay: Công ty Phần mềm Sao Mai — Lập trình viên C#
        • Xây dựng API bán hàng bằng ASP.NET Core 8, SQL Server, Entity Framework Core
        • Viết unit test xUnit, triển khai Docker trên máy chủ nội bộ
        2022: Thực tập sinh .NET — FPT Software

        KỸ NĂNG
        C#, ASP.NET Core, SQL Server, EF Core, Git, Docker, REST API

        HỌC VẤN
        Đại học Bách khoa TP.HCM — Kỹ thuật phần mềm (2018–2022), GPA 3.2/4
        """.ReplaceLineEndings("\n");

    /// <summary>Giả lập OCR ảnh mờ (cv-anh-mo.png): mất dấu, nhầm ký tự, nhưng email/điện thoại vẫn đọc ra được.</summary>
    internal static readonly string BlurryImageCv = """
        NGUYEN THI LAN ANH
        Lap trinh vien Backend (.NET)
        Emall: lananh.nguyen@exarnple.com . DT: 0938 214 657
        KINH NGHIEM
        2023 - nay: Cong ty Phan mem Sao Mai - Lap trinh vien C#
        Xay dung API ban hang bang ASP.NET Core 8, SQL Server
        KY NANG: C#, ASP.NET Core, SQL Server, EF Core, Git, Docker
        """.ReplaceLineEndings("\n");

    private static readonly string[] RawPersonalData =
        ["NGUYỄN THỊ LAN ANH", "NGUYEN THI LAN ANH", "Lan Anh", "lananh.nguyen@", "0938 214 657", "0938214657"];

    private static AiEvaluationRequest Request(string cv, string? candidate = CandidateName, string prompt = SystemPrompt) =>
        new("model-a41", 0.2m, prompt, Job, cv, candidate);

    public static TheoryData<string, string> ImageCvs => new()
    {
        { "ảnh rõ", ClearImageCv },
        { "ảnh mờ", BlurryImageCv }
    };

    [Theory]
    [MemberData(nameof(ImageCvs))]
    public void Masks_name_email_and_phone_of_synthetic_image_cv(string _, string cv)
    {
        var a = AiPromptAssembler.Assemble(Request(cv), 12_000, maskPersonalData: true);

        Assert.True(a.PersonalDataMasked);
        Assert.NotNull(a.Mask);
        Assert.Contains(PersonalDataMasker.NameToken, a.UserPrompt);
        Assert.Contains(PersonalDataMasker.EmailToken, a.UserPrompt);
        Assert.Contains(PersonalDataMasker.PhoneToken, a.UserPrompt);
        Assert.True(a.Mask!.Names >= 1 && a.Mask.Emails == 1 && a.Mask.Phones == 1);
        foreach (var raw in RawPersonalData)
            Assert.DoesNotContain(raw, a.UserPrompt, StringComparison.OrdinalIgnoreCase);
        // Thông tin năng lực vẫn còn để AI chấm
        Assert.Contains("ASP.NET Core", a.UserPrompt);
        Assert.Contains("SQL Server", a.UserPrompt);
        Assert.Contains(AiPromptBuilder.MaskedDataRule, a.UserPrompt);
        Assert.Equal(SystemPrompt, a.SystemPrompt);
        Assert.Equal(cv.Length, a.CvCharacters);
        Assert.False(a.Truncated);
    }

    [Fact]
    public void Header_name_is_masked_even_without_candidate_record()
    {
        var a = AiPromptAssembler.Assemble(Request(ClearImageCv, candidate: null), 12_000, maskPersonalData: true);

        Assert.NotEqual(NameSource.None, a.Mask!.NameSource);
        Assert.DoesNotContain("NGUYỄN THỊ LAN ANH", a.UserPrompt);
        Assert.Contains(PersonalDataMasker.NameToken, a.UserPrompt);
    }

    [Fact]
    public void Masking_disabled_keeps_legacy_prompt_and_reports_no_mask()
    {
        var a = AiPromptAssembler.Assemble(Request(ClearImageCv), 12_000, maskPersonalData: false);

        Assert.False(a.PersonalDataMasked);
        Assert.Null(a.Mask);
        Assert.Contains("lananh.nguyen@example.com", a.UserPrompt);
        Assert.DoesNotContain(AiPromptBuilder.MaskedDataRule, a.UserPrompt);
        Assert.Equal(AiPromptBuilder.BuildUserPrompt(Job, ClearImageCv, 12_000), a.UserPrompt);
    }

    [Fact]
    public void Truncation_reports_original_and_sent_lengths()
    {
        var cv = "Kinh nghiệm C# ASP.NET Core. " + new string('x', 2_000);

        var a = AiPromptAssembler.Assemble(Request(cv, candidate: null), 1_000, maskPersonalData: true);

        Assert.True(a.Truncated);
        Assert.Equal(cv.Length, a.CvCharacters);
        Assert.Equal(1_000, a.CvCharactersSent);
        Assert.Contains("chỉ có 1000 ký tự đầu", a.UserPrompt);
    }

    [Fact]
    public void Truncation_never_splits_an_emoji()
    {
        var cv = new string('a', 999) + "😀" + new string('b', 100);

        var a = AiPromptAssembler.Assemble(Request(cv, candidate: null), 1_000, maskPersonalData: false);

        Assert.Equal(999, a.CvCharactersSent);
        Assert.DoesNotContain("\uD83D" + Environment.NewLine, a.UserPrompt);
    }

    [Fact]
    public void Personal_data_is_masked_before_truncation()
    {
        // Email nằm vắt qua điểm cắt: phải che cả email TRƯỚC khi cắt, không để lộ một phần "lananh.ng"
        var cv = new string('x', 990) + " lananh.nguyen@example.com " + new string('y', 50);

        var a = AiPromptAssembler.Assemble(Request(cv, candidate: null), 1_000, maskPersonalData: true);

        Assert.Equal(1, a.Mask!.Emails);
        Assert.DoesNotContain("lananh", a.UserPrompt);
        Assert.Contains(PersonalDataMasker.EmailToken, a.UserPrompt);
    }

    [Fact]
    public void Allowed_skill_codes_follow_job_skills()
    {
        var a = AiPromptAssembler.Assemble(Request(ClearImageCv), 12_000, maskPersonalData: true);

        Assert.Equal(["csharp", "aspnet_core", "docker"], a.AllowedSkillCodes);
    }

    [Fact]
    public void Same_input_gives_identical_output()
    {
        var first = AiPromptAssembler.Assemble(Request(ClearImageCv), 12_000, true);
        var second = AiPromptAssembler.Assemble(Request(ClearImageCv), 12_000, true);

        Assert.Equal(first.UserPrompt, second.UserPrompt);
        Assert.Equal(first.Mask, second.Mask);
    }

    // ---------- Payload THẬT của GeminiEvaluationService = kết quả assembler ----------

    public static TheoryData<bool, int, string> PayloadCases => new()
    {
        { true, 12_000, ClearImageCv },
        { true, 12_000, BlurryImageCv },
        { false, 12_000, ClearImageCv },
        { true, 1_000, ClearImageCv + new string('z', 2_000) }
    };

    [Theory]
    [MemberData(nameof(PayloadCases))]
    public async Task Gemini_payload_is_exactly_the_assembled_prompt(bool mask, int maxChars, string cv)
    {
        var options = Options.Create(new GeminiOptions
        {
            ApiKey = "test-not-a-real-key-a41", MinDelayBetweenCallsMs = 0, MaxCvCharacters = maxChars, MaskPersonalData = mask
        });
        var handler = new GeminiPayloadCapture();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/v1beta/") };
        var service = new GeminiEvaluationService(http, new GeminiRateLimiter(options), options,
            new CapturingLogger<GeminiEvaluationService>()) { DelayAsync = (_, _) => Task.CompletedTask, NextJitter = () => 0 };
        var request = Request(cv);

        var result = await service.EvaluateAsync(request);
        var assembled = AiPromptAssembler.Assemble(request, maxChars, mask);

        var body = Assert.Single(handler.Bodies);
        Assert.True(result.IsSuccess);
        Assert.Equal(assembled.SystemPrompt, GeminiPayloadCapture.SystemText(body));
        Assert.Equal(assembled.UserPrompt, GeminiPayloadCapture.UserText(body));
        Assert.Equal(assembled.Truncated, result.CvTruncated);
        var schemaCodes = JsonNode.Parse(body)!["generationConfig"]!["responseSchema"]!["properties"]!["matchedSkills"]!["items"]!["enum"]!
            .AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(assembled.AllowedSkillCodes, schemaCodes);
        if (mask)
            foreach (var raw in RawPersonalData)
                Assert.DoesNotContain(raw, GeminiPayloadCapture.UserText(body), StringComparison.OrdinalIgnoreCase);
    }
}
