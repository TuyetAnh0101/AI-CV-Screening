# SCApp — Rà soát phân quyền nghiệp vụ Admin/HR

Ngày thực hiện: 20/11/2026. Kết luận và số liệu kiểm thử được cập nhật sau khi chạy kiểm tra cuối.

## 1. Git và phạm vi

- Repository duy nhất: `C:\TTCN\project\AI-CV-Screening`, origin `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Nhánh: `fix/hr-admin-business-permissions`.
- SHA nền: `d1d9ffaa282e750535fb4aaf626ed05082161207`, lấy từ `origin/main` sau `git fetch origin --prune`.
- Trước khi tạo nhánh: working tree sạch, không staged/unstaged/untracked, không conflict hoặc merge/rebase/cherry-pick/revert dở dang. Nhánh đích chưa tồn tại local/remote.
- Đã xác minh main có A29 (#85), A30 (#87), B27 (#86), B28 (#88), B29 (#89), B43 (#90), cùng trang Users/ActivityLogs và các client bảo vệ file thực tế.
- Không tạo worktree/clone; không chỉnh sửa các worktree khác. Không stage, commit, push, merge, rebase hoặc cherry-pick.
- Không dừng tiến trình API/Web đang chạy của người dùng; kiểm tra bằng bản Release và host test riêng.

Đây là thay đổi quyền theo yêu cầu mới, có chủ đích khác ma trận A29/B28 cũ. Không bổ sung API nghiệp vụ, hệ thống xác thực hoặc schema mới. Prompt đã lưu tiếp tục bất biến: chỉnh sửa là lưu thành phiên bản mới. Quản lý bộ đánh giá sử dụng API thành viên/nhãn hiện có; không tự thêm API đổi tên/xóa nguyên bộ khi hợp đồng hiện tại chưa cung cấp.

## 2. Kết quả rà soát toàn bộ ranh giới quyền

Đã kiểm tra `AuthSetup/AuthPolicies`, `SmartCvApi`, cả hai `Program.cs`, tất cả đăng ký endpoint Minimal API và controller, chính sách Web, mọi trang có route, `AccessRules`, `AppNavigation`, các điều kiện hiển thị/thực thi nút và form. Ma trận A29 liệt kê từng method/route thực tế, gộp metadata authorization và thử request anonymous; không chỉ kiểm tra tên policy trên từng file. Với feature flag nghiệp vụ được bật trong test: 91 route `/api`, gồm 2 public, 2 SignedIn, 12 Admin-only và 75 nghiệp vụ chung sau sửa (trước đó 24 Admin-only, 63 nghiệp vụ chung). OpenAPI chỉ ở Development vẫn giữ nguyên.

Sai lệch chính: API gắn Admin cho 12 thao tác nghiệp vụ, UI đồng thời xem HR như người chỉ đọc hoặc chặn form Job trực tiếp. Quản lý bộ đánh giá còn bất nhất: HR được thêm CV/gán nhãn nhưng không được tạo bộ.

| Nhóm quyền | Admin trước → sau | HR trước → sau |
|---|---|---|
| Tạo/sửa/mở/ngừng tuyển Job | Được → Được | Bị chặn → Được |
| Thêm/sửa/bỏ kỹ năng và thay alias Job | Được → Được | Bị chặn → Được |
| Tạo bộ dữ liệu đánh giá | Được → Được | Bị chặn → Được |
| Thêm/bỏ CV, lưu/nhập/bỏ nhãn Đạt/Không đạt | Được → Được | Được → Được |
| Soạn/sửa bản nháp, lưu phiên bản Prompt, kích hoạt | Được → Được | Chỉ đọc → Được |
| Xem nội dung Prompt đã ghép và che thông tin cá nhân | Được → Được | Bị chặn → Được |
| Prompt Test, so sánh độ chính xác, đọc kết quả | Được → Được | Được → Được |
| Upload, gán ứng tuyển, chấm CV, kết quả, lịch sử lượt chấm | Được → Được | Được → Được |
| Ứng viên, giai đoạn, cờ và nghiệp vụ tuyển dụng thường lệ | Được → Được | Được → Được |
| Tài khoản: xem/tạo/sửa/khóa/mở/reset mật khẩu HR | Được → Được | Bị chặn → Bị chặn |
| Audit Log toàn hệ thống | Được → Được | Bị chặn → Bị chặn |
| Chạy retention, ẩn danh hóa CV trực tiếp | Được → Được | Bị chặn → Bị chặn |
| Chẩn đoán database/Gemini | Được → Được | Bị chặn → Bị chặn |

"Được" luôn có điều kiện: JWT và phiên/tài khoản hợp lệ, đúng vai trò Admin/HR, không `MustChangePassword`, feature flag hiện có được bật, và vượt qua validation/quy tắc nghiệp vụ/privacy của endpoint. Không có quyền truy cập anonymous mới.

## 3. API thay đổi

Các endpoint sau chuyển từ `Admin` sang `.RequireAuthorization()` sử dụng chính sách nghiệp vụ mặc định **đã có**, không xóa kiểm tra xác thực:

| Method | Route | UI tương ứng |
|---|---|---|
| POST | `/api/jobs` | Tạo Job, `/jobs/new` |
| PUT | `/api/jobs/{id:int}` | Sửa Job, `/jobs/{id}` |
| POST | `/api/jobs/{id:int}/activate` | Mở tuyển |
| POST | `/api/jobs/{id:int}/deactivate` | Ngừng tuyển |
| POST | `/api/jobs/{id:int}/skills` | Thêm kỹ năng |
| PUT | `/api/jobs/{id:int}/skills/{code}` | Sửa kỹ năng |
| DELETE | `/api/jobs/{id:int}/skills/{code}` | Bỏ kỹ năng chưa được dùng |
| PUT | `/api/jobs/{id:int}/skills/{code}/aliases` | Thay bộ từ khóa |
| POST | `/api/prompts` | Lưu thành phiên bản mới |
| POST | `/api/prompts/{id:int}/activate` | Kích hoạt phiên bản |
| POST | `/api/prompts/assembled-preview` | Thẻ Nội dung gửi Gemini |
| POST | `/api/evaluation-sets` | Tạo bộ đánh giá |

Job skills áp dụng policy nghiệp vụ ở cả group; các endpoint còn lại khai báo riêng. Nhóm API tổng và DefaultPolicy/FallbackPolicy vẫn giữ nguyên. Không đổi `AuthPolicies.Admin` thành policy có HR vì sẽ mở cả tài khoản, Audit, retention và chẩn đoán nhạy cảm.

Không tạo API sửa đè Prompt cũ: chỉnh sửa tiếp tục lưu thành phiên bản mới, bảo toàn nội dung các lượt chấm trước. Quản lý bộ đánh giá mở quyền trên các API hiện có; không tự tạo endpoint đổi metadata hoặc xóa nguyên bộ chưa được codebase hỗ trợ.

## 4. Blazor UI

- `AccessRules`: `ManageJobs`, `ManagePromptVersions`, `PreviewAssembledPrompt`, `CreateEvaluationSet` dùng `CanUseApp`, có cùng điều kiện verified/role/MustChangePassword như ranh giới trang. `ManageUsers` và `ViewAudit` vẫn `IsAdmin`; quyền không được nhận diện vẫn fail-closed.
- JobEdit bỏ policy Admin riêng, **vẫn kế thừa `[Authorize]` nghiệp vụ từ `Pages/_Imports.razor`**. HR mở trực tiếp cả route tạo/sửa được form hợp lệ.
- Jobs hiển thị nút tạo/sửa/ngừng/mở tuyển cho HR; PromptEditor có nội dung chỉnh sửa, tên phiên bản và nút lưu; PromptHistory có kích hoạt; PromptPlayground có tab xem nội dung đã che; EvaluationSetManager có form tạo bộ.
- Sửa thông báo và chú thích "chỉ Admin" đã hết đúng. Giữ readonly/fail-closed khi phiên chưa đủ điều kiện.
- Menu tuyển dụng/AI và `/runs` giữ nguyên. Nhóm Quản trị chỉ có Tài khoản và Nhật ký hoạt động, vẫn Admin-only. Không thêm menu trùng hoặc cơ chế quyền thứ hai.
- Không thay CSS/theme, không thay cấu trúc responsive B29/B43.

## 5. Quyền nhạy cảm và dữ liệu được bảo toàn

- `/api/users`, `/users`, `/api/admin/audit-events`, `/admin/activity-logs` vẫn Admin-only. HR gọi API được 403; anonymous được 401. Các biện pháp bảo vệ Admin cuối cùng, If-Match, rate limiting, reset password và thu hồi JWT của A30 giữ nguyên.
- `/api/retention/run` và `/api/cvs/{id}/anonymize` vẫn Admin-only do thao tác xóa/ẩn danh không thể hoàn tác. Không mở quyền thao tác hàng loạt hoặc thay chính sách retention.
- Xóa dữ liệu ứng viên theo yêu cầu và xuất dữ liệu giữ nguyên luồng A43, validation và quyền HR/Admin hiện có; không mở rộng hoặc làm tắt lớp bảo vệ.
- Chẩn đoán database/Gemini vẫn Admin-only. Thay khóa JWT, API key, options bảo mật, quyền hạn và cấu hình hệ thống vẫn do cấu hình triển khai; không thêm API sửa cấu hình cho HR.
- Assembled preview vẫn chặn khi tắt masking hoặc CV chưa đủ điều kiện; mở quyền xem payload **đã che** không cho phép đọc dữ liệu riêng tư bằng đường khác.
- JWT claims, validation, kiểm tra phiên tươi từ database, SecurityStamp, MustChangePassword, cache/no-store, file streaming/blob cleanup, tombstone và read lease B43/A39 không sửa.
- Actor tiếp tục lấy từ JWT đã kiểm tra ở backend. Các trường legacy `AddedBy`/`LabeledBy` giả mạo không quyết định danh tính; test đối chiếu actor ID/role thật trong Audit và tên trong membership/nhãn. Không thêm ô nhập tên thủ công.
- AuditCatalog/AuditTrail/filter, append-only guard/trigger, dữ liệu lịch sử và User FK không thay đổi. Phạm vi các loại sự kiện được ghi vẫn theo B43 hiện có.

**Database thật và Admin thật:** không mở kết nối SQL Server, không chạy migration/cleanup/reset, không tạo dữ liệu test vào SmartCvDb, không đăng nhập hoặc thay đổi tài khoản thật. Không đọc/ghi giá trị User Secrets hoặc JWT key. Không sửa entity/configuration/migration/snapshot, AdminSeeder, connection string hay appsettings. Không cần migration cho bản sửa quyền này.

Trạng thái áp dụng các migration A27/B43 trên database thật: **NOT INSPECTED**. Ứng dụng vẫn cần schema nền hiện có đầy đủ; bản sửa quyền không tự chữa schema thiếu và không thay database để né lỗi.

## 6. Kiểm thử

Lệnh kiểm tra, dùng cấu hình Release để tránh DLL Debug đang được API/Web giữ:

```powershell
dotnet restore SmartCv.slnx
dotnet build SmartCv.slnx -c Release --no-restore
dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4
```

Trước mỗi lượt regression đã kiểm tra SQL/inspection opt-in không được thiết lập. `ApplicationDatabaseGuard` được giữ nguyên và chặn catalog ứng dụng trước kết nối. Host API test dùng InMemory, file/tài khoản tổng hợp; không gọi Gemini thật. Các test SQL opt-in không bật.

Các kiểm tra liên quan trực tiếp:

- Ma trận API đầy đủ: public/anonymous, Admin, HR, MustChangePassword, controllers và fallback policy; cập nhật đúng danh sách route nhạy cảm, không bỏ assertion deny.
- Ma trận trang/menu/quyền cho anonymous, unverified, vai trò lạ, MustChangePassword, HR, Admin.
- Render Job form/list, tạo bộ, soạn/lưu/kích hoạt Prompt, tab payload cho HR/Admin.
- Web client thật qua AuthSession và JwtBearer: tạo Job, ngừng tuyển, lưu Prompt, tạo bộ; gọi Users bằng HR vẫn 403 và không đăng xuất nhầm.
- HTTP ghi thực tế bằng cả Admin/HR: CRUD kỹ năng/alias, Job activation, upload/gán CV, tạo Prompt, masked preview, thành viên/nhãn/bỏ nhãn/bỏ CV, khởi tạo Screening/Prompt Test và đọc lịch sử.
- Mọi endpoint mới mở vẫn trả 403 `password_change_required` cho cả hai vai trò đang phải đổi mật khẩu.
- Đối chiếu actor Audit/tên membership/nhãn; dữ liệu tài khoản Admin tổng hợp trong fixture không đổi.

**Giới hạn:** kích hoạt Prompt thành công cần transaction/ExecuteUpdate SQL Server. Test InMemory gọi mã không tồn tại nhận 404 để kiểm tra đã qua authorization và vào handler, không tuyên bố đã kiểm thử activation SQL thành công. Fixture dựng sẵn active prompt để kiểm tra độc lập luồng khởi tạo Screening. SQL thật và tài khoản thật: **NOT RUN**. Không thay nghiệp vụ activation để ép InMemory chạy được.

Baseline hoàn chỉnh trên assembly build trước khi sửa: **3333 PASS, 29 FAIL, 132 SKIP, tổng 3494**, 11 phút 18 giây. Restore và build baseline PASS, 0 warning/0 error. Một lượt shell ban đầu bị dừng sớm do PowerShell coi stderr của xUnit là lỗi terminating; số liệu ở đây chỉ lấy từ lượt TRX hoàn chỉnh sau đó.

| Lỗi đã tồn tại ở baseline | Số test | Bằng chứng/nguyên nhân |
|---|---:|---|
| Fixture UI B23/B25/B38/EvaluationDetail | 22 | Thiếu đăng ký `FileDownloadService` mà `CvFileButton` của B43 cần; lỗi DI trước khi chạy assertion nghiệp vụ |
| A27MigrationTests, JobSeedDataTests, JobCrudMigrationTests | 3 | Kỳ vọng migration/model cũ kết thúc ở AddUsers; main hiện có AddAuditEvents |
| A43 export save-error | 2 | Bộ đếm interceptor mong 1 lần SaveChanges, thực tế 2 do có lần lưu audit thất bại riêng; chưa phân biệt việc ghi audit với retry nghiệp vụ |
| B41 checkbox event | 1 | Fixture dùng `new HrActorState()` không có phiên verified; sau B43 không thể dùng tên nhập tay làm actor hợp lệ |
| Idle_worker_polls_on_schedule_not_in_a_tight_loop | 1 | Kiểm tra theo thời gian mong 2–5 lần poll, baseline thấy 6; cần phân biệt độ nhạy timing với regression |

Không sửa/skip các test baseline trên để ép xanh. Một test A29 qua Kestrel còn kỳ vọng HR tạo Job phải 403 đã được cập nhật theo yêu cầu mới: kiểm tra Job được 201 và vẫn kiểm tra HR tạo Users bị 403; các assertion actor/history được giữ nguyên.

Build cuối: **PASS, 0 warning, 0 error**. Focused cuối: **361 PASS, 0 FAIL, 2 SKIP, tổng 363** (37 giây), gồm toàn bộ nhóm khớp A29/B27/B28/B29/B43 và test quyền nghiệp vụ mới. Hai skip là A29 SQL và B43 SQL opt-in chưa được bật. Không có test mới bị skip.

Test timing worker chạy riêng: **1 PASS, 0 FAIL, 0 SKIP** (4 giây), không sửa code/assertion worker. Baseline lỗi khi thấy 6 lần poll thay vì 2–5 cho thấy kiểm tra nhạy với timing; kết quả chạy riêng không thay thế số liệu full suite.

Kiểm thử trình duyệt B43 sẵn có: **PASS** trong focused, dùng Chromium thật, tài khoản tổng hợp, API InMemory và các cổng Kestrel riêng. Bao gồm CV PDF/PNG/JPG, tải tên tiếng Việt, export JSON, Audit Admin/HR, và các kích thước màn hình của B43. Không tuyên bố đã nghiệm thu thủ công toàn bộ luồng Prompt/Job mới bằng trình duyệt hoặc SQL Server; phần này có kiểm tra render, policy, Web client thật và HTTP/Kestrel như mô tả trên.

Trong một lượt full, browser test B43 timeout ở empty filter dù các lượt baseline/focused trước đó đã PASS. Ảnh tổng hợp cho thấy ô tìm kiếm rỗng, không có Console error. Test cũ chỉ chờ tiêu đề modal rồi đóng ngay khi request chi tiết có thể chưa xong. Đã bổ sung chờ nội dung `.event-detail` và modal được gỡ khỏi DOM trước khi nhập bộ lọc. Không sửa UI/API Audit, không tăng timeout, thêm sleep hoặc bỏ assertion. Kết quả kiểm tra lại được ghi cùng số liệu cuối.

Kết quả full cuối cùng được cập nhật sau khi đối chiếu TRX. Log/TRX cục bộ nằm trong `artifacts/hr-admin-permissions/` (ignored, không commit).

## 7. File cần review/commit

### Danh sách chính xác: 21 Modified, 2 Added, 0 Deleted/Renamed

| Trạng thái | File tương đối repository |
|---|---|
| M | `src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs` |
| M | `src/SmartCv.Api/Endpoints/JobEndpoints.cs` |
| M | `src/SmartCv.Api/Endpoints/JobSkillEndpoints.cs` |
| M | `src/SmartCv.Api/Endpoints/PromptEndpoints.cs` |
| M | `src/SmartCv.Web/Components/Pages/JobEdit.razor` |
| M | `src/SmartCv.Web/Components/Pages/Jobs.razor` |
| M | `src/SmartCv.Web/Components/Pages/Prompts.razor` |
| M | `src/SmartCv.Web/Components/Pages/_Imports.razor` |
| M | `src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor` |
| M | `src/SmartCv.Web/Components/Prompts/PromptEditor.razor` |
| M | `src/SmartCv.Web/Components/Prompts/PromptHistory.razor` |
| M | `src/SmartCv.Web/Components/Prompts/PromptPlayground.razor` |
| M | `src/SmartCv.Web/Helpers/AccessRules.cs` |
| M | `src/SmartCv.Web/Helpers/AppNavigation.cs` |
| M | `tests/SmartCv.Tests/Auth/A29AuthorizationTests.cs` |
| M | `tests/SmartCv.Tests/Auth/A29AuditTests.cs` |
| M | `tests/SmartCv.Tests/Auth/B43BrowserTests.cs` |
| M | `tests/SmartCv.Tests/B27EndToEndTests.cs` |
| M | `tests/SmartCv.Tests/B28AccessRulesTests.cs` |
| M | `tests/SmartCv.Tests/B28EndToEndTests.cs` |
| M | `tests/SmartCv.Tests/B28RoleUiTests.cs` |
| A | `docs/hr-admin-permissions-fix-report.md` |
| A | `tests/SmartCv.Tests/Auth/HrAdminBusinessPermissionsTests.cs` |

Đã đối chiếu hai chiều với Git status: chỉ các file A/M trên, không staged, không file xóa/đổi tên, không bin/obj/log/TRX/CV/secrets. Artifacts kiểm thử được Git ignore.

## 8. Nghiệm thu và kết luận

1. Dùng môi trường cô lập và tài khoản tổng hợp: HR đăng nhập, mở `/jobs/new`, tạo/sửa Job và kỹ năng; tạo bộ đánh giá, chọn CV, lưu hai loại nhãn; soạn/lưu/kích hoạt Prompt, chạy Prompt Test và so sánh.
2. Kiểm tra HR vẫn đọc `/runs`, xem/tải CV được phép qua cơ chế B43; không vào Users/Audit qua menu hay URL trực tiếp.
3. Admin đối chiếu Audit với UserId/role thực hiện; dữ liệu nhạy cảm không xuất hiện trong payload audit.
4. Phiên hết hạn/thu hồi bị 401; tài khoản MustChangePassword bị chặn nghiệp vụ và chỉ dùng luồng đổi mật khẩu hiện có.

Sau review, cần triển khai/khởi động lại **cả API và Web từ cùng phiên bản** đã duyệt để các policy và nút giao diện đồng bộ. Các tiến trình Debug đang chạy của người dùng chưa được restart trong nhiệm vụ này; nghiệm thu tự động dùng host riêng. Không tự thay cấu hình để thử với database thật.

Không tự áp dụng thay đổi lên database hoặc thao tác tài khoản thật để nghiệm thu. Người dùng review báo cáo và diff, tự quyết định commit/push.

**Phát hiện ngoài thay đổi quyền:** `ActivityLogs.razor` tại SHA nền còn mã `Probe` ghi file vào đường dẫn tuyệt đối trong thư mục Temp của máy phát triển. Bản sửa quyền giữ nguyên file UI này và không sử dụng log Probe để nghiệm thu hay thay thế AuditEvents. Cần review loại bỏ debug probe trước khi triển khai sang môi trường khác; đường dẫn đó có thể không tồn tại và từ khóa tìm kiếm không nên đi vào log phụ. Không đọc hoặc sao chép nội dung file Probe.

## 9. Phụ lục: ma trận toàn bộ 91 API đã kiểm tra

Nguồn: output của test ma trận endpoint chạy trên assembly baseline và assembly đã sửa. Chỉ có 12 khác biệt về quyền HR; Admin, public endpoints và quyền MustChangePassword không đổi. Đây là quyền qua authorization, còn validation, feature flag và các quy tắc nghiệp vụ tiếp tục quyết định response thực tế.

| Method | Route | HR trước | HR sau | Admin sau | Anonymous | MustChangePassword sau |
|---|---|---|---|---|---|---|
| DELETE | `/api/applications/{id:int}` | Được | Được | Được | 401 | 403 |
| DELETE | `/api/evaluation-sets/{id:int}/labels/{applicationId:int}` | Được | Được | Được | 401 | 403 |
| DELETE | `/api/evaluation-sets/{id:int}/members/{applicationId:int}` | Được | Được | Được | 401 | 403 |
| DELETE | `/api/jobs/{id:int}/skills/{code}` | 403 | Được | Được | 401 | 403 |
| GET | `/api/admin/audit-events` | 403 | 403 | Được | 401 | 403 |
| GET | `/api/admin/audit-events/{id:long}` | 403 | 403 | Được | 401 | 403 |
| GET | `/api/applications` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/{id:int}/events` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/{id:int}/flags` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/{id:int}/keyword-score` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/ai-preview/quota` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/flag-reasons` | Được | Được | Được | 401 | 403 |
| GET | `/api/applications/stage-options` | Được | Được | Được | 401 | 403 |
| GET | `/api/auth/me` | Được | Được | Được | 401 | Được |
| GET | `/api/candidates` | Được | Được | Được | 401 | 403 |
| GET | `/api/candidates/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/candidates/{id:int}/data-export` | Được | Được | Được | 401 | 403 |
| GET | `/api/cvs` | Được | Được | Được | 401 | 403 |
| GET | `/api/cvs/{id:guid}` | Được | Được | Được | 401 | 403 |
| GET | `/api/cvs/{id:guid}/candidate-suggestion` | Được | Được | Được | 401 | 403 |
| GET | `/api/cvs/{id:guid}/file` | Được | Được | Được | 401 | 403 |
| GET | `/api/cvs/{id:guid}/text` | Được | Được | Được | 401 | 403 |
| GET | `/api/dashboard/summary` | Được | Được | Được | 401 | 403 |
| GET | `/api/dashboard/trend` | Được | Được | Được | 401 | 403 |
| GET | `/api/data-requests` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluations/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluation-sets` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluation-sets/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluation-sets/{id:int}/applications` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluation-sets/{id:int}/labels/template` | Được | Được | Được | 401 | 403 |
| GET | `/api/evaluation-sets/{id:int}/label-suggestions` | Được | Được | Được | 401 | 403 |
| GET | `/api/health` | Được | Được | Được | Công khai | Được |
| GET | `/api/health/database` | 403 | 403 | Được | 401 | 403 |
| GET | `/api/health/gemini` | 403 | 403 | Được | 401 | 403 |
| GET | `/api/jobs` | Được | Được | Được | 401 | 403 |
| GET | `/api/jobs/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompts` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompts/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompts/active` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompts/compare` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompt-tests` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompt-tests/{runId:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/prompt-tests/compare` | Được | Được | Được | 401 | 403 |
| GET | `/api/retention/settings` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/{id:int}` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/{id:int}/compare` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/{id:int}/failures` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/{id:int}/ranking` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/estimate` | Được | Được | Được | 401 | 403 |
| GET | `/api/runs/latest` | Được | Được | Được | 401 | 403 |
| GET | `/api/users` | 403 | 403 | Được | 401 | 403 |
| PATCH | `/api/applications/{id:int}/stage` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications/{id:int}/ai-preview` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications/{id:int}/flags` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications/flags/{flagId:int}/reopen` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications/flags/{flagId:int}/resolve` | Được | Được | Được | 401 | 403 |
| POST | `/api/applications/stage` | Được | Được | Được | 401 | 403 |
| POST | `/api/auth/change-password` | Được | Được | Được | 401 | Được |
| POST | `/api/auth/login` | Được | Được | Được | Công khai | Được |
| POST | `/api/candidates` | Được | Được | Được | 401 | 403 |
| POST | `/api/candidates/{id:int}/erase` | Được | Được | Được | 401 | 403 |
| POST | `/api/candidates/{id:int}/merge` | Được | Được | Được | 401 | 403 |
| POST | `/api/cvs/{id:guid}/anonymize` | 403 | 403 | Được | 401 | 403 |
| POST | `/api/cvs/{id:guid}/extract` | Được | Được | Được | 401 | 403 |
| POST | `/api/cvs/upload` | Được | Được | Được | 401 | 403 |
| POST | `/api/evaluation-sets` | 403 | Được | Được | 401 | 403 |
| POST | `/api/evaluation-sets/{id:int}/labels/import` | Được | Được | Được | 401 | 403 |
| POST | `/api/evaluation-sets/{id:int}/members` | Được | Được | Được | 401 | 403 |
| POST | `/api/jobs` | 403 | Được | Được | 401 | 403 |
| POST | `/api/jobs/{id:int}/activate` | 403 | Được | Được | 401 | 403 |
| POST | `/api/jobs/{id:int}/deactivate` | 403 | Được | Được | 401 | 403 |
| POST | `/api/jobs/{id:int}/skills` | 403 | Được | Được | 401 | 403 |
| POST | `/api/prompts` | 403 | Được | Được | 401 | 403 |
| POST | `/api/prompts/{id:int}/activate` | 403 | Được | Được | 401 | 403 |
| POST | `/api/prompts/assembled-preview` | 403 | Được | Được | 401 | 403 |
| POST | `/api/prompt-tests` | Được | Được | Được | 401 | 403 |
| POST | `/api/retention/run` | 403 | 403 | Được | 401 | 403 |
| POST | `/api/runs` | Được | Được | Được | 401 | 403 |
| POST | `/api/users` | 403 | 403 | Được | 401 | 403 |
| POST | `/api/users/{id:int}/lock` | 403 | 403 | Được | 401 | 403 |
| POST | `/api/users/{id:int}/reset-password` | 403 | 403 | Được | 401 | 403 |
| POST | `/api/users/{id:int}/unlock` | 403 | 403 | Được | 401 | 403 |
| PUT | `/api/candidates/{id:int}` | Được | Được | Được | 401 | 403 |
| PUT | `/api/cvs/{id:guid}/candidate` | Được | Được | Được | 401 | 403 |
| PUT | `/api/evaluation-sets/{id:int}/labels` | Được | Được | Được | 401 | 403 |
| PUT | `/api/jobs/{id:int}` | 403 | Được | Được | 401 | 403 |
| PUT | `/api/jobs/{id:int}/skills/{code}` | 403 | Được | Được | 401 | 403 |
| PUT | `/api/jobs/{id:int}/skills/{code}/aliases` | 403 | Được | Được | 401 | 403 |
| PUT | `/api/users/{id:int}` | 403 | 403 | Được | 401 | 403 |
