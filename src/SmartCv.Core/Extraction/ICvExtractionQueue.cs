namespace SmartCv.Core.Extraction;

/// <summary>
/// Hàng đợi CV chờ đọc chữ. Tải CV lên xong thì xếp CvId vào đây, một tiến trình chạy nền lấy ra xử lý.
/// Nhờ vậy API tải lên trả kết quả ngay, không phải chờ OCR (OCR một CV scan mất vài giây).
/// </summary>
public interface ICvExtractionQueue
{
    ValueTask EnqueueAsync(Guid cvId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
