# Báo cáo B10 — Trạng thái phân tích CV

Ngày: 11/11/2026 (Asia/Saigon).

## 1. Tổng quan

- Nhánh: `feature/B10-processing-status`.
- Commit nền: `aad69f281a7de86c70603d157d2a4ae11d392521`.
- Working tree ban đầu sạch, không có file staged/untracked B9.
- B9 đã merge vào main: `227c4a6`, chứa `8d931f5`; `git merge-base --is-ancestor 8d931f5 main` trả 0.
- B7: merge `4c5842a`; B8: merge `1f1b833`; A13: `d5d58ab`.
- Đã fetch/prune, switch main, pull fast-forward thành công rồi mới tạo nhánh B10. B10 chưa tồn tại khi kiểm tra local/remote sau fetch.
- Trạng thái: hoàn thành triển khai và kiểm thử tự động; kiểm thử bằng trình duyệt/dữ liệu seed thật chưa chạy.
- Chưa commit, push, tạo PR hoặc merge. Không sửa database, API A13, processor, Gemini, upload hay B11–B16.

## 2. Chức năng

| Mục | Thực hiện / kết quả |
|---|---|
| B10.1 | Dải Queued/Running có trạng thái thực, ProcessedItems/TotalItems, thông báo 3 giây. Ghi rõ số xử lý gồm CV lỗi. Không tính phần trăm hoặc chia cho TotalItems. Icon nhấp nhẹ, reduced-motion và role=status. |
| B10.2 | PeriodicTimer 3 giây chỉ khởi động sau render tương tác khi Ready và Queued/Running. Mỗi vòng chờ API tuần tự; cập nhật qua InvokeAsync. CancellationToken, phiên tải và disposed chặn phản hồi cũ; DisposeAsync hủy timer/request. Đổi Run/Job hoặc Làm mới hủy vòng cũ. Completed/Failed/Cancelled dừng tự cập nhật. Lỗi tạm giữ dữ liệu và thử lại theo chu kỳ; 404/401/403 dừng, có Làm mới. Không reload trang hoặc gọi Gemini lại. |
| B10.3 | Pending/Failed không hiện rank, keyword/AI/final số giả, thanh điểm giả hoặc dự đoán Đạt/Không đạt. Bộ sắp xếp B7 vẫn đặt Evaluation chưa hoàn tất ở cuối. |
| B10.4 | Dòng Failed hoặc ExtractionStatus=Failed có nền hồng, nhãn lỗi, thông báo đã lọc. Chi tiết Failed có khung đỏ, lý do nếu API cung cấp, hướng kiểm tra file và nút quay lại. Không kết luận PDF/OCR nếu thiếu bằng chứng. Lỗi kết nối API dùng trạng thái trang/cảnh báo cập nhật riêng. |
| B10.5 | Nhãn Chỉ keyword; ScoreBar/ScoreRing dùng màu trung tính sẵn có B8/B9. AI luôn là dấu — cho KeywordOnly, giữ điểm keyword/final thật. Chi tiết cảnh báo phương pháp Keyword, không hiển thị Reasoning hoặc raw response như kết quả Gemini thành công. HTTP 503 được mô tả là lỗi Gemini, không phải lỗi đọc CV. |
| B10.6 | Cần chú ý đếm theo job đang chọn trong run: lỗi trước; KeywordOnly chỉ đếm Evaluation Completed không thuộc nhóm lỗi. Một CV không cộng hai lần. Đổi bộ lọc và polling cập nhật thống kê. |
| B10.7 | Giữ cấu trúc B9: vòng điểm, trọng số, kỹ năng, bằng chứng, CV gốc, backlink và query. Thiếu AiScore không hiện Reasoning như AI đã thành công. Completed/Pending/Failed/KeywordOnly có trạng thái riêng. |

### Nguồn dữ liệu thực tế

- RunStatus: `RankingResponse.RunStatus`, bổ sung trạng thái mới nhất từ `RunProgressDto.Status`.
- Evaluation.Status, ScoreSource, ErrorMessage, điểm: DTO A13 ranking/detail.
- ExtractionStatus không có trong DTO A13 ranking/detail; bảng đối chiếu `RunProgressDto.Items` (B6), theo EvaluationId. Chi tiết dùng Evaluation.Status và ErrorMessage đã ghi nhận, không tự suy diễn trạng thái trích xuất.
- ProcessedItems/TotalItems: `GET /api/runs/{id}`; danh sách lượt là fallback khi request tiến độ thất bại.
- Trước B10, Results chưa có polling; đã có phiên tải và CancellationToken cho thay đổi query. Không có RunHero.razor trong source thực tế; hero nằm trong Results.razor.
- Khi đọc tiến độ rồi ranking ở polling, bảng được đọc sau trạng thái để tránh dừng ở terminal nhưng giữ bảng trước lúc hoàn tất. Lần tải đầu đọc lại ranking nếu trạng thái tiến độ thay đổi trong lúc tải.

## 3. Danh sách file đầy đủ

| File | Thêm/Sửa | Lý do | Thuộc B10? |
|---|---|---|---|
| src/SmartCv.Web/Components/Pages/Results.razor | Sửa, tracked | Polling, lifecycle, tiến độ, đối chiếu extraction, Cần chú ý | Có |
| src/SmartCv.Web/Components/Pages/Results.razor.css | Sửa, tracked | Thống kê, hiệu ứng nhẹ, reduced-motion | Có |
| src/SmartCv.Web/Components/Results/RankingTable.razor | Sửa, tracked | Lỗi trích xuất, lý do lỗi, ẩn điểm/rank chưa hoàn tất, ẩn AI KeywordOnly | Có |
| src/SmartCv.Web/Components/Results/RankingTable.razor.css | Sửa, tracked | Lý do lỗi dễ đọc, ngắt dòng | Có |
| src/SmartCv.Web/Components/Pages/EvaluationDetail.razor | Sửa, tracked | Khung Failed, hướng xử lý, KeywordOnly, thiếu AI | Có |
| src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css | Sửa, tracked | Màu Failed/Pending/KeywordOnly | Có |
| src/SmartCv.Web/Components/Shared/FitBadge.razor | Sửa, tracked | Lọc tooltip lỗi và thống nhất nhãn Chỉ keyword | Có |
| src/SmartCv.Web/Helpers/Display.cs | Sửa, tracked | Ánh xạ thông báo extraction đã biết sang nội dung an toàn | Có |
| tests/SmartCv.Tests/ResultsUiTests.cs | Sửa, tracked | Mock tiến độ thực; polling, chuyển trạng thái, lỗi, timeout, dispose, phản hồi cũ, extraction, thống kê | Có |
| tests/SmartCv.Tests/FitLevelUiTests.cs | Sửa, tracked | Cập nhật assertion nhãn Chỉ keyword theo yêu cầu B10 | Có |
| docs/B10-processing-status.md | Thêm, untracked | Báo cáo và lệnh Git đề xuất | Có |

Không có file staged. TRX nằm trong TestResults bị Git ignore, không nằm trong danh sách stage.

## 4. Kiểm thử và bằng chứng

| Test | Kết quả | Bằng chứng |
|---|---|---|
| Build solution thật | PASS | `dotnet build SmartCv.slnx --no-restore`: 0 warning, 0 error, lần cuối 3,76 giây |
| Toàn bộ unit/component/API fixture suite | PASS | `dotnet test SmartCv.slnx --no-build --no-restore --logger 'trx;LogFileName=B10-full.trx' --verbosity quiet`: 828 passed, 0 failed, 28 skipped / 856, 57 giây |
| Queued và TotalItems=0 | PASS | B10_queued_zero_items_is_not_fake_completion: 0/0, không hiển thị hoàn tất giả |
| Running, tiến độ thật | PASS | Without_query_opens_latest_screening_run_and_lists_runs; dải 1/3 từ mock API |
| Running → Completed/Failed/Cancelled | PASS | B10_poll_updates_terminal_status_without_navigation_and_stops, 3 trường hợp; không đổi URL, không còn request sau terminal |
| Chu kỳ 3 giây, không render loop/poll trùng | PASS | Kiểm tra không request mới trong 500ms đầu; đúng số ranking request sau tick, chờ thêm 3,2 giây sau terminal không tăng |
| Pending Evaluation, Failed, điểm null | PASS | ResultsUiTests, FitLevelUiTests và EvaluationDetailUiTests; dấu — và không nhận xét AI giả |
| Extraction Failed và không đếm trùng KeywordOnly | PASS | B10_extraction_failure_has_priority_over_keyword_and_is_counted_once |
| KeywordOnly, AI null, ErrorMessage null, HTTP 503 | PASS | Các test nhãn/thanh điểm B8, Unavailable_ai_is_never_presented_as_success, Dynamic_weights_and_cap_require_explicit_backend_evidence, cùng test thống kê B10 |
| API timeout và hồi phục | PASS | B10_timeout_is_connection_warning_and_recovers_on_next_tick: giữ bảng, không lộ chi tiết, tick sau hồi phục |
| API 404/500, mất kết nối | PASS | B10_poll_failure_preserves_data_and_disposal_stops_requests và test lỗi API B7/B9 |
| Chuyển RunId/JobId, request cũ về muộn | PASS | Late_response_of_previous_run_does_not_override_new_selection; B10_late_poll_cannot_overwrite_new_run_or_disposed_component |
| Dispose timer/request, không cập nhật sau dispose | PASS | Các test trên giữ request polling đang chạy, dispose rồi thả phản hồi; không phát sinh request tiếp. B9 có Leaving_page_cancels_the_in_flight_request |
| Regression B7/B8/B9 và upload/API fixtures | PASS | Suite đầy đủ pass; bộ UI riêng trước các bổ sung cuối: 122/122 pass |
| SQL Server integration yêu cầu môi trường riêng | NOT RUN | 28 test được framework skip; xem TRX để biết từng lý do |
| Seed Run #4, Lý Văn Minh, Dương Mỹ Linh trên DB thật | NOT RUN | Chỉ dùng mock có kiểm soát; không truy vấn hoặc sửa SmartCvDb để tạo dữ liệu nghiệm thu |
| Responsive và thao tác bằng trình duyệt | NOT RUN | Có giữ CSS responsive/scroll của B7/B9 và bổ sung wrap; chưa nghiệm thu trực quan bằng trình duyệt |

Bằng chứng máy: `tests/SmartCv.Tests/TestResults/B10-full.trx`. Có thể đọc kết quả UI riêng cũ tại `B10-ui.trx`; TRX full mới nhất là bằng chứng cuối cùng.

Lần test đầu trong sandbox lỗi quyền Windows EventLog. Đã chạy lại ngoài sandbox. Một build trung gian phải retry copy DLL vì test do phiên này chạy còn giữ DLL; không kill tiến trình hay xóa bin/obj. Đã chờ test kết thúc rồi build cuối sạch. Hai lỗi UI phát hiện trong quá trình kiểm thử (CTS đã dispose; assertion thiếu thông báo lỗi mới) đã được sửa và suite cuối pass.

## 5. Vấn đề / giới hạn còn tồn tại

| Mô tả | Nguyên nhân | Mức độ | Phạm vi | Hướng xử lý |
|---|---|---|---|---|
| Chưa nghiệm thu responsive và dữ liệu seed thật | Chưa chạy phiên trình duyệt/API/SQL dành cho nghiệm thu | Cần nghiệm thu | B10 | Chạy Web/API và kiểm tra desktop/mobile, các seed nếu tồn tại; không UPDATE DB sản xuất |
| Không xác định chính xác PDF hỏng hay OCR lỗi khi API không cung cấp | DTO A13 chi tiết thiếu ExtractionStatus và nguyên nhân phân loại; ErrorMessage có thể chung | Giới hạn dữ liệu | Hợp đồng API ngoài B10 | Hiện đúng lỗi đã ghi nhận; chỉ bổ sung hợp đồng trong nhiệm vụ API riêng nếu cần |
| Lý do không thuộc danh sách an toàn được thay bằng hướng liên hệ quản trị viên | ErrorMessage có thể chứa đường dẫn, URL, khóa hoặc chi tiết kỹ thuật | Chủ ý bảo vệ dữ liệu | B10 | Quản trị viên xem log; không đưa raw lỗi ra UI |

Không phát hiện hoặc sửa lỗi upload/Gemini ngoài nhiệm vụ. Không đổi System Prompt, model, retry policy hoặc API key.

## 6. Kiểm tra Git cuối

- Nhánh đúng B10, HEAD vẫn là commit nền; `origin/main..HEAD` rỗng.
- `git diff --check` không có lỗi whitespace.
- 10 file tracked modified và 1 file báo cáo untracked trong bảng trên; staged diff rỗng.
- Đã kiểm tra branch, status, diff check/stat/name-status, cached name-status, untracked và log origin/main..HEAD.

## 7. Đối chiếu ảnh

Đã đọc toàn bộ `B7B16/HUONG_DAN_B7_B16.md` trong ZIP, gồm B.3/B.6–B.9/B.16/lỗi thường gặp. Chỉ trích xuất ba ảnh vào thư mục tạm, không giải nén đè source.

- `1-ket-qua-xep-hang.png`: áp dụng nền hồng dòng lỗi, dấu —, lý do lỗi ngay dưới tên/file và thống kê Cần chú ý. Giữ bảng, màu Deep Teal, typography và spacing B7–B9 hiện tại.
- `2-ket-qua-chi-keyword.png`: nhãn trung tính, AI —, thanh điểm xám; giữ keyword/final thật.
- `3-chi-tiet-cv.png`: giữ vòng điểm, trọng số, kỹ năng/bằng chứng và các khối B9; cảnh báo đặt trước lưới chi tiết, không che nút CV gốc.
- Cải tiến: giải thích số xử lý có bao gồm lỗi; giải thích quy tắc đếm tránh trùng; cảnh báo API nhẹ giữ dữ liệu; tooltip lỗi đã lọc; reduced-motion. Mục đích là phân biệt tiến độ, lỗi CV và dự phòng Keyword rõ ràng hơn.

## 8. Lệnh PowerShell đề xuất — chỉ hiển thị, chưa thực thi

Chỉ chạy sau khi người dùng duyệt báo cáo. Không tạo PR hoặc merge. Đoạn kiểm tra allowlist dừng khi có file ngoài phạm vi.

```powershell
Set-Location 'D:\Projects\AI-CV-Screening'
if ((git branch --show-current) -ne 'feature/B10-processing-status') { throw 'Sai nhánh B10' }
git status --short --branch
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'Diff có lỗi' }
$b10Files = @(
    'src/SmartCv.Web/Components/Pages/Results.razor'
    'src/SmartCv.Web/Components/Pages/Results.razor.css'
    'src/SmartCv.Web/Components/Results/RankingTable.razor'
    'src/SmartCv.Web/Components/Results/RankingTable.razor.css'
    'src/SmartCv.Web/Components/Pages/EvaluationDetail.razor'
    'src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css'
    'src/SmartCv.Web/Components/Shared/FitBadge.razor'
    'src/SmartCv.Web/Helpers/Display.cs'
    'tests/SmartCv.Tests/ResultsUiTests.cs'
    'tests/SmartCv.Tests/FitLevelUiTests.cs'
    'docs/B10-processing-status.md'
)
$changed = @(git diff --name-only) + @(git diff --cached --name-only) + @(git ls-files --others --exclude-standard)
$outside = @($changed | Where-Object { $_ -notin $b10Files })
if ($outside.Count -gt 0) { $outside; throw 'Có file ngoài B10. Dừng để kiểm tra.' }
git add -- $b10Files
if ($LASTEXITCODE -ne 0) { throw 'Stage thất bại' }
git diff --cached --name-status
git diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged diff có lỗi' }
git diff --cached
# Dừng và đọc staged diff trước khi tiếp tục các lệnh dưới.
dotnet build SmartCv.slnx --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build thất bại' }
dotnet test SmartCv.slnx --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Test thất bại' }
git commit -m 'feat(B10): implement processing states and AI fallback UI'
if ($LASTEXITCODE -ne 0) { throw 'Commit thất bại' }
git push -u origin feature/B10-processing-status
```

B10 đã được kiểm tra. Tôi chưa commit, chưa push, chưa tạo Pull Request và chưa merge. Đang chờ người dùng duyệt báo cáo.
