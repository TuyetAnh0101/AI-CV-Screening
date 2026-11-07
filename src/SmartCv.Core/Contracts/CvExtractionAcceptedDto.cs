namespace SmartCv.Core.Contracts;

/// <summary>Kết quả POST /api/cvs/{id}/extract (A7): CV đã được xếp vào hàng đợi đọc chữ.</summary>
public sealed class CvExtractionAcceptedDto
{
    public Guid CvId { get; init; }

    /// <summary>Luôn là "Pending". Hỏi lại GET /api/cvs/{id} để biết Completed / Failed.</summary>
    public string ExtractionStatus { get; init; } = string.Empty;
}
