using SmartCv.Core.Common;
using SmartCv.Core.Contracts;
using SmartCv.Core.Ops;
using SmartCv.Web.Services;

namespace SmartCv.Web.Helpers;

public sealed record AdminAccountCounts(int Total, int Admins, int Hr);

/// <summary>Only existing, bounded read APIs. Independent sections keep a partial outage from blanking the dashboard.</summary>
public sealed class AdminDashboardWorkspace(DashboardApiClient dashboard, UserApiClient users, RunApiClient runs,
    HealthApiClient health, AuditApiClient audit, ExportApiClient exports, TimeProvider clock) : IDisposable
{
    public sealed class Section<T>
    {
        public T? Data { get; private set; }
        public string? Error { get; private set; }
        public bool Loading { get; private set; } = true;
        internal async Task LoadAsync(Func<CancellationToken, Task<ApiResult<T>>> fetch, CancellationToken ct, Action changed)
        {
            Loading = true; Data = default; Error = null; changed();
            try
            {
                var result = await fetch(ct);
                if (ct.IsCancellationRequested) return;
                if (result.IsSuccess) Data = result.Data;
                else Error = result.ErrorMessage ?? "Chưa tải được dữ liệu. Vui lòng thử lại.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!ct.IsCancellationRequested) Error = "Chưa tải được dữ liệu. Vui lòng thử lại."; }
            finally { if (!ct.IsCancellationRequested) { Loading = false; changed(); } }
        }
    }

    public Section<DashboardSummaryDto> Recruitment { get; } = new();
    public Section<AdminAccountCounts> Accounts { get; } = new();
    public Section<RunPageDto> Screening { get; } = new();
    public Section<RunPageDto> FailedScreening { get; } = new();
    public Section<HealthReportDto> Health { get; } = new();
    public Section<AuditPageDto> Activity { get; } = new();
    public Section<ExportLogPageDto> Exports { get; } = new();
    public bool Loading { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public DateTime? CheckedAt { get; private set; }
    public event Action? Changed;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private void Notify() { if (!disposed) Changed?.Invoke(); }

    public async Task LoadAsync()
    {
        if (disposed || Loading || (CheckedAt is not null && Exports.Loading)) return;
        Loading = true; To = VietnamTime.Today(clock.GetUtcNow().UtcDateTime); From = To.AddDays(-6); Notify();
        var ct = lifetime.Token;
        try
        {
            await Task.WhenAll(
                Recruitment.LoadAsync(t => dashboard.GetSummaryAsync(new DashboardFilter(), t), ct, Notify),
                Accounts.LoadAsync(AccountsAsync, ct, Notify),
                Screening.LoadAsync(t => runs.GetScreeningTotalsAsync(ct: t), ct, Notify),
                FailedScreening.LoadAsync(t => runs.GetScreeningTotalsAsync(true, t), ct, Notify),
                Health.LoadAsync(health.GetReadinessAsync, ct, Notify),
                Activity.LoadAsync(t => audit.ListAsync(From, To, null, null, null, null, null, null, null, null, 1, 5, t), ct, Notify),
                Exports.LoadAsync(t => exports.ListAsync(From, To, 1, t), ct, Notify));
            if (!disposed) CheckedAt = clock.GetUtcNow().UtcDateTime;
        }
        finally { if (!disposed) { Loading = false; Notify(); } }
    }

    private async Task<ApiResult<AdminAccountCounts>> AccountsAsync(CancellationToken ct)
    {
        var requests = await Task.WhenAll(users.ListAsync(new UserQuery(PageSize: 1), ct),
            users.ListAsync(new UserQuery(Role: "Admin", PageSize: 1), ct), users.ListAsync(new UserQuery(Role: "HR", PageSize: 1), ct));
        if (requests.Any(r => !r.IsSuccess))
        {
            var error = requests.First(r => !r.IsSuccess);
            return ApiResult<AdminAccountCounts>.Failure(error.ErrorMessage ?? "Chưa tải được thống kê tài khoản.", error.StatusCode);
        }
        if (requests.Any(r => r.Data?.Total is null))
            return ApiResult<AdminAccountCounts>.Failure("Máy chủ chưa trả tổng số tài khoản. Mở Quản lý tài khoản để tra cứu.", null);
        return ApiResult<AdminAccountCounts>.Success(new(requests[0].Data!.Total!.Value, requests[1].Data!.Total!.Value, requests[2].Data!.Total!.Value));
    }

    public Task ExportPageAsync(int page)
    {
        if (disposed || Loading || Exports.Loading || page < 1 || page > Math.Max(1, Exports.Data?.TotalPages ?? 1)) return Task.CompletedTask;
        return Exports.LoadAsync(t => exports.ListAsync(From, To, page, t), lifetime.Token, Notify);
    }
    public void Dispose() { disposed = true; lifetime.Cancel(); lifetime.Dispose(); Changed = null; }
}
