namespace SmartCv.Core.Storage;
/// <summary>Private durable recovery copy; not a distributed SQL/filesystem transaction.</summary>
public interface ICvErasureStorage
{
    IReadOnlyList<Guid> PendingErasureIds(int limit);
    bool PrepareErasure(Guid cvId, string storedPath);
    void RollbackErasure(Guid cvId);
    void CompleteErasure(Guid cvId);
}
