# B15 — So sánh tỷ lệ đúng giữa các phiên bản prompt

## Git và phạm vi

- Repository: `D:\Projects\AI-CV-Screening`.
- Nhánh: `feature/B15-prompt-accuracy-comparison`.
- Base/HEAD: `996d7da7fcc35415f6b24bea8dbb9a57f3cdbab2`, từ `origin/main`, đã merge B12–B14.
- Working tree sạch trước khi tạo nhánh; không mang thay đổi từ nhánh B14.
- Không commit, push, tạo PR hoặc merge. Không sửa backend, scoring, migration,
  Upload, Results, editor, activation hoặc worker/progress B14. Không triển khai B16.

## Dữ liệu và nghiệp vụ

B15 dùng API thật qua hai phương thức mới của `PromptApiClient`:

- `GET /api/prompt-tests?evaluationSetId={id}` trả `PromptTestRunDto` thực tế của A15/A16.
- `GET /api/prompt-tests/compare?baseRunId={id}&candidateRunId={id}` dùng A17 có sẵn
  để xác minh tính tương đương; không gọi Gemini hoặc tạo lượt chạy.

`PromptTestSummaryDto` cũ không mang đủ metadata. DTO A15/A16 hiện có sẵn
`Temperature`, `KeywordWeight`, `AiWeight`, `PassThreshold`, `TotalLabeledCount`,
`UnscoredCount`, `KeywordOnlyCount` và các counter nullable; không tạo DTO trùng.

Mỗi EvaluationSet có bảng riêng. Chọn lượt Completed có thống kê hợp lệ mới nhất
của mỗi phiên bản theo `CompletedAt` giảm dần, rồi `RunId` giảm dần. Không lấy
Queued/Running/Failed hoặc dùng 0% thay cho null. Loại dữ liệu âm, vượt 100%,
mẫu số bằng 0, counter thiếu/không khớp và accuracy không khớp công thức A16.

Mẫu số thực tế của A16 là **TotalLabeledCount**, tức CV của lượt còn nhãn HR hiện
tại, có thể khác TotalItems ban đầu. Accuracy = CorrectCount / TotalLabeledCount,
làm tròn 2 chữ số theo A16. Correct + Missed + WrongPick + Unscored phải bằng mẫu số.
CV chưa chấm được vẫn nằm trong mẫu số, được hiển thị riêng, không gán thành Không đạt.

## Cao nhất và tính công bằng

Lượt đầu bảng (ID phiên bản tăng dần) là mốc. Các lượt còn lại được A17 xác minh
với mốc: cùng cấu hình, danh sách CV và nhãn HR. Đối chiếu thêm counter/accuracy
với dữ liệu danh sách để không dùng xác nhận đã lệch số liệu. Không đủ xác nhận,
API lỗi, Verdict NotComparable, một lượt duy nhất, hoặc có Unscored/KeywordOnly:
vẫn hiển thị số liệu mô tả nhưng không công bố Cao nhất.

Khi cả bảng được xác minh, chọn tỷ lệ cao nhất; đồng tỷ lệ ưu tiên ít Bỏ sót,
rồi ID phiên bản tăng dần, sau đó RunId. Không dựa vào tên phiên bản, không tự
kích hoạt. Cao nhất chỉ trong các lượt hiển thị; không khẳng định chất lượng AI
trên mọi tập dữ liệu.

Xác minh A17 theo từng cặp; không phải một transaction đọc chung cho toàn bộ bảng.
Nếu HR sửa nhãn liên tục giữa các request, cần làm mới; UI từ chối xếp hạng khi
các số liệu đối chiếu không khớp. Không có API batch snapshot toàn bảng trong B15.

## Tích hợp và UI

- Component mới `AccuracyCompare` nằm dưới panel B14, có bộ chọn EvaluationSet riêng.
- Nhãn Đang dùng nhận trực tiếp danh sách Versions của trang/B13, không có active riêng.
- Tín hiệu Completed B14 tăng `accuracyRefresh`, làm mới B15 không cần F5.
  Không thêm polling, POST hoặc nút B16.
- Bộ chọn đổi nhanh hủy request cũ, dùng generation guard để bỏ phản hồi về muộn;
  dispose hủy request. Có loading/error/retry, empty state và thông báo dữ liệu bị loại.
- Các cột số căn phải, thanh tỷ lệ đúng, chỉ bản cao nhất dùng gradient AI,
  Bỏ sót đỏ/hồng, Chọn nhầm cam. Bảng cuộn ngang trong khung trên mobile, có caption,
  scope header, tên vùng cuộn và focus bàn phím.
- Đã đọc tài liệu ZIP, code mẫu và xem trực tiếp cả bốn ảnh, trọng tâm ảnh 4.

## Kiểm thử ngày 11/11/2026

- `dotnet build`: thành công, **0 warning, 0 error**.
- `AccuracyComparisonTests`: **16 pass, 0 fail**. Bao gồm lọc trạng thái/null/
  accuracy sai/mẫu số 0, thứ tự CompletedAt/RunId, tách EvaluationSet, đồng hạng,
  Unscored, A17 không tương đương, số liệu đổi giữa request, render counter/active/
  Cao nhất/giờ Việt Nam, empty/error và refresh signal không tự polling/POST.
- Bộ đầy đủ `dotnet test --no-build`: **1.191 pass, 0 fail, 31 skipped**, tổng 1.222.
  Test dùng biến môi trường EventLog=None riêng cho tiến trình, không sửa config dự án.
- Các test SQL Server tùy chọn bị skip vì chưa có cấu hình database test riêng;
  không coi các test đó là đã chạy. Không chạy Gemini, không tạo dữ liệu database nhóm.
- Chrome headless chụp component render thật với HTTP fixture cách ly, được ghi
  nhãn rõ **không phải kết quả Gemini thật**. Viewport desktop 1440px có page width
  1440px; mobile 390px có page width 375px và bảng 850px cuộn trong vùng riêng.
- Ảnh ngoài repository: `%TEMP%/SCApp-B15-desktop.png`, `SCApp-B15-mobile.png`.
- `git diff --check`: đạt. Không có file ngoài B15.

## Tồn đọng ngoài B15

Đã tái hiện bằng HTTP fixture theo contract thật: `PromptApiClient.GetTestsAsync`
của B14 deserialize `PromptTestSummaryDto` cũ có `CorrectCount/MissedCount/
WrongPickCount` không nullable. A15 trả các counter null khi Queued/Running trong
`PromptTestRunDto`. Vì vậy B14 có thể báo lỗi định dạng khi danh sách có lượt chưa
hoàn thành. B15 dùng phương thức mới với đúng DTO và không sửa luồng B14 theo giới
hạn nhiệm vụ. Cần xử lý riêng trong B14/A15 integration; ảnh fixture và test B15
không phải bằng chứng đã chạy end-to-end với SQL Server/Gemini thật.

## File bàn giao

1. `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor` — mới: tải/hiển thị và xác minh dữ liệu.
2. `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor.css` — mới: style riêng responsive.
3. `src/SmartCv.Web/Helpers/AccuracyComparisonView.cs` — mới: validation/latest/best thuần, dễ test.
4. `src/SmartCv.Web/Services/PromptApiClient.cs` — sửa: bổ sung GET A15/A17, giữ API B12–B14.
5. `src/SmartCv.Web/Components/Pages/Prompts.razor` — sửa: gắn B15 và tín hiệu refresh B14.
6. `tests/SmartCv.Tests/AccuracyComparisonTests.cs` — mới: unit/component tests.
7. `docs/B15-prompt-accuracy-comparison.md` — mới: tài liệu này.
