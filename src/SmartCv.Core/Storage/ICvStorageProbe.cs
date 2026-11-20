namespace SmartCv.Core.Storage;

/// <summary>Checks the active storage provider using a disposable marker, never an existing CV.</summary>
public interface ICvStorageProbe
{
    Task CheckWritableAsync(CancellationToken cancellationToken = default);
}
