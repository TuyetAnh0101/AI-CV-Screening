using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController(
    SmartCvDbContext db,
    IHttpClientFactory httpClientFactory,
    IOptions<GeminiOptions> geminiOptions,
    IOptions<ScoringOptions> scoringOptions,
    IWebHostEnvironment env) : ControllerBase
{
    // GET /api/health/database
    [HttpGet("database")]
    public async Task<IActionResult> CheckDatabase(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();

        try
        {
            await db.Database.OpenConnectionAsync(ct);
            var conn = db.Database.GetDbConnection();
            var result = new { status = "OK", server = conn.DataSource, database = conn.Database };
            await db.Database.CloseConnectionAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "Lỗi", message = ex.Message });
        }
    }

    // GET /api/health/gemini – chỉ hỏi thông tin model, không tốn lượt sinh nội dung.
    // Kiểm tra ĐÚNG model dùng để chấm (Scoring:ModelName). Lưu ý: models.get thành công chỉ chứng minh model có trong
    // danh sách của API key, KHÔNG chứng minh generateContent dùng được (gemini-2.5-flash vẫn qua models.get nhưng
    // generateContent trả 404 "no longer available to new users").
    [HttpGet("gemini")]
    public async Task<IActionResult> CheckGemini(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();

        var options = geminiOptions.Value;
        var model = scoringOptions.Value.ModelName;
        var client = httpClientFactory.CreateClient("Gemini");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"models/{Uri.EscapeDataString(model)}");   // tương đối với Gemini:BaseUrl
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return Ok(new
                {
                    status = "OK", model, key = Mask(options.ApiKey),
                    note = "Đã kiểm tra thông tin model (models.get). Chưa chứng minh generateContent: lỗi 404/503 khi chấm vẫn có thể xảy ra."
                });

            var body = await response.Content.ReadAsStringAsync(ct);
            return StatusCode(503, new { status = "Lỗi", httpStatus = (int)response.StatusCode, detail = body });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { status = "Lỗi mạng", message = ex.Message });
        }
    }

    // Chỉ hiện 4 ký tự đầu và 4 ký tự cuối, không bao giờ trả về key đầy đủ
    private static string Mask(string key) =>
        key.Length <= 8 ? "****" : $"{key[..4]}...{key[^4..]}";
}
