using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartCv.Infrastructure.Gemini;
using SmartCv.Infrastructure.Persistence;

namespace SmartCv.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController(
    SmartCvDbContext db,
    IHttpClientFactory httpClientFactory,
    IOptions<GeminiOptions> geminiOptions,
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

    // GET /api/health/gemini – chỉ hỏi thông tin model, không tốn lượt sinh nội dung
    [HttpGet("gemini")]
    public async Task<IActionResult> CheckGemini(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();

        var options = geminiOptions.Value;
        var client = httpClientFactory.CreateClient("Gemini");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1beta/models/{options.Model}");
        request.Headers.Add("x-goog-api-key", options.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return Ok(new { status = "OK", model = options.Model, key = Mask(options.ApiKey) });

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