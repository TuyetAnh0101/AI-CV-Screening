using System.Diagnostics;
using System.Text.Json;

namespace SmartCv.Infrastructure.Ai;

/// <summary>Only trusted server code establishes the operation. Never accept it from HTTP input.</summary>
public static class GeminiTelemetryScope
{
    private sealed record Frame(string Source, int? RunId, int? EvaluationId, Frame? Parent);
    private static readonly AsyncLocal<Frame?> Current = new();
    public static (string Source, int? RunId, int? EvaluationId) Snapshot =>
        Current.Value is { } value ? (value.Source, value.RunId, value.EvaluationId) : ("Unknown", null, null);

    public static IDisposable Begin(string source, int? runId = null, int? evaluationId = null)
    {
        if (source is not ("Screening" or "PromptTest" or "Preview"))
            throw new ArgumentOutOfRangeException(nameof(source));
        var item = new Frame(source, runId, evaluationId, Current.Value);
        Current.Value = item;
        return new Restore(item);
    }

    private sealed class Restore(Frame frame) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(Current.Value, frame)) Current.Value = frame.Parent;
        }
    }
}

public sealed record GeminiTokenUsage(
    int? PromptTokens, int? CandidateTokens, int? ThoughtsTokens,
    int? CachedTokens, int? TotalTokens);

public sealed record GeminiInvocation(
    DateTime OccurredAtUtc, string Model, string Source, int? RunId, int? EvaluationId,
    int Attempt, int DurationMs, int? HttpStatus, string Outcome, GeminiTokenUsage? Usage);

/// <summary>Pure JSON parser. Missing metadata is NULL, never an invented zero.</summary>
public static class GeminiUsageParser
{
    public static GeminiTokenUsage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("usageMetadata", out var usage) ||
                usage.ValueKind != JsonValueKind.Object) return null;
            var result = new GeminiTokenUsage(
                Read(usage, "promptTokenCount"), Read(usage, "candidatesTokenCount"),
                Read(usage, "thoughtsTokenCount"), Read(usage, "cachedContentTokenCount"),
                Read(usage, "totalTokenCount"));
            return result.PromptTokens is null && result.CandidateTokens is null &&
                   result.ThoughtsTokens is null && result.CachedTokens is null &&
                   result.TotalTokens is null ? null : result;
        }
        catch (JsonException) { return null; }
    }

    private static int? Read(JsonElement json, string key) =>
        json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) && count >= 0 ? count : null;
}

public interface IGeminiInvocationRecorder
{
    Task RecordAsync(GeminiInvocation invocation, CancellationToken ct);
}
