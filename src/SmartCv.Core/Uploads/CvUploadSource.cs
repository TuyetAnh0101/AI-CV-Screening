using SmartCv.Core.Constants;

namespace SmartCv.Core.Uploads;

public sealed record CvUploadSource(string SourceChannel, DateTime ConsentConfirmedAtUtc)
{
    public static (CvUploadSource? Source, string? Title, string? Error) Create(string? sourceChannel, bool? consentConfirmed, DateTime nowUtc)
    {
        if (consentConfirmed != true)
            return (null, "Chưa xác nhận đồng ý", "Hãy xác nhận ứng viên đã đồng ý xử lý dữ liệu tuyển dụng (consentConfirmed = true) trước khi tải CV.");
        var channel = sourceChannel?.Trim();
        if (!CvSourceChannels.IsValid(channel))
            return (null, "Thiếu nguồn CV", $"sourceChannel phải là một trong: {string.Join(", ", CvSourceChannels.All)}.");
        return (new(channel!, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)), null, null);
    }
}
