using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using SmartCv.Core.Constants;
using SmartCv.Web.Helpers;

namespace SmartCv.Web.Components.Results;

public partial class RankingTable
{
    [Parameter] public string? RunStatus { get; set; }
    [Parameter] public string JobTitle { get; set; } = "";
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
    private RankingExportContext? exportContext;
    private bool OffersExport => RunStatus is not null && AccessRules.CanUseApp(AccessRules.Current(AuthState));
    private bool ExportLocked => Loading || busy || pending is not null || flagRequest is not null || needsSync || exportContext is not null || RunStatus != RunStatuses.Completed || Items.Count == 0 || RunId is not > 0 || JobId is not > 0;
    private string ExportHelp => RunStatus != RunStatuses.Completed ? "Chỉ xuất khi lượt chấm đã hoàn tất." : Items.Count == 0 ? "Chưa có hồ sơ để xuất." : "Chọn phạm vi, xem trước và tải file Excel.";
    private async Task OpenExportAsync()
    {
        if (disposed || ExportLocked || !AccessRules.CanUseApp(await AccessRules.ResolveAsync(AuthState))) return;
        openMenu = null; drawerRow = null;
        exportContext = new(RunId!.Value, JobId!.Value, JobTitle, RunStatus!, stageFilter, filter, search, flaggedOnly, manualOnly, Anonymity.Enabled, sort);
        await OnInteractionChanged.InvokeAsync(true);
    }
    private async Task CloseExportAsync() { exportContext = null; await OnInteractionChanged.InvokeAsync(false); }
}
