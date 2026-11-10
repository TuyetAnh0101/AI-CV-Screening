using SmartCv.Core.Options;
using SmartCv.Web.Models;

namespace SmartCv.Web.Helpers;

/// <summary>
/// B5 – lớp kiểm tra thứ nhất, ngay trên giao diện, KHÔNG cần đọc nội dung file:
/// đuôi file, file rỗng, dung lượng, trùng trong danh sách, số file mỗi lần tải.
/// File không qua được sẽ thành dòng "Lỗi file" và không được gửi lên API.
/// API (A5) luôn kiểm tra lại nội dung thật (magic bytes) và trùng với database.
/// Mọi giới hạn lấy từ mục "CvUpload" trong appsettings.json, không viết cứng.
/// </summary>
public sealed class UploadValidator
{
    private readonly CvUploadOptions limits;
    private readonly int maxFilesPerSelection;
    private readonly HashSet<string> allowed;
    private readonly string allowedText;

    public UploadValidator(CvUploadOptions limits, int maxFilesPerSelection)
    {
        this.limits = limits;
        this.maxFilesPerSelection = Math.Max(1, maxFilesPerSelection);
        allowed = new HashSet<string>(limits.AllowedExtensions.Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
        allowedText = AllowedFormatsText(limits.AllowedExtensions);
    }

    /// <summary>"PDF, JPG, PNG" (JPEG gộp vào JPG cho gọn, giống chú thích trong Figma).</summary>
    public static string AllowedFormatsText(IEnumerable<string> extensions) => string.Join(", ",
        extensions.Select(e => e.Trim().TrimStart('.').ToUpperInvariant())
                  .Where(e => e.Length > 0 && e != "JPEG")
                  .Distinct());

    /// <summary>
    /// Trả lý do lỗi (tiếng Việt) hoặc null nếu file được gửi.
    /// </summary>
    /// <param name="fileName">Tên file người dùng chọn.</param>
    /// <param name="size">Dung lượng (byte).</param>
    /// <param name="currentItems">Các dòng đang có trong danh sách, gồm cả các file hợp lệ vừa xét trong cùng lần chọn.</param>
    /// <param name="acceptedInSelection">Số file đã hợp lệ trong lần chọn này (để áp giới hạn số CV mỗi lần).</param>
    public string? Validate(string fileName, long size, IEnumerable<UploadItem> currentItems, int acceptedInSelection)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || ext == ".")
            return $"Sai định dạng. Chỉ nhận {allowedText}.";

        if (!allowed.Contains(ext))
            return $"Sai định dạng \"{ext.ToLowerInvariant()}\". Chỉ nhận {allowedText}.";

        if (size <= 0)
            return "File rỗng (0 byte).";

        if (size > limits.MaxFileSizeBytes)
            return $"File nặng {StatusText.FileSize(size)}, vượt giới hạn {limits.MaxFileSizeMB} MB.";

        // Trùng trong danh sách: cùng tên và cùng dung lượng với một dòng đang tải / đã tải.
        // Dòng lỗi không tính, để người dùng thả lại đúng file đó sau khi API có kết nối lại.
        if (currentItems.Any(i => !i.IsError && i.SizeBytes == size
                                  && string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            return "File này đã có trong danh sách.";

        // Đặt cuối: chỉ file hợp lệ mới chiếm suất, file lỗi không làm file hợp lệ phía sau bị loại.
        if (acceptedInSelection >= maxFilesPerSelection)
            return $"Vượt quá {maxFilesPerSelection} CV mỗi lần tải. Hãy tải file này ở lần sau.";

        return null;
    }
}
