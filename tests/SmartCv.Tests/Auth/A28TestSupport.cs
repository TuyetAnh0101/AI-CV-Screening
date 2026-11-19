using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartCv.Api.Auth;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Data;

namespace SmartCv.Tests.Auth;

internal static class A28TestSupport
{
    internal static string Key => StartupGatedApiFactory.JwtTestKey;
    internal static string Password() => "Synthetic9" + Guid.NewGuid().ToString("N");
    internal static JwtOptions Settings(string? key = null) => new() { Key = key ?? Key };
    internal static string Token(User user, TimeProvider? time = null, JwtOptions? settings = null) =>
        new JwtTokenService(Options.Create(settings ?? Settings()), time ?? TimeProvider.System).Create(user).Token;

    internal static WebApplicationFactory<Program> Factory(Dictionary<string, string?>? settings = null, DataFault? fault = null, LogCapture? logs = null)
    {
        var config = new Dictionary<string, string?> { ["Auth:LoginAttemptsPerMinute"] = "100" };
        if (settings is not null) foreach (var pair in settings) config[pair.Key] = pair.Value;
        return TestSupport.ApiFactory(settings: config).WithWebHostBuilder(builder =>
        {
            if (logs is not null) builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(logs); });
            if (fault is not null)
            {
                var name = Guid.NewGuid().ToString("N");
                var readFault = new ReadFault(fault);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<SmartCvDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<SmartCvDbContext>>();
                    services.AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase(name).AddInterceptors(fault, readFault));
                });
            }
        });
    }

    internal static async Task<User> Seed(WebApplicationFactory<Program> factory, string password, string role = "Admin", bool active = true, bool mustChange = false, bool oldHash = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartCvDbContext>();
        var user = new User { Email = Guid.NewGuid().ToString("N") + "@example.invalid", FullName = "Tài khoản tổng hợp", Role = role, IsActive = active, MustChangePassword = mustChange, CreatedAt = DateTime.UtcNow };
        var hasher = oldHash ? new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 10000 })) : new PasswordHasher<User>();
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Add(user); await db.SaveChangesAsync();
        return user;
    }

    internal static async Task WithDb(WebApplicationFactory<Program> factory, Func<SmartCvDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<SmartCvDbContext>());
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }

    internal sealed class SyntheticDatabaseException() : DbException("SYNTHETIC_PRIVATE_SQL_ERROR") { }
    internal sealed class DataFault : SaveChangesInterceptor
    {
        internal bool FailRead;
        internal bool WrapReadFailure;
        internal bool FailSave;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            FailSave ? throw new DbUpdateException("SYNTHETIC_PRIVATE_SQL_ERROR") : ValueTask.FromResult(result);
    }
    private sealed class ReadFault(DataFault fault) : IQueryExpressionInterceptor
    {
        public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        {
            if (!fault.FailRead) return queryExpression;
            if (fault.WrapReadFailure) throw new InvalidOperationException("Synthetic provider wrapper", new SyntheticDatabaseException());
            throw new SyntheticDatabaseException();
        }
    }

    internal sealed class LogCapture : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, error));
        }
    }
}
