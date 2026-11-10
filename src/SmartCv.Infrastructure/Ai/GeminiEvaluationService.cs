using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Core.Ai;
using SmartCv.Infrastructure.Gemini;

namespace SmartCv.Infrastructure.Ai;

/// <summary>
/// A10: gọi Gemini (REST generateContent), nhận JSON CÓ CẤU TRÚC, thử lại khi lỗi tạm thời.
/// - Thử lại (tối đa MaxRetries lần): 429, 500/502/503/504, mất mạng, quá thời gian, JSON hỏng.
///   Chờ theo retryDelay / Retry-After của Gemini nếu có (đúng giá trị server yêu cầu); nếu không thì lùi theo hàm mũ
///   2s, 4s... cộng jitter 0–25% (tránh nhiều yêu cầu thử lại cùng lúc khi Gemini quá tải); không vượt MaxRetryDelaySeconds.
/// - Không thử lại: 400/401/403/404, nội dung bị chặn. Người gọi hủy thì dừng ngay (ném OperationCanceledException).
/// - Phân loại: 503 = Overloaded (quá tải), 500/502/504 và mất kết nối = Network, hết thời gian chờ = Timeout,
///   404 = ModelUnavailable (model không tồn tại / đã bị gỡ), 429 = QuotaExceeded.
/// - Model gọi luôn là request.Model do người gọi truyền vào (lượt chấm dùng model đã chụp); service KHÔNG tự đổi model.
/// - Hết lượt thử: trả Failure, không bao giờ tạo điểm giả.
/// Không ghi log API key, nội dung CV hay JSON của AI.
/// </summary>
public sealed class GeminiEvaluationService(
    HttpClient http,
    GeminiRateLimiter rateLimiter,
    IOptions<GeminiOptions> options,
    ILogger<GeminiEvaluationService> logger) : IAiEvaluator
{
    private const decimal MaxYears = 60m;
    private const decimal MaxGpa = 4m;

    private readonly GeminiOptions _options = options.Value;

    /// <summary>Cách chờ trước khi thử lại; test thay bằng hàm ghi lại thời gian để không phải chờ thật.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Số ngẫu nhiên 0–1 cho jitter; test thay bằng giá trị cố định.</summary>
    public Func<double> NextJitter { get; init; } = static () => Random.Shared.NextDouble();

    /// <summary>Đồng hồ để đổi Retry-After dạng ngày giờ thành thời gian chờ; test thay bằng giờ cố định.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    private const double MaxJitterFraction = 0.25;

    public async Task<AiEvaluationResult> EvaluateAsync(AiEvaluationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return AiEvaluationResult.Failure(AiErrorKind.Configuration,
                "Chưa cấu hình Gemini API key (User Secrets \"Gemini:ApiKey\")", attempts: 0);

        var body = BuildRequestBody(request);
        var url = $"models/{Uri.EscapeDataString(request.Model)}:generateContent";
        var maxAttempts = Math.Max(1, _options.MaxRetries + 1);
        AiEvaluationResult? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await rateLimiter.WaitTurnAsync(ct);
            TimeSpan? serverDelay = null;

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                message.Headers.Add("x-goog-api-key", _options.ApiKey);   // gửi key qua header, không để lộ trên URL

                using var response = await http.SendAsync(message, ct);
                var text = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    last = ParseSuccess(text, request, attempt);
                    if (last.IsSuccess || last.ErrorKind == AiErrorKind.Blocked) return last;
                }
                else
                {
                    (last, serverDelay, var retryable) = ParseError(response.StatusCode, text, RetryAfter(response), attempt);
                    if (!retryable)
                    {
                        logger.LogError("Gemini {Model} lỗi không thử lại ({Kind}): {Message}", request.Model, last.ErrorKind, last.ErrorMessage);
                        return last;
                    }
                }
            }
            catch (HttpRequestException)
            {
                last = AiEvaluationResult.Failure(AiErrorKind.Network, "Không kết nối được Gemini", attempt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient hết thời gian chờ. Nếu người gọi hủy thì ct đã bật và ngoại lệ được ném tiếp.
                last = AiEvaluationResult.Failure(AiErrorKind.Timeout, $"Gemini không phản hồi sau {_options.TimeoutSeconds} giây", attempt);
            }

            if (attempt == maxAttempts) break;

            var delay = serverDelay ?? Backoff(attempt);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, _options.MaxRetryDelaySeconds));
            logger.LogWarning("Gemini {Model} lỗi lần {Attempt}/{Max} ({Kind}: {Message}). Thử lại sau {Delay:0.#} giây",
                request.Model, attempt, maxAttempts, last!.ErrorKind, last.ErrorMessage, delay.TotalSeconds);
            await DelayAsync(delay, ct);
        }

        var reason = last!.ErrorKind == AiErrorKind.QuotaExceeded
            ? $"Gemini vượt hạn mức sau {maxAttempts} lần thử lại"
            : $"Gemini lỗi sau {maxAttempts} lần thử ({last.ErrorMessage})";
        logger.LogError("Bỏ cuộc gọi Gemini {Model} ({Kind}): {Reason}", request.Model, last.ErrorKind, reason);
        return last with { ErrorMessage = reason, Attempts = maxAttempts };
    }

    /// <summary>Lùi theo hàm mũ 2s, 4s, 8s... cộng jitter 0–25%.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(seconds * (1 + MaxJitterFraction * Math.Clamp(NextJitter(), 0, 1)));
    }

    /// <summary>Header Retry-After: số giây hoặc ngày giờ (RFC 7231). Giá trị đã qua hoặc không hợp lệ thì bỏ qua.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - UtcNow();
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    // ---------- Nội dung gửi đi ----------
    private object BuildRequestBody(AiEvaluationRequest request)
    {
        var codes = request.Job.Skills.Select(s => s.SkillCode).ToArray();

        // Mảng mã kỹ năng: chỉ được chọn trong danh sách mã của job
        object SkillArray(string description) => codes.Length > 0
            ? new { type = "ARRAY", description, items = new { type = "STRING", @enum = codes } }
            : new { type = "ARRAY", description, items = new { type = "STRING" } };

        return new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = AiPromptBuilder.BuildUserPrompt(request.Job, request.CvText, _options.MaxCvCharacters) } }
                }
            },
            generationConfig = new
            {
                temperature = request.Temperature,
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["score"] = new { type = "INTEGER", description = "Mức phù hợp 0-100" },
                        ["matchedSkills"] = SkillArray("Mã các kỹ năng CV có"),
                        ["missingSkills"] = SkillArray("Mã các kỹ năng CV không có"),
                        ["yearsExperience"] = new { type = "NUMBER", nullable = true, description = "Số năm kinh nghiệm; null nếu CV không ghi" },
                        ["gpa"] = new { type = "NUMBER", nullable = true, description = "GPA thang 4; null nếu CV không ghi" },
                        ["reasoning"] = new { type = "STRING", description = "Tiếng Việt, tối đa 3 câu" }
                    },
                    required = new[] { "score", "matchedSkills", "missingSkills", "reasoning" },
                    propertyOrdering = new[] { "score", "matchedSkills", "missingSkills", "yearsExperience", "gpa", "reasoning" }
                }
            }
        };
    }

    // ---------- Đọc kết quả: candidates[0].content.parts[].text là chuỗi JSON theo responseSchema ----------
    private AiEvaluationResult ParseSuccess(string responseText, AiEvaluationRequest request, int attempt)
    {
        try
        {
            var root = JsonNode.Parse(responseText);

            var blockReason = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
            if (blockReason is not null)
                return AiEvaluationResult.Failure(AiErrorKind.Blocked, $"Gemini từ chối nội dung ({blockReason})", attempt);

            var candidate = root?["candidates"] is JsonArray { Count: > 0 } candidates ? candidates[0] : null;
            var finish = candidate?["finishReason"]?.GetValue<string>();
            if (finish is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII")
                return AiEvaluationResult.Failure(AiErrorKind.Blocked, $"Gemini dừng trả lời ({finish})", attempt);

            var json = string.Concat(candidate?["content"]?["parts"]?.AsArray()
                .Select(p => p?["text"]?.GetValue<string>() ?? string.Empty) ?? []);
            if (string.IsNullOrWhiteSpace(json))
                return Invalid($"Gemini trả về rỗng (finishReason = {finish})", attempt, responseText);

            return JsonNode.Parse(json) is JsonObject data
                ? Validate(data, json, request, attempt)
                : Invalid("JSON không phải đối tượng", attempt, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Không đưa ex.Message vào kết quả: có thể chứa một phần nội dung AI trả về
            return Invalid("JSON không hợp lệ hoặc sai kiểu dữ liệu", attempt, responseText);
        }
    }

    private AiEvaluationResult Validate(JsonObject data, string json, AiEvaluationRequest request, int attempt)
    {
        var score = ReadNumber(data["score"]);
        if (score is null) return Invalid("JSON thiếu trường score", attempt, json);
        if (score is < 0m or > 100m) return Invalid("Điểm AI nằm ngoài khoảng 0-100", attempt, json);

        var reasoning = data["reasoning"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(reasoning)) return Invalid("JSON thiếu trường reasoning", attempt, json);

        var years = ReadNumber(data["yearsExperience"]);
        if (years is < 0m or > MaxYears) return Invalid("Số năm kinh nghiệm không hợp lệ", attempt, json);

        var gpa = ReadNumber(data["gpa"]);
        if (gpa is < 0m or > MaxGpa) return Invalid("GPA không nằm trong thang 4", attempt, json);

        var canonical = request.Job.Skills.ToDictionary(s => s.SkillCode, s => s.SkillCode, StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();
        var matched = ReadCodes(data, "matchedSkills", canonical, invalid);
        var missing = ReadCodes(data, "missingSkills", canonical, invalid);

        if (matched.Intersect(missing).Any())
            return Invalid("Một kỹ năng vừa nằm trong matchedSkills vừa nằm trong missingSkills", attempt, json);

        if (invalid.Count > 0)
            logger.LogWarning("Gemini trả {Count} mã kỹ năng không thuộc job, đã loại", invalid.Count);

        return new AiEvaluationResult
        {
            IsSuccess = true,
            Score = Math.Round(score.Value, 2, MidpointRounding.AwayFromZero),
            MatchedSkillCodes = matched,
            MissingSkillCodes = missing,
            InvalidSkillCodes = invalid.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            YearsExperience = years is null ? null : Math.Round(years.Value, 1, MidpointRounding.AwayFromZero),
            Gpa = gpa,
            Reasoning = reasoning,
            RawResponse = json,
            Attempts = attempt,
            CvTruncated = AiPromptBuilder.IsTruncated(request.CvText, _options.MaxCvCharacters)
        };
    }

    private static AiEvaluationResult Invalid(string message, int attempt, string? raw) =>
        AiEvaluationResult.Failure(AiErrorKind.InvalidResponse, message, attempt, raw);

    private static List<string> ReadCodes(JsonObject data, string name, Dictionary<string, string> canonical, List<string> invalid)
    {
        var codes = new List<string>();
        foreach (var node in data[name] as JsonArray ?? throw new InvalidOperationException($"Thiếu mảng {name}"))
        {
            var code = (node ?? throw new InvalidOperationException("Mã kỹ năng null")).GetValue<string>().Trim();
            if (!canonical.TryGetValue(code, out var known)) invalid.Add(code);
            else if (!codes.Contains(known)) codes.Add(known);
        }
        return codes;
    }

    /// <summary>Chỉ nhận số JSON thật; null nghĩa là không có giá trị. Sai kiểu thì ném lỗi để coi là JSON không hợp lệ.</summary>
    private static decimal? ReadNumber(JsonNode? node)
    {
        if (node is null) return null;
        return node.GetValueKind() == JsonValueKind.Number
            ? node.GetValue<decimal>()
            : throw new InvalidOperationException("Không phải số");
    }

    // ---------- Đọc lỗi: phân loại, có nên thử lại không, Gemini bảo chờ bao lâu ----------
    private (AiEvaluationResult Result, TimeSpan? Delay, bool Retryable) ParseError(HttpStatusCode status, string body, TimeSpan? retryAfter, int attempt)
    {
        string? apiMessage = null;
        var delay = retryAfter is { } header && header > TimeSpan.Zero ? header : (TimeSpan?)null;

        try
        {
            var error = JsonNode.Parse(body)?["error"];
            apiMessage = error?["message"]?.GetValue<string>();

            // details: [{ "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "37s" }]
            foreach (var detail in error?["details"]?.AsArray() ?? [])
            {
                var value = detail?["retryDelay"]?.GetValue<string>();
                if (value is not null && value.EndsWith('s')
                    && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    delay = TimeSpan.FromSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* nội dung lỗi không đúng định dạng */ }

        var code = (int)status;
        var detailText = apiMessage is null ? $"HTTP {code}" : $"HTTP {code}: {Clean(apiMessage)}";
        var invalidKey = apiMessage?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true
                         || body.Contains("API_KEY_INVALID", StringComparison.Ordinal);

        return code switch
        {
            429 => (AiEvaluationResult.Failure(AiErrorKind.QuotaExceeded, $"Gemini vượt hạn mức ({detailText})", attempt), delay, true),
            503 => (AiEvaluationResult.Failure(AiErrorKind.Overloaded, $"Gemini đang quá tải ({detailText})", attempt), delay, true),
            500 or 502 or 504 => (AiEvaluationResult.Failure(AiErrorKind.Network, $"Máy chủ Gemini lỗi tạm thời ({detailText})", attempt), delay, true),
            400 when invalidKey => (AiEvaluationResult.Failure(AiErrorKind.Configuration, "Gemini API key không hợp lệ", attempt), null, false),
            400 => (AiEvaluationResult.Failure(AiErrorKind.InvalidRequest, $"Gemini từ chối yêu cầu, kiểm tra schema hoặc tham số ({detailText})", attempt), null, false),
            401 or 403 => (AiEvaluationResult.Failure(AiErrorKind.Configuration, $"Không có quyền sử dụng Gemini, kiểm tra API key ({detailText})", attempt), null, false),
            404 => (AiEvaluationResult.Failure(AiErrorKind.ModelUnavailable,
                $"Không tìm thấy model hoặc model không còn khả dụng, kiểm tra Scoring:ModelName ({detailText})", attempt), null, false),
            _ => (AiEvaluationResult.Failure(AiErrorKind.Other, $"Gemini báo lỗi ({detailText})", attempt), null, false)
        };
    }

    /// <summary>Thông báo của Gemini có thể đi vào log và phản hồi: cắt ngắn và che API key nếu bị lặp lại.</summary>
    private string Clean(string message)
    {
        var text = message.Replace(_options.ApiKey, "***", StringComparison.Ordinal);
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
