# B20 — Trang quản lý vị trí tuyển dụng

Ngày bàn giao: 18/11/2026 (Asia/Saigon). Phạm vi: B20 trong `C:\TTCN\A19_B26_SmartCv.zip`, đã đọc hướng dẫn B20 và toàn bộ Razor/CSS/service/helper/sidebar mẫu, đối chiếu với source hiện tại; không chép đè file mẫu.

## Git và SHA nền

- Repository duy nhất: `C:\TTCN\project\AI-CV-Screening`.
- Nhánh: `feature/b20-jobs-page`, tạo bằng `git switch --no-track -c feature/b20-jobs-page origin/main` sau khi fetch thành công.
- SHA nền đầy đủ: `b5fd7a22d93ede567f8c6dcc64ca977711e2f097`.
- Trước khi triển khai: `main...origin/main`, working tree/index sạch, không có file untracked, không có merge/rebase/cherry-pick/revert/sequencer dang dở; nhánh B20 chưa tồn tại ở local hoặc remote.
- Remote: `origin`, `https://github.com/TuyetAnh0101/AI-CV-Screening.git`. Đã `fetch origin --prune` và xác nhận nhánh B20 bằng `git branch --show-current` trước khi sửa code. Origin/main sau fetch vẫn bằng SHA trên.
- Git chạy với `-c safe.directory=C:/TTCN/project/AI-CV-Screening` cho từng lệnh vì tài khoản sandbox khác chủ sở hữu; không sửa Git config toàn cục. Fetch/switch cần quyền ghi `.git` ngoài sandbox.
- Không tạo worktree, clone, thư mục repository thứ hai. Không git add, commit, push, merge hoặc tạo PR.

Metadata UTF-8 không BOM: [base SHA](../artifacts/b20-base-sha.txt), [manifest](../artifacts/b20-commit-files.txt). `artifacts/` được ignore theo `.gitignore` hiện có; metadata, log, TRX, browser harness và ảnh không nằm trong source manifest.

## Chức năng hoàn thành

- Trang `/jobs` gọi `GET /api/jobs?activeOnly=false`; ba bộ lọc Đang tuyển / Ngừng tuyển / Tất cả kèm số lượng, tìm tên không phân biệt hoa thường và bỏ khoảng trắng ở hai đầu. Lọc/tìm chạy trên danh sách đã tải.
- Hiển thị tên/ID vị trí, số và tên kỹ năng bắt buộc, số cần tuyển, hạn nhận CV, tổng hồ sơ và sáu giai đoạn A21. Giá trị `Headcount`/`ClosingDate` trống có nhãn riêng. Không thêm dữ liệu mẫu vào ứng dụng thật.
- Trạng thái theo thứ tự ưu tiên Ngừng tuyển > Quá hạn > Đang tuyển; dùng `IsExpired` do API tính theo ngày Việt Nam. Bộ lọc Đang tuyển gồm cả vị trí đang hoạt động nhưng quá hạn, đúng hợp đồng B20/A21.
- `ApplicationCount`, `StageCounts`, `HasEvaluations` nullable được giữ nguyên. Không có thống kê được ghi rõ là chưa được cung cấp; số 0 vẫn là 0. Khóa giai đoạn thiếu hiển thị `—`, không tự gán 0. Vị trí không có tín hiệu truy cập nội bộ (`HasEvaluations == null`) không cho đổi trạng thái.
- Hộp thoại native `dialog`, có mô tả hệ quả ngừng/mở lại, nhắc sửa hạn khi mở lại vị trí quá hạn, Hủy/Escape, focus ban đầu ở Hủy, Tab/Shift+Tab trong hộp thoại và trả focus về nút mở hoặc tiêu đề khi dòng biến mất.
- Gọi đúng `POST /api/jobs/{id}/deactivate` và `/activate` với JSON `{}` qua `JobApiClient` hiện có. Chặn xác nhận trùng khi request đang chạy, không sửa danh sách lạc quan trước khi thành công.
- Khi POST lỗi/timeout/mất kết nối/phản hồi không khớp, khóa mọi thao tác đổi trạng thái. Người dùng chủ động bấm **Tải lại trạng thái** để GET xác minh; GET lỗi tiếp tục khóa. Không phát lại POST tự động. Sau GET thành công vẫn phải mở hộp thoại và xác nhận nếu muốn thao tác khác.
- Liên kết kết quả và giai đoạn: `/results?jobId=N&filter=New|Reviewing|Shortlisted|Interview|Rejected|OnHold`. Results lọc đúng dòng, giữ lựa chọn khi tải lại/điều hướng và khi đổi lượt/job; thay đổi bộ lọc không tải lại ranking nếu run/job không đổi.
- Giữ nguyên B19 `filter=flagged`. Khi kết hợp hai bộ lọc, dùng `filter=flagged&stage=Shortlisted` (ví dụ); bỏ một bộ lọc giữ bộ lọc còn lại. Cơ chế polling, stage decision, ẩn danh và optimistic concurrency hiện có được giữ nguyên.
- Jobs giải thích phạm vi: thống kê A21 gồm toàn bộ hồ sơ của vị trí, trong khi Results hiển thị một lượt chấm. Không giả định hai tập dữ liệu có cùng số lượng.
- Sidebar thêm **Quản lý › Vị trí tuyển dụng**, giữ các mục hiện có và menu mobile.
- Loading skeleton, danh sách thực sự trống, lọc không có kết quả, lỗi tải và retry; lỗi máy chủ không lộ chi tiết kỹ thuật. Rời trang hủy request qua cancellation token.

## Build và kiểm thử

Mọi lệnh chạy tại repository hiện tại. Build output tách trong `artifacts/b20/build`; không sửa solution/package/appsettings. Runsettings dùng môi trường Testing, không bật SQL/demo/external opt-in; các test A21 tích hợp dùng TestServer và EF InMemory, loại bỏ hosted workers theo fixture hiện có. Không chạy SQL Server, database migration, retention trên dữ liệu thật hoặc Gemini thật.

| Kiểm tra | Passed | Failed | Skipped | Ghi chú |
|---|---:|---:|---:|---|
| Baseline full regression | 2281 | 0 | 107 | Tổng 2388; baseline build 0 lỗi, 1 warning NU1900 |
| B20 riêng | 43 | 0 | 0 | Tổng 43; gồm Web client nối endpoint A21 thật trong bộ nhớ |
| Focused B20/A21/B19/B39/B41/Results/B34 | 287 | 0 | 1 | Tổng 288; skip demo B34 opt-in có sẵn |
| Full regression cuối | 2324 | 0 | 107 | Tổng 2431; thêm đúng 43 test B20, bỏ 0; danh sách 107 skipped không đổi |
| Browser QA | 10 nhóm | 0 | 0 | Chromium 156.0.8078.4, 14 ảnh, 0 JS/console errors |

Đối chiếu tên test có phân biệt hoa thường: thêm đúng 43 tên thuộc `B20JobsTests`, bỏ 0 test cũ, không thay đổi tên/skipped của 107 test opt-in. Skipped được đếm theo `UnitTestResult outcome="NotExecuted"` trong TRX (counter `notExecuted` của adapter xUnit là 0). Bảng trên chỉ là các lượt cuối đã hoàn tất.

Final restore/build: **0 lỗi, 0 warning**. Cảnh báo NU1900 ở baseline do không lấy được dữ liệu vulnerability NuGet; restore cuối `--force-evaluate` đã thành công, không tắt kiểm tra hay suppress warning. Restore ban đầu trong sandbox gặp SSL/NU1301, đã chạy lại thành công với quyền thích hợp.

Test logger của focused cuối có **1 cảnh báo vận hành**: `Overwriting results file` do ghi lại TRX của lượt phát triển trước; không phải compiler warning hoặc test failure. Test B20 riêng và full cuối ghi vào thư mục mới. Lượt focused trước khi sửa cấu hình fixture Production có 286 passed / 1 failed / 1 skipped; lượt cuối trong bảng đã thay thế kết quả đó.

Lệnh tái hiện chính:

```powershell
dotnet restore SmartCv.slnx --force-evaluate -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/b20/build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/b20/build
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b20/build --settings artifacts/b20/tests.runsettings --filter 'FullyQualifiedName~B20JobsTests'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b20/build --settings artifacts/b20/tests.runsettings --filter 'FullyQualifiedName~B20JobsTests|FullyQualifiedName~B19DashboardTests|FullyQualifiedName~ResultsUiTests|FullyQualifiedName~B34|FullyQualifiedName~JobCrudEndpointTests|FullyQualifiedName~B39|FullyQualifiedName~B41'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b20/build --settings artifacts/b20/tests.runsettings
```

Test B20 mới kiểm tra status, format nullable, whitelist sáu stage/URL, client HTTP, lọc/tìm, confirm/cancel, POST timeout đã áp dụng ở server, 400/401/403/404/409/500/503, khóa và GET xác minh, chống gửi trùng, hủy request, stats null/0, tương tác Razor thực tế, deep link sáu giai đoạn/Back/reload, kết hợp flagged, và Web client nối endpoint A21 thật trong bộ nhớ cùng danh sách chọn activeOnly ở Upload. Giữ nguyên toàn bộ test cũ.

Log/TRX: `artifacts/b20/baseline-restore.log`, `baseline-build.log`, `baseline-tests.log`, `baseline-tests/baseline.trx`, `final-restore.log`, `final-build.log`, `b20-final-tests/b20-final.trx`, `focused-final-tests/`, `full-tests/`, `browser-build.log`, `browser-run.log`, `browser-results.json`, `verification.json`.

## Giao diện và responsive đã kiểm tra

Deep Teal theo token sẵn có, nền sáng, glow mint/indigo nhẹ ở thẻ tổng quan. Giữ font Inter local và shell hiện có; không sửa CSS chung. Panel radius 16px, card/dialog 12–16px, padding 16–20px, shadow/viền nhẹ. Tên dài, kỹ năng dài và mô tả được xuống dòng, không cắt mất nội dung.

Đã chạy Chromium thật với Web Blazor thật và API fixture chỉ tồn tại trong harness ignored. Đo DOM xác nhận ở **1440, 1024, 768, 390, 360, 320px**: `documentElement.scrollWidth == innerWidth`, không có thành phần Jobs vượt mép viewport. 1440 dùng bảng; 1024/768 và mobile chuyển thành thẻ để đọc được toàn bộ dữ liệu. Giữ sidebar/menu responsive hiện có.

- Tiêu đề trang: 28px desktop/tablet, 24px mobile; tiêu đề phần 18px; dialog 19px.
- Tên vị trí 14px; nội dung chính 13px; nhãn 12px; badge 11px; số liệu tổng quan 26px desktop, 24px mobile.
- Ô tìm, nút lọc và nút đầu trang đo cùng chiều cao 44px ở cả sáu viewport. Giai đoạn dùng chip có label và số riêng, badge có chữ và dấu nhận diện màu.
- Kiểm trực tiếp ảnh desktop/mobile/tablet và dialog 320px; kiểm tìm kiếm, hủy, bàn phím/focus, ngừng/mở lại, quá hạn, null/0, loading/empty/error/retry và Results flagged+stage. Không tuyên bố kiểm trên thiết bị vật lý, screen reader, Firefox/WebKit hay chứng nhận WCAG.

Ảnh minh chứng (local, không stage):

| Nội dung | Ảnh |
|---|---|
| Bảng desktop | [1440px](../artifacts/b20/screenshots/jobs-1440.png) |
| Tablet | [1024px](../artifacts/b20/screenshots/jobs-1024.png), [768px](../artifacts/b20/screenshots/jobs-768.png) |
| Mobile | [390px](../artifacts/b20/screenshots/jobs-390.png), [360px](../artifacts/b20/screenshots/jobs-360.png), [320px](../artifacts/b20/screenshots/jobs-320.png) |
| Xác nhận | [Ngừng tuyển](../artifacts/b20/screenshots/jobs-confirm-stop.png), [Mở lại quá hạn 320px](../artifacts/b20/screenshots/jobs-confirm-expired-320.png) |
| Trạng thái trang | [Loading](../artifacts/b20/screenshots/jobs-loading.png), [Empty](../artifacts/b20/screenshots/jobs-empty.png), [Error](../artifacts/b20/screenshots/jobs-error.png), [Không khớp bộ lọc](../artifacts/b20/screenshots/jobs-no-match.png) |
| Chưa xác minh POST | [Khóa ghi và GET xác minh](../artifacts/b20/screenshots/jobs-unverified.png) |
| Results | [Stage + flagged](../artifacts/b20/screenshots/results-stage-and-flagged.png) |

## Các lỗi đã sửa và giới hạn

- Sửa truyền string parameter trong Razor để StageFilter nhận giá trị URL thực, không nhận literal tên property.
- Sửa cleanup modal khi Blazor đã tháo phần tử khỏi DOM và truyền ID nút mở rõ ràng, để Escape/Hủy trả focus chính xác; đã kiểm browser lại.
- Thay giả định thống kê luôn tồn tại trong ZIP bằng xử lý nullable đúng DTO A21; giữ số 0 riêng.
- Sửa fixture test mới về alias `Jobs`, cancellation callback, assertion chỉ kiểm dòng ranking thay vì KPI toàn lượt, và cấu hình Production theo startup gate hiện có. Không sửa test cũ hoặc startup guard. Một lượt test phát triển bị treo ở callback của fixture mới đã được sửa và chạy lại; lượt build bị khóa DLL trong quá trình đó được chạy lại sau khi dừng tiến trình test. Các lượt chưa hoàn tất không được tính là pass cuối.
- Chưa kiểm với backend/SQL production hoặc dữ liệu CV thật. Kết quả browser fixture không thay thế xác minh vận hành trên dữ liệu thật; các test API A21 không cần SQL vẫn chạy trong regression.
- Nút Tạo vị trí và liên kết tên/Sửa trỏ đúng hợp đồng `/jobs/new`, `/jobs/{id}`. Trang form thuộc B21 chưa có trên base nên các URL đó vẫn có thể hiển thị Not Found cho tới khi B21 được triển khai. Không tạo trang giả hoặc triển khai ngoài phạm vi.
- Không thêm phân trang server do A21 hiện trả danh sách; tìm/lọc vẫn trên tập dữ liệu được API cung cấp. Với tập vị trí rất lớn cần đánh giá riêng.

## Manifest toàn bộ file tạo/sửa/xóa

**14 file: 9 A mới (untracked, chưa stage), 5 M sửa, 0 D xóa.** Manifest chứa đúng mỗi path một dòng, không có artifact/ảnh/bin/obj/secret.

| A/M/D | File |
|---|---|
| A | `docs/B20-implementation-report.md` |
| M | `src/SmartCv.Web/Components/Layout/NavMenu.razor` |
| A | `src/SmartCv.Web/Components/Pages/Jobs.razor` |
| A | `src/SmartCv.Web/Components/Pages/Jobs.razor.css` |
| M | `src/SmartCv.Web/Components/Pages/Results.razor` |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor` |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor.cs` |
| A | `src/SmartCv.Web/Components/Shared/ConfirmDialog.razor` |
| A | `src/SmartCv.Web/Components/Shared/ConfirmDialog.razor.css` |
| A | `src/SmartCv.Web/Components/Shared/ConfirmDialog.razor.js` |
| A | `src/SmartCv.Web/Helpers/JobsWorkspace.cs` |
| A | `src/SmartCv.Web/Helpers/JobText.cs` |
| M | `src/SmartCv.Web/Services/JobApiClient.cs` |
| A | `tests/SmartCv.Tests/B20JobsTests.cs` |

Phạm vi source chỉ B20: Web Jobs/dialog/helper/client/sidebar và phần tối thiểu Results để nhận liên kết giai đoạn. Không sửa Core/API/Infrastructure/database/migration, package/solution, B19 Dashboard, B39 Playground, B41 Prompt Metrics hoặc các test hiện có. Các chức năng cũ được đối chiếu bằng baseline và regression.

## Checkpoint bàn giao

- `git branch --show-current`: `feature/b20-jobs-page`.
- `git rev-parse HEAD` và `origin/main`: `b5fd7a22d93ede567f8c6dcc64ca977711e2f097`, bằng SHA nền.
- `git diff --check`: exit 0. Kiểm tra bổ sung whitespace/conflict marker cho cả file mới cũng sạch.
- `git diff --cached --name-status`: rỗng; không có staged file. Không có unmerged, merge/rebase/cherry-pick/revert/sequencer đang dở.
- Manifest: đúng **14 path unique**, khớp union toàn bộ tracked diff và untracked status; **9 A / 5 M / 0 D**. Hai metadata và toàn bộ ảnh/log/harness được ignore, không nằm trong source manifest.
- [Audit Git cuối](../artifacts/b20/final-git-audit.txt), [đối chiếu test/manifest](../artifacts/b20/verification.json), [skipped baseline](../artifacts/b20/skipped-baseline.txt), [skipped cuối](../artifacts/b20/skipped-final.txt).
- Các host API/Web/Chromium của browser harness đã tự dừng; các lượt test do phiên này tạo đã kết thúc. Không dừng dịch vụ hoặc build server dùng chung của người dùng.

`git status --short --branch`:

```text
## feature/b20-jobs-page
 M src/SmartCv.Web/Components/Layout/NavMenu.razor
 M src/SmartCv.Web/Components/Pages/Results.razor
 M src/SmartCv.Web/Components/Results/RankingTable.razor
 M src/SmartCv.Web/Components/Results/RankingTable.razor.cs
 M src/SmartCv.Web/Services/JobApiClient.cs
?? docs/B20-implementation-report.md
?? src/SmartCv.Web/Components/Pages/Jobs.razor
?? src/SmartCv.Web/Components/Pages/Jobs.razor.css
?? src/SmartCv.Web/Components/Shared/ConfirmDialog.razor
?? src/SmartCv.Web/Components/Shared/ConfirmDialog.razor.css
?? src/SmartCv.Web/Components/Shared/ConfirmDialog.razor.js
?? src/SmartCv.Web/Helpers/JobText.cs
?? src/SmartCv.Web/Helpers/JobsWorkspace.cs
?? tests/SmartCv.Tests/B20JobsTests.cs
```

Dừng tại bàn giao báo cáo cho người dùng review. Chưa stage/commit/push/merge/PR; người dùng tự thực hiện sau khi xem báo cáo.
