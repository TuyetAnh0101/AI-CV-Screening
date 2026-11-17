extern alias web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using SmartCv.Core.Constants;
using SmartCv.Core.Contracts;
using SmartCv.Core.Extraction;
using SmartCv.Core.Options;
using web::SmartCv.Web.Helpers;
using web::SmartCv.Web.Models;
using web::SmartCv.Web.Services;

namespace SmartCv.Tests;

/// <summary>
/// B38: quy tắc thuần của trang Tải CV (UploadRules), client gọi API (CvApiClient / RunApiClient) và hợp đồng với API thật
/// chạy trong bộ nhớ (A38 nguồn + đồng ý, A42 text quality / trùng / ước tính). Không SQL Server, không Gemini, không OCR thật.
/// </summary>
public class B38UploadRulesTests
{
    // ================= (1) Nguồn CV + xác nhận đồng ý (A38) =================

    [Fact]
    public void Source_options_are_exactly_the_five_A38_codes_with_vietnamese_labels()
    {
        Assert.Equal(["Email", "Website", "JobBoard", "Referral", "Other"], UploadRules.SourceOptions.Select(o => o.Code));
        Assert.Equal(CvSourceChannels.All, UploadRules.SourceOptions.Select(o => o.Code));
        Assert.All(UploadRules.SourceOptions, o => Assert.Equal(CvSourceChannels.DisplayName(o.Code), o.Label));
        Assert.Equal("Email ứng tuyển", UploadRules.SourceOptions[0].Label);
    }

    [Theory]
    [InlineData(null, false, "Chọn nguồn CV và tích xác nhận ứng viên đã đồng ý trước khi tải lên.", 2)]
    [InlineData("", true, "Chọn nguồn CV trước khi tải lên.", 1)]
    [InlineData("email", true, "Chọn nguồn CV trước khi tải lên.", 1)]       // phân biệt hoa thường như API
    [InlineData("Facebook", true, "Chọn nguồn CV trước khi tải lên.", 1)]
    [InlineData("Email", false, "Tích xác nhận ứng viên đã đồng ý trước khi tải lên.", 1)]
    public void Upload_stays_locked_until_valid_source_and_consent(string? source, bool consent, string reason, int missing)
    {
        Assert.False(UploadRules.CanUpload(source, consent));
        Assert.Equal(reason, UploadRules.LockReason(source, consent));
        Assert.Equal(missing, UploadRules.MissingCount(source, consent));
    }

    [Theory]
    [InlineData("Email")] [InlineData("Website")] [InlineData("JobBoard")] [InlineData("Referral")] [InlineData("Other")]
    public void Each_valid_source_with_consent_unlocks(string source)
    {
        Assert.True(UploadRules.CanUpload(source, consentConfirmed: true));
        Assert.Null(UploadRules.LockReason(source, true));
        Assert.Equal(0, UploadRules.MissingCount(source, true));
    }

    // ================= (2) Lỗi cả request, CV trùng (A42) =================

    [Fact]
    public void Http_400_from_A38_is_explained_without_technical_field_names()
    {
        var consent = UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Failure(
            "Hãy xác nhận ứng viên đã đồng ý xử lý dữ liệu tuyển dụng (consentConfirmed = true) trước khi tải CV.", 400))!.Value;
        Assert.Equal("Máy chủ chưa nhận lần tải này", consent.Title);
        Assert.DoesNotContain("consentConfirmed", consent.Message);
        Assert.Contains("Kiểm tra lại nguồn CV và ô xác nhận đồng ý", consent.Message);
        Assert.Contains("chưa được lưu", consent.Message);

        var source = UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Failure(
            "sourceChannel phải là một trong: Email, Website, JobBoard, Referral, Other.", 400))!.Value;
        Assert.DoesNotContain("sourceChannel", source.Message);
        Assert.StartsWith("Nguồn CV gửi lên không hợp lệ.", source.Message);

        Assert.Equal("Tải lên quá nhanh", UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Failure("x", 429))!.Value.Title);
        // B5 giữ nguyên cho lỗi máy chủ / mất kết nối
        Assert.Equal(UploadFlow.DescribeBatchFailure(ApiResult<CvUploadResponse>.Failure("boom", 500)),
                     UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Failure("boom", 500)));
        Assert.Equal("Không kết nối được máy chủ API", UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Failure("offline"))!.Value.Title);
        Assert.Null(UploadRules.DescribeUploadFailure(ApiResult<CvUploadResponse>.Success(new CvUploadResponse())));
    }

    [Fact]
    public void Request_level_rejection_rewrites_row_messages_and_stops_remaining_batches()
    {
        var a = new UploadItem { FileName = "a.pdf", State = UploadItemState.Uploading };
        var result = ApiResult<CvUploadResponse>.Failure("Hãy xác nhận (consentConfirmed = true) trước khi tải CV.", 400);
        UploadFlow.ApplyResult([a], result);
        UploadRules.ApplyRequestFailure([a], result);

        Assert.Equal(UploadItemState.Rejected, a.State);
        Assert.Null(a.CvId);                                      // không bao giờ coi là đã lưu
        Assert.StartsWith("Chưa lưu: ", a.Message);
        Assert.DoesNotContain("consentConfirmed", a.Message);

        Assert.True(UploadRules.StopsRemainingBatches(400));
        Assert.True(UploadRules.StopsRemainingBatches(429));
        Assert.False(UploadRules.StopsRemainingBatches(500));     // lỗi tạm thời: nhóm sau vẫn thử (giữ B5)
        Assert.False(UploadRules.StopsRemainingBatches(null));
        Assert.False(UploadRules.StopsRemainingBatches(200));
    }

    private static UploadItem Sending(string name) => new() { FileName = name, State = UploadItemState.Uploading };

    [Fact]
    public void Duplicate_with_existingCvId_becomes_existing_selectable_and_never_uses_a_fake_id()
    {
        var old = Guid.NewGuid();
        var dup = Sending("cv-anh-ro.png");
        var inBatch = Sending("cv-anh-ro-ban-sao.png");
        var anonymizedOld = Sending("cv-cu-da-an-danh.pdf");
        var fresh = Sending("moi.pdf");
        var freshId = Guid.NewGuid();
        var response = new CvUploadResponse
        {
            Files =
            [
                new() { FileName = "cv-anh-ro.png", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                        ErrorMessage = "Trùng với CV đã tải trước đó: \"ten-cu-cua-ung-vien.png\" (ngày 05/11/2026), đã ứng tuyển: Backend Developer (.NET).",
                        ExistingCvId = old, ExistingFileName = "ten-cu-cua-ung-vien.png",
                        ExistingUploadedAt = new DateTime(2026, 11, 4, 18, 30, 0, DateTimeKind.Utc),
                        ExistingJobTitles = ["Backend Developer (.NET)", "Tester (QA)", "Backend Developer (.NET)"] },
                new() { FileName = "cv-anh-ro-ban-sao.png", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                        ErrorMessage = "Trùng nội dung với file \"cv-anh-ro.png\" trong cùng lần tải." },
                new() { FileName = "cv-cu-da-an-danh.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                        ErrorMessage = "Trùng với một hồ sơ đã được ẩn danh hóa theo chính sách lưu trữ; không hiển thị thông tin hồ sơ cũ.", ExistingCvId = Guid.Empty },
                new() { FileName = "moi.pdf", Status = CvUploadStatuses.Accepted, CvId = freshId }
            ]
        };
        var result = ApiResult<CvUploadResponse>.Success(response);

        UploadFlow.ApplyResult([dup, inBatch, anonymizedOld, fresh], result);
        UploadRules.ApplyDuplicateDetails([dup, inBatch, anonymizedOld, fresh], result);

        Assert.Equal((UploadItemState.Existing, (Guid?)old, true, true), (dup.State, dup.CvId, dup.CanAnalyze, dup.Selected));
        Assert.Equal("Đã có từ ngày 05/11/2026; đã ứng tuyển: Backend Developer (.NET), Tester (QA).", dup.Message);   // giờ Việt Nam, không trùng job
        Assert.DoesNotContain("ten-cu-cua-ung-vien", dup.Message);                                                       // không hiện tên file cũ
        Assert.True(dup.HasDuplicateDetails);
        Assert.True(dup.IsDuplicate);

        Assert.Equal((UploadItemState.Rejected, (Guid?)null, false), (inBatch.State, inBatch.CvId, inBatch.CanAnalyze));
        Assert.Equal(CvUploadErrorCodes.Duplicate, inBatch.ErrorCode);                 // giữ ngữ nghĩa Duplicate, không gọi chung là lỗi
        Assert.Equal(("Trùng hồ sơ", BadgeVariant.Warning), UploadRules.UploadBadge(inBatch));
        Assert.Equal((UploadItemState.Rejected, (Guid?)null), (anonymizedOld.State, anonymizedOld.CvId));   // Guid.Empty không được dùng
        Assert.Equal((UploadItemState.Uploaded, (Guid?)freshId), (fresh.State, fresh.CvId));
        Assert.Equal(("Bị từ chối", BadgeVariant.Danger), UploadRules.UploadBadge(new UploadItem { FileName = "x", State = UploadItemState.Rejected }));
        Assert.Equal(StatusText.ForUpload(UploadItemState.Existing), UploadRules.UploadBadge(dup));
    }

    [Fact]
    public void Duplicate_details_match_rows_by_name_when_api_order_differs_and_ignore_failed_requests()
    {
        var first = Sending("a.pdf");
        var second = Sending("b.pdf");
        var id = Guid.NewGuid();
        var result = ApiResult<CvUploadResponse>.Success(new CvUploadResponse
        {
            Files =
            [
                new() { FileName = "b.pdf", Status = CvUploadStatuses.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate, ExistingCvId = id },
                new() { FileName = "a.pdf", Status = CvUploadStatuses.Accepted, CvId = Guid.NewGuid() }
            ]
        });
        UploadFlow.ApplyResult([first, second], result);
        UploadRules.ApplyDuplicateDetails([first, second], result);
        Assert.Equal((UploadItemState.Existing, (Guid?)id), (second.State, second.CvId));
        Assert.Equal("Đã có trong hệ thống; chưa ứng tuyển vị trí nào.", second.Message);   // API không gửi ngày / job

        var failed = Sending("c.pdf");
        UploadRules.ApplyDuplicateDetails([failed], ApiResult<CvUploadResponse>.Failure("offline"));
        Assert.Equal(UploadItemState.Uploading, failed.State);
    }

    [Theory]
    [InlineData(2026, 11, 4, 16, 59, DateTimeKind.Utc, "04/11/2026")]
    [InlineData(2026, 11, 4, 17, 0, DateTimeKind.Utc, "05/11/2026")]          // 00:00 giờ Việt Nam
    [InlineData(2026, 12, 31, 23, 0, DateTimeKind.Unspecified, "01/01/2027")] // API trả UTC không ghi Kind
    public void Duplicate_dates_use_vietnam_time(int y, int mo, int d, int h, int mi, DateTimeKind kind, string expected) =>
        Assert.Equal(expected, UploadRules.VnDate(new DateTime(y, mo, d, h, mi, 0, kind)));

    [Fact]
    public void Anonymous_mode_hides_server_duplicate_messages_that_may_name_another_candidate_file()
    {
        var legacy = new UploadItem { FileName = "cv.pdf", State = UploadItemState.Existing, CvId = Guid.NewGuid(), ErrorCode = CvUploadErrorCodes.Duplicate,
                                      Message = "Trùng với CV đã tải trước đó: \"Nguyen-Van-A.pdf\"." };
        var inBatch = new UploadItem { FileName = "b.pdf", State = UploadItemState.Rejected, ErrorCode = CvUploadErrorCodes.Duplicate,
                                       Message = "Trùng nội dung với file \"Tran-Thi-B.pdf\" trong cùng lần tải." };
        var detailed = new UploadItem { FileName = "c.pdf", State = UploadItemState.Existing, CvId = Guid.NewGuid(), ErrorCode = CvUploadErrorCodes.Duplicate,
                                        HasDuplicateDetails = true, Message = UploadRules.DuplicateText(DateTime.UtcNow, ["Tester (QA)"]) };
        var invalid = new UploadItem { FileName = "x.docx", State = UploadItemState.Invalid, Message = "Sai định dạng" };

        Assert.Equal("CV này đã có trong hệ thống.", UploadRules.DisplayMessage(legacy, anonymous: true));
        Assert.Equal("Trùng nội dung với một CV khác.", UploadRules.DisplayMessage(inBatch, anonymous: true));
        Assert.Equal(detailed.Message, UploadRules.DisplayMessage(detailed, anonymous: true));
        Assert.Equal("Sai định dạng", UploadRules.DisplayMessage(invalid, anonymous: true));
        Assert.Equal(legacy.Message, UploadRules.DisplayMessage(legacy, anonymous: false));
    }

    // ================= (3) Chữ đã đọc (A7 + A42) =================

    private static TextQualityDto Quality(string level, int chars = 724, decimal ratio = .85m, params string[] warnings) => new()
    {
        CharCount = chars, WordCount = chars / 5, LetterRatio = ratio, Level = level,
        MaybeMisread = level != TextQualityLevels.Good, Warnings = [.. warnings]
    };

    private static CvDetailDto Detail(Guid id, string status, TextQualityDto? quality = null, string? method = ExtractionMethods.ImageOcr,
        DateTime? anonymizedAt = null) =>
        new() { CvId = id, ExtractionStatus = status, ExtractionMethod = method, TextQuality = quality, AnonymizedAt = anonymizedAt,
                ExtractedText = "nội dung CV không được giữ" };

    [Fact]
    public void Detail_updates_status_and_quality_but_never_keeps_the_cv_text()
    {
        var id = Guid.NewGuid();
        var item = new UploadItem { FileName = "cv.png", State = UploadItemState.Uploaded, CvId = id, TextCheck = TextCheck.Reading };

        Assert.False(UploadRules.ApplyDetail(item, Detail(id, ExtractionStatuses.Pending)));
        Assert.Equal((TextCheck.Reading, (TextQualityDto?)null), (item.TextCheck, item.Quality));
        Assert.Equal(TextTone.Reading, UploadRules.TextStatus(item).Tone);
        Assert.Equal("Đang đọc chữ…", UploadRules.TextStatus(item).Label);

        Assert.True(UploadRules.ApplyDetail(item, Detail(id, ExtractionStatuses.Completed, Quality(TextQualityLevels.Low, 182, .68m, "Chỉ đọc được 182 ký tự"))));
        Assert.Equal(TextCheck.Done, item.TextCheck);
        Assert.True(item.MaybeMisread);
        Assert.True(item.NeedsTextReview);
        var low = UploadRules.TextStatus(item);
        Assert.Equal(TextTone.Review, low.Tone);
        Assert.Equal("⚠ Có thể đọc sai · Xem chữ đã đọc", low.Action);
        Assert.Equal("182 ký tự", low.Detail);
        // Không thuộc tính chuỗi nào của dòng giữ nội dung CV (ExtractedText của GET /api/cvs/{id})
        Assert.DoesNotContain(typeof(UploadItem).GetProperties().Where(p => p.PropertyType == typeof(string)),
            p => ((string?)p.GetValue(item))?.Contains("nội dung CV không được giữ") == true);

        // phản hồi của CV khác: bỏ qua
        Assert.False(UploadRules.ApplyDetail(item, Detail(Guid.NewGuid(), ExtractionStatuses.Failed)));
        Assert.True(item.MaybeMisread);
    }

    [Fact]
    public void Text_status_distinguishes_good_empty_low_failed_pending_timeout_offline_anonymized_and_missing()
    {
        UploadItem Row(Action<UploadItem> set) { var i = new UploadItem { FileName = "cv.pdf", State = UploadItemState.Uploaded, CvId = Guid.NewGuid() }; set(i); return i; }

        var good = UploadRules.TextStatus(Row(i => { i.ExtractionStatus = ExtractionStatuses.Completed; i.Quality = Quality(TextQualityLevels.Good, 1234); i.TextCheck = TextCheck.Done; }));
        Assert.Equal((TextTone.Good, "Đọc tốt", "1.234 ký tự", (string?)null), (good.Tone, good.Label, good.Detail, good.Action));

        var empty = UploadRules.TextStatus(Row(i => { i.ExtractionStatus = ExtractionStatuses.Completed; i.Quality = Quality(TextQualityLevels.Empty, 0, 0m, TextQuality.EmptyWarning); }));
        Assert.Equal((TextTone.Review, "Không có chữ"), (empty.Tone, empty.Label));
        Assert.Equal("⚠ Không có chữ · Xem chữ đã đọc", empty.Action);

        var failedItem = Row(i => { i.ExtractionStatus = ExtractionStatuses.Failed; i.TextCheck = TextCheck.Done; });
        var failed = UploadRules.TextStatus(failedItem);
        Assert.Equal((TextTone.Failed, "Không đọc được chữ"), (failed.Tone, failed.Label));
        Assert.True(failedItem.NeedsTextReview);
        Assert.False(failedItem.MaybeMisread);

        Assert.Equal(("Chưa đọc xong sau 2 phút", "Kiểm tra lại"),
            (UploadRules.TextStatus(Row(i => i.TextCheck = TextCheck.TimedOut)).Label, UploadRules.TextStatus(Row(i => i.TextCheck = TextCheck.TimedOut)).Action));
        Assert.Equal("Thử lại", UploadRules.TextStatus(Row(i => i.TextCheck = TextCheck.Unavailable)).Action);
        Assert.Equal("Hồ sơ đã được ẩn danh hóa", UploadRules.TextStatus(Row(i => UploadRules.MarkAnonymized(i))).Label);
        Assert.Equal("Không còn CV này trên máy chủ", UploadRules.TextStatus(Row(i => i.TextCheck = TextCheck.NotFound)).Label);
        Assert.Equal(TextTone.None, UploadRules.TextStatus(new UploadItem { FileName = "x.docx", State = UploadItemState.Invalid }).Tone);
    }

    [Fact]
    public void Anonymized_detail_stops_polling_and_clears_quality()
    {
        var id = Guid.NewGuid();
        var item = new UploadItem { FileName = "cv.pdf", State = UploadItemState.Existing, CvId = id, Quality = Quality(TextQualityLevels.Good),
                                    ExtractionStatus = ExtractionStatuses.Completed };
        Assert.True(UploadRules.ApplyDetail(item, Detail(id, ExtractionStatuses.Completed, anonymizedAt: DateTime.UtcNow)));
        Assert.Equal((TextCheck.Anonymized, (TextQualityDto?)null, (string?)null), (item.TextCheck, item.Quality, item.ExtractionStatus));
        Assert.False(item.NeedsTextReview);
    }

    [Fact]
    public void Text_dto_from_dialog_updates_the_row_only_for_the_same_cv()
    {
        var id = Guid.NewGuid();
        var item = new UploadItem { FileName = "cv.pdf", State = UploadItemState.Uploaded, CvId = id, TextCheck = TextCheck.TimedOut };
        Assert.False(UploadRules.ApplyText(item, new CvTextDto { CvId = Guid.NewGuid(), ExtractionStatus = ExtractionStatuses.Completed }));
        Assert.Equal(TextCheck.TimedOut, item.TextCheck);
        Assert.True(UploadRules.ApplyText(item, new CvTextDto { CvId = id, ExtractionStatus = ExtractionStatuses.Completed, Quality = Quality(TextQualityLevels.Good) }));
        Assert.Equal(TextCheck.Done, item.TextCheck);
    }

    [Fact]
    public void Review_and_reading_counts_only_consider_saved_cvs()
    {
        var low = new UploadItem { FileName = "a", State = UploadItemState.Uploaded, CvId = Guid.NewGuid(), ExtractionStatus = ExtractionStatuses.Completed,
                                   Quality = Quality(TextQualityLevels.Low), TextCheck = TextCheck.Done };
        var reading = new UploadItem { FileName = "b", State = UploadItemState.Existing, CvId = Guid.NewGuid(), TextCheck = TextCheck.Reading };
        var readingUnselected = new UploadItem { FileName = "c", State = UploadItemState.Uploaded, CvId = Guid.NewGuid(), TextCheck = TextCheck.Reading, Selected = false };
        var invalid = new UploadItem { FileName = "d", State = UploadItemState.Invalid };
        Assert.Equal(1, UploadRules.ReviewCount([low, reading, readingUnselected, invalid]));
        Assert.Equal(1, UploadRules.ReadingSelectedCount([low, reading, readingUnselected, invalid]));
    }

    [Theory]
    [InlineData(".68", "68%")]
    [InlineData(".849", "84%")]
    [InlineData("1", "100%")]
    [InlineData("0", "0%")]
    public void Letter_ratio_is_shown_as_a_truncated_percentage(string ratio, string expected) =>
        Assert.Equal(expected, UploadRules.RatioText(decimal.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Dialog_labels_and_errors_are_friendly()
    {
        Assert.Equal("OCR từ ảnh", UploadRules.MethodText(ExtractionMethods.ImageOcr));
        Assert.Equal("OCR từ PDF scan", UploadRules.MethodText(ExtractionMethods.PdfOcr));
        Assert.Equal("Chữ có sẵn trong PDF", UploadRules.MethodText(ExtractionMethods.PdfText));
        Assert.Equal("Chưa rõ", UploadRules.MethodText(null));
        Assert.Equal(("Đọc tốt", "Có thể đọc sai", "Không có chữ"),
            (UploadRules.LevelText(TextQualityLevels.Good), UploadRules.LevelText(TextQualityLevels.Low), UploadRules.LevelText(TextQualityLevels.Empty)));
        Assert.Equal("1.234", UploadRules.CountText(1234));

        Assert.Equal(("Hồ sơ đã được ẩn danh hóa", false), (UploadRules.TextLoadError(410, false).Title, UploadRules.TextLoadError(410, false).CanRetry));
        Assert.False(UploadRules.TextLoadError(404, false).CanRetry);
        Assert.True(UploadRules.TextLoadError(503, false).CanRetry);
        Assert.Equal("Máy chủ phản hồi quá lâu", UploadRules.TextLoadError(null, timedOut: true).Title);
        Assert.Equal("Không kết nối được máy chủ API", UploadRules.TextLoadError(null, timedOut: false).Title);
    }

    // ================= (4) Ước tính thời gian (A42) =================

    [Theory]
    [InlineData(0, false)] [InlineData(-1, false)] [InlineData(1, true)] [InlineData(500, true)] [InlineData(501, false)]
    public void Estimate_is_only_requested_for_1_to_500_selected_cvs(int count, bool expected) =>
        Assert.Equal(expected, UploadRules.ShouldEstimate(count));

    [Fact]
    public void Estimate_line_uses_api_text_only_when_it_matches_the_current_count()
    {
        var e = new RunEstimateDto { Count = 10, Text = "10 CV · khoảng 2 phút", BasedOnRuns = 3, AiSimulated = true };
        Assert.Equal("10 CV · khoảng 2 phút", UploadRules.EstimateLine(10, e));
        Assert.Equal("12 CV", UploadRules.EstimateLine(12, e));                    // vừa chọn thêm: chưa có ước tính mới
        Assert.Equal("Chưa chọn CV nào", UploadRules.EstimateLine(0, e));
        Assert.Equal("3 CV", UploadRules.EstimateLine(3, new RunEstimateDto { Count = 3, Text = " " }));
        Assert.Equal("4 CV", UploadRules.EstimateLine(4, null));

        Assert.Equal("Ước tính theo 3 lượt chấm gần nhất · AI giả lập", UploadRules.EstimateNote(10, e, loading: false, failed: false));
        Assert.Equal("Ước tính ban đầu", UploadRules.EstimateNote(2, new RunEstimateDto { Count = 2, Text = "2 CV · dưới 1 phút" }, false, false));
        Assert.Equal("Đang ước tính thời gian…", UploadRules.EstimateNote(12, e, loading: true, failed: false));
        Assert.Equal("Chưa ước tính được thời gian, vẫn phân tích được.", UploadRules.EstimateNote(12, null, loading: false, failed: true));
        Assert.Equal("Tích chọn CV để xem thời gian dự kiến.", UploadRules.EstimateNote(0, null, false, false));
    }

    // ================= (5) Client gọi API =================

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return respond(request, ct);
        }
    }

    private sealed class OpenTracker { public int Open, MaxOpen, Total; }

    private sealed class TrackedFile(string name, byte[] bytes, OpenTracker tracker, string contentType = "application/pdf") : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("Supplied file exceeds the maximum");
            tracker.Total++; tracker.Open++; tracker.MaxOpen = Math.Max(tracker.MaxOpen, tracker.Open);
            return new Tracked(bytes, tracker);
        }
        private sealed class Tracked(byte[] b, OpenTracker t) : MemoryStream(b)
        {
            private bool closed;
            protected override void Dispose(bool disposing) { if (!closed) { closed = true; t.Open--; } base.Dispose(disposing); }
        }
    }

    private static CvApiClient CvClient(HttpMessageHandler handler, int maxMb = 2) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") },
            Microsoft.Extensions.Options.Options.Create(new CvUploadOptions { MaxFileSizeMB = maxMb }), NullLogger<CvApiClient>.Instance);

    [Fact]
    public async Task Upload_sends_source_and_consent_fields_and_still_streams_files_one_at_a_time()
    {
        var tracker = new OpenTracker();
        var fields = new Dictionary<string, string>();
        var fileNames = new List<string>();
        int? openedBeforeSend = null;
        var stub = new Stub(async (request, ct) =>
        {
            openedBeforeSend = tracker.Total;
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                var disposition = part.Headers.ContentDisposition!;
                if (disposition.FileName is { } file) { fileNames.Add(file.Trim('"')); await part.CopyToAsync(Stream.Null, ct); }
                else fields[disposition.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CvUploadResponse()) };
        });
        var files = Enumerable.Range(1, 20).Select(i => (IBrowserFile)new TrackedFile($"cv-{i}.pdf", TestSupport.Pdf($"b38-{i}", 32 * 1024), tracker)).ToList();

        var result = await CvClient(stub).UploadAsync(files, CvSourceChannels.Referral, consentConfirmed: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(new Dictionary<string, string> { ["sourceChannel"] = "Referral", ["consentConfirmed"] = "true" }, fields);
        Assert.Equal(files.Select(f => f.Name), fileNames);
        Assert.Equal(0, openedBeforeSend);   // BrowserFileContent vẫn mở trễ: chưa file nào mở khi bắt đầu gửi
        Assert.Equal(1, tracker.MaxOpen);    // mỗi lúc chỉ một file truyền qua circuit
        Assert.Equal(20, tracker.Total);
        Assert.Equal(0, tracker.Open);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("email", true)]
    [InlineData("Email", false)]
    public async Task Upload_without_valid_source_or_consent_sends_nothing(string? source, bool consent)
    {
        var stub = new Stub((_, _) => throw new InvalidOperationException("Không được gửi request"));
        var tracker = new OpenTracker();
        await Assert.ThrowsAsync<ArgumentException>(() => CvClient(stub).UploadAsync([new TrackedFile("a.pdf", TestSupport.Pdf("a"), tracker)], source!, consent));
        Assert.Empty(stub.Urls);
        Assert.Equal(0, tracker.Total);
    }

    [Fact]
    public async Task Detail_text_and_estimate_use_the_A42_routes()
    {
        var id = Guid.NewGuid();
        var stub = new Stub((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/text") => new HttpResponseMessage(HttpStatusCode.Gone)
            { Content = JsonContent.Create(new { title = "CV đã được ẩn danh hóa", status = 410 }) },
            "/api/runs/estimate" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RunEstimateDto { Count = 7, Text = "7 CV · dưới 1 phút" }) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CvDetailDto { CvId = id, ExtractionStatus = ExtractionStatuses.Pending }) }
        }));
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.test/") };
        var cv = CvClient(stub);
        var runs = new RunApiClient(http, NullLogger<RunApiClient>.Instance);

        Assert.Equal(id, (await cv.GetCvAsync(id)).Data!.CvId);
        var gone = await cv.GetTextAsync(id);
        Assert.Equal((false, (int?)410), (gone.IsSuccess, gone.StatusCode));
        Assert.Equal("7 CV · dưới 1 phút", (await runs.EstimateAsync(7)).Data!.Text);
        Assert.Equal([$"GET /api/cvs/{id}", $"GET /api/cvs/{id}/text", "GET /api/runs/estimate?count=7"], stub.Urls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runs.EstimateAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runs.EstimateAsync(501));
        Assert.Equal(3, stub.Urls.Count);   // giá trị ngoài 0–500 không tạo request
    }

    // ================= (6) Hợp đồng với API thật (chạy trong bộ nhớ, A38 + A42) =================

    private sealed class Browserless(string name, byte[] bytes, string contentType) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => bytes.Length;
        public string ContentType => contentType;
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(bytes);
    }

    [Fact]
    public async Task Real_api_accepts_B38_metadata_stores_source_and_consent_reports_duplicate_and_estimates()
    {
        using var api = TestSupport.ApiFactory();
        var http = api.CreateDefaultClient();   // KHÔNG có handler tự thêm đồng ý: client B38 phải tự gửi
        var cv = new CvApiClient(http, Microsoft.Extensions.Options.Options.Create(new CvUploadOptions()), NullLogger<CvApiClient>.Instance);
        var runs = new RunApiClient(http, NullLogger<RunApiClient>.Instance);
        var bytes = TestSupport.Pdf("b38-hop-dong-" + Guid.NewGuid());

        var first = await cv.UploadAsync([new Browserless("cv-b38.pdf", bytes, "application/pdf")], CvSourceChannels.Referral, true);
        Assert.True(first.IsSuccess, first.ErrorMessage);
        var saved = Assert.Single(first.Data!.Files);
        Assert.Equal(CvUploadStatuses.Accepted, saved.Status);

        var detail = (await cv.GetCvAsync(saved.CvId!.Value)).Data!;
        Assert.Equal((CvSourceChannels.Referral, "Người quen giới thiệu"), (detail.SourceChannel, detail.SourceChannelName));
        Assert.NotNull(detail.ConsentConfirmedAt);
        Assert.Equal(ExtractionStatuses.Pending, detail.ExtractionStatus);   // worker đọc chữ tắt trong test
        Assert.Null(detail.TextQuality);

        var text = (await cv.GetTextAsync(saved.CvId.Value)).Data!;
        Assert.Equal((ExtractionStatuses.Pending, (string?)null, (TextQualityDto?)null), (text.ExtractionStatus, text.Text, text.Quality));

        // Tải lại cùng nội dung (nguồn khác): API báo trùng với existingCvId; giao diện dùng lại đúng mã CV cũ
        var again = await cv.UploadAsync([new Browserless("cv-b38-lan-2.pdf", bytes, "application/pdf")], CvSourceChannels.Website, true);
        var row = new UploadItem { FileName = "cv-b38-lan-2.pdf", State = UploadItemState.Uploading };
        UploadFlow.ApplyResult([row], again);
        UploadRules.ApplyDuplicateDetails([row], again);
        Assert.Equal((UploadItemState.Existing, saved.CvId), (row.State, row.CvId));
        Assert.StartsWith("Đã có từ ngày ", row.Message);
        Assert.EndsWith("; chưa ứng tuyển vị trí nào.", row.Message);
        Assert.Equal(CvSourceChannels.Referral, (await cv.GetCvAsync(saved.CvId.Value)).Data!.SourceChannel);   // nguồn CV cũ không đổi

        var estimate = await runs.EstimateAsync(3);
        Assert.True(estimate.IsSuccess, estimate.ErrorMessage);
        Assert.Equal(3, estimate.Data!.Count);
        Assert.Equal(estimate.Data.Text, UploadRules.EstimateLine(3, estimate.Data));
        Assert.StartsWith("3 CV", estimate.Data.Text);
    }

    [Fact]
    public async Task Real_api_rejects_a_raw_request_without_consent_and_the_web_explains_it()
    {
        using var api = TestSupport.ApiFactory();
        var http = api.CreateDefaultClient();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(TestSupport.Pdf("khong-dong-y")), "files", "cv.pdf" } };
        form.Add(new StringContent("Email"), "sourceChannel");
        var response = await http.PostAsync("api/cvs/upload", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var result = ApiResult<CvUploadResponse>.Failure(problem!["detail"].ToString()!, 400);
        var banner = UploadRules.DescribeUploadFailure(result)!.Value;
        Assert.Equal("Máy chủ chưa nhận lần tải này", banner.Title);
        Assert.DoesNotContain("consentConfirmed", banner.Message);
    }
}
