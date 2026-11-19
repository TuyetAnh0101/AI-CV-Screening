# B23 — Báo cáo triển khai trước commit/push

Ngày báo cáo: 19/11/2026 (UTC+7). Trạng thái: thay đổi chưa stage; người dùng tự duyệt và commit/push.

## 1. Tên và phạm vi đã xác minh

Tên chính xác trong guide: **“B23 – Trang kho CV”**. Bảng phân công đầu guide mô tả **“Trang Kho CV `/cvs` + hộp thoại Gắn ứng viên”**, phụ thuộc A23 và A24 cho thao tác gắn.

Đã triển khai trang `/cvs`, điều hướng, tìm kiếm/lọc/phân trang phía server, hạn lưu, xem file và hộp thoại gắn/đổi ứng viên. Hộp thoại thuộc B23 theo ZIP, không chuyển sang phạm vi B24. Không có trang chi tiết ứng viên, gộp, xuất/xóa dữ liệu, bộ đánh giá, thay đổi scoring hoặc migration.

## 2. ZIP và tài liệu đã đọc

Đọc ZIP trực tiếp bằng `System.IO.Compression.ZipFile`, không chép đè source từ ZIP. Kiểm kê: **128 entry, 56 file, 13 file B23**. Bản kiểm kê cục bộ: `artifacts/b23-zip-inventory.txt`.

Đã đọc `A23_B25_SmartCv/HUONG_DAN_A23_A24_A43_B23_B24_B25.md`: bảng phụ thuộc và thứ tự tích hợp; chuẩn bị chung; A23 và quy tắc tìm có dấu; A24 và hợp đồng gắn ứng viên/NeedsDecision; A43 và dữ liệu sau ẩn danh; phần “Trước khi làm”; toàn bộ B23; ghi chú tương thích B24/B25; bảng lỗi thường gặp. Các lệnh commit/push/chép đè trong guide không được thực hiện vì chỉ thị trực tiếp của người dùng có ưu tiên.

13 file mẫu B23 đã đọc, relative với `A23_B25_SmartCv/B23/`:

```text
src/SmartCv.Web/Services/ServiceCollectionExtensions.cs
src/SmartCv.Web/Services/CvApiClient.cs
src/SmartCv.Web/Services/CandidateApiClient.cs
src/SmartCv.Web/Components/Pages/Cvs.razor
src/SmartCv.Web/Components/Pages/Cvs.razor.css
src/SmartCv.Web/Components/Cvs/AttachCandidateDialog.razor
src/SmartCv.Web/Components/Cvs/AttachCandidateDialog.razor.css
src/SmartCv.Web/Components/Layout/NavMenu.razor
src/SmartCv.Web/Components/_Imports.razor
src/SmartCv.Web/Helpers/CvLibraryText.cs
src/SmartCv.Web/Helpers/Debouncer.cs
tests/SmartCv.Tests/Web/CvLibraryTextTests.cs
tests/SmartCv.Tests/Web/DebouncerTests.cs
```

## 3. Ảnh và cơ sở thiết kế

**ZIP không có ảnh mẫu, SVG, PDF hoặc sơ đồ**; số file hình/PDF là 0. Vì vậy không có ảnh trong ZIP để mở xem.

Thiết kế dùng token SCApp hiện tại trong `wwwroot/app.css`, Inter cục bộ, sidebar Deep Teal, mint nhẹ; tham chiếu bố cục và source Home/B19, Jobs/B20, JobEdit/B21, Results, Runs/B26 và Upload. CSS mới dùng isolation, không sửa CSS toàn hệ thống. Ảnh QA sinh từ dữ liệu tổng hợp đã được mở xem trực tiếp, liệt kê ở mục 14.

## 4. Git, remote và SHA nền

- Repository duy nhất: `C:\TTCN\project\AI-CV-Screening`.
- Remote `origin` (fetch/push): `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Nhánh: `feature/b23-cv-library-ui` (theo yêu cầu người dùng; guide dùng tên ngắn hơn `feature/b23-cv-library`).
- SHA nền/HEAD: **`e3c4e4eee274565f83f9a77e446e6549d3b5a937`**.
- Lúc bắt đầu: nhánh A43, working tree sạch; staged, unstaged, untracked và unmerged đều trống. Không thấy merge/rebase/cherry-pick/revert/sequencer đang dở; không có nhánh B23 local/remote sau fetch.
- Đã fetch/prune và tạo nhánh B23 từ `origin/main`. Main đã chứa A23, A24, A43 và B26. Không đưa thay đổi chưa commit từ nhiệm vụ khác vào B23.
- Dùng `git -c safe.directory=C:/TTCN/project/AI-CV-Screening ...` do khác chủ sở hữu trong sandbox; không đổi global Git config.

Không tạo worktree/clone/project mới; không add/commit/push/PR, merge/rebase/reset/stash/clean/restore/cherry-pick.

## 5. Chức năng đã hoàn thành

- Header Kho CV, tổng theo bộ lọc, làm mới, lối tắt `/upload`; một mục menu mới trong nhóm Tuyển dụng.
- Tìm trên API thật A23, debounce 400 ms; dùng `CvListQuery.NormalizeSearch/Create/Parse` của Core; không gọi API khi từ khóa không rỗng ngoài 2–100 ký tự. Xóa từ khóa giữ các bộ lọc còn lại.
- Job gồm cả vị trí ngừng tuyển từ `GetJobsAsync(activeOnly: false)`; stage A35, 5 nguồn A38, 4 trạng thái đọc chữ/ẩn danh. Mã query giữ nguyên; thay filter về trang 1.
- Bảng desktop và card responsive. Một CV có nhiều Job: hiện từng cặp Job/stage, ưu tiên Job đang lọc, các Job còn lại trong `+N vị trí`. Tên Job dài được xuống dòng đầy đủ.
- Điểm đọc từ `cv.LatestScore` của DTO A23; không tính lại hoặc lấy PromptTest. `null` là “Chưa chấm”; 0 vẫn là 0.
- Hạn lưu theo ngày UTC+7, cảnh báo ≤14 ngày/quá hạn, null là `—`. Hồ sơ A39 có diện mạo trầm và không có thao tác.
- Phân trang server, mặc định 20; có page/pageSize trong URL, hỗ trợ pageSize hợp lệ từ đường dẫn theo A23. Không tải cả kho về client.
- Các trạng thái skeleton, kho trống, bộ lọc rỗng kết quả, query sai, lỗi API, thử lại, trang vượt giới hạn và lỗi tải danh sách Job độc lập.
- Request danh sách có deadline 30 giây, cancellation và generation guard; input mới vô hiệu ngay kết quả cũ. Dispose hủy các tác vụ và bỏ đăng ký sự kiện.
- F5, Back/Forward và đổi query đồng bộ qua URL. Trang vượt giới hạn có nút về trang đầu, không gửi trang 0.
- Hộp thoại A24: điền gợi ý; dùng đúng `CandidateRules`; chọn người đã có với tìm kiếm debounce/phân trang; xử lý `NeedsDecision` bằng lựa chọn rõ ràng; chỉ gửi PUT khi HR bấm lưu/chọn/xác nhận. Kết quả ghi chưa xác định không tự gửi lại.

## 6. Đối chiếu guide B23

| Yêu cầu guide | Thực hiện / bằng chứng |
|---|---|
| Route `/cvs` | `CvLibrary.razor`, renderer test và Chromium |
| Debounce 400 ms, 1 ký tự chờ thêm | `Debouncer`, chặn độ dài theo A23; Chromium gõ React tạo đúng 1 request |
| Gợi ý tìm có dấu | Có lời nhắc dưới ô tìm, không quảng cáo tìm không dấu |
| Kết quả cũ về muộn bị bỏ | Cancellation + generation guard; test handler cố tình không tuân thủ cancellation |
| Job cả ngừng tuyển, stage, nguồn, trạng thái | API Job thật và constants Core; test query kết hợp, mã không dịch |
| Filter ghi lên địa chỉ, F5 | Có cả page/pageSize; test renderer và browser F5/Back/Forward |
| Ứng viên/tên file, nguồn, ngày tải | Render theo DTO và B36; dữ liệu thiếu có ký hiệu phù hợp |
| Stage riêng từng vị trí, lý do loại | Component `CvApplication`; reason chỉ dùng mã hợp lệ, không đưa nội dung tự do vào tooltip khi ẩn danh |
| Điểm gần nhất của Screening | Đọc DTO; integration test qua A23 thật xác nhận 82, không dùng điểm PromptTest 95 |
| Hạn lưu, cảnh báo 14 ngày | Helper ngày Việt Nam, test hôm nay/quá hạn/null/biên 14–15 ngày |
| Hồ sơ đã ẩn danh mờ, không nút | Kiểm tra cả DTO “legacy” cố tình còn PII; override mọi dữ liệu danh tính |
| Xem file tab mới | URL từ BaseAddress client tới `/api/cvs/{id}/file`, `noopener noreferrer`; không render link cho A39 hoặc khi B36 bật |
| Gắn/Đổi ứng viên, người mới/người có sẵn | Hộp thoại B23, gợi ý A24, validation Core, tìm có phân trang |
| NeedsDecision, gắn người trùng / vẫn tạo mới | Hai lựa chọn riêng; không tự gộp; test cả CandidateId và ConfirmNew |
| Ẩn danh B36 | Dùng `AnonymityState` hiện có; không render PII trong DOM/tooltip/ARIA; bật lại đóng hộp thoại |
| Menu Tuyển dụng › Kho CV | Chỉ thêm một dòng; test giữ upload/results/prompts/jobs/runs |
| Loading / trống / lỗi / retry | Có trạng thái riêng; HTTP 400/404/500/503, timeout, retry có test |
| Tổng trang/tổng CV | Footer hiển thị tổng kết quả, trang hiện tại/tổng trang và Trước/Sau; không hiện elapsedMs trên giao diện HR |

## 7. Component, helper và service

Audit source thực tế gồm `CvLibraryContracts.cs`, `CvListQuery.cs`, `CvEndpoints.cs`, `CvApiClient.cs`, `ApiClientBase.cs`, `ApiResult.cs`, `JobApiClient.cs`, `AnonymityState.cs`, NavMenu, Upload/Results/Jobs/Runs và helper/component/token/test liên quan. Xác minh A23/A24 đã có trên main, file endpoint và DTO hiện tại là căn cứ, không thay bằng bản mẫu cũ.

`CvLibrary` điều phối URL và các bộ lọc. `CvLibraryWorkspace` giữ request và dữ liệu trang. `CvLibraryFilter` dùng validation A23, tạo URL/query. `CvLibraryText` chỉ định dạng/trình bày và thông báo lỗi cố định. `CvApplication` giữ Job đi cùng stage. `AttachCandidateDialog` bao gồm vòng đời modal, tìm người có sẵn và quyết định gắn; JS nhỏ dùng native dialog để giữ focus và trả focus khi đóng.

`CvApiClient` được thêm phương thức danh sách/gợi ý/gắn/link file, giữ nguyên `UploadAsync`, `GetCvAsync`, `GetTextAsync`. `CandidateApiClient` chỉ có tìm kiếm phục vụ B23. DI thêm client này, tắt HttpClient log URL cho hai client có query cá nhân. `ApiClientBase` thêm riêng đường gọi private, giữ nguyên hành vi các phương thức cũ. Không đổi Core/API/Infrastructure/database.

## 8. Khác biệt có chủ đích so với mẫu ZIP

1. Không ghi đè `CvApiClient`, NavMenu, DI hoặc `_Imports`. Thêm từng phần vào source mới của main; trang dùng import riêng.
2. Tên trang/component là `CvLibrary` thay vì `Cvs`; test dùng namespace `SmartCv.Tests.B23Ui` và alias rõ ràng, tránh CS0118/đụng namespace.
3. Thiết kế lại bảng/card, panel và bộ lọc mobile thu gọn. Không dùng bảng rộng tối thiểu 1080px như mẫu ZIP.
4. URL bao gồm phân trang, theo dõi LocationChanged, kiểm tra query trùng/sai thay vì chỉ đọc filter lúc khởi tạo. Không phát request “tất cả CV” thay cho từ khóa không hợp lệ.
5. Tăng cường cancellation/deadline/dispose, kiểm tra page DTO và loại phản hồi cũ. Thông báo lỗi dùng câu cố định, không đưa ProblemDetails kỹ thuật lên UI.
6. Chặn xem file/gắn ứng viên khi B36 bật để đáp ứng yêu cầu bảo vệ danh tính của người dùng. Mẫu ZIP chỉ che chữ trong bảng.
7. Native dialog, focus/keyboard thực, phân trang người có sẵn, bảo vệ thao tác ghi chưa xác nhận. Không tự tìm bằng tên ứng viên khi chuyển tab.
8. Không hiển thị elapsedMs trong UI; DTO vẫn giữ nguyên. Không tạo link `/candidates/{id}` vì đây là B24 và route chưa được triển khai trên main.

## 9. Dữ liệu cá nhân và ẩn danh

- `AnonymityState` là state B36 hiện có, mặc định che khi chưa đọc được lựa chọn trình duyệt. Chỉ lưu preference bool qua cơ chế hiện có, không lưu DTO hoặc dữ liệu cá nhân.
- Khi bật: alias CV ổn định thay tên, “CV đã ẩn tên” thay tên file; không render email, file gốc, trường nhập liên hệ, link file hoặc reason nhạy cảm. Hộp thoại đang mở được tháo khỏi cây component và các tác vụ bị hủy.
- A39 luôn ưu tiên hơn preference UI: tên/file thành “Hồ sơ đã ẩn danh”, không khôi phục qua CandidateId hay dữ liệu khác, không có nút kể cả khi B36 tắt. Test cố tình cung cấp DTO còn tên/file/email để kiểm tra phòng thủ này.
- Không render StoredPath/FileHash/raw text. Không gọi API lấy text, không tạo endpoint tải file mới, không bỏ Development gate của A23/A24.
- Log lỗi private chỉ chứa tên thao tác cố định và HTTP status; không ghi query hoặc exception có URI/PII. Tắt built-in HttpClient logging cho CvApiClient/CandidateApiClient để tránh URL tìm kiếm bị log. Các lỗi trên UI cũng không phản chiếu server detail.
- Test và browser fixture đều dùng thông tin tổng hợp. Không dùng CV thật, không gọi Gemini thật, không chạy migration hoặc chỉnh database thật.

## 10. File A/M/D

<!-- FILE_STATUS_BEGIN -->
```text
M src/SmartCv.Web/Components/Layout/NavMenu.razor
M src/SmartCv.Web/Services/ApiClientBase.cs
M src/SmartCv.Web/Services/CvApiClient.cs
M src/SmartCv.Web/Services/ServiceCollectionExtensions.cs
A  docs/B23-implementation-report.md
A  src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor
A  src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.css
A  src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.js
A  src/SmartCv.Web/Components/CvLibrary/CvApplication.razor
A  src/SmartCv.Web/Components/CvLibrary/CvApplication.razor.css
A  src/SmartCv.Web/Components/Pages/CvLibrary.razor
A  src/SmartCv.Web/Components/Pages/CvLibrary.razor.css
A  src/SmartCv.Web/Helpers/CvLibraryFilter.cs
A  src/SmartCv.Web/Helpers/CvLibraryText.cs
A  src/SmartCv.Web/Helpers/CvLibraryWorkspace.cs
A  src/SmartCv.Web/Helpers/Debouncer.cs
A  src/SmartCv.Web/Services/CandidateApiClient.cs
A  tests/SmartCv.Tests/B23CvLibraryTests.cs
```
<!-- FILE_STATUS_END -->

Không có file xóa. Chỉ Web, một file test B23 mới và báo cáo này thuộc manifest commit. Artifacts/log/ảnh/bin/obj không thuộc manifest.

## 11. Baseline build/test

Baseline tại SHA mục 4, trước build triển khai mới:

| Lần chạy | Passed | Failed | Skipped | Ghi chú |
|---|---:|---:|---:|---|
| Restore / build baseline | — | 0 | — | Restore thành công; build 0 warning, 0 error |
| Full test lần đầu trong sandbox | 2227 | 467 | 121 | 465 failure có Windows EventLog trong lỗi/stack; 2 test worker timeout/timing, gồm Idle_worker thực tế 7 so với khoảng 2–5 |
| Full test baseline với EventLog tắt | 2694 | 0 | 121 | 4 phút 55 giây; không sửa test/source baseline |

Thiết lập chỉ cho tiến trình test: `Logging__EventLog__LogLevel__Default=None`. Giữ thiết lập này trong các lần regression để lỗi quyền Windows Event Log không che kết quả nghiệp vụ. Logs/TRX nằm trong `artifacts/`.

## 12. Focused tests

- **44/44 B23 Passed, 0 Failed, 0 Skipped** qua `B23CvLibraryTests`.
- Sau chỉnh CSS cuối, chạy lại 44 test B23 và test worker cũ bị lỗi timing: **45 Passed, 0 Failed, 0 Skipped**.
- Bao phủ strict URL/A23, Unicode và khoảng trắng, query trùng, API query thật, không gọi khi sai, log private, debounce/cancel/dispose, request cũ cố tình về muộn, timeout, page DTO lệch, tích hợp client/trang với pipeline A23 thật trên InMemory, Screening khác PromptTest, null khác 0, nhiều Job, A39/B36 trong HTML và thuộc tính, lỗi/retry, các filter, pagination/F5/Back/Forward, menu, validation/NeedsDecision/ConfirmNew/tìm người có sẵn và chỉ PUT khi quyết định rõ ràng.
- Không sửa, xóa hoặc skip test A23/A24/A43 hay test giao diện cũ.

## 13. Full regression

Lần đầu sau triển khai: **2737 Passed, 1 Failed, 121 Skipped, tổng 2859**, 3 phút 59 giây. Failure duy nhất: `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`, đếm 6 thay vì khoảng 2–5 trong 3,5 giây chờ. Cùng test này đã lỗi ở baseline lần đầu (đếm 7), và chạy riêng sau triển khai đã Passed. Đây là test worker phụ thuộc thời gian thực, không nằm trong source B23; chưa quy kết là lỗi B23 hoặc thay đổi để ép xanh.

Đã dừng các tiến trình QA do phiên này tạo; không dừng Web có sẵn của người dùng. Lần thứ hai với mức song song mặc định vẫn có đúng test worker đó Failed (đếm 7): 2737 Passed / 1 Failed / 121 Skipped, tổng 2859, 6 phút 43 giây. Hai kết quả mặc định được giữ nguyên trong TRX, không ghi đè để che failure.

Chạy thêm toàn bộ suite với `--settings artifacts/b23-regression.runsettings`, giới hạn `MaxParallelThreads=2` và vẫn bật song song collection. Đây là cấu hình chỉ cho lần kiểm tra, không sửa file test/project, không loại test. Log adapter xác nhận `parallel test collections = on [2 threads]`.

<!-- FINAL_REGRESSION_BEGIN -->
**Kết quả full regression cuối với 2 luồng xUnit: 2738 Passed, 0 Failed, 121 Skipped, tổng 2859; khoảng 5 phút 30 giây.** Adapter xác nhận giới hạn 2 luồng. Toàn bộ 44 test B23 nằm trong lần này; test worker Idle_worker cũng Passed (3 giây). Không giảm số test, không sửa/skip test cũ. TRX: `artifacts/b23-tests/b23-full-regression-bounded.trx`; log: `artifacts/b23-full-regression-bounded.log`.
<!-- FINAL_REGRESSION_END -->

Lệnh chạy bản cuối (build outputs riêng để tránh khóa DLL/EXE của Web đang chạy):

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/b23-build
dotnet build SmartCv.slnx --artifacts-path artifacts/b23-build --no-restore
$env:Logging__EventLog__LogLevel__Default = 'None'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --artifacts-path artifacts/b23-build --no-build --no-restore --settings artifacts/b23-regression.runsettings
```

File runsettings chỉ cấu hình xUnit `MaxParallelThreads=2`, `ParallelizeTestCollections=true`, `DiagnosticMessages=true`. Các test case và điều kiện Skip giữ nguyên.

Build cuối thành công, **0 error, 1 warning NU1900**: chưa lấy được dữ liệu kiểm tra lỗ hổng từ `https://api.nuget.org/v3/index.json`. Không thay package hoặc tắt audit trong project để che warning. Restore đầu ra riêng từng gặp lỗi SSL/chứng thực của sandbox, sau đó restore ngoài sandbox thành công; warning audit của Web còn lưu trong kết quả restore.

## 14. Browser QA và sáu viewport

Đã chạy **Chromium headless 1248 qua CDP**, không phải chỉ render HTML. Web B23 chạy bản build thật, API QA là HTTP fixture cục bộ, có 27 CV tổng hợp. Kiểm tra dữ liệu/API thật A23 được thực hiện riêng qua integration test trên InMemory. Không tuyên bố đã kiểm thử browser với SQL Server hoặc CV thật.

| Viewport rộng | Scroll width đo được | Kết quả / bố cục |
|---:|---:|---|
| 1440 | 1425 | PASS, bảng 6 cột cân đối, không tràn ngang |
| 1024 | 1009 | PASS, card một cột trong vùng nội dung/sidebar |
| 768 | 753 | PASS, card; tên/Job xuống dòng |
| 390 | 375 | PASS, bộ lọc thu gọn, card một cột |
| 360 | 345 | PASS, bộ lọc thu gọn, đủ Job/nguồn/điểm |
| 320 | 305 | PASS, ô lọc xếp dọc khi mở, card một cột; hộp thoại và nút lưu không cắt chữ |

Chênh 15px là thanh cuộn dọc, không phải overflow. Đã kiểm tra trực tiếp ảnh của cả sáu viewport sau sửa mobile. Ảnh cục bộ không thuộc commit:

```text
artifacts/b23-browser/cvs-1440.png
artifacts/b23-browser/cvs-1024.png
artifacts/b23-browser/cvs-768.png
artifacts/b23-browser/cvs-390.png
artifacts/b23-browser/cvs-360.png
artifacts/b23-browser/cvs-320.png
artifacts/b23-browser/cvs-anonymous.png
artifacts/b23-browser/cvs-error.png
artifacts/b23-browser/attach-duplicates.png
artifacts/b23-browser/cvs-320-filters.png
artifacts/b23-browser/attach-320.png
```

Tất cả ảnh trên đã mở xem. Phát hiện từ ảnh và đã sửa: bộ lọc ban đầu quá cao trên mobile; border input/select chưa đồng bộ; nhãn nút lưu bị cắt ở dialog 320px. Ảnh `attach-320.png` đã chụp và xem lại sau sửa cuối.

Browser PASS: gõ React tạo đúng **1 request**; chặn từ khóa một ký tự; xóa tìm giữ filter; Job+stage; F5; phân trang và Back/Forward; ẩn danh không còn tên/file trong DOM; preference còn sau F5; validation thiếu tên; NeedsDecision; error 500 không lộ server detail; Tab từ tên tới email, Shift+Tab quay lại; focus trong dialog, Escape đóng và trả focus về nút mở; mở filter ở 320px không overflow; mở `+N vị trí` đọc đầy đủ Job dài. JSON phép đo nằm trong `artifacts/b23-browser/`.

Accessibility đã kiểm: hierarchy h1/h2/h3, label input/select, caption/th, ARIA busy/status/alert, badge có chữ, focus-visible; skeleton có reduced-motion. Chưa chạy screen reader hoặc bộ audit axe, không tuyên bố chứng nhận WCAG.

## 15. Lỗi phát hiện và xử lý

- Razor nhận nhầm biến tên `code` thành directive trong attribute: dùng expression rõ `@(code)`, build lại thành công.
- Test hộp thoại độc lập ban đầu tìm tab sau khi đã hoàn tất gắn nhưng fixture không tháo dialog: tách test đúng vòng đời, không sửa nghiệp vụ để ép test.
- Helper browser ban đầu bấm trước khi circuit kết nối sau F5; thêm điều kiện chờ hydration. Helper cũng từng bắt nhầm dialog reconnect của Blazor sau Escape; đổi selector sang `.attach-dialog`. Xác minh Escape/focus thực đã PASS.
- Các lỗi bố cục phát hiện bằng ảnh nêu ở mục 14 đã sửa bằng CSS isolation.
- EventLog/khóa output/NuGet là các giới hạn môi trường ghi riêng, không chỉnh worker hoặc test cũ để né.

## 16. Giới hạn còn lại

- 121 test có điều kiện về SQL Server, OCR/tessdata hoặc fixture ngoài máy vẫn Skipped theo cơ chế sẵn có; không bật chúng bằng cách trỏ vào database thật.
- Không đo tốc độ LIKE/collation SQL Server, không thử tải file thật trong browser. A23 chịu trách nhiệm tìm theo nội dung và số điện thoại; Web không mô phỏng tìm kiếm trên danh sách đang xem.
- Browser dùng fixture tổng hợp; luồng A24 production với SQL Server vẫn cần người dùng kiểm tra trong môi trường Development đã cấu hình. Không nới gate để chạy ngoài môi trường cho phép.
- Test worker timing cũ vẫn có thể Failed ở mức song song mặc định trên máy này; suite giới hạn 2 luồng Passed. Các lần Failed được công bố ở mục 13, không phải kết quả xanh mặc định.
- NU1900 còn tồn tại; thông tin audit package chưa tải lại được. Không có dependency mới.
- Không thực hiện B24/B25/A43. CV chưa gắn ứng viên vẫn có thể được gắn bằng hộp thoại B23; không có link chi tiết giả.

## 17. Git status cuối

<!-- GIT_STATUS_BEGIN -->
```text
## feature/b23-cv-library-ui
 M src/SmartCv.Web/Components/Layout/NavMenu.razor
 M src/SmartCv.Web/Services/ApiClientBase.cs
 M src/SmartCv.Web/Services/CvApiClient.cs
 M src/SmartCv.Web/Services/ServiceCollectionExtensions.cs
?? docs/B23-implementation-report.md
?? src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor
?? src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.css
?? src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.js
?? src/SmartCv.Web/Components/CvLibrary/CvApplication.razor
?? src/SmartCv.Web/Components/CvLibrary/CvApplication.razor.css
?? src/SmartCv.Web/Components/Pages/CvLibrary.razor
?? src/SmartCv.Web/Components/Pages/CvLibrary.razor.css
?? src/SmartCv.Web/Helpers/CvLibraryFilter.cs
?? src/SmartCv.Web/Helpers/CvLibraryText.cs
?? src/SmartCv.Web/Helpers/CvLibraryWorkspace.cs
?? src/SmartCv.Web/Helpers/Debouncer.cs
?? src/SmartCv.Web/Services/CandidateApiClient.cs
?? tests/SmartCv.Tests/B23CvLibraryTests.cs
```
<!-- GIT_STATUS_END -->

## 18. git diff --check

<!-- DIFF_CHECK_BEGIN -->
Exit code: 0. Không có output; không phát hiện lỗi whitespace/conflict marker trong tracked diff. File mới được kiểm tra trailing whitespace riêng.
<!-- DIFF_CHECK_END -->

## 19. git diff --cached --name-status

<!-- CACHED_STATUS_BEGIN -->
Output rỗng. Index không có file staged; chưa chạy git add.
<!-- CACHED_STATUS_END -->

## 20. Manifest và kiểm tra hai chiều

Metadata cục bộ, đều được `.gitignore` loại khỏi commit:

- `artifacts/b23-base-sha.txt`: đúng SHA 40 ký tự tại mục 4.
- `artifacts/b23-commit-files.txt`: mỗi dòng một đường dẫn relative, không trùng; bao gồm source/test/báo cáo cần duyệt.

<!-- MANIFEST_CHECK_BEGIN -->
PASS: 18 file, 4 M / 14 A / 0 D; không trùng, tất cả tồn tại. So sánh hai chiều với git status --porcelain=v1 --untracked-files=all: 0 đường dẫn thiếu, 0 đường dẫn thừa. Không có artifacts/bin/obj/log/ảnh/TRX trong manifest. Index rỗng. Manifest bao gồm chính báo cáo này.
<!-- MANIFEST_CHECK_END -->

Người dùng duyệt giao diện, code, báo cáo và manifest rồi tự stage/commit/push. Không đưa artifacts, profile trình duyệt, logs, screenshots, TRX, bin/obj hoặc dữ liệu CV vào commit.
