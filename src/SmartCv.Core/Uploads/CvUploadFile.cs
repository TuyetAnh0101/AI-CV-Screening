namespace SmartCv.Core.Uploads;

/// <summary>
/// Một file CV được gửi lên, ở dạng không phụ thuộc ASP.NET.
/// API chuyển IFormFile sang class này, nhờ vậy Core và Infrastructure không cần biết đến HTTP
/// và viết unit test (A18) dễ hơn: chỉ cần tạo CvUploadFile từ một MemoryStream.
/// </summary>
/// <param name="FileName">Tên file do trình duyệt gửi. KHÔNG dùng tên này để lưu file (A6 đặt tên theo GUID).</param>
/// <param name="ContentType">Kiểu nội dung do trình duyệt khai báo, ví dụ application/pdf. Không tin tuyệt đối (A5 kiểm tra lại).</param>
/// <param name="Length">Dung lượng (byte).</param>
/// <param name="OpenReadStream">Hàm mở luồng đọc nội dung file.</param>
public sealed record CvUploadFile(string FileName, string ContentType, long Length, Func<Stream> OpenReadStream);
