using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace SmartCv.Web.Services;

/// <summary>Only file bytes cross JS interop. JWT remains in the server-side AuthSession.</summary>
public sealed class FileDownloadService : IAsyncDisposable
{
    private readonly IJSRuntime js;
    private readonly NavigationManager nav;
    private readonly AuthSession session;
    private readonly AnonymityState privacy;
    private IJSObjectReference? module;
    private Task<IJSObjectReference>? importing;
    private bool disposed;
    public FileDownloadService(IJSRuntime js, NavigationManager nav, AuthSession session, AnonymityState privacy)
    {
        this.js = js; this.nav = nav; this.session = session; this.privacy = privacy;
        nav.LocationChanged += Navigated; session.Changed += SessionChanged; privacy.Changed += PrivacyChanged;
    }
    public static bool CanPreview(string contentType) => contentType.ToLowerInvariant() is "application/pdf" or "image/png" or "image/jpeg";
    public async Task<bool> OpenAsync(DownloadedFile file, bool preferPreview = true, CancellationToken ct = default)
    {
        if (disposed || !session.IsSignedIn || !session.IsVerified || privacy.Enabled) throw new InvalidOperationException("Không mở file khi phiên chưa hợp lệ hoặc đang Ẩn danh.");
        importing ??= js.InvokeAsync<IJSObjectReference>("import", "./js/smartcv-files.js").AsTask();
        module = await importing;
        // Navigation/sign-out/anonymity may change while awaiting the module or the network.
        if (disposed || !session.IsSignedIn || !session.IsVerified || privacy.Enabled) throw new InvalidOperationException("Phiên hoặc chế độ riêng tư đã thay đổi.");
        using var stream = new DotNetStreamReference(file.Content, leaveOpen: true);
        return await module.InvokeAsync<bool>("open", ct, stream, file.ContentType, file.FileName, preferPreview && CanPreview(file.ContentType));
    }

    /// <summary>A31 has already applied the explicit export privacy choice. This does not relax CV preview's privacy guard.</summary>
    public async Task SaveRankingAsync(DownloadedFile file, CancellationToken ct = default)
    {
        var token = session.Token;
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (disposed || token != session.Token || !session.IsSignedIn || !session.IsVerified || session.MustChangePassword)
                throw new InvalidOperationException("Phiên đăng nhập đã thay đổi.");
        }
        Check();
        if (file.ContentType != "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Máy chủ chưa cung cấp file Excel hợp lệ.");
        importing ??= js.InvokeAsync<IJSObjectReference>("import", "./js/smartcv-files.js").AsTask();
        module = await importing; Check();
        using var stream = new DotNetStreamReference(file.Content, leaveOpen: true);
        await module.InvokeAsync<bool>("open", ct, stream, file.ContentType, file.FileName, false);
    }
    /// <summary>Dedicated authorized report download; independent of CV preview privacy toggle.</summary>
    public async Task SaveReportPdfAsync(DownloadedFile file, CancellationToken ct = default)
    {
        var token = session.Token;
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (disposed || token != session.Token || !session.IsSignedIn || !session.IsVerified || session.MustChangePassword)
                throw new InvalidOperationException("Phiên đăng nhập đã thay đổi.");
        }
        Check();
        if (file.ContentType != "application/pdf" || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Máy chủ chưa cung cấp file PDF hợp lệ.");
        importing ??= js.InvokeAsync<IJSObjectReference>("import", "./js/smartcv-files.js").AsTask();
        module = await importing;
        Check();
        using var stream = new DotNetStreamReference(file.Content, leaveOpen: true);
        await module.InvokeAsync<bool>("open", ct, stream, file.ContentType, file.FileName, false);
    }

    private void Navigated(object? sender, LocationChangedEventArgs args) => _ = CloseAsync();
    private void SessionChanged() { if (!session.IsSignedIn || !session.IsVerified || session.MustChangePassword) _ = CloseAsync(); }
    private void PrivacyChanged() { if (privacy.Enabled) _ = CloseAsync(); }
    private async Task CloseAsync()
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("closeAll"); }
        catch (Exception error) when (error is JSException or TaskCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; nav.LocationChanged -= Navigated; session.Changed -= SessionChanged; privacy.Changed -= PrivacyChanged;
        await CloseAsync();
        if (module is not null) { try { await module.DisposeAsync(); } catch (JSDisconnectedException) { } }
    }
}
