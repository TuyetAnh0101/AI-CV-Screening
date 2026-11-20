using SmartCv.Core.Auth;

namespace SmartCv.Web.Services;

/// <summary>Compatibility facade for B34/B35/B41. Identity comes only from the verified AuthSession.</summary>
public sealed class HrActorState(AuthSession? session = null)
{
    public bool HasActor => session is { IsSignedIn: true, IsVerified: true, MustChangePassword: false }
        && UserRoles.IsValid(session.User?.Role) && !string.IsNullOrWhiteSpace(session.User?.FullName);
    public int? UserId => HasActor ? session!.User!.UserId : null;
    public string? DisplayName => HasActor ? session!.User!.FullName : null;
    // Existing decision/label contracts limit names to 100; backend applies the same truncation to its trusted claim.
    public string? Actor
    {
        get
        {
            if (DisplayName is not { } name) return null;
            name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length <= 100) return name;
            return name[..(char.IsHighSurrogate(name[99]) ? 99 : 100)];
        }
    }
    public string? StorageWarning => null;
    public Task EnsureLoadedAsync() => session?.EnsureRestoredAsync() ?? Task.CompletedTask;
    // Kept for older consumers. A manual name can never replace the authenticated identity or reach storage.
    public Task SetAsync(string actor) => Task.CompletedTask;
}
