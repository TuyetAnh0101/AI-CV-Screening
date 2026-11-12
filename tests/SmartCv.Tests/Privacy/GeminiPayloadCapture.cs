using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SmartCv.Tests;

/// <summary>
/// A37: HttpMessageHandler giả thay Gemini. Ghi lại NGUYÊN VĂN mỗi payload gửi đi (không có request mạng thật, không tốn quota),
/// trả lời theo kịch bản (mặc định: JSON hợp lệ).
/// </summary>
internal sealed class GeminiPayloadCapture(Func<int, HttpResponseMessage>? respond = null) : HttpMessageHandler
{
    public const string AiJson = """{"score":75,"matchedSkills":[],"missingSkills":[],"yearsExperience":2,"gpa":3.5,"reasoning":"Ứng viên phù hợp."}""";

    private readonly List<string> _bodies = [];
    public IReadOnlyList<string> Bodies { get { lock (_bodies) return _bodies.ToList(); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        int n;
        lock (_bodies) { _bodies.Add(body); n = _bodies.Count; }
        return respond?.Invoke(n) ?? Ok();
    }

    public static HttpResponseMessage Ok(string aiJson = AiJson) => Json(HttpStatusCode.OK, new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["finishReason"] = "STOP",
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = aiJson }) }
        })
    }.ToJsonString());

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Phần "user" (job + CV) của payload, đã giải mã JSON (chữ tiếng Việt trong payload bị escape \uXXXX).</summary>
    public static string UserText(string body) => JsonNode.Parse(body)!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();

    /// <summary>Phần "system" của payload: SystemPrompt của phiên bản prompt.</summary>
    public static string SystemText(string body) => JsonNode.Parse(body)!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
}

/// <summary>Logger ghi lại mức và nội dung từng dòng log để kiểm tra không lộ dữ liệu cá nhân.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    public IReadOnlyList<(LogLevel Level, string Message)> Entries { get { lock (_entries) return _entries.ToList(); } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception) + exception));
    }
}
