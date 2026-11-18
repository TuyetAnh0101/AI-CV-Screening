using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;

namespace SmartCv.Api.Endpoints;

/// <summary>Bounded JSON parsing keeps malformed A24 writes inside the safe ProblemDetails pipeline.</summary>
internal static class CandidateRequestReader
{
    private const int MaxBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<(T? Request, ProblemHttpResult? Error)> ReadAsync<T>(HttpRequest http, CancellationToken ct) where T : class
    {
        if (!http.HasJsonContentType())
            return (null, TypedResults.Problem(title: "Cần gửi JSON", statusCode: 415));
        if (http.ContentLength > MaxBytes) return (null, CandidateEndpoints.InvalidBody());
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await http.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxBytes) return (null, CandidateEndpoints.InvalidBody());
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, CandidateEndpoints.InvalidBody());
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.EnumerateObject().Any(p => !keys.Add(p.Name))) return (null, CandidateEndpoints.InvalidBody());
            var value = document.RootElement.Deserialize<T>(Json);
            return value is null ? (null, CandidateEndpoints.InvalidBody()) : (value, null);
        }
        catch (JsonException) { return (null, CandidateEndpoints.InvalidBody()); }
    }
}
