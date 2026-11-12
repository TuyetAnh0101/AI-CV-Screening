using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using SmartCv.Core.Contracts;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Services;

/// <summary>Presentation privacy only. Unreadable browser preference always fails closed.</summary>
public sealed class AnonymityState(ProtectedLocalStorage? storage = null)
{
    private const string Key = "scapp.anonymous";
    private bool enabled = true;
    private Task? loading;
    private int revision;
    public bool Loaded { get; private set; }
    public bool Enabled => !Loaded || enabled;
    public bool Saving { get; private set; }
    public string? Warning { get; private set; }
    public event Action? Changed;

    public Task EnsureLoadedAsync() => Loaded ? Task.CompletedTask : loading ??= LoadAsync();
    private async Task LoadAsync()
    {
        var expected = revision;
        try
        {
            if (storage is null) return;
            var result = await storage.GetAsync<bool>(Key);
            if (expected != revision) return; // Never let a late read override an explicit toggle.
            enabled = result.Success && result.Value;
            Loaded = true; Warning = null;
        }
        catch (Exception e) when (e is InvalidOperationException or JSException or CryptographicException)
        {
            Warning = "Chưa đọc được lựa chọn đã lưu. Thông tin vẫn được che; hãy thử lại hoặc chọn lại chế độ.";
        }
        finally { Changed?.Invoke(); }
    }
    public async Task RetryAsync() { loading = null; await EnsureLoadedAsync(); }
    public async Task SetAsync(bool value)
    {
        if (Saving) return;
        ++revision; enabled = value; Loaded = true; Saving = true; Warning = null; Changed?.Invoke();
        try { if (storage is not null) await storage.SetAsync(Key, value); }
        catch (Exception e) when (e is InvalidOperationException or JSException or CryptographicException)
        { Warning = "Trình duyệt chưa lưu được lựa chọn. Chế độ hiện tại chỉ áp dụng trong phiên này; tải lại có thể dùng lựa chọn cũ. Hãy kiểm tra công tắc sau khi tải lại."; }
        finally { Saving = false; Changed?.Invoke(); }
    }
    // Include the stable application ID to distinguish tied ranks, including on Detail.
    public static string Alias(int? rank, int applicationId) => $"Ứng viên #{rank ?? applicationId} · HS{applicationId}";
    public string Name(string? name, int? rank, int applicationId) => Enabled ? Alias(rank, applicationId) : name ?? "Chưa rõ tên ứng viên";
    public string FileName(string? file) => Enabled ? "CV đã ẩn tên" : file ?? "—";
    public string Initials(RankingItemDto row) => Enabled ? $"#{row.Rank ?? row.ApplicationId}" : RankingView.Initials(row.CandidateName, row.FileName);
    public string? Text(string? text) => Enabled && !string.IsNullOrWhiteSpace(text) ? "Nội dung tự do được ẩn để bảo vệ thông tin cá nhân. Tắt Ẩn danh để xem." : text;
    public string? Label(string? label) => !Enabled || label is null || FitLevel.IsKnown(label) ? label : "Mức không xác định";
    public bool Matches(RankingItemDto row, string? search) => !Enabled ? RankingView.MatchesSearch(row, search)
        : RankingView.Fold(search).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(RankingView.Fold(Alias(row.Rank, row.ApplicationId)).Contains);
    public IEnumerable<RankingItemDto> Sort(IEnumerable<RankingItemDto> rows, RankingSort sort) => Enabled
        ? RankingView.Sort(rows.Select(r => new RankingItemDto
        { EvaluationId=r.EvaluationId, Status=r.Status, FinalScore=r.FinalScore, AiScore=r.AiScore, KeywordScore=r.KeywordScore,
          YearsExperience=r.YearsExperience, Rank=r.Rank, FileName=r.ApplicationId.ToString("D10") }), sort == RankingSort.NameAsc ? RankingSort.FinalDesc : sort)
          .Join(rows, r => r.EvaluationId, r => r.EvaluationId, (_, original) => original)
        : RankingView.Sort(rows, sort);
}
