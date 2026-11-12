# B34 — Giai đoạn hồ sơ trên bảng xếp hạng SCApp

Ngày thực hiện: 12/11/2026. Phạm vi chỉ B34.

## 1. Nhánh, commit nền và tài liệu

- Nhánh duy nhất: `feature/B34-application-stages-ui`.
- Commit nền và HEAD cuối: `6554f3badc53c5dc06ec8166d9a6f68cd10698ca`.
- Working tree sạch, không conflict trước khi sửa. Không tạo/chuyển nhánh, pull hoặc merge.
- Đã đọc mục B34, Razor/CSS mẫu, HrActorState, StageDialog, StageText và snippets liên quan trong ZIP `C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip`. Không chép đè các file hiện có bằng mẫu gộp B34/B35/B36.
- Không tìm thấy `B34_Codex_Prompt.md` trong repository, C:\TTCN hoặc ZIP. Người dùng đã xác nhận dùng yêu cầu trong tin nhắn và hướng dẫn B34 trong ZIP.
- Đã khảo sát Results, RankingTable, RankingView, CSS/design tokens, API clients và tests hiện tại. Giữ tìm kiếm không dấu, lọc mức, sắp xếp, xếp hạng, điểm thiếu, thanh điểm, cảnh báo dữ liệu, liên kết chi tiết, URL run/job, polling và tiến độ.
- A35 đã có trong commit nền: MapApplicationStageEndpoints, stage-options, PATCH đơn, POST bulk, GET events; ranking/detail có Stage/RejectReason/StageUpdatedAt/StageVersion và StageCounts. Không tự tích hợp hoặc sửa A35.
- Solution: `SmartCv.slnx`; SDK `global.json` và SDK đang dùng đều `10.0.403`.

## 2. Chức năng hoàn thành

- Cột giai đoạn đủ sáu trạng thái: Mới, Đang xem, Qua vòng CV, Phỏng vấn, Loại, Dự phòng. Màu trạng thái rõ, luôn kèm chữ.
- Đổi trực tiếp trên dòng; chọn Loại mở modal bắt buộc chọn một trong sáu lý do, có ghi chú và tên HR. Dòng đã loại hiển thị lý do và có nút đổi lý do.
- Nhập HR lần đầu, nhớ bằng ProtectedSessionStorage trong tab, giữ sau F5; có Đổi tên mà không tạo quyết định. Nếu browser storage lỗi, tên vẫn dùng được trong circuit và có thông báo giới hạn lưu.
- Giai đoạn khác Loại lưu ngay khi đã có tên HR. Khi chưa có tên, modal chỉ hoàn tất thao tác sau khi người dùng nhập và xác nhận.
- Checkbox từng hồ sơ; chọn tất cả chỉ áp dụng các dòng đang hiển thị. Tối đa 200, không tự gửi một phần hoặc tự chia thành nhiều request. Đổi tìm kiếm/bộ lọc xóa lựa chọn cũ để tránh tác động hồ sơ đang bị ẩn.
- Bulk Qua vòng CV, Dự phòng và Loại; thông báo số Changed/Unchanged/NotFound, nêu ID không tìm thấy. Chỉ áp dụng kết quả đã được API xác nhận.
- Lọc nhanh theo cả sáu giai đoạn, phối hợp bộ lọc mức và tìm kiếm; số đếm tính từ cùng tập Items do API trả về, nhất quán với dòng hiển thị. Không thay đổi điểm hay mức dự đoán của AI.
- Gửi ExpectedStage và ExpectedStageVersion cho đổi đơn; ExpectedStageVersions theo từng ID cho bulk, chụp tại lúc bắt đầu thao tác. Hiện lỗi 409 và tải lại dữ liệu, không tự retry quyết định.
- Khóa lưu lặp, hiển thị đang lưu, hỗ trợ hủy; hủy tái tạo select về giá trị đã lưu. Mất kết nối được báo là chưa xác nhận kết quả, không khẳng định thất bại rồi tự gửi lại.
- Sau lưu hoặc lỗi đều GET lại ranking. Nếu GET thất bại, giữ bảng/quyết định đã xác nhận và khóa thao tác mới cho đến khi đồng bộ thành công. Làm mới thủ công hoặc polling thành công cũng mở lại thao tác.
- Polling tạm ngừng trong lúc quyết định; phản hồi polling cũ bị loại bằng revision để không ghi đè dữ liệu mới. Thay run/job tái tạo component, xóa selection và hủy request UI cũ.

## 3. UI và accessibility

- Dùng SCApp Deep Teal, Inter, spacing, border, radius và tokens hiện tại; thêm thanh quyết định, stage pills, action bar và modal phù hợp nghiệp vụ HR.
- Desktop/tablet giữ bảng có vùng cuộn ngang riêng; mobile dùng bố cục thẻ hiện có, thêm checkbox và giai đoạn dễ thao tác. Đã kiểm tra chiều rộng 390px không tràn ngang toàn trang.
- Native modal dialog, aria-labelledby/describedby, radio group bắt buộc, label cho từng control, trạng thái lỗi/status, focus-visible và nút tối thiểu 44px trong luồng quyết định.
- Hỗ trợ Escape, backdrop và Hủy; trong lúc lưu không cho hủy. Có vòng Tab/Shift+Tab theo đúng tab stop của radio group và trả focus về control hoặc tiêu đề bảng khi đóng.
- Không thêm timeline B35, ẩn danh B36, review flags A40 hoặc chức năng A37. Không tạo UI đánh giá mới hoặc sửa trang chi tiết.

## 4. Toàn bộ file mới/sửa

`M`: tracked sửa chưa stage. `??`: mới chưa theo dõi. Đường dẫn tương đối từ gốc repository.

| Git | File | Mục đích |
|---|---|---|
| M | `src/SmartCv.Web/Components/Pages/Results.razor` | Gắn callback reload, bảo vệ polling, reset theo run/job; giữ trang hiện có. |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor` | Cột stage, checkbox, bộ lọc, bulk, thông báo, tên HR, modal. |
| ?? | `src/SmartCv.Web/Components/Results/RankingTable.razor.cs` | Điều phối quyết định, snapshot versions, selection, lỗi, đồng bộ và cancellation. |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor.css` | Style B34 responsive, focus, trạng thái, action bar, cell giai đoạn. |
| ?? | `src/SmartCv.Web/Components/Results/StageDialog.razor` | Modal lý do loại, ghi chú, HR, validation, chống xác nhận lặp. |
| ?? | `src/SmartCv.Web/Components/Results/StageDialog.razor.css` | Style dialog desktop/mobile theo SCApp. |
| ?? | `src/SmartCv.Web/Components/Results/StageDialog.razor.js` | showModal, Escape/backdrop, vòng focus, phục hồi focus. |
| ?? | `src/SmartCv.Web/Helpers/StageText.cs` | Nhãn từ Core A35 và CSS class cho sáu stage. |
| ?? | `src/SmartCv.Web/Services/HrActorState.cs` | Nhớ tên HR trong tab và xử lý lỗi protected storage. |
| M | `src/SmartCv.Web/Services/ApiClientBase.cs` | Thêm PatchJsonAsync dùng pipeline xử lý lỗi hiện có. |
| M | `src/SmartCv.Web/Services/ApplicationApiClient.cs` | Thêm API client options/PATCH/bulk, giữ nguyên AssignSelectedAsync. |
| M | `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` | Đăng ký protected session storage và HrActorState. |
| M | `src/SmartCv.Web/wwwroot/app.css` | Thêm riêng các màu semantic cho giai đoạn, không thay tokens cũ. |
| M | `tests/SmartCv.Tests/FitLevelUiTests.cs` | Bổ sung dependency B34 cho renderer; giữ assertions B8. |
| M | `tests/SmartCv.Tests/ResultsUiTests.cs` | Bổ sung dependency B34 cho renderer; giữ tests Kết quả cũ. |
| ?? | `tests/SmartCv.Tests/B34StageUiTests.cs` | 16 test/case cho B34, bao gồm kịch bản 20 PDF qua API A35 thật. |
| ?? | `docs/B34-implementation-report.md` | Báo cáo triển khai và kiểm thử. |

Không có file backend, migration, upload, OCR, Gemini, scoring hoặc UI Prompt bị sửa. Không xóa file cũ. Các file trong `artifacts/` là log, TRX, output build, ảnh và helper browser bị Git ignore, không phải file để commit.

## 5. Kết quả build và tests

| Kiểm tra | Pass | Fail | Skipped |
|---|---:|---:|---:|
| Build solution bằng output riêng | — | 0 | — |
| Test B34 (nằm trong suite tổng) | 16 | 0 | 0 |
| Toàn bộ regression cuối | 1329 | 1 | 38 |
| Browser Chromium headless, desktop/mobile | PASS kịch bản | 0 | 0 |

Build cuối: **0 Warning(s), 0 Error(s)**. Build mặc định ban đầu bị MSB3021/MSB3027 do DLL của API/Web đang chạy bị khóa. Không dừng tiến trình của người dùng; dùng `--artifacts-path artifacts/b34-build` cho toàn bộ build/test.

Lệnh chính:

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/b34-build --no-restore
$env:SMARTCV_B34_DEMO_ZIP = 'C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip'
dotnet test SmartCv.slnx --artifacts-path artifacts/b34-build --no-restore --logger 'trx;LogFileName=b34-full-final.trx' --results-directory artifacts/b34-tests
dotnet run --project artifacts/b34-browser/B34Browser.csproj --no-restore
```

38 skipped là SQL tests cũ opt-in chưa được cấu hình: A11 6, A15 7, A16 10, A17 3, Prompt 5, A35 7. Không ghi skipped thành pass. B34 không sửa schema/backend nên không tự bật hoặc chạy migration lên database đang dùng.

Bằng chứng trong workspace:

- `artifacts/b34-build.log`: build cuối.
- `artifacts/b34-tests/b34-full-final.trx`, `artifacts/b34-full-final.log`: regression cuối.
- `artifacts/b34-tests/b34-ui.trx`, `artifacts/b34-ui.log`: focused tests B34.
- `artifacts/b34-browser.log`, `artifacts/b34-tests/browser-web.log`: browser helper.
- `artifacts/b34-tests/b34-desktop.png`, `b34-reject-dialog.png`, `b34-mobile.png`, `b34-mobile-card.png`: ảnh đã kiểm tra trực quan.

## 6. Các tình huống kiểm thử

- Lần đầu hỏi tên HR, lưu actor trim và gửi version; lần tiếp theo non-Rejected lưu ngay.
- Loại thiếu lý do không gửi API; hủy trả select về New; gửi note/actor/reason đúng; đổi lý do của hồ sơ đã loại.
- Kịch bản bulk 20, có một hồ sơ đã đúng stage nên Unchanged; mở lại hồ sơ đã loại không gửi rejectReason.
- Chọn tất cả theo dòng đang hiện, phối hợp search/lọc stage, xóa lựa chọn khi đổi filter, trạng thái không khớp và reset filter.
- 400/404/409/500 không áp dụng trạng thái giả, không tự retry write, tải lại sau lỗi.
- Mất mạng báo kết quả chưa xác nhận; bulk có NotFound nêu riêng ID; không có optimistic success khi bulk 409.
- Khi GET đồng bộ thất bại giữ quyết định vừa xác nhận và khóa ghi; nút tải lại chỉ GET, không gửi lại quyết định.
- Bấm lặp trong lúc request bị giữ chỉ tạo một write; chọn tối đa 200 trong tập 201 không gửi ngầm request thứ hai.
- Đổi tên HR không gửi quyết định.
- Các regression cũ cho ranking/score/fit/URL/polling vẫn được chạy, không bỏ assertions để tests xanh.

**Kịch bản PDF + API A35 thật:** đọc đủ 20 file PDF từ ZIP bằng PdfPig; mỗi file một trang, nội dung đọc được 168–743 ký tự. Seed nội dung demo vào database InMemory tách biệt của TestServer, gán CV qua HTTP A8, tạo lượt đánh giá bằng fixture điểm để tránh Gemini. Dựng component RankingTable thật, gọi các API A35 thật qua HttpClient. Kết quả: 10 Shortlisted, 6 Rejected/MissingSkills, 2 OnHold, 2 New; đúng 18 ApplicationEvents, cùng actor, điểm đánh giá không thay đổi. Không chạy upload/OCR/Gemini trên database thật.

**Browser:** chạy đúng Web đã build bằng process riêng và cổng tự chọn, Chromium headless/profile mới; API test trong bộ nhớ chỉ phục vụ kiểm tra UI, không thay endpoint production. Kiểm tra desktop 1440px, mobile 390px, modal, required reason, Escape, vòng focus, F5 giữ tên HR, lưu ngay, stale version 409 rồi reload, bulk20. Không có page errors; mobile scrollWidth = innerWidth = 390. Helper chỉ dừng process Web do chính nó tạo. Browser API double và integration API A35 thật là hai lớp kiểm chứng riêng, không tuyên bố browser dùng database production.

## 7. Lỗi còn tồn tại và giới hạn

Lỗi duy nhất của suite tổng:

```text
SmartCv.Tests.PromptTestFlowTests.Parent_render_with_previous_query_does_not_restore_old_run_or_reset_new_prompt
tests/SmartCv.Tests/PromptTestFlowTests.cs:192
Assert.Contains: không tìm thấy "#8" trong kết quả render.
```

Đây là lỗi đã tái hiện trên commit nền `303142afb6d41d846cc954f709a233936f184f65` ở phiên A35. Trong phiên B34, đã đối chiếu `git diff 303142a HEAD` cho test này, PromptTestPanel và PromptTestFlow: không có thay đổi; các file này cũng không bị sửa trong B34. Không sửa UI Prompt hoặc bỏ test ngoài phạm vi. Giải pháp: xử lý trong nhiệm vụ riêng rồi chạy lại regression đầy đủ.

Chưa kiểm thử Safari/Firefox, tài khoản HR có xác thực hoặc môi trường triển khai thật. Actor hiện là tên HR tự nhập theo contract A35, không phải danh tính đã xác thực. Chưa chạy chuỗi upload→OCR→Gemini với 20 PDF; không thay các chức năng này để phục vụ B34. Bản mẫu gộp B36 không được đưa vào để tránh dependency A37/A40.

## 8. Git cuối phiên và bảo toàn phạm vi

- `git status --porcelain=v1 --untracked-files=all`: **9 modified, 8 untracked**, danh sách đầy đủ ở mục 4.
- `git diff --stat`: **9 files changed, 192 insertions(+), 15 deletions(-)**; không tính tám file mới chưa theo dõi.
- `git diff --check`: sạch. `git diff --cached --name-only`: rỗng. Không conflict, deleted hoặc renamed files.
- Diff tracked đầy đủ: `artifacts/b34-tests/b34-tracked.diff`.
- Nhánh/HEAD không đổi. Không stage, add, commit, push, pull, merge, rebase, reset, stash, clean, xóa file cũ hoặc mở PR.
- Chỉ sửa phần B34 và dependency test renderer trực tiếp. Không chép đè tính năng cũ; không sửa backend A35, A37, A40, B35 hoặc B36.

## 9. Kết luận

**CHƯA SẴN SÀNG** để xác nhận toàn bộ regression xanh: B34 đã hoàn thành, build sạch, 16 tests B34 pass, browser pass và kịch bản 20 PDF/API A35 pass; suite tổng còn một test UI Prompt lỗi có sẵn ngoài phạm vi. Công việc dừng ở báo cáo, không tự thực hiện bước Git tiếp theo.

Tham chiếu kỹ thuật khi kiểm chứng: [Protected browser storage của Blazor](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/protected-browser-storage?view=aspnetcore-10.0), [Playwright .NET](https://playwright.dev/dotnet/docs/intro). Không thêm Playwright vào project sản phẩm hay dependencies của test suite; helper nằm riêng trong artifacts.
