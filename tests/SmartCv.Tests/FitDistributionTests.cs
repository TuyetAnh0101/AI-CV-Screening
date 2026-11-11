extern alias web;

using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Components.Results;
using web::SmartCv.Web.Helpers;

namespace SmartCv.Tests;

public class FitDistributionTests
{
    private static RankingItemDto Item(int id, decimal? score, string status = EvaluationStatuses.Completed,
        string source = ScoreSources.Combined) => new()
    {
        EvaluationId = id, CvId = new Guid(id, 0, 0, new byte[8]), Status = status,
        FinalScore = score, FitLabel = FitLevel.FromScore(score), ScoreSource = source
    };

    private static RankingResponse Ranking(params RankingItemDto[] items) => new()
    {
        JobId = 2, RunId = 3, RunStatus = RunStatuses.Running, PassThreshold = 80, PassCount = items.Length == 0 ? 0 : 1,
        Items = [.. items], LevelCounts = FitLevel.All.ToDictionary(l => l,
            l => items.Count(i => RankingView.IsScored(i) && i.FitLabel == l))
    };

    [Fact]
    public void Four_levels_errors_pending_and_keyword_only_are_disjoint()
    {
        var ranking = Ranking(Item(1, 85), Item(2, 84.99m), Item(3, 55, source: ScoreSources.KeywordOnly),
            Item(4, 0), Item(5, 99, EvaluationStatuses.Failed), Item(6, null, EvaluationStatuses.Pending));
        var view = FitDistributionView.From(ranking, []);
        Assert.False(view.Reconciled);
        Assert.Equal(6, view.Total);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1 }, view.Segments.Select(s => s.Count));
        Assert.Equal("Đang chấm", view.Segments.Last().Label);
    }

    [Fact]
    public void Extraction_failure_overrides_score_and_duplicate_cv_is_counted_once()
    {
        var item = Item(1, 90);
        var ranking = Ranking(item, item, Item(2, 54.99m));
        var view = FitDistributionView.From(ranking,
            [new() { EvaluationId = 1, JobId = 2, ExtractionStatus = ExtractionStatuses.Failed }]);
        Assert.True(view.Reconciled);
        Assert.Equal(2, view.Total);
        Assert.Equal(0, view.Segments[0].Count);
        Assert.Equal(1, view.Segments[3].Count);
        Assert.Equal(1, view.Segments.Last().Count);
        Assert.Equal(view.Total, view.Segments.Sum(s => s.Count));
    }

    [Fact]
    public void Terminal_run_without_score_is_not_labelled_as_still_processing()
    {
        var view = FitDistributionView.From(new RankingResponse
        {
            RunStatus = RunStatuses.Failed, Items = [Item(1, null, EvaluationStatuses.Pending)]
        }, []);
        Assert.Equal("Chưa có điểm", view.Segments.Last().Label);
        Assert.All(view.Segments.Take(4), s => Assert.Equal(0, s.Count));
    }

    private static async Task<string> Html(RankingResponse ranking)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<FitDistribution>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(FitDistribution.Ranking)] = ranking }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    [Fact]
    public async Task Component_renders_real_threshold_counts_percentages_and_accessible_bar()
    {
        var html = await Html(Ranking(Item(1, 90), Item(2, 60), Item(3, 10), Item(4, null, EvaluationStatuses.Failed)));
        Assert.Contains("Ngưỡng Đạt 80", html);
        Assert.Contains("1/4 CV dự đoán Đạt", html);
        Assert.Contains("25%", html);
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("Tổng 4 CV", html);
        Assert.Contains("is-error", html);
        Assert.Contains("Phù hợp", html);
        Assert.Contains("0%", html);
        Assert.DoesNotContain("is-pending", html);
        Assert.Contains("flex: 1 1 0", html);
    }

    [Fact]
    public async Task Empty_component_keeps_four_zero_legends_without_bar()
    {
        var html = await Html(Ranking());
        Assert.Contains("Chưa có CV nào", html);
        foreach (var level in FitLevel.All) Assert.Contains(level, html);
        Assert.DoesNotContain("role=\"img\"", html);
        Assert.Contains("0/0 CV dự đoán Đạt", html);
    }
}
