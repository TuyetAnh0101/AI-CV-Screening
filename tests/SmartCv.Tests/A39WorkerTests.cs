using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Api.Background;
using SmartCv.Api.Endpoints;
using SmartCv.Core.Options;
using SmartCv.Core.Storage;
using SmartCv.Infrastructure.Data;
using SmartCv.Infrastructure.Privacy;

namespace SmartCv.Tests;

public class A39WorkerTests
{
    [Fact]
    public async Task Scheduled_worker_waits_a_minute_retries_next_interval_after_failure_and_stops_on_cancellation()
    {
        using var services = new ServiceCollection().AddLogging().AddDbContext<SmartCvDbContext>(o => o.UseInMemoryDatabase("A39-worker-empty-" + Guid.NewGuid())).AddSingleton<ICvFileStorage>(TestSupport.Storage(TestSupport.TempRoot())).AddSingleton(Options.Create(new RetentionOptions())).AddScoped<CvAnonymizer>().AddScoped<RetentionProcessor>().BuildServiceProvider();
        var scopes = new CountingScopes(services.GetRequiredService<IServiceScopeFactory>(), failFirst: true);
        var clock = new ManualClock();
        using var worker = new RetentionWorker(scopes, Options.Create(new RetentionOptions { AutoAnonymize = true }), Options.Create(new CvUploadEndpointOptions { Enabled = true }), new HostEnvironment("Development"), NullLogger<RetentionWorker>.Instance, clock);
        await worker.StartAsync(default);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scopes.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        await scopes.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromHours(24));
        await scopes.Second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(2, scopes.Calls);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false, "Development", true)]
    [InlineData(true, "Production", true)]
    [InlineData(true, "Development", false)]
    public async Task Disabled_or_wrong_environment_worker_never_opens_a_scope(bool auto, string environment, bool endpoint)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopes = new CountingScopes(services.GetRequiredService<IServiceScopeFactory>(), false);
        var clock = new ManualClock();
        using var worker = new RetentionWorker(scopes, Options.Create(new RetentionOptions { AutoAnonymize = auto }), Options.Create(new CvUploadEndpointOptions { Enabled = endpoint }), new HostEnvironment(environment), NullLogger<RetentionWorker>.Instance, clock);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scopes.Calls);
        Assert.False(clock.TimerCreated.Task.IsCompleted);
    }

    private sealed class HostEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "A39Synthetic";
        public string ContentRootPath { get; set; } = TestSupport.TempRoot();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CountingScopes(IServiceScopeFactory inner, bool failFirst) : IServiceScopeFactory
    {
        public int Calls;
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceScope CreateScope()
        {
            var call = Interlocked.Increment(ref Calls);
            if (call == 1)
                First.TrySetResult();
            else
                Second.TrySetResult();
            if (call == 1 && failFirst)
                throw new IOException("Synthetic first scan failure");
            return inner.CreateScope();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> timers = [];
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
                return now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period);
                timers.Add(timer);
                TimerCreated.TrySetResult();
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> ready;
            lock (gate)
            {
                now += amount;
                ready = timers.Where(t => !t.Disposed && t.Next <= now).ToList();
                foreach (var timer in ready)
                    timer.Next = timer.Period > TimeSpan.Zero ? now + timer.Period : DateTimeOffset.MaxValue;
            }

            foreach (var timer in ready)
                timer.Fire();
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualClock clock;
            private readonly TimerCallback callback;
            private readonly object? state;
            public bool Disposed;
            public DateTimeOffset Next;
            public TimeSpan Period;
            public ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            {
                this.clock = clock;
                this.callback = callback;
                this.state = state;
                Change(due, period);
            }

            public void Fire()
            {
                if (!Disposed)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    if (Disposed)
                        return false;
                    Next = dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : clock.now + dueTime;
                    Period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (clock.gate)
                    Disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
