using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SmartCv.Core.Workspaces;
using SmartCv.Infrastructure.Workspaces;

namespace SmartCv.Infrastructure.Ai;

/// <summary>
/// Isolated SQL connection: never calls SaveChanges on a scoring request's DbContext.
/// A failed telemetry insert is reported by the evaluator but does not retry the billable AI request.
/// No prompt, CV, response body, bearer token or API key is stored.
/// </summary>
public sealed class SqlGeminiInvocationRecorder(
    IConfiguration configuration,
    IWorkspaceContext? workspace = null) : IGeminiInvocationRecorder
{
    public async Task RecordAsync(GeminiInvocation value, CancellationToken ct)
    {
        var workspaceId = (workspace ?? WorkspaceScope.Ambient).WorkspaceId;
        if (workspaceId is null or <= 0)
            throw new InvalidOperationException("Telemetry has no validated workspace context.");
        if (value.Model.Length is < 1 or > 120 ||
            value.Source is not ("Screening" or "PromptTest" or "Preview" or "Unknown") ||
            value.Attempt is < 1 or > 100 || value.DurationMs < 0)
            throw new ArgumentException("Invalid telemetry dimensions.");
        var cs = configuration.GetConnectionString("SmartCvDb")
            ?? throw new InvalidOperationException("Missing SmartCvDb connection string.");

        // Rates are supplied by the operator from the published model-specific price list.
        // Missing prices -> NULL, not $0. Prices are snapped at event time.
        var price = GeminiCostEstimate.TryEstimate(configuration, value.Model, value.Usage);

        const string sql = """
            INSERT INTO dbo.GeminiInvocationLogs
              (WorkspaceId, Source, RunId, EvaluationId, ModelName, AttemptNumber,
               OccurredAtUtc, DurationMs, HttpStatus, Outcome,
               PromptTokens, CandidateTokens, ThoughtsTokens, CachedTokens, TotalTokens,
               EstimatedCostUsd, PricingDescription)
            VALUES
              (@WorkspaceId, @Source, @RunId, @EvaluationId, @ModelName, @AttemptNumber,
               @OccurredAtUtc, @DurationMs, @HttpStatus, @Outcome,
               @PromptTokens, @CandidateTokens, @ThoughtsTokens, @CachedTokens, @TotalTokens,
               @EstimatedCostUsd, @PricingDescription)
            """;

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var command = new SqlCommand(sql, conn) { CommandTimeout = 5 };
        Add(command, "@WorkspaceId", SqlDbType.Int, workspaceId.Value);
        Add(command, "@Source", SqlDbType.NVarChar, value.Source, 16);
        Add(command, "@RunId", SqlDbType.Int, value.RunId);
        Add(command, "@EvaluationId", SqlDbType.Int, value.EvaluationId);
        Add(command, "@ModelName", SqlDbType.NVarChar, value.Model, 120);
        Add(command, "@AttemptNumber", SqlDbType.Int, value.Attempt);
        Add(command, "@OccurredAtUtc", SqlDbType.DateTime2, value.OccurredAtUtc);
        Add(command, "@DurationMs", SqlDbType.Int, value.DurationMs);
        Add(command, "@HttpStatus", SqlDbType.Int, value.HttpStatus);
        Add(command, "@Outcome", SqlDbType.NVarChar, value.Outcome, 24);
        Add(command, "@PromptTokens", SqlDbType.Int, value.Usage?.PromptTokens);
        Add(command, "@CandidateTokens", SqlDbType.Int, value.Usage?.CandidateTokens);
        Add(command, "@ThoughtsTokens", SqlDbType.Int, value.Usage?.ThoughtsTokens);
        Add(command, "@CachedTokens", SqlDbType.Int, value.Usage?.CachedTokens);
        Add(command, "@TotalTokens", SqlDbType.Int, value.Usage?.TotalTokens);
        var costParam = new SqlParameter("@EstimatedCostUsd", SqlDbType.Decimal)
            { Precision = 18, Scale = 8, Value = (object?)price.CostUsd ?? DBNull.Value };
        command.Parameters.Add(costParam);
        Add(command, "@PricingDescription", SqlDbType.NVarChar, price.Description, 160);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Add(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = size > 0 ? new SqlParameter(name, type, size) : new SqlParameter(name, type);
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}

public static class GeminiCostEstimate
{
    public static (decimal? CostUsd, string? Description) TryEstimate(
        IConfiguration cfg, string model, GeminiTokenUsage? usage)
    {
        if (usage?.PromptTokens is not { } prompt || usage.CandidateTokens is not { } output)
            return (null, null);
        var path = cfg.GetSection("AiTelemetry:Pricing").GetSection(model);
        decimal? Rate(string name) => decimal.TryParse(path[name], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
        var inputRate = Rate("InputUsdPerMillion");
        var outputRate = Rate("OutputUsdPerMillion");
        var cachedRate = Rate("CachedInputUsdPerMillion");
        var description = path["PriceVersion"];
        if (inputRate is null || outputRate is null || string.IsNullOrWhiteSpace(description))
            return (null, null);
        if (description.Length > 160) return (null, null);
        var cached = Math.Min(prompt, usage.CachedTokens ?? 0);
        if (cached != 0 && cachedRate is null) return (null, null);
        var thoughts = usage.ThoughtsTokens ?? 0; // absent when provider doesn't report thinking separately
        var result = ((prompt - cached) * inputRate.Value + cached * (cachedRate ?? 0m)
                      + (output + (decimal)thoughts) * outputRate.Value) / 1_000_000m;
        return (Math.Round(result, 8, MidpointRounding.AwayFromZero), description);
    }
}
