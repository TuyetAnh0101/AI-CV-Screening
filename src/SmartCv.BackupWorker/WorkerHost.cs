using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartCv.Infrastructure.Backup;
using SmartCv.Infrastructure.Data;

namespace SmartCv.BackupWorker;

public static class WorkerServices
{
    /// <summary>Same Data Protection application as the API (read-only here) so the Drive credential can be decrypted.</summary>
    public static IServiceCollection AddBackupWorker(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("SmartCvDb") ?? throw new InvalidOperationException("ConnectionStrings:SmartCvDb is required.");
        var keys = config["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(keys) || !Path.IsPathFullyQualified(keys))
            throw new InvalidOperationException("DataProtection:KeysPath must be the API's key ring (absolute path).");
        var options = config.GetSection(BackupWorkerOptions.SectionName).Get<BackupWorkerOptions>() ?? new();
        services.AddDataProtection().SetApplicationName("SmartCv.Api").PersistKeysToFileSystem(new DirectoryInfo(keys)).DisableAutomaticKeyGeneration();
        services.AddHttpClient(GoogleDriveStoreFactory.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BackupDriveCredentials>();
        services.AddSingleton<IBackupCloudStoreFactory, GoogleDriveStoreFactory>();
        services.AddSingleton<IBackupKeyProvider>(_ => OperatingSystem.IsWindows()
            ? new DpapiBackupKeyProvider(options.KeyDirectory) : throw new PlatformNotSupportedException("The backup key uses Windows DPAPI."));
        var contextOptions = new DbContextOptionsBuilder<SmartCvDbContext>().UseSqlServer(connection).Options;
        services.AddSingleton(sp => new BackupEngineContext(options, connection, () => new SmartCvDbContext(contextOptions),
            sp.GetRequiredService<IBackupKeyProvider>(), sp.GetRequiredService<IBackupCloudStoreFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("SCApp.Backup")));
        services.AddSingleton<BackupCoordinator>();
        return services;
    }
}

/// <summary>Ticks forever; one failing tick is logged and the next one still runs.</summary>
public sealed class BackupLoop(BackupCoordinator coordinator, BackupWorkerOptions options, ILogger<BackupLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SCApp backup worker started (role {Role})", options.Role);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await coordinator.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { logger.LogError("Backup worker tick failed: {Type}", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
