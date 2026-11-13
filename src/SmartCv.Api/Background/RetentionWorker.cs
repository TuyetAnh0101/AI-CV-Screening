using Microsoft.Extensions.Options;
using SmartCv.Api.Endpoints;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Api.Background;

public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, IOptions<CvUploadEndpointOptions> endpoint, IHostEnvironment environment, ILogger<RetentionWorker> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoAnonymize || !environment.IsDevelopment() || !endpoint.Value.Enabled)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.CheckIntervalHours), clock);
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionProcessor>().RunOnceAsync(DateTime.UtcNow, false, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Quét hạn lưu chưa hoàn tất ({ErrorType}); cần kiểm tra trước lần chạy sau", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
