using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartCv.Core.Email;
using SmartCv.Core.Entities;
using SmartCv.Core.Options;
using SmartCv.Infrastructure.Email;

namespace SmartCv.Tests.Email;

public sealed class A55WorkerTests
{
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void AdvanceMinutes(int minutes) => Now = Now.AddMinutes(minutes);
    }
    private sealed class FakeSender : IEmailSender
    {
        public int Calls;
        public bool Fail;
        public Task SendAsync(long id, EmailMessage msg, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new IOException("secret@example.test must not appear in logs");
            return Task.CompletedTask;
        }
    }
    private sealed class FakeStore : IEmailOutboxStore
    {
        public readonly EmailOutboxItem Row;
        private readonly object gate = new();
        public FakeStore(EmailOutboxItem row) => Row = row;
        public Task<IReadOnlyList<long>> ClaimDueAsync(Guid claim, int n, DateTime now,
            DateTime until, CancellationToken ct)
        {
            lock (gate)
            {
                if (Row.Status != "Pending" || Row.NextAttemptAt > now || Row.LeaseUntilUtc > now)
                    return Task.FromResult<IReadOnlyList<long>>([]);
                Row.ClaimId = claim;
                Row.LeaseUntilUtc = until;
                return Task.FromResult<IReadOnlyList<long>>([Row.EmailOutboxId]);
            }
        }
        public Task<EmailOutboxItem?> ReadClaimedAsync(long id, Guid claim, CancellationToken ct)
        {
            lock (gate) return Task.FromResult<EmailOutboxItem?>(Row.ClaimId == claim ? Row : null);
        }
        public Task<bool> MarkSentAsync(long id, Guid claim, DateTime now, CancellationToken ct)
        {
            lock (gate)
            {
                if (Row.ClaimId != claim) return Task.FromResult(false);
                Row.Status = "Sent"; Row.SentAt = now; Row.ProtectedPayload = null;
                Row.ClaimId = null; Row.LeaseUntilUtc = null;
                return Task.FromResult(true);
            }
        }
        public Task<bool> MarkFailureAsync(long id, Guid claim, byte n, DateTime next, bool terminal,
            string code, CancellationToken ct)
        {
            lock (gate)
            {
                if (Row.ClaimId != claim) return Task.FromResult(false);
                Row.Attempts = n; Row.NextAttemptAt = next; Row.LastError = code;
                Row.Status = terminal ? "Failed" : "Pending";
                if (terminal) Row.ProtectedPayload = null;
                Row.ClaimId = null; Row.LeaseUntilUtc = null;
                return Task.FromResult(true);
            }
        }
    }
    private static (EmailDispatchProcessor processor, FakeStore store, FakeSender sender, MutableTime clock, IDataProtectionProvider provider)
        Build(bool fail)
    {
        var clock = new MutableTime();
        var provider = new EphemeralDataProtectionProvider();
        var message = new EmailMessage("a@example.test", "SCApp", "<b>A</b>", "A");
        var protectedText = provider.CreateProtector(EmailQueue.Purpose).Protect(JsonSerializer.Serialize(message));
        var row = new EmailOutboxItem { EmailOutboxId = 1, Status = "Pending", CreatedAt = clock.Now.UtcDateTime,
            NextAttemptAt = clock.Now.UtcDateTime, ProtectedPayload = protectedText };
        var store = new FakeStore(row);
        var sender = new FakeSender { Fail = fail };
        var processor = new EmailDispatchProcessor(store, sender, provider, clock,
            Options.Create(new EmailOptions { BatchSize = 20 }), NullLogger<EmailDispatchProcessor>.Instance);
        return (processor, store, sender, clock, provider);
    }
    [Fact]
    public async Task SuccessClearsPayload()
    {
        var (p, s, sender, _, _) = Build(false);
        Assert.Equal(1, await p.RunOnceAsync());
        Assert.Equal("Sent", s.Row.Status);
        Assert.Null(s.Row.ProtectedPayload);
        Assert.Equal(1, sender.Calls);
    }
    [Fact]
    public async Task ThreeAttemptsHaveOneFiveFifteenMinuteBackoffsAndFail()
    {
        var (p, s, sender, time, _) = Build(true);
        await p.RunOnceAsync();
        Assert.Equal((byte)1, s.Row.Attempts);
        Assert.Equal(time.Now.UtcDateTime.AddMinutes(1), s.Row.NextAttemptAt);
        time.AdvanceMinutes(1);
        await p.RunOnceAsync();
        Assert.Equal((byte)2, s.Row.Attempts);
        Assert.Equal(time.Now.UtcDateTime.AddMinutes(5), s.Row.NextAttemptAt);
        time.AdvanceMinutes(5);
        await p.RunOnceAsync();
        Assert.Equal((byte)3, s.Row.Attempts);
        Assert.Equal(time.Now.UtcDateTime.AddMinutes(15), s.Row.NextAttemptAt);
        Assert.Equal("Failed", s.Row.Status);
        Assert.Null(s.Row.ProtectedPayload);
        Assert.Equal(3, sender.Calls);
    }
    [Fact]
    public async Task TwoProcessorsDoNotSendSameRowConcurrently()
    {
        var (p, s, sender, clock, provider) = Build(false);
        // Simulate two independent worker scopes sharing one atomic database store.
        var second = new EmailDispatchProcessor(s, sender,
            // Shared DataProtection key material is required for real multi-instance deployments.
            provider, clock,
            Options.Create(new EmailOptions { BatchSize = 20 }), NullLogger<EmailDispatchProcessor>.Instance);
        // Hai processor dùng chung key ring; fake store bảo đảm claim độc quyền bằng lock.
        await Task.WhenAll(p.RunOnceAsync(), second.RunOnceAsync());
        Assert.Equal(1, sender.Calls);
    }
}
