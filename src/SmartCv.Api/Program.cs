using Microsoft.AspNetCore.RateLimiting;
using SmartCv.Api.Background;
using SmartCv.Api.Endpoints;
using SmartCv.Core.Options;
using SmartCv.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// A7: tiến trình chạy nền đọc chữ CV (PdfPig / OCR Tesseract) từ hàng đợi; đăng ký các service trong AddInfrastructure.
builder.Services.AddHostedService<CvExtractionWorker>();

// A11: tiến trình chấm điểm nền, chỉ chạy khi Scoring:WorkerEnabled = true.
builder.Services.AddHostedService<RunScoringWorker>();

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

// A10: giới hạn tần suất riêng cho API chấm thử (mỗi lần gọi tốn quota Gemini).
builder.Services.AddOptions<RateLimiterOptions>().Configure(o =>
    o.AddPolicy<string, AiPreviewRateLimitPolicy>(AiPreviewRateLimitPolicy.PolicyName));

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

app.MapControllers();
app.MapCvEndpoints();
app.MapApplicationEndpoints();   // A8: POST/GET/DELETE /api/applications (chỉ Development, cùng chốt chặn với API CV)
app.MapScoringPreviewEndpoints();   // A9: GET /api/applications/{id}/keyword-score (chỉ Development, cùng chốt chặn với API hồ sơ)
app.MapAiPreviewEndpoints();   // A10: POST /api/applications/{id}/ai-preview (chỉ Development, có giới hạn tần suất)
app.MapJobEndpoints();   // GET /api/health, /api/jobs, /api/jobs/{id} (dùng cho giao diện SmartCv.Web)

app.Run();

// Cho phép project test tạo ứng dụng bằng WebApplicationFactory<Program>.
public partial class Program;
