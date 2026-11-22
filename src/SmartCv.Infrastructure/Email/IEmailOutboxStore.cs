using SmartCv.Core.Entities;

namespace SmartCv.Infrastructure.Email;

/// <summary>SQL claim atomic cho nhiều instance; interface giúp worker unit test bằng fake store.</summary>
public interface IEmailOutboxStore
{
    Task<IReadOnlyList<long>> ClaimDueAsync(Guid claimId, int batchSize, DateTime nowUtc,
        DateTime leaseUntilUtc, CancellationToken cancellationToken);
    Task<EmailOutboxItem?> ReadClaimedAsync(long id, Guid claimId, CancellationToken cancellationToken);
    Task<bool> MarkSentAsync(long id, Guid claimId, DateTime nowUtc, CancellationToken cancellationToken);
    Task<bool> MarkFailureAsync(long id, Guid claimId, byte attempts, DateTime nextUtc,
        bool terminal, string errorCode, CancellationToken cancellationToken);
}
