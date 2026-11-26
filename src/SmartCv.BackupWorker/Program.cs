using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.EventLog;
using SmartCv.BackupWorker;
using SmartCv.Infrastructure.Backup;

// SCApp backup worker (A62). Commands: run (default, Windows Service) | once | check | cv-report | verify-key |
// init-key [--import [--retired] | --rotate] | sql-grants <database> <backupAccount> <restoreAccount> [--restore-claim].
// Option: --config <settings.json>
const string EventSource = "SCApp Backup Worker";
var arguments = args.ToList();
var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.BackupWorker.json");
var index = arguments.IndexOf("--config");
if (index >= 0 && index + 1 < arguments.Count) { configPath = Path.GetFullPath(arguments[index + 1]); arguments.RemoveRange(index, 2); }
var command = arguments.Count > 0 ? arguments[0] : "run";

if (command == "sql-grants")
{
    var claim = arguments.Remove("--restore-claim");
    if (arguments.Count != 4) { Console.WriteLine("sql-grants <database> <backupAccount> <restoreAccount> [--restore-claim]"); return 1; }
    return WorkerCommands.PrintSqlGrants(arguments[1], arguments[2], arguments[3], claim);
}

if (!File.Exists(configPath)) { Console.WriteLine("Settings file not found: " + configPath); return 1; }
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: false).AddEnvironmentVariables("SCAPP_BACKUP_");

if (command is "init-key" or "verify-key")
{
    var options = builder.Configuration.GetSection(BackupWorkerOptions.SectionName).Get<BackupWorkerOptions>() ?? new();
    return command == "verify-key"
        ? WorkerCommands.VerifyKey(options)
        : WorkerCommands.InitKey(options, arguments.Contains("--import"), arguments.Contains("--rotate"), arguments.Contains("--retired"));
}

builder.Services.AddBackupWorker(builder.Configuration);
if (command == "run")
{
    // The installer registers this event source; the service account is not allowed to create it itself.
    if (OperatingSystem.IsWindows())
        builder.Services.Configure<EventLogSettings>(o => { if (OperatingSystem.IsWindows()) o.SourceName = EventSource; });
    builder.Services.AddWindowsService(o => o.ServiceName = EventSource);
    builder.Services.AddHostedService<BackupLoop>();
}
using var host = builder.Build();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
switch (command)
{
    case "run": await host.RunAsync(); return 0;
    case "once": await host.Services.GetRequiredService<BackupCoordinator>().TickAsync(cancel.Token); return 0;
    case "check": return await WorkerCommands.CheckAsync(host.Services, cancel.Token);
    case "cv-report": return await WorkerCommands.CvReportAsync(host.Services, cancel.Token);
    default: Console.WriteLine("Commands: run | once | check | cv-report | verify-key | init-key | sql-grants"); return 1;
}
