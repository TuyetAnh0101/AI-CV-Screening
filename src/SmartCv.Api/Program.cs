using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Api.Endpoints;
using SmartCv.Api.Middleware;
using SmartCv.Core.Ai;
using SmartCv.Core.Options;
using SmartCv.Core.Scoring;
using SmartCv.Infrastructure;
using SmartCv.Infrastructure.Privacy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// A7: tiến trình chạy nền đọc chữ CV (PdfPig / OCR Tesseract) từ hàng đợi; đăng ký các service trong AddInfrastructure.
builder.Services.AddHostedService<CvExtractionWorker>();

// A11: tiến trình chấm điểm nền, chỉ chạy khi Scoring:WorkerEnabled = true.
builder.Services.AddHostedService<RunScoringWorker>();
builder.Services.AddScoped<CvAnonymizer>();
builder.Services.AddScoped<RetentionProcessor>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<RetentionWorker>();

// A4: giới hạn tần suất cho API tải CV (mục "CvUpload:Endpoint" trong appsettings).
// Giới hạn dung lượng request (201 MiB) được gắn riêng cho endpoint tải CV trong CvEndpoints,
// các endpoint khác giữ mặc định của Kestrel (30 MB).
builder.Services.AddCvUploadRateLimiting(builder.Configuration);

// A9: trọng số chấm điểm (mục "Scoring" trong appsettings); sai cấu hình thì dừng ngay lúc khởi động.
builder.Services.AddOptions<ScoringOptions>()
    .Bind(builder.Configuration.GetSection(ScoringOptions.SectionName))
    .Validate(o => o.HasValidWeights(), "Scoring: trọng số không được âm, KeywordWeight + AiWeight và 4 trọng số Keyword* phải có tổng bằng 1.")
    .Validate(o => o.HasValidScoreLimits(), "Scoring: PassThreshold và MissingMustHaveCap phải nằm trong khoảng 0–100.")
    .Validate(o => o.PollIntervalSeconds >= 1, "Scoring: PollIntervalSeconds phải từ 1 giây trở lên.")
    .ValidateOnStart();

builder.Services.AddOptions<ReviewFlagOptions>()
    .Bind(builder.Configuration.GetSection(ReviewFlagOptions.SectionName))
    .Validate(o => o.HasValidThresholds(), "ReviewFlags: ScoreGap phải > 0 và <= 100, MinTextLength > 0, CappedHighAiScore từ 0 đến 100.")
    .ValidateOnStart();

builder.Services.AddOptions<RetentionOptions>()
    .Bind(builder.Configuration.GetSection(RetentionOptions.SectionName))
    .Validate(o => o.Months is >= 1 and <= 120, "Retention:Months phải từ 1 đến 120.")
    .Validate(o => o.BatchSize is >= 1 and <= 200 && o.CheckIntervalHours is >= 1 and <= 720,
        "Retention: BatchSize từ 1 đến 200, CheckIntervalHours từ 1 đến 720.")
    .ValidateOnStart();

// A10: giới hạn tần suất riêng cho API chấm thử (mỗi lần gọi tốn quota Gemini).
builder.Services.AddOptions<RateLimiterOptions>().Configure(o =>
    o.AddPolicy<string, AiPreviewRateLimitPolicy>(AiPreviewRateLimitPolicy.PolicyName));

// A41: số lượt chấm thử dùng chung cả ứng dụng (cửa sổ trượt), thêm vào giới hạn theo IP của A10 ở trên.
// Sai cấu hình thì dừng lúc khởi động, không tự sửa giá trị. Bộ đếm trong bộ nhớ: chỉ đúng cho MỘT tiến trình API.
builder.Services.AddOptions<AiPreviewOptions>()
    .Bind(builder.Configuration.GetSection(AiPreviewOptions.SectionName))
    .Validate(o => o.LimitPerWindow is >= 1 and <= AiPreviewOptions.MaxLimitPerWindow,
        $"AiPreview:LimitPerWindow phải từ 1 đến {AiPreviewOptions.MaxLimitPerWindow}.")
    .Validate(o => o.WindowMinutes is >= 1 and <= AiPreviewOptions.MaxWindowMinutes,
        $"AiPreview:WindowMinutes phải từ 1 đến {AiPreviewOptions.MaxWindowMinutes} (24 giờ).")
    .Validate(o => o.MaxConcurrent is >= 1 and <= AiPreviewOptions.MaxConcurrentLimit,
        $"AiPreview:MaxConcurrent phải từ 1 đến {AiPreviewOptions.MaxConcurrentLimit}.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<AiPreviewOptions>>().Value;
    return new PreviewQuota(o.LimitPerWindow, TimeSpan.FromMinutes(o.WindowMinutes), o.MaxConcurrent, sp.GetRequiredService<TimeProvider>());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
// Sau routing (WebApplication tự thêm ở đầu pipeline) để đọc được policy của từng endpoint.
// M6: đặt UseAuthentication/UseAuthorization trước dòng này nếu muốn giới hạn theo người dùng.
app.UseRateLimiter();

app.UseMiddleware<SqlReadCancellationMiddleware>();
app.UseMiddleware<CvPrivacyReadMiddleware>();

app.MapControllers();
app.MapEvaluationEndpoints();
app.MapRunEndpoints(); // A12: persist run settings before background scoring.
app.MapCvEndpoints();
app.MapRetentionEndpoints();
app.MapApplicationEndpoints();   // A8: POST/GET/DELETE /api/applications (chỉ Development, cùng chốt chặn với API CV)
app.MapApplicationStageEndpoints(); // A35: quyết định của HR và lịch sử chỉ thêm.
app.MapScoringPreviewEndpoints();   // A9: GET /api/applications/{id}/keyword-score (chỉ Development, cùng chốt chặn với API hồ sơ)
app.MapAiPreviewEndpoints();   // A10/A41: POST /api/applications/{id}/ai-preview, GET .../ai-preview/quota (chỉ Development, có giới hạn tần suất)
app.MapJobEndpoints();   // GET /api/health, /api/jobs, /api/jobs/{id} (dùng cho giao diện SmartCv.Web)
app.MapPromptEndpoints(); // A14
app.MapEvaluationSetEndpoints(); // A15: HR labels and CSV import.
app.MapPromptTestEndpoints(); // A15: queue and inspect persisted prompt-test results.

app.Run();

// Cho phép project test tạo ứng dụng bằng WebApplicationFactory<Program>.
public partial class Program;
