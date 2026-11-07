using System.ComponentModel.DataAnnotations;

namespace SmartCv.Api.Endpoints;

/// <summary>
/// Cấu hình bật/tắt và giới hạn tần suất của API POST /api/cvs/upload,
/// đọc từ mục "CvUpload:Endpoint" trong appsettings.
/// Đây là cấu hình của tầng HTTP (Api), tách khỏi CvUploadOptions (Core) để hợp đồng dùng chung với Blazor không đổi.
/// </summary>
public sealed class CvUploadEndpointOptions
{
    public const string SectionName = "CvUpload:Endpoint";

    /// <summary>
    /// Bật endpoint. Mặc định TẮT (an toàn): appsettings.json để false, chỉ appsettings.Development.json bật true.
    /// Khi chưa có xác thực (M6), bật ở môi trường khác Development sẽ bị chặn ngay lúc khởi động.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Số request tải CV tối đa của một địa chỉ IP trong mỗi cửa sổ thời gian.</summary>
    [Range(1, 10_000)]
    public int PermitLimit { get; set; } = 30;

    /// <summary>Độ dài cửa sổ thời gian (giây) cho <see cref="PermitLimit"/>.</summary>
    [Range(1, 3_600)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Số request tải CV được xử lý đồng thời trên toàn API (giới hạn bộ nhớ, ổ đĩa tạm).</summary>
    [Range(1, 100)]
    public int ConcurrentRequests { get; set; } = 4;

    /// <summary>Số request được xếp hàng chờ khi đã đủ <see cref="ConcurrentRequests"/>; quá số này trả 429.</summary>
    [Range(0, 100)]
    public int QueueLimit { get; set; } = 2;
}
