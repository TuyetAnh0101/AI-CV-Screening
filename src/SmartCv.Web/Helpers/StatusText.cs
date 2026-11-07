using System.Globalization;
using SmartCv.Web.Models;

namespace SmartCv.Web.Helpers;

/// <summary>Màu nhãn trạng thái (component StatusBadge), khớp Component/Badge trong Figma.</summary>
public enum BadgeVariant { Success, Info, Warning, Danger, Neutral }

/// <summary>Đổi trạng thái sang chữ tiếng Việt + màu nhãn; định dạng dung lượng. Gom một chỗ để mọi trang hiển thị giống nhau.</summary>
public static class StatusText
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static (string Text, BadgeVariant Variant) ForUpload(UploadItemState state) => state switch
    {
        UploadItemState.Uploading => ("Đang tải lên", BadgeVariant.Info),
        UploadItemState.Uploaded => ("Hoàn thành", BadgeVariant.Success),
        UploadItemState.Existing => ("Đã có sẵn", BadgeVariant.Warning),
        UploadItemState.Rejected => ("Không thành công", BadgeVariant.Danger),
        _ => ("Lỗi file", BadgeVariant.Danger)
    };

    /// <summary>Dung lượng dễ đọc: 512 B, 845 KB, 1,2 MB.</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Ceiling(bytes / 1024d):0} KB",
        _ => (bytes / 1024d / 1024d).ToString("0.0 'MB'", Vi)
    };
}
