using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Contracts;
using SmartCv.Core.Options;
using SmartCv.Core.Uploads;
using SmartCv.Infrastructure.Uploads;

namespace SmartCv.Tests;

/// <summary>
/// A4: CvUploadService chỉ nhận file và trả thông tin từng file với trạng thái Received.
/// Khi làm A5 (kiểm tra file) và A6 (lưu file), cập nhật các test đánh dấu "A4" cho đúng hành vi mới.
/// </summary>
public class CvUploadServiceTests
{
    private static readonly CvUploadService Service = new(NullLogger<CvUploadService>.Instance);

    private static CvUploadFile File(string name, string contentType = "application/pdf", int size = 100)
    {
        var bytes = new byte[size];
        return new CvUploadFile(name, contentType, bytes.Length, () => new MemoryStream(bytes));
    }

    [Fact]
    public async Task One_file_is_received()
    {
        var result = await Service.UploadAsync([File("cv-mau-1.pdf", size: 1421)]);

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);

        var item = Assert.Single(result.Files);
        Assert.Equal("cv-mau-1.pdf", item.FileName);
        Assert.Equal(1421, item.SizeBytes);
        Assert.Equal("application/pdf", item.ContentType);
        Assert.Equal(CvUploadStatuses.Received, item.Status);
        Assert.Null(item.CvId);
        Assert.Null(item.ErrorCode);
        Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public async Task Many_files_keep_order_and_counts()
    {
        var result = await Service.UploadAsync(
        [
            File("cv-mau-1.pdf", "application/pdf", 1421),
            File("cv-mau-2.png", "image/png", 8625),
            File("cv-mau-4.jpg", "image/jpeg", 17619),
        ]);

        Assert.Equal(3, result.TotalFiles);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(result.TotalFiles, result.Files.Count);
        Assert.Equal(["cv-mau-1.pdf", "cv-mau-2.png", "cv-mau-4.jpg"], result.Files.Select(f => f.FileName));
        Assert.Equal(["application/pdf", "image/png", "image/jpeg"], result.Files.Select(f => f.ContentType));
        Assert.Equal([1421L, 8625L, 17619L], result.Files.Select(f => f.SizeBytes));
        Assert.All(result.Files, f => Assert.Equal(CvUploadStatuses.Received, f.Status));
    }

    [Theory]
    [InlineData(@"C:\Users\hr\Desktop\cv.pdf", "cv.pdf")]
    [InlineData("../../etc/cv.pdf", "cv.pdf")]
    [InlineData(@"..\..\cv.png", "cv.png")]
    [InlineData("thu-muc/con/cv.jpg", "cv.jpg")]
    [InlineData("Nguyễn Văn An - CV.pdf", "Nguyễn Văn An - CV.pdf")]
    public async Task File_name_is_stripped_of_client_path(string sent, string expected)
    {
        var result = await Service.UploadAsync([File(sent)]);
        Assert.Equal(expected, Assert.Single(result.Files).FileName);
    }

    [Fact]
    public async Task A4_does_not_reject_any_format()
    {
        // A4 chưa kiểm tra định dạng: file .txt vẫn Received (A5 sẽ trả Rejected + InvalidFormat).
        var result = await Service.UploadAsync([File("file-sai-dinh-dang.txt", "text/plain", 50)]);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(CvUploadStatuses.Received, Assert.Single(result.Files).Status);
    }

    [Fact]
    public async Task A4_does_not_read_file_content()
    {
        // A4 chưa đọc nội dung file (chưa tính hash, chưa lưu): không được mở stream.
        var file = new CvUploadFile("cv.pdf", "application/pdf", 10,
            () => throw new InvalidOperationException("A4 không được đọc nội dung file"));

        var result = await Service.UploadAsync([file]);

        Assert.Equal(CvUploadStatuses.Received, Assert.Single(result.Files).Status);
    }

    [Fact]
    public async Task Empty_list_returns_zero_counts()
    {
        var result = await Service.UploadAsync([]);

        Assert.Equal(0, result.TotalFiles);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Cancelled_request_stops_processing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.UploadAsync([File("cv.pdf")], cts.Token));
    }

    [Fact]
    public void Default_options_match_A4_limits()
    {
        var options = new CvUploadOptions();

        Assert.Equal(10, options.MaxFileSizeMB);
        Assert.Equal(20, options.MaxFilesPerRequest);
        Assert.Equal([".pdf", ".png", ".jpg", ".jpeg"], options.AllowedExtensions);
        Assert.Equal("Uploads/CV", options.StorageRoot);
        Assert.Equal(10L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Equal(201L * 1024 * 1024, options.MaxRequestBytes);   // 20 × 10 MiB + 1 MiB
    }
}
