using Microsoft.Extensions.Options;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Api.Background;

public sealed class EmailWorker(IServiceScopeFactory factory, IOptions<EmailOptions> options,
    TimeProvider clock, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            logger.LogInformation("EmailWorker disabled by Email:WorkerEnabled=false.");
            return;
        }
        var poll = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = factory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<EmailDispatchProcessor>();
                await processor.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Không ghi nội dung exception: SMTP error có thể chứa email/URL.
                logger.LogWarning("EmailWorker cycle failed. ErrorType={ErrorType}", error.GetType().Name);
            }
            try { await Task.Delay(poll, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
