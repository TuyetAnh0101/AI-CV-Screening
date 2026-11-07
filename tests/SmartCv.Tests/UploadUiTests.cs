extern alias web;

using System.Text.Json;
using SmartCv.Core.Contracts;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B3: logic trang tải CV. (1) Chia request theo giới hạn API. (2) Chỉ "Accepted + CvId" mới là Hoàn thành.
/// (3) Cấu hình CvUpload của Web khớp với API.
/// </summary>
public class UploadUiTests
{
    private const long MB = 1024 * 1024;

    // ---------- (1) Chia nhóm ----------

    [Fact]
    public void Splits_25_files_into_requests_of_at_most_20()
    {
        var files = Enumerable.Range(1, 25).Select(i => (long)i).ToList();

        var batches = UploadFlow.SplitIntoBatches(files, _ => MB, maxFiles: 20, maxBytes: 200 * MB);

        Assert.Equal([20, 5], batches.Select(b => b.Count));
        Assert.Equal(files, batches.SelectMany(b => b));   // giữ đúng thứ tự, không mất file
    }

    [Fact]
    public void Splits_when_total_size_would_exceed_request_limit()
    {
        var sizes = new long[] { 6 * MB, 6 * MB, 6 * MB };

        var batches = UploadFlow.SplitIntoBatches(sizes, s => s, maxFiles: 20, maxBytes: 10 * MB);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.True(b.Sum() <= 10 * MB));
    }

    // ---------- (2) Ánh xạ kết quả ----------

    private static UploadItem Item(string name) => new() { FileName = name, State = UploadItemState.Uploading };

    [Fact]
    public void Accepted_with_cvId_is_completed_and_rejected_is_not()
    {
        var a = Item("a.pdf");
        var b = Item("b.pdf");
        var c = Item("c.pdf");
        var id = Guid.NewGuid();
        var response = new CvUploadResponse
        {
            Files =
            [
                new() { FileName = "a.pdf", Status = CvUploadStatuses.Accepted, CvId = id },
                new() { FileName = "b.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate, ErrorMessage = "Trùng" },
                new() { FileName = "c.pdf", Status = CvUploadStatuses.Received },   // chưa lưu: không phải thành công
            ]
        };

        UploadFlow.ApplyResult([a, b, c], ApiResult<CvUploadResponse>.Success(response));

        Assert.Equal((UploadItemState.Uploaded, id, true), (a.State, a.CvId, a.CanAnalyze));
        Assert.Equal((UploadItemState.Rejected, (Guid?)null, false, "Trùng"), (b.State, b.CvId, b.CanAnalyze, b.Message));
        Assert.Equal((UploadItemState.Rejected, false), (c.State, c.CanAnalyze));
    }

    [Fact]
    public void Connection_error_never_marks_files_completed()
    {
        var a = Item("a.pdf");

        UploadFlow.ApplyResult([a], ApiResult<CvUploadResponse>.Failure("Không kết nối được máy chủ API."));

        Assert.Equal(UploadItemState.Rejected, a.State);
        Assert.Null(a.CvId);
        Assert.False(a.CanAnalyze);
        Assert.Equal("Không kết nối được máy chủ API.", a.Message);
    }

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(845 * 1024, "845 KB")]
    [InlineData(1258291, "1,2 MB")]
    public void Formats_file_size(long bytes, string expected) => Assert.Equal(expected, StatusText.FileSize(bytes));

    // ---------- (3) Cấu hình Web khớp API ----------

    [Fact]
    public void Web_upload_limits_match_api_config()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src", "SmartCv.Web", "appsettings.json")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc repo");

        JsonElement Section(string project) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", project, "appsettings.json"))).RootElement.GetProperty("CvUpload");

        var api = Section("SmartCv.Api");
        var webCfg = Section("SmartCv.Web");

        Assert.Equal(api.GetProperty("MaxFileSizeMB").GetInt32(), webCfg.GetProperty("MaxFileSizeMB").GetInt32());
        Assert.Equal(api.GetProperty("MaxFilesPerRequest").GetInt32(), webCfg.GetProperty("MaxFilesPerRequest").GetInt32());
        Assert.Equal(api.GetProperty("AllowedExtensions").GetRawText(), webCfg.GetProperty("AllowedExtensions").GetRawText());
    }
}
