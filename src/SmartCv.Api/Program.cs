using Microsoft.AspNetCore.Http.Features;
using SmartCv.Api.Endpoints;
using SmartCv.Core.Options;
using SmartCv.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// A4: nới giới hạn dung lượng request cho API tải CV.
// Mặc định Kestrel chỉ nhận 30 MB/request và form multipart tối đa 128 MB, không đủ khi tải
// nhiều CV cùng lúc. Giới hạn = MaxFileSizeMB × MaxFilesPerRequest + 1 MB (mặc định 201 MB),
// tự đổi theo mục "CvUpload" trong appsettings.json. Vượt giới hạn: máy chủ trả 413.
var cvUploadOptions = builder.Configuration.GetSection(CvUploadOptions.SectionName).Get<CvUploadOptions>()
                      ?? new CvUploadOptions();
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = cvUploadOptions.MaxRequestBytes);
builder.Services.Configure<FormOptions>(form =>
    form.MultipartBodyLengthLimit = cvUploadOptions.MaxRequestBytes);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapCvEndpoints();

app.Run();
