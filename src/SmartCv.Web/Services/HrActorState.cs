using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using System.Security.Cryptography;
using SmartCv.Core.Applications;

namespace SmartCv.Web.Services;

/// <summary>HR name per browser tab, until account authentication is available.</summary>
public sealed class HrActorState(ProtectedSessionStorage? storage = null)
{
    private const string Key = "scapp.hr-actor";
    private bool loaded;
    public string? Actor { get; private set; }
    public bool HasActor => !string.IsNullOrWhiteSpace(Actor);
    public string? StorageWarning { get; private set; }

    // Called after interactive render, never during prerender.
    public async Task EnsureLoadedAsync()
    {
        if (loaded) return;
        loaded = true;
        if (storage is null) return;
        try
        {
            var result = await storage.GetAsync<string>(Key);
            var actor = result.Value?.Trim();
            if (result.Success && actor is { Length: > 0 and <= StageChangeRules.MaxActorLength }) Actor = actor;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or CryptographicException)
        {
            StorageWarning = "Không đọc được tên đã nhớ. Hãy nhập lại tên HR khi lưu quyết định.";
        }
    }

    public async Task SetAsync(string actor)
    {
        actor = actor.Trim();
        if (actor.Length is 0 or > StageChangeRules.MaxActorLength) throw new ArgumentException("Tên HR không hợp lệ.", nameof(actor));
        Actor = actor;
        loaded = true;
        if (storage is null) return;
        try { await storage.SetAsync(Key, actor); StorageWarning = null; }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or CryptographicException)
        {
            StorageWarning = "Tên HR chỉ được nhớ trong phiên đang mở. Trình duyệt chưa lưu được tên cho lần tải lại.";
        }
    }
}
