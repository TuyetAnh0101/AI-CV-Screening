using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Workspaces;

namespace SmartCv.Infrastructure.Workspaces;

/// <summary>
/// Ngữ cảnh workspace theo luồng bất đồng bộ (AsyncLocal): mỗi request/worker có trạng thái riêng, không rò sang luồng khác.
/// <see cref="Begin"/> dành cho code đã biết workspace (worker xử lý đúng bản ghi, test). <see cref="BeginSystem"/> chỉ cho các tác vụ
/// hệ thống đã được cho phép và có lý do; vị trí gọi được kiểm soát bởi test quét source (A52).
/// Scope lồng nhau được khôi phục đúng khi Dispose.
/// </summary>
public static class WorkspaceScope
{
    private sealed class Frame(int? workspaceId, string? role, string? kind, bool system, string? reason, Frame? parent)
    {
        public int? WorkspaceId { get; } = workspaceId;
        public string? Role { get; } = role;
        public string? Kind { get; } = kind;
        public bool System { get; } = system;
        public string? Reason { get; } = reason;
        public Frame? Parent { get; } = parent;
        public bool Disposed;
    }

    private static readonly AsyncLocal<Frame?> Current = new();
    private static ILogger logger = NullLogger.Instance;

    /// <summary>Gọi một lần lúc khởi động để ghi log Debug lý do mở system scope.</summary>
    public static void UseLogger(ILoggerFactory factory) => logger = factory.CreateLogger("SmartCv.Workspaces.Scope");

    /// <summary>Ngữ cảnh đọc từ scope hiện hành của luồng gọi (không có scope thì không có workspace).</summary>
    public static IWorkspaceContext Ambient { get; } = new AmbientWorkspaceContext();

    /// <summary>true nếu đang trong một scope mở bằng Begin/BeginSystem.</summary>
    public static bool IsActive => Current.Value is not null;

    public static IDisposable Begin(int workspaceId, string? role = null, string? kind = null)
    {
        if (workspaceId <= 0) throw new ArgumentOutOfRangeException(nameof(workspaceId), "Mã workspace phải là số dương.");
        return Push(f => new Frame(workspaceId, role, kind, false, null, f));
    }

    /// <summary>Bỏ lọc workspace cho tác vụ hệ thống. <paramref name="reason"/> bắt buộc (log Debug, kiểm tra bởi test allowlist).</summary>
    public static IDisposable BeginSystem(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("System scope phải có lý do.", nameof(reason));
        logger.LogDebug("Mở system scope: {Reason}", reason);
        return Push(f => new Frame(null, null, null, true, reason, f));
    }

    private static Restore Push(Func<Frame?, Frame> create)
    {
        var frame = create(Current.Value);
        Current.Value = frame;
        return new Restore(frame);
    }

    private sealed class Restore(Frame mine) : IDisposable
    {
        public void Dispose()
        {
            if (mine.Disposed) return;
            mine.Disposed = true;
            // Dispose lệch thứ tự: scope của mình đã bị scope trong che khuất thì chỉ đánh dấu đã đóng; khi scope trong đóng,
            // trạng thái trở về scope tổ tiên gần nhất CHƯA đóng (scope đã Dispose không bao giờ sống lại).
            if (!ReferenceEquals(Current.Value, mine)) return;
            var parent = mine.Parent;
            while (parent is { Disposed: true }) parent = parent.Parent;
            Current.Value = parent;
        }
    }

    private sealed class AmbientWorkspaceContext : IWorkspaceContext
    {
        public int? WorkspaceId => Current.Value?.WorkspaceId;
        public string? Role => Current.Value?.Role;
        public string? Kind => Current.Value?.Kind;
        public bool IsSystemScope => Current.Value is { System: true };
    }
}
