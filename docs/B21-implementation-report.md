# B21 — Form tạo/sửa vị trí, kỹ năng và từ khóa

Ngày bàn giao: 18/11/2026 (Asia/Saigon).

## 1. Repository, nhánh và kiểm tra Git đầu phiên

- Repository duy nhất được dùng: `C:\TTCN\project\AI-CV-Screening`.
- Nhánh triển khai: `feature/b21-job-form`, không có upstream.
- SHA nền đầy đủ và HEAD: `a947ac84b75a321642faa9e81ec102b6bd9ab7d1`.
- Remote `origin`: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Ban đầu ở `feature/b20-jobs-page`, HEAD `2f0b22d18304dffba29fb75f2770c274ce81f060`, working tree/index sạch; không có staged/unstaged/untracked/unmerged hoặc merge/rebase/cherry-pick/revert/sequencer dở dang. B21 chưa tồn tại ở local/remote.
- Đã `fetch origin --prune`, kiểm tra lại điều kiện an toàn, rồi `git switch --no-track -c feature/b21-job-form origin/main` và xác nhận bằng `git branch --show-current`, `git rev-parse HEAD`, `git status --short --branch` trước khi viết code.
- B20 đã merge theo kiểu squash ở `a947ac8` (#73). Commit riêng B20 không là ancestor của squash; đã đối chiếu toàn bộ 14 file trong manifest B20 với main trước khi sửa và không có khác biệt. Không cherry-pick/gộp B20 thủ công. Nhánh local B20 và main không bị di chuyển.
- `git worktree list --porcelain` có 6 đăng ký cũ: repository hiện tại, B19, B39, gemini-fix, main-latest, scratchpad wt-a11; 4 đăng ký được Git đánh dấu prunable. Chỉ đọc danh sách, không truy cập/sửa/dọn các worktree đó; **không tạo worktree hoặc clone/repository thứ hai**.
- Dùng `git -c safe.directory=C:/TTCN/project/AI-CV-Screening` theo từng lệnh do tài khoản sandbox khác chủ sở hữu. Không thay Git config toàn cục. Fetch/switch dùng quyền phù hợp với sandbox `.git`.
- Không thực hiện git add, commit, push, merge, rebase, reset, stash, restore, clean hoặc tạo PR.

Metadata: [SHA nền](../artifacts/b21-base-sha.txt), [manifest](../artifacts/b21-commit-files.txt). Hai file UTF-8 không BOM, bị ignore theo `.gitignore` hiện có; không sửa `.gitignore`.

## 2. Tài liệu và contract đã đối chiếu

Đọc trực tiếp ZIP `C:\TTCN\A19_B26_SmartCv.zip`: hướng dẫn B21, A21/A22, phần B22 liên quan, bảng lỗi thường gặp và toàn bộ mẫu JobEdit/CSS, SkillEditor/CSS, TagInput/CSS, JobFormModel, SkillDiff, JobApiClient, ApiClientBase. Không tạo file prompt; không chép đè nguyên mẫu.

Đối chiếu source thật: `JobRules`, `JobSkillRules`, Job DTO/request contracts, `JobEndpoints`, `JobSkillEndpoints`, client/result/base HTTP, B20 dialog và NavigationLock hiện có. Các ngưỡng được lấy từ source:

| Dữ liệu | Quy tắc |
|---|---|
| Tên | NormalizeTitle, bắt buộc, tối đa 150 ký tự sau chuẩn hóa |
| Mô tả | Tối đa 20.000 ký tự |
| GPA | null hoặc 0–4 |
| Kinh nghiệm | 0–50 năm |
| Headcount | null hoặc 1–1000 |
| Hạn | DateOnly; khi tạo không trước hôm nay Việt Nam, khi sửa giữ được hạn cũ đã qua |
| Mã kỹ năng | Gợi ý/chuẩn hóa bằng A22; 2–50 ký tự hợp lệ; giữ nguyên mã đã lưu, kể cả legacy lowercase |
| Tên kỹ năng | Bắt buộc, tối đa 100 ký tự sau trim |
| Aliases | A22 NormalizeAliases: Unicode Form C, chữ thường, chuẩn hóa khoảng trắng, bỏ trùng; tối đa 30 × 100 ký tự |

Ít nhất một MustHave là quy tắc form B21 được yêu cầu; API vẫn giữ khả năng tạo job chưa có kỹ năng. Không sửa backend để áp thêm quy tắc này.

## 3. Chức năng hoàn thành

- `/jobs/new` và `/jobs/{Id:int}` hoạt động độc lập. B20 có sẵn trên main nên Tạo vị trí/tên/Sửa từ danh sách mở trực tiếp các route mới; không sửa Jobs/sidebar/Results.
- EditForm + DataAnnotationsValidator + IValidatableObject gọi JobRules/JobSkillRules, hiển thị lỗi ngay tại tên/GPA/kinh nghiệm/headcount/hạn/mô tả và các ô kỹ năng. ValidationMessageStore gắn lỗi API theo tên trường JSON; lỗi body chung hiển thị thông báo và không khóa form vĩnh viễn.
- Form thông tin chung đủ sáu trường; DateOnly gửi `yyyy-MM-dd`. So sánh GPA/kinh nghiệm tính đến precision mà A21 lưu; không PUT thông tin chung khi không đổi.
- Hai panel MustHave/NiceToHave cùng cấu trúc: tên, mã, aliases, chuyển loại, xóa/thêm. Mã gợi ý bằng `SuggestCode`: ASP.NET Core → ASPNET_CORE, C# → CSHARP, C++ → CPLUSPLUS. Mã mới được sửa; mã đã lưu readonly và được kiểm tra bất biến ở model.
- Xóa dòng chưa lưu chỉ sửa bản nháp. Xóa dòng đã lưu tạo đánh dấu **Sẽ xóa khi lưu**, có Hoàn tác. Chưa có DELETE cho tới khi bấm Lưu.
- TagInput: Enter/dấu phẩy thêm, × xóa, Backspace ở ô trống bỏ thẻ cuối, paste nhiều giá trị phân cách bằng dấu phẩy, commit khi blur. Lỗi giới hạn không xóa draft. Gõ liên tục và Unicode tiếng Việt được giữ đúng.
- Keyboard handler chặn submit trong TagInput, kể cả chuỗi composition; giữ hành vi chọn ký tự của IME. Interop được xếp hàng; text mới không bị response của commit trước ghi đè. Trước submit/chuyển/xóa dòng, flush draft đang chờ. Native listener được tháo cả khi ElementReference đã mất sau unmount.
- Tạo mới gửi **một POST `/api/jobs`** chứa thông tin chung + toàn bộ skills/aliases, chặn double submit, nhận JobId thật rồi điều hướng `/jobs/{id}` và báo **Đã tạo vị trí tuyển dụng**. Không gửi POST kỹ năng riêng khi tạo.
- Sửa gọi PUT thông tin chung khi cần; SkillDiff trả thứ tự toàn cục **DELETE → POST thêm → PUT tên/loại → PUT thay toàn bộ aliases**. So sánh aliases đã chuẩn hóa và không phụ thuộc thứ tự; không có thay đổi thì không gửi write hoặc GET dư.
- Luôn phân biệt bản nháp với snapshot đã xác nhận. Sau batch có write được xác nhận, GET lại trạng thái và đối chiếu; chỉ cập nhật phần đã lưu, giữ tên/loại/aliases còn lỗi, dòng thêm chưa lưu và thay đổi chung chưa thành công. Dòng biến mất từ server giữ nội dung để đối chiếu, không tự tạo lại mã cũ.
- HTTP 409 trùng tên hiện ngay dưới Tên. HTTP 409 kỹ năng có lịch sử: giữ/khôi phục dòng, tô đỏ nhạt và giải thích bảo toàn lịch sử; khóa thao tác xóa lại dòng đó trong phiên, vẫn cho sửa tên/loại/aliases. Thay đổi hợp lệ khác tiếp tục lưu. Thông báo đếm thay đổi còn chưa lưu; không báo toàn bộ thành công.
- HTTP 400/401/403/404/409/429/500/503, timeout, cancellation, JSON sai cú pháp hoặc DTO không hợp lệ đều có xử lý. Khi write chưa xác nhận, dừng phần còn lại, giữ draft và khóa gửi tiếp; không phát lại write tự động.
- **Tải lại trạng thái** chỉ GET. Với create chưa biết ID, kiểm tra danh sách theo tên và so toàn bộ tiêu chí; chỉ nhận vị trí có nội dung khớp. Nếu cùng tên nhưng tiêu chí khác, giữ khóa và draft để đối chiếu. Không thấy vị trí thì thông báo rõ trước khi người dùng chủ động tạo lại; không tự POST.
- HasEvaluations=true có cảnh báo lịch sử đúng yêu cầu; null hiển thị chưa xác định và form sửa chỉ xem. Không coi null là false. Không gỡ gate Development của API; form mới nhận lỗi quyền/endpoint từ server nếu môi trường không cho ghi.
- NavigationLock cho external reload/close; điều hướng nội bộ dùng dialog B20 để xác nhận, có Hủy giữ draft. Nút Hủy không gửi API ghi. Chặn điều hướng nội bộ khi đang lưu. Không lưu draft/tiêu chí vào localStorage hay log.
- Loading, form rỗng, validation, saving/saved, chưa xác nhận write, không kết nối/503, retry, job 404, conflict 409 và trạng thái dirty có giao diện riêng.

## 4. Khác mẫu ZIP và lý do

1. Dùng Core rules trực tiếp thay vì lặp Range/StringLength rồi dễ lệch với title đã chuẩn hóa; validation kỹ năng được đưa xuống đúng ô. Thêm structured ValidationErrors vào ApiResult, giữ ErrorCode/RetryAfter.
2. Chỉ mở rộng ApiClientBase: DELETE 204 dùng chung cancellation/timeout/error handling; không thay nguyên class hoặc mất các hàm B39/B41. Không log body lỗi API; giữ log URL/status để tránh đưa nội dung lỗi vào log.
3. SkillDiff sắp theo loại thao tác trên toàn bộ danh sách; mẫu có thể xen thêm/sửa theo từng dòng. PUT chung bỏ qua khi không đổi.
4. Workspace quản lý snapshot/draft và reconciliation. Mẫu reload dựng lại model có thể làm mất sửa tên/loại/aliases bị lỗi trên dòng đã tồn tại; bản này giữ từng phần chưa thành công, không biến lỗi một phần thành thành công toàn bộ.
5. Thêm khóa xác minh write, kiểm tra shape DTO, chặn double-submit và cancellation khi đổi route/rời trang. Không lặp POST/DELETE sau timeout/409.
6. TagInput có JS nhỏ cho bàn phím/IME và điều phối interop; không dùng preventDefault cho mọi phím. Mẫu Enter có thể submit form, mất draft khi vượt giới hạn hoặc khi render/unmount.
7. Bố cục panel/thẻ kỹ năng thay bảng min-width cố định, giúp dữ liệu/tag dài wrap và form mobile một cột. Tái sử dụng token, Inter, dialog và NavigationLock; không sửa design system chung.

## 5. UI/UX và bằng chứng trình duyệt

Deep Teal, nền sáng, glow mint nhẹ ở panel thông tin. Inter local hiện có; không tải font ngoài. Heading trang 28px desktop/tablet, 24px mobile; section 18px (17px cho panel kỹ năng mobile); nhãn 13px; input/nội dung 14px; mã 13px; tag, badge, hint, lỗi 12px. Panel padding 20px desktop, 16px mobile; row 16/14px; card radius 12–16px; gap 12/16/20px; viền/shadow nhẹ. Không giảm chữ để nhét dữ liệu.

Đã chạy **Chromium 156.0.8078.4** với **Blazor Web thật** và A21/A22-shaped API fixture riêng trong harness ignored. Không chèn fixture/data mẫu vào ứng dụng thật hoặc gọi API production. Đã kiểm 11 nhóm chính + 4 nhóm bổ sung, **17 ảnh**, **0 JS/console errors** ở các lượt browser cuối. Build harness cuối 0 lỗi/0 warning.

| Viewport | Kiểm tra |
|---|---|
| 1440px | Grid thông tin 4 cột phụ, hai panel kỹ năng song song |
| 1024/768px | Thông tin 2 cột, panel kỹ năng xếp dọc |
| 390/360/320px | Form một cột, tag wrap, nút lưu/hủy nhìn thấy và dễ bấm |

Đo DOM cả sáu viewport: `documentElement.scrollWidth == innerWidth`, không có phần tử trong JobEdit vượt mép viewport; các input thông tin chung cùng chiều cao 44px. Test bổ sung ở 320px với mã 50 ký tự, tag 100 ký tự, tên dài, giới hạn 30 tag, gõ nhanh và chuyển loại đang composition cũng không tràn ngang. Đã xem trực tiếp ảnh desktop/mobile và trạng thái xung đột để đối chiếu cỡ chữ/căn chỉnh. Date picker dùng format hiển thị theo browser/OS locale; JSON luôn ISO DateOnly.

| Minh chứng | Ảnh |
|---|---|
| Form rỗng/validation | [Tạo mới 1440](../artifacts/b21/screenshots/new-empty-1440.png), [Validation](../artifacts/b21/screenshots/new-validation.png) |
| Sáu viewport | [1440](../artifacts/b21/screenshots/edit-1440.png), [1024](../artifacts/b21/screenshots/edit-1024.png), [768](../artifacts/b21/screenshots/edit-768.png), [390](../artifacts/b21/screenshots/edit-390.png), [360](../artifacts/b21/screenshots/edit-360.png), [320](../artifacts/b21/screenshots/edit-320.png) |
| Hai lỗi 409 | [Trùng tên](../artifacts/b21/screenshots/create-title-409.png), [Kỹ năng đang dùng](../artifacts/b21/screenshots/delete-skill-409.png) |
| Lifecycle | [Loading](../artifacts/b21/screenshots/loading.png), [Saving](../artifacts/b21/screenshots/saving.png), [API unavailable](../artifacts/b21/screenshots/api-unavailable.png), [404](../artifacts/b21/screenshots/not-found.png) |
| Bảo vệ dữ liệu | [Dirty navigation](../artifacts/b21/screenshots/unsaved-navigation.png), [Create chưa xác nhận](../artifacts/b21/screenshots/unverified-create.png), [Dữ liệu dài 320](../artifacts/b21/screenshots/skills-long-data-320.png) |

Chi tiết: `artifacts/b21/browser-results.json`, `browser-extra-results.json`, `browser-run.log`, `browser-extra-run.log`; harness trong `artifacts/b21/browser/`. Các ảnh/log/harness không nằm trong commit manifest.

## 6. Build, baseline và regression

Tất cả chạy tại repository hiện tại, output trong `artifacts/b21/build`. Giữ nguyên framework/package/solution và toàn bộ test cũ. Runsettings không bật SQL/demo/Gemini external opt-in; các test mới nối endpoint thật dùng TestServer + EF InMemory và fixture loại bỏ hosted workers.

| Kiểm tra | Passed | Failed | Skipped | Build warning |
|---|---:|---:|---:|---:|
| Baseline full trước sửa | 2324 | 0 | 107 | 0 |
| B21 riêng cuối | 51 | 0 | 0 | 0 |
| Focused regression cuối | 522 | 0 | 17 | 0 |
| Full regression cuối | 2375 | 0 | 107 | 0 |

Final build 0 lỗi/0 warning; restore baseline thành công, không suppress analyzer/compiler warning. Các lượt test cuối dùng thư mục TRX mới, không cảnh báo ghi đè. Lượt phát triển có 5 fail do parser URL của fixture, 3 fail do assertion FormName metadata không hiện trong HTML; chỉ sửa test mới. Một selector browser dùng Last trước khi dòng mới render đã nhập nhầm dòng cũ; đã thêm chờ số dòng/mã gợi ý trước khi thao tác, không nới validation để vượt test. Browser phát hiện và giúp sửa disabled binding, HTML constraint chặn inline validation, Enter composition, race nhập tag nhanh và cleanup/flush khi move; đã kiểm lại, không coi các lượt thử chưa qua là pass cuối. Harness từng có warning obsolete NoWaitAfter, đã thay bằng DOM click cho bước quan sát Saving; build harness cuối sạch.

Lệnh tái hiện:

```powershell
dotnet restore SmartCv.slnx -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/b21/build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/b21/build
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b21/build --settings artifacts/b21/tests.runsettings --filter 'FullyQualifiedName~B21JobFormTests'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b21/build --settings artifacts/b21/tests.runsettings --filter 'FullyQualifiedName~B21JobFormTests|FullyQualifiedName~B20JobsTests|FullyQualifiedName~B19DashboardTests|FullyQualifiedName~ResultsUiTests|FullyQualifiedName~B34|FullyQualifiedName~B36|FullyQualifiedName~B38|FullyQualifiedName~B39|FullyQualifiedName~B41|FullyQualifiedName~Jobs.Job|FullyQualifiedName~WebApiClientTests'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b21/build --settings artifacts/b21/tests.runsettings
```

Log/TRX chính: `baseline-build.log`, `baseline-tests/baseline.trx`, `final-build.log`, `release-b21-tests/b21.trx`, `release-focused-tests/focused.trx`, `handoff-full-tests/full.trx` trong `artifacts/b21/`. Đối chiếu tên test phân biệt hoa thường; skipped đếm từng UnitTestResult `NotExecuted`, không dùng counter adapter vốn là 0.

Đối chiếu baseline → full cuối: tổng 2431 → 2482, **thêm đúng 51 test B21, bỏ 0 test cũ, thay đổi danh sách skipped 0**. 107 skipped gồm **102 test SQL opt-in** và **5 kịch bản demo ZIP** (A38, A42 OCR demo, B34, SampleCvMasking, A40 ReviewFlags); không tự cấp kết nối SQL hay bật demo. Bốn test OCR chuẩn (PNG, scanned PDF, JPG và extraction endpoint) đều **Passed**; không có skip do thiếu tessdata. Focused có 16 SQL + 1 B34 demo skipped. Danh sách từng tên/lý do: [skipped-reasons.csv](../artifacts/b21/skipped-reasons.csv); [đối chiếu tự động](../artifacts/b21/verification.json), [baseline skipped](../artifacts/b21/skipped-baseline.txt), [final skipped](../artifacts/b21/skipped-final.txt).

51 test B21 gồm: validation/range/null/date/title chuẩn hóa, MustHave, gợi ý/mã trùng/khóa mã, giới hạn aliases, một POST và ISO date, structured title 409, diff/no-op/thứ tự, DELETE 409/204, lỗi một phần theo từng field, timeout đã áp dụng ở server, GET xác minh, không double-submit, cancellation, JSON sai cú pháp/shape, quyền null, render Razor và client nối A21/A22 thật trong bộ nhớ. Browser kiểm các thao tác thực tế mà HTML renderer không chạy JavaScript.

## 7. Hai kịch bản HTTP 409 và bảo toàn hệ thống

**Trùng tên:** UI tạo lại tên có sẵn → POST 409 → thông báo đỏ dưới Tên, giữ nguyên skills/aliases và trường khác, không điều hướng hoặc tự gửi lại. Có test cả HTTP fixture và endpoint A21 thật trong bộ nhớ; ảnh `create-title-409.png`.

**Kỹ năng đang dùng:** đánh dấu xóa CSHARP, thêm alias của SQL, Lưu → DELETE CSHARP 409 → vẫn PUT aliases SQL → GET lại → CSHARP giữ nguyên, dòng đỏ có lý do giữ lịch sử, SQL có alias mới; thông báo còn 1 thay đổi chưa lưu. Bấm Lưu lần nữa không gửi lại DELETE. Test endpoint thật tạo EvaluationSkills synthetic trong EF InMemory và xác nhận evidence lịch sử còn nguyên; DELETE kỹ năng mới không tham chiếu trả 204 cũng được kiểm. Không tuyên bố đã kiểm FK/locking SQL thật.

B19/B20/B7/B34/B36/B38/B39/B41 được đưa vào focused/full regression; không sửa Dashboard, Jobs, Results, Upload, Prompts, dialog dùng chung hoặc test cũ. JobApiClient chỉ thêm các method B21; giữ activate/deactivate B20. ErrorCode và RetryAfter của B39/B41 được giữ qua mở rộng result/base client.

Không sửa Core/API/Infrastructure/database/migrations/AI/worker/scoring/kết quả cũ; không chạy Gemini thật, migration, SQL test opt-in hoặc tự chấm lại. Không triển khai B22/B26.

## 8. Giới hạn đã biết

- Chưa kiểm production API/SQL, thiết bị vật lý, screen reader, Firefox/WebKit hoặc IME native ở mọi OS; composition được kiểm bằng sự kiện browser và chuỗi bàn phím, không phải chứng nhận mọi bộ gõ. Không tuyên bố chứng nhận WCAG.
- A21/A22 không cung cấp ETag/version cho chỉnh sửa tiêu chí. Client giữ draft và đọc lại sau batch, nhưng không loại trừ mọi race với người khác chỉnh đồng thời; không thay đổi backend để tạo giao dịch xuyên nhiều request.
- Khi create timeout, GET không thấy vị trí chỉ phản ánh thời điểm đọc; server có thể còn xử lý. Không tự POST lại, yêu cầu người dùng chủ động đối chiếu/gửi lại, và uniqueness tên của API vẫn là bảo vệ cuối. Không tự nhận một vị trí cùng tên nhưng tiêu chí khác là kết quả của request.
- Khi DELETE bị chặn để giữ lịch sử, cảnh báo và số thao tác chưa lưu vẫn giữ trong phiên; sửa tên/loại/aliases vẫn được lưu riêng. Reload có NavigationLock và tải lại snapshot thực.
- Draft nằm trong phiên trang, không tự lưu xuống đĩa/localStorage. Nếu người dùng xác nhận bỏ trang hoặc đóng trình duyệt sau cảnh báo, draft không được phục hồi tự động.
- Browser fixture an toàn chỉ chứng minh giao diện/client; kiểm vận hành với backend/SQL thật cần thực hiện riêng sau khi người dùng duyệt.

## 9. Manifest A/M/D

**15 file: 12 A mới (untracked), 3 M sửa, 0 D xóa; chưa stage.**

| Trạng thái | File |
|---|---|
| A | `docs/B21-implementation-report.md` |
| A | `src/SmartCv.Web/Components/Jobs/SkillEditor.razor` |
| A | `src/SmartCv.Web/Components/Jobs/SkillEditor.razor.css` |
| A | `src/SmartCv.Web/Components/Jobs/TagInput.razor` |
| A | `src/SmartCv.Web/Components/Jobs/TagInput.razor.css` |
| A | `src/SmartCv.Web/Components/Jobs/TagInput.razor.js` |
| A | `src/SmartCv.Web/Components/Pages/JobEdit.razor` |
| A | `src/SmartCv.Web/Components/Pages/JobEdit.razor.css` |
| A | `src/SmartCv.Web/Helpers/JobFormWorkspace.cs` |
| A | `src/SmartCv.Web/Helpers/SkillDiff.cs` |
| A | `src/SmartCv.Web/Models/JobFormModel.cs` |
| M | `src/SmartCv.Web/Services/ApiClientBase.cs` |
| M | `src/SmartCv.Web/Services/ApiResult.cs` |
| M | `src/SmartCv.Web/Services/JobApiClient.cs` |
| A | `tests/SmartCv.Tests/B21JobFormTests.cs` |

Manifest mỗi dòng một path relative, không artifact/log/screenshot/secret; hai metadata trong artifacts không nằm trong source manifest. Các file B20 đã merge không bị đưa vào danh sách B21, ngoại trừ JobApiClient mở rộng đúng phạm vi B21.

## 10. Checkpoint Git cuối

- Nhánh đúng `feature/b21-job-form`; HEAD/origin/main bằng SHA nền `a947ac84b75a321642faa9e81ec102b6bd9ab7d1`.
- Local main vẫn `b5fd7a22d93ede567f8c6dcc64ca977711e2f097`; local B20 vẫn `2f0b22d18304dffba29fb75f2770c274ce81f060`. Không di chuyển nhánh đồng đội hoặc tạo thêm worktree.
- `git diff --check`: exit 0; kiểm tra thêm whitespace/conflict marker cả file mới cũng sạch.
- `git diff --cached --name-status`: **rỗng**. Không unmerged hoặc thao tác Git dở dang.
- Manifest chính xác **15 path unique = 12 A + 3 M + 0 D**, khớp toàn bộ `git status --porcelain=v1 --untracked-files=all`. Không có file ngoài B21. Metadata/log/ảnh/harness bị ignore.
- [Audit Git cuối](../artifacts/b21/final-git-audit.txt), [manifest](../artifacts/b21-commit-files.txt), [SHA nền](../artifacts/b21-base-sha.txt), [verification](../artifacts/b21/verification.json).
- Các host browser/API/Web và tiến trình test do phiên này tạo đã kết thúc; không dừng dịch vụ/build server dùng chung của người dùng.

`git status --short --branch` (Git gộp 5 file mới trong thư mục Jobs; manifest liệt kê riêng đủ từng file):

```text
## feature/b21-job-form
 M src/SmartCv.Web/Services/ApiClientBase.cs
 M src/SmartCv.Web/Services/ApiResult.cs
 M src/SmartCv.Web/Services/JobApiClient.cs
?? docs/B21-implementation-report.md
?? src/SmartCv.Web/Components/Jobs/
?? src/SmartCv.Web/Components/Pages/JobEdit.razor
?? src/SmartCv.Web/Components/Pages/JobEdit.razor.css
?? src/SmartCv.Web/Helpers/JobFormWorkspace.cs
?? src/SmartCv.Web/Helpers/SkillDiff.cs
?? src/SmartCv.Web/Models/JobFormModel.cs
?? tests/SmartCv.Tests/B21JobFormTests.cs
```

**Dừng tại báo cáo để người dùng duyệt. Chưa git add/commit/push/merge/rebase/reset/stash/restore/clean hoặc tạo PR.**
