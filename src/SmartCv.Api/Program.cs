using SmartCv.Api.Background;
using SmartCv.Api.Endpoints;
using SmartCv.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// A7: tiến trình chạy nền đọc chữ CV (PdfPig / OCR Tesseract) từ hàng đợi; đăng ký các service trong AddInfrastructure.
builder.Services.AddHostedService<CvExtractionWorker>();

// A4: giới hạn tần suất cho API tải CV (mục "CvUpload:Endpoint" trong appsettings).
// Giới hạn dung lượng request (201 MiB) được gắn riêng cho endpoint tải CV trong CvEndpoints,
// các endpoint khác giữ mặc định của Kestrel (30 MB).
builder.Services.AddCvUploadRateLimiting(builder.Configuration);

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
app.MapJobEndpoints();   // GET /api/health, /api/jobs, /api/jobs/{id} (dùng cho giao diện SmartCv.Web)

app.Run();

// Cho phép project test tạo ứng dụng bằng WebApplicationFactory<Program>.
public partial class Program;
