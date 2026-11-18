using SmartCv.Core.Storage;

namespace SmartCv.Infrastructure.Privacy;

/// <summary>A39 durable journals for an A43 batch. Call rollback only after SQL rollback is confirmed.</summary>
public sealed class CvFileErasureBatch(ICvErasureStorage files)
{
    private readonly List<Guid> prepared = [];
    public void Prepare(Guid id, string path)
    {
        files.RollbackErasure(id);
        files.CompleteErasure(id); // also removes an orphan backup whose journal was never written
        prepared.Add(id); // include the current item even if preparation fails midway
        files.PrepareErasure(id, path);
    }

    public void Rollback()
    {
        var failed = false;
        foreach (var id in prepared.AsEnumerable().Reverse())
        {
            try { files.RollbackErasure(id); files.CompleteErasure(id); }
            catch { failed = true; }
        }
        if (failed) throw new CandidateErasureRecoveryException();
    }

    public void Complete()
    {
        var failed = false;
        foreach (var id in prepared)
        {
            try { files.CompleteErasure(id); }
            catch { failed = true; }
        }
        if (failed) throw new CandidateErasureRecoveryException();
    }
}

/// <summary>Outcome needs verification/recovery. Never retry the write automatically.</summary>
public sealed class CandidateErasureRecoveryException : TimeoutException;
