# SCApp — Google-only Authentication Cleanup

## 1. Tổng quan

- Repository: <https://github.com/TuyetAnh0101/AI-CV-Screening>.
- Thư mục: `C:/TTCN/project/AI-CV-Screening`.
- Ngày thực hiện: 26/11/2026, múi giờ Asia/Saigon.
- Nhánh: `chore/google-only-auth-cleanup`.
- `BASE_SHA`: `2b2c514b66b34c2cd378fc7e17f3b83e34fb7add`.
- `HEAD_SHA`: `2b2c514b66b34c2cd378fc7e17f3b83e34fb7add` (các thay đổi chưa commit).
- SDK: .NET `10.0.403`, khớp `global.json`; giữ nguyên ASP.NET Core, Blazor, EF Core và NuGet của repository.
- Phạm vi: email/mật khẩu và Google; dọn UI provider không hỗ trợ, xác minh thông báo liên kết bằng API và bảo vệ phương thức đăng nhập cuối cùng.
- Trạng thái: **PARTIAL**. Phần sửa, Debug/Release build và các kiểm thử mới đã đạt. Hồi quy toàn hệ thống sau sửa: 4.095 PASS, 1 FAIL, 162 SKIP; FAIL duy nhất là polling worker đã có trong baseline. Không phát sinh test failure hoặc warning mới trong các lượt đối chiếu này. Google/SMTP thật và các test SQL opt-in chưa được xác minh. Chưa đủ căn cứ tuyên bố hoàn thành 100% hoặc sẵn sàng push.

### Preflight

Working tree ban đầu sạch, ở nhánh `feature/a62-system-backup`; không có merge/rebase/cherry-pick dở dang. Đã kiểm tra repository, remote (không in credentials), worktree và lịch sử. Nhánh cleanup chưa tồn tại ở local hoặc remote và không có worktree sử dụng nó.

Đã chạy `git fetch origin` thành công. `origin/main` sau fetch chính là BASE_SHA nêu trên. Kiểm tra `git merge-base --is-ancestor 2b2c514b66b34c2cd378fc7e17f3b83e34fb7add origin/main` trả exit 0. Lịch sử cũng có A58, B55, B57, phân quyền, báo cáo HR/Admin và A62. Đã tạo nhánh bằng `git switch --no-track -c chore/google-only-auth-cleanup origin/main`.

Git trong sandbox yêu cầu xác nhận ownership; các lệnh dùng `git -c safe.directory=C:/TTCN/project/AI-CV-Screening ...` cho đúng repository này. Không thay cấu hình Git toàn cục. Không commit, push, merge, rebase, tag hoặc tạo PR.

## 2. Kết quả rà soát toàn repository

Đã tìm tham chiếu trên file được Git theo dõi và lần theo API → service → EF/entity → JWT/session/workspace → Web → tests. Bao gồm cấu hình, database, tài liệu, scripts, BackupWorker và các thư viện tĩnh. Không đọc User Secrets, token, key ring hoặc CV thực tế để phục vụ cleanup.

| File/khu vực | Vấn đề hoặc tham chiếu | Phân loại | Quyết định | Lý do |
|---|---|---|---|---|
| `src/SmartCv.Core/Auth/ExternalProviders.cs` | `IsSupported` chỉ nhận `google` | Đã đúng | Giữ nguyên | Là allowlist dùng chung; không chuẩn hóa provider lạ thành Google |
| `src/SmartCv.Api/Auth/ExternalAuthSetup.cs` | Google OIDC, cookie tạm, PKCE, `/signin-google` | Xác thực đang hoạt động | Giữ nguyên | Không phát hiện nhu cầu sửa cấu hình hay cơ chế bảo vệ |
| `src/SmartCv.Api/Endpoints/ExternalAuthEndpoints.cs` | Start/complete/link-ticket/unlink từ chối provider khác | Đã đúng | Giữ nguyên, thêm regression | Bảo toàn mã 404/400 và chính sách xác thực |
| `src/SmartCv.Web/Helpers/ExternalAuthUiRules.cs` | UI nhận cả `github` | Mã xác thực dư thừa | Dùng allowlist của Core | Không sinh URL đăng nhập/liên kết provider không hỗ trợ |
| `src/SmartCv.Web/Components/Shared/ExternalLoginButtons.razor` | Nhánh render/khởi tạo GitHub | UI provider không hỗ trợ | Loại bỏ | Login/Register dùng chung component này |
| `src/SmartCv.Web/wwwroot/css/b55-external.css` | `.b55-github-button` không còn nơi dùng | CSS dư thừa | Xóa đúng một rule | Giữ nguyên layout, Google logo, trạng thái busy/focus và responsive |
| `src/SmartCv.Web/Components/Account/LoginMethodsCard.razor` | Nhãn dự phòng GitHub | UI liên kết mới | Chỉ render Google sau allowlist | Provider khác từ settings hoặc bản ghi cũ không trở thành thao tác đăng nhập mới |
| `src/SmartCv.Web/Components/Account/AccountPage.razor` | Query tự tạo thông báo thành công Google/GitHub, có thể che lỗi API | Lỗi hiển thị kết quả xác thực | Đối chiếu `ProfileDto.Logins` | Query chỉ là gợi ý; lỗi tải hồ sơ và lỗi callback được ưu tiên |
| `src/SmartCv.Api/Auth/ExternalLoginService.cs` | Unlink tính mọi provider cũ là phương thức thay thế | Lỗi bảo vệ tài khoản | Chỉ đếm provider được hỗ trợ | Không để người chỉ có Google bị mất cách đăng nhập vì tồn tại record GitHub/LinkedIn cũ |
| `src/SmartCv.Web/Helpers/UserText.cs` | Nhãn Google/GitHub/LinkedIn | Hiển thị nguồn đăng ký lịch sử | Giữ, thêm chú thích | Nơi gọi là cột Nguồn của Admin Users; không phải danh sách provider đăng nhập |
| `src/SmartCv.Web/Components/Pages/Users.razor` | Bộ lọc nguồn GitHub trong trang hiện tại | Metadata lịch sử | Ghi rõ “lịch sử”, thêm LinkedIn lịch sử | Đọc được dữ liệu schema cho phép; không thay filter API hoặc quyền Admin |
| `src/SmartCv.Core/Constants/SignupSources.cs`, `Entities/User.cs` | `Admin`, `Local`, `google`, `linkedin` | Schema/metadata lịch sử | Giữ nguyên giá trị, thêm chú thích cho `All` | `All` được `UserConfiguration` dùng để tạo CHECK constraint |
| `src/SmartCv.Api/Auth/RegistrationService.cs`, `AccountSelfServiceService.cs` | Email cho tài khoản có nguồn provider cũ | Tương thích tài khoản cũ | Giữ nguyên | Không tạo external login mới hoặc tự liên kết bằng email; template chỉ mô tả nguồn tạo tài khoản |
| `src/SmartCv.Api/Reports/AdminOperationsReportReader.cs` | Tổng hợp nguồn `linkedin` | Báo cáo lịch sử | Giữ nguyên | Không mất chiều dữ liệu đã lưu |
| `src/SmartCv.Infrastructure/Data/Configurations/*`, `Data/Migrations/*`, `database/a51_add_workspaces.sql` | CHECK source và bảng external login mở cho dữ liệu cũ | Schema đã triển khai | Giữ nguyên | Không migration, đổi constraint, chuyển đổi hay xóa dữ liệu |
| `src/SmartCv.Infrastructure/Data/Seed/JobSeedData.cs`, seed SQL | GitHub/GitHub Issues là alias kỹ năng | Nghiệp vụ tuyển dụng | Giữ nguyên | Không liên quan OAuth |
| `src/SmartCv.Core/Privacy/PersonalDataMasker*`, `GeminiOptions.cs`, tests privacy/scoring | Che URL GitHub/LinkedIn, phân biệt tên công nghệ | Bảo vệ PII/ngữ liệu kiểm thử | Giữ nguyên | Không làm giảm che dữ liệu CV hoặc độ chính xác chấm điểm |
| `tests/SmartCv.Tests/E2E/B57GoogleLoginTests.cs`, tests external auth/schema | Từ chối LinkedIn/provider lạ, dữ liệu lịch sử | Regression bảo mật | Giữ và bổ sung phần thiếu | Những từ khóa này phải còn để kiểm tra hành vi từ chối |
| `README.md`, `CONTRIBUTING.md`, `.gitignore`, `scripts/tai-tessdata.ps1` | GitHub repo, hướng dẫn phát triển, URL tải mã nguồn | Quản lý source/tooling | Giữ nguyên | Không phải phương thức đăng nhập SCApp |
| `docs/*-implementation-report.md`, các báo cáo audit/UI, `SCApp-FINAL-Week7-Week8-Implementation-Guide.md` | Lịch sử B55, đề xuất A59/LinkedIn và liên kết PR | Tài liệu lịch sử/kế hoạch | Không viết lại; báo cáo này xác định trạng thái hiện tại | Không mở rộng sang triển khai A59 hoặc đổi lịch sử bàn giao |
| `src/SmartCv.Web/wwwroot` vendor Bootstrap/validation/font/license/source map | GitHub là URL tác giả/thư viện | Bên thứ ba | Giữ nguyên | Không thay thư viện hoặc license |
| `src/SmartCv.Api/appsettings*.json`, `src/SmartCv.Web/appsettings*.json`, launch settings, project references | Không có scheme/config OAuth GitHub/LinkedIn đang đăng ký | Cấu hình | Không sửa | Không cần xóa package hoặc secret của provider dư thừa |
| `.github`/GitHub workflows | Không có workflow được theo dõi trong checkout này | CI/CD | Không tạo hoặc sửa | Kiểm tra bằng danh sách file được Git theo dõi |
| Toàn bộ các đường dẫn A62 | OAuth Drive, fallback Google client, `drive.file`, mã hóa/worker | Sao lưu/khôi phục | Giữ nguyên toàn bộ | Xác thực đăng nhập và quyền sao lưu Drive là hai hệ thống riêng |

### Logic trước và sau

| Luồng | Trước | Sau |
|---|---|---|
| Login/Register ngoài | Backend chỉ Google nhưng Web còn nhánh GitHub | Web và backend cùng dùng allowlist Google |
| Google tắt | Nhánh GitHub có thể xuất hiện nếu settings quảng bá nó | Không có nút provider ngoài; giữ email/mật khẩu |
| `/account?linked=github` | Có thể hiện thành công GitHub | Không tạo thông báo thành công |
| `/account?linked=google` khi chưa liên kết/lỗi profile | Query có thể tự tạo thành công hoặc che lỗi tải hồ sơ | Chỉ xác nhận nếu GET profile thành công và có login Google |
| Query trùng `linked` hoặc lỗi callback | Không kiểm soát đầy đủ; query thành công có thể lấn lỗi | Chỉ nhận đúng một `linked=google`; ưu tiên lỗi callback và giữ lỗi profile |
| Đổi query trong cùng circuit | `OnInitializedAsync` không chạy lại | Tải/xác minh lại qua `OnParametersSetAsync` |
| Hủy Google, không mật khẩu, còn record provider cũ | Có thể trả 204 dù provider cũ không dùng được | Trả 409 `last_login_method`, giữ nguyên liên kết và không tạo email hủy |
| Hủy Google khi có mật khẩu | Theo luồng A58/B57 hiện có | Giữ nguyên; không xoay SecurityStamp, không xóa workspace |

## 3. Danh sách thay đổi

Tất cả đường dẫn đầy đủ bên dưới thuộc nhánh cleanup. Không có file bị xóa.

| Loại | Đường dẫn đầy đủ | Nội dung/lý do và nghiệp vụ tác động |
|---|---|---|
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Api/Auth/ExternalLoginService.cs` | Chặn hủy phương thức cuối khi record provider không hỗ trợ tồn tại |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Core/Constants/SignupSources.cs` | Chú thích danh sách schema/lịch sử; không đổi giá trị hoặc EF model |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Components/Account/AccountPage.razor` | Xác minh link qua profile, giữ lỗi thật, xử lý query trùng và thay query trong circuit |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Components/Account/LoginMethodsCard.razor` | Gỡ nhãn GitHub dự phòng khỏi phương thức liên kết |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Components/Pages/Users.razor` | Phân biệt nguồn lịch sử; giữ dữ liệu cũ và phạm vi lọc trong trang |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Components/Shared/ExternalLoginButtons.razor` | Xóa nhánh/nút/trạng thái GitHub ở Login/Register |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Helpers/ExternalAuthUiRules.cs` | Tái sử dụng `ExternalProviders.IsSupported` cho URL login/link |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/Helpers/UserText.cs` | Chú thích nhãn nguồn đăng ký lịch sử, không đổi hiển thị dữ liệu cũ |
| Sửa | `C:/TTCN/project/AI-CV-Screening/src/SmartCv.Web/wwwroot/css/b55-external.css` | Xóa rule GitHub không còn dùng |
| Sửa | `C:/TTCN/project/AI-CV-Screening/tests/SmartCv.Tests/ExternalAuth/A58EndpointTests.cs` | Kiểm tra provider bị từ chối, callback giả không gọi Google/không cấp identity và Drive client fallback |
| Sửa | `C:/TTCN/project/AI-CV-Screening/tests/SmartCv.Tests/ExternalAuth/A58LinkTests.cs` | Ba ca record lịch sử không được tính là cách đăng nhập thay thế |
| Sửa | `C:/TTCN/project/AI-CV-Screening/tests/SmartCv.Tests/E2E/B57BrowserTests.cs` | Thêm browser regression cho Google tắt, query giả, thay query trong circuit và ba viewport |
| Mới | `C:/TTCN/project/AI-CV-Screening/tests/SmartCv.Tests/GoogleOnlyAuthUiTests.cs` | Render component thật với HTTP fixture cách ly; kiểm tra nút, trạng thái profile, lỗi và nhãn lịch sử |
| Mới | `C:/TTCN/project/AI-CV-Screening/scripts/commit-google-only-auth-cleanup.ps1` | Script để người dùng tự xem/commit/push, mặc định dừng khi chưa xác nhận nghiệm thu |
| Mới | `C:/TTCN/project/AI-CV-Screening/docs/google-only-auth-cleanup-report.md` | Báo cáo này |

## 4. Các thành phần chủ động giữ nguyên

Google OIDC giữ callback `/signin-google`, cookie `__Host-SCApp.External` HttpOnly/Secure, state/correlation/nonce của middleware, code flow + PKCE, HTTPS metadata và scopes `openid email profile`. Không đổi thời hạn cookie/code/ticket, redirect contract, Audit hoặc logic bật/tắt theo môi trường.

Định danh vẫn là `(Provider, Subject)`. Không tự liên kết theo email; Admin không được đăng nhập/liên kết Google. Giữ kiểm tra active, SecurityStamp, JWT/session revocation, workspace membership và các policy Admin/HR/Owner.

Registration, email verification, reset password, resend limit, outbox được mã hóa, Data Protection và SMTP TLS giữ nguyên. Email token vẫn đúng purpose, có hạn, dùng một lần; reset đổi SecurityStamp theo cơ chế cũ.

Giữ toàn bộ migration, ModelSnapshot, CHECK `SignupSource`, unique/FK, `UserExternalLogins` và metadata lịch sử. `SignupSources.All` không phải allowlist OAuth. Không đưa LinkedIn/GitHub cũ về Google.

Không đổi bất kỳ mã/cấu hình A62, BackupWorker, Drive callback, scopes, refresh token, AES-256-GCM, key ring, backup catalog, lịch sao lưu, báo cáo hoặc restore. Không gọi Gemini thật, không gửi email thật, không kết nối/ngắt Drive hay chạy backup/restore thật.

## 5. Kết quả kiểm thử

### Lệnh và môi trường

Release baseline chạy trên assembly build từ BASE_SHA, trước khi sửa ứng dụng; không build lại Release trong lúc baseline đang chạy. Có 4.215 test được discovery từ assembly baseline. Fixture đã đọc trước khi chạy: `TestSupport`, `StartupGatedApiFactory`, `ApplicationDatabaseGuard`, `A28TestSupport`, `B57World`, SQL/browser attributes và `A62Installation`. SQL chỉ chạy khi có biến opt-in và catalog cách ly; phiên này không đặt các biến đó. Browser dùng Chromium hiện có, Kestrel loopback, tài khoản tổng hợp/InMemory và key ring tạm.

Lượt baseline Windows đã hoàn tất, exit 1: **4.057 PASS, 1 FAIL, 162 SKIP, tổng 4.220**. Thống kê dùng kết quả runtime trong TRX, không dùng số mục discovery. FAIL duy nhất: `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`, actual 11 ngoài khoảng 2–5. Không sửa worker hoặc assertion trong nhiệm vụ cleanup. TRX: `artifacts/google-only-auth-cleanup/baseline-windows/baseline-windows.trx`; thời gian khoảng 52 phút. Hash DLL baseline và thời điểm build được lưu ở `baseline-assembly.json` trong cùng thư mục artifacts.

Lượt sau sửa đã hoàn tất, exit 1: **4.095 PASS, 1 FAIL, 162 SKIP, tổng 4.258**; console ghi 54 phút 26 giây, khoảng thời gian TRX là 3.270 giây. FAIL duy nhất vẫn là cùng phương thức polling worker, actual 10 ngoài khoảng 2–5. Không có phương thức FAIL mới so với baseline; toàn suite vẫn FAIL vì lỗi cũ này. TRX cuối: `artifacts/google-only-auth-cleanup/final/final.trx`. Kết quả tổng hợp nằm trong `baseline-stats.json` và `final-stats.json`.

Discovery sau sửa có 4.253 test, tăng 38 so với 4.215 baseline; runtime cũng tăng đúng 38. Cả 38 ca mới đều PASS, gồm 24 UI/helper, 6 provider không hỗ trợ, 2 callback giả, 3 unlink với record lịch sử, 2 cấu hình Drive client và 1 browser. Không tăng SKIP, xóa hoặc vô hiệu hóa test. Số SKIP được đếm từ từng dòng kết quả `NotExecuted` trong TRX; không dùng riêng thuộc tính `Counters.notExecuted` (runner ghi 0 dù có 162 dòng SKIP).

Lượt mặc định trong sandbox được **dừng bằng Ctrl+C riêng session test** sau hơn 40 phút và hơn 4 GB bộ nhớ; không dừng API/Web/SQL hay tiến trình người dùng. Lượt đó chưa hoàn tất và không có TRX tổng kết, vì vậy không báo tổng PASS/SKIP cho lượt đã hủy. Log `baseline-tests.log` giữ nguyên bảy lỗi đã quan sát:

- `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`: actual 8 ngoài khoảng 2–5.
- `B29UsersTests.Duplicate_email_is_a_field_error_and_the_dialog_stays_open`: login fixture trả null.
- `B29UsersTests.Lock_and_unlock_hr_with_if_match_and_unlock_keeps_the_password_state`: login fixture trả null.
- `B29UsersTests.Admin_account_needs_explicit_confirmation_and_invalid_input_sends_nothing`: login fixture trả null.
- `B28EndToEndTests.Hr_hitting_an_admin_api_gets_403_access_denied_and_stays_signed_in`: login fixture trả null.
- `B29ChangePasswordTests.Business_apis_stay_closed_until_the_password_is_changed`: login fixture trả null.
- `Ops.A44ProductionTests.Web_keys_survive_restart_and_a_new_deployment_directory_with_the_same_application_identity`: DPAPI `CryptographicException` ở bước bảo vệ key ring tạm.

Chạy chẩn đoán trên chính assembly baseline: ca B29 Duplicate PASS riêng trong 3 giây; ca A44 PASS ngoài sandbox trong 172 ms với key ring tạm. Kết quả riêng không xóa các FAIL đã ghi nhận. Chúng cung cấp căn cứ chạy lại **toàn bộ** baseline ở môi trường Windows có DPAPI hoạt động, giới hạn 4 luồng. Lượt baseline Windows và lượt sau sửa dùng cùng cấu hình runner; trước mỗi lượt kiểm tra không có biến SQL/application database opt-in. Không rebuild assembly Release gốc trước khi baseline Windows hoàn tất.

Các lệnh chính (tiếp tục bước phụ thuộc chỉ khi lệnh trước thành công):

```powershell
dotnet restore SmartCv.slnx
dotnet build SmartCv.slnx -c Release --no-restore
dotnet test SmartCv.slnx -c Release --no-build --no-restore
dotnet test SmartCv.slnx -c Release --no-build --no-restore --list-tests
```

Build Debug theo output mặc định ban đầu bị `MSB3026/MSB3027/MSB3021`: `SmartCv.Api.exe` PID 11288 và `SmartCv.Web.exe` PID 7944 dùng DLL ở `bin/Debug/net10.0`. Không dừng tiến trình của người dùng. Đã dùng output cách ly và giữ assets/NuGet đã restore:

```powershell
dotnet build SmartCv.slnx -c Debug --no-restore --artifacts-path artifacts/google-only-auth-cleanup/isolated -p:UseArtifactsIntermediateOutput=false
```

Một lần restore riêng cho artifacts path gặp `NU1301`/TLS security package và `NU1900` trong sandbox. Không bỏ TLS, thay NuGet hay cập nhật dependency. Phương án trên dùng assets hiện có, không cần restore lại vào đường dẫn khác.

Về sau không còn thấy hai tiến trình giữ DLL. Đã chạy lại đúng `dotnet build SmartCv.slnx -c Debug --no-restore`: **PASS, exit 0, 12 warnings đã có, 0 errors**, log `final-debug-standard.log`.

### So sánh baseline và sau sửa

| Hạng mục | Baseline | Sau chỉnh sửa | Kết luận |
|---|---|---|---|
| Restore chuẩn | PASS, exit 0 | PASS, exit 0, up-to-date | Không đổi dependency |
| Debug Build | Output chuẩn ban đầu bị khóa DLL | PASS output chuẩn, 12 warnings, 0 errors; output cách ly cũng PASS | Không dừng ứng dụng của người dùng |
| Release Build | PASS, exit 0 | PASS, exit 0 | 12 warnings, 0 errors ở cả hai lượt |
| Warnings | 12 | Debug chuẩn 12, Release 12 | So sánh từng warning: 0 mới, không suppress warning |
| Errors | 0 ở Release | 0 ở Debug chuẩn và Release | Lỗi file lock ban đầu thuộc môi trường |
| Tests PASS toàn suite | 4.057 | 4.095 | Tăng đúng 38 ca mới |
| Tests FAIL toàn suite | 1 (polling worker) | 1 (cùng polling worker) | Hai lượt đều exit 1; không bỏ qua lỗi cũ |
| Tests SKIP toàn suite | 162 | 162 | Không đổi điều kiện SKIP |
| Tổng test runtime | 4.220 | 4.258 | Discovery và runtime được phân biệt |
| Auth tập trung | Lỗi unlink cuối đã tái hiện HTTP 204 | 66 PASS, 0 FAIL, 0 SKIP trước hai ca Drive mới | Không dùng query để chứng minh link |
| Browser mới | Chưa có test tương ứng | 1 PASS, 0 FAIL, 0 SKIP | 1366×768, 1024×768, 390×844; đổi query cùng circuit |
| Drive client fallback/riêng khi Google login tắt | Chưa có test tương ứng | 2 PASS, 0 FAIL, 0 SKIP | Không gọi Google/Drive thật |
| A51 schema + A62 rules/crypto/Drive client | Có sẵn trong suite | 45 PASS, 0 FAIL, 0 SKIP | EF xác nhận không có pending model changes, không mở kết nối SQL |
| Auth Regression (PASS/FAIL/SKIP) | 605 / 0 / 10 | 642 / 0 / 10 | Không tính NOT RUN là PASS |
| Workspace Regression | 284 / 0 / 13 | 284 / 0 / 13 | Cùng phạm vi bộ lọc tên test |
| Email/Registration Regression | 84 / 0 / 1 | 84 / 0 / 1 | Fixture, không SMTP thật |
| Browser toàn suite | 5 / 0 / 1 | 6 / 0 / 1 | Một browser A62 cần SQL chưa chạy |
| E2E toàn suite | 44 / 0 / 5 | 45 / 0 / 5 | Năm test SQL opt-in chưa chạy |
| A62 toàn suite | 33 / 0 / 10 | 33 / 0 / 10 | Lõi/fake Drive; SQL pipeline/restore chưa chạy |
| Reports toàn suite | 51 / 0 / 0 | 51 / 0 / 0 | Báo cáo HR/Admin và export theo test có sẵn |

Warning có sẵn: `CS9113` ở `VerificationFlow`; ba `CS0618` của query filter; `xUnit2013`, `xUnit2004`, `xUnit2009`; năm `CA1416` của DPAPI backup tests. Không sửa warning ngoài phạm vi.

Các nhóm trong bảng có thể giao nhau; không cộng chúng để suy ra tổng suite. Log build cuối: `final-debug-standard.log`, `final-release.log`, `final-restore.log` trong `artifacts/google-only-auth-cleanup/`.

Các test mới kiểm tra URL provider lạ/null/khác hoa thường, settings quảng bá provider không hỗ trợ, profile không có link, profile lỗi, callback lỗi, query trùng, Google tắt, nhãn lịch sử, bản ghi provider cũ khi unlink, endpoint 400/404, callback giả qua middleware với backchannel chặn outbound, và Drive client riêng/fallback.

Lần kiểm thử đầu đã chứng minh ba ca unlink trả 204 trên code cũ thay vì 409. Fixture UI ban đầu thiếu dấu `/` cuối route `api/workspace/`; đã sửa đúng contract, không tính lỗi fixture đó là lỗi sản phẩm hoặc bằng chứng regression. Các lần chạy thử trước khi fixture hoàn chỉnh không thay cho bảng nghiệm thu cuối.

Log/TRX nằm tại `artifacts/google-only-auth-cleanup/` và được `.gitignore` loại trừ; không stage DLL, browser state, screenshots hoặc log.

Baseline Windows và lượt hồi quy sau sửa dùng `artifacts/google-only-auth-cleanup/limited-parallel.runsettings` với `MaxParallelThreads=4`, diagnostic messages và thông báo test chạy lâu. Đây là [cấu hình runner được xUnit hỗ trợ](https://xunit.net/docs/config-runsettings); không đổi assertion, retry logic, skip condition hoặc danh sách test. Lượt sandbox mặc định và các lỗi của nó được ghi riêng để không quy việc test timing/DPAPI hồi phục cho cleanup.

Cách tạo lại cấu hình runner và kiểm tra toàn suite trong terminal Windows của người dùng (không cần lưu file này trong Git):

```powershell
New-Item -ItemType Directory -Force artifacts/google-only-auth-cleanup | Out-Null
@'
<RunSettings>
  <xUnit>
    <MaxParallelThreads>4</MaxParallelThreads>
    <DiagnosticMessages>true</DiagnosticMessages>
    <LongRunningTestSeconds>60</LongRunningTestSeconds>
  </xUnit>
</RunSettings>
'@ | Set-Content -LiteralPath artifacts/google-only-auth-cleanup/limited-parallel.runsettings -Encoding UTF8
dotnet test SmartCv.slnx -c Release --no-build --no-restore --settings artifacts/google-only-auth-cleanup/limited-parallel.runsettings
```

### Email: bốn trạng thái độc lập

| Trạng thái | Mức xác minh |
|---|---|
| Tạo email/outbox | Kiểm tra bằng fixture/test double; không dùng outbox người dùng thật |
| Xử lý dispatch/worker | Test processor với fake store/sender và đường lỗi/retry; hosted SMTP worker thật chưa xác minh |
| SMTP chấp nhận email | NOT RUN |
| Email đến hộp thư | NOT RUN |

`appsettings.Development.json` hiện chọn `Email:Mode=Smtp`, worker bật. Không đọc/sửa SMTP credentials; vì vậy không kết luận lỗi nhận email trước đây đã được khắc phục. Khi kiểm tra thực tế cần phân biệt mode, worker, trạng thái outbox, lỗi an toàn, base URL và Data Protection key ring.

## 6. Xác nhận an toàn

| Câu hỏi | Kết quả |
|---|---|
| Đổi schema/constraint hoặc EF model? | Không; chỉ chú thích `SignupSources.All`, không đổi giá trị |
| Đổi migration/ModelSnapshot? | Không |
| Ghi/xóa dữ liệu người dùng hoặc database thật? | Không; chỉ fixture InMemory/tạm, SQL opt-in không được cấu hình |
| Đổi Google Cloud/OAuth configuration thật? | Không |
| Đổi SMTP configuration thật? | Không |
| Đổi mã/cấu hình A62 Backup/Restore? | Không; có bổ sung kiểm tra client fallback, không gọi Drive thật |
| Đổi quyền Admin/HR/Owner? | Không |
| Secrets trong thay đổi? | Chỉ credential giả có nhãn `synthetic-*` trong test; không đọc hoặc chép secrets thật |
| Có test bảo mật bị xóa/tắt? | Không |
| Có lỗi mới do cleanup? | Không có test failure hoặc warning mới trong lượt đối chiếu đầy đủ; vẫn còn một FAIL polling worker đã có từ baseline và các phần NOT RUN |

## 7. Hạn chế và vấn đề còn tồn tại

1. Google OAuth thật chưa chạy; các principal Google trong regression nghiệp vụ là tổng hợp. Callback giả được kiểm tra qua middleware thật với outbound bị chặn. Chữ ký/nonce/correlation của vòng đăng nhập Google thành công thực tế vẫn **NOT VERIFIED**.
2. SMTP thật và việc nhận email chưa chạy. Không gửi thư đến ứng viên/người dùng.
3. SQL Server integration, registration/reset transaction/concurrency thật và A62 backup/restore pipeline opt-in chưa chạy. Không tự tạo catalog, thay quyền SQL hay khởi động service để mở rộng nhiệm vụ.
4. Lỗi khóa DLL Debug ban đầu đã được ghi nhận; sau khi tiến trình giữ DLL không còn chạy, build output chuẩn và cách ly đều PASS. Không dừng API/Web của người dùng để đạt kết quả này.
5. Lượt sandbox đã hủy có bảy FAIL quan sát được, được liệt kê nguyên vẹn ở mục 5. Baseline Windows và lượt sau sửa đầy đủ đều chỉ FAIL polling worker (actual lần lượt 11 và 10 ngoài 2–5); không sửa rộng worker/quản lý tài khoản hoặc đổi assertion để che lỗi. Lỗi DPAPI đã PASS khi chạy bằng môi trường Windows phù hợp.
6. Không triển khai A59, A63, B59 Owner, B58 hoặc A61; tài liệu kế hoạch/lịch sử của chúng được giữ nguyên.
7. PowerShell trên máy đặt `LocalMachine: AllSigned`. File `.ps1` chưa có chữ ký bị chặn khi thử chạy helper chỉ đọc TRX. Đã tổng hợp TRX bằng lệnh đọc XML trực tiếp; không thay ExecutionPolicy. Script commit/push bàn giao cũng cần được ký theo quy trình/certificate được máy tin cậy trước khi thực thi. Agent không tạo certificate hoặc tự ký/chạy script commit/push.

## 8. Hướng dẫn kiểm thử thủ công trên localhost

Chỉ dùng tài khoản/email thử nghiệm do bạn kiểm soát; không công khai mật khẩu, link token, cookies hoặc secrets. Nếu còn tiến trình ứng dụng cũ, tự dừng chúng trong terminal của bạn trước khi chạy bản mới với cấu hình hiện có:

```powershell
dotnet run --project src/SmartCv.Api -c Release --no-build --launch-profile https
# Terminal khác:
dotnet run --project src/SmartCv.Web -c Release --no-build --launch-profile https
```

Dùng địa chỉ thực tế của launch profile đã kiểm tra trong repository: Web `https://localhost:7258`, API `https://localhost:7269` (HTTP phụ lần lượt 5172 và 5156). Nếu máy có cấu hình local riêng, đối chiếu với `ApiBaseUrl` và `App:WebBaseUrl` đang dùng; không công khai giá trị nhạy cảm. Không thay Google Console, secrets hay startup guard để vượt lỗi cấu hình.

1. **Email/mật khẩu:** đăng nhập tài khoản thử đã xác minh; thử mật khẩu sai, tài khoản khóa và chưa xác minh. Kiểm tra Admin bắt buộc đổi mật khẩu vẫn được chuyển đúng trang.
2. **Google login:** với môi trường thử nghiệm đã cấu hình Google, dùng nút Google; không có GitHub/LinkedIn. Đăng nhập lần hai phải về cùng user/workspace. Google trùng email local phải yêu cầu liên kết, không tự gộp. Thử hủy Google OAuth và kiểm tra thông báo tiếng Việt. Khi Google tắt, không có nút provider ngoài.
3. **Liên kết:** đăng nhập HR/Owner thử nghiệm → `/account` → Liên kết Google. Sau callback, hồ sơ và danh sách phương thức xác nhận link thật. Google thuộc người khác và ticket hết hạn/replay phải bị từ chối.
4. **Query giả:** mở `/account?linked=github`, `?linked=linkedin`, `?linked=unknown`, và `?linked=google` trên tài khoản chưa liên kết. Không được có banner thành công. Thử `?linked=google&linked=github`, lỗi profile, `?linked=google&external_error=already_linked` và đổi query trong cùng trang.
5. **Hủy liên kết:** tài khoản có mật khẩu được hủy Google; đăng nhập mật khẩu và dữ liệu/workspace vẫn còn. Tài khoản chỉ có Google phải bị chặn, kể cả fixture có record provider lịch sử. Thiết lập mật khẩu qua email trước khi hủy.
6. **Đăng ký/xác minh:** dùng email thử mới, chấp nhận điều khoản; kiểm tra outbox Pending và protected payload bằng công cụ nội bộ, không chép token. Xác minh đúng link theo Web base URL. Dùng lại/hết hạn phải bị từ chối; gửi lại có hạn mức.
7. **Quên/đặt lại mật khẩu:** response không tiết lộ tài khoản tồn tại. Token đúng mới đặt được mật khẩu; token sai/hết hạn/replay không đổi dữ liệu. Phiên cũ bị thu hồi theo cơ chế hiện có.
8. **Email delivery:** kiểm tra riêng worker bật, SMTP mode/credentials được cấp qua kênh local an toàn, `EmailOutbox.Status/Attempts/LastError` và base URL. `Sent`/SMTP accepted không chứng minh thư đã vào Inbox; kiểm tra cả Spam trên hộp thư thử. Không gửi thư đến ứng viên.
9. **Admin/HR/Owner:** Admin truy cập quản trị; HR/Owner không vào Admin API. Mỗi Owner chỉ thấy workspace của mình. Kiểm tra menu, logout, đổi quyền/khóa tài khoản và URL trực tiếp.
10. **Admin Backup:** chỉ xem `/admin/backups`, overview, lịch/catalog và báo cáo với quyền Admin; HR/Owner bị chặn. Kiểm tra contract `/api/admin/backups/drive/callback` và fallback bằng test fixture. Không connect/disconnect, backup hoặc restore Drive/database thật để nghiệm thu nhánh này.
11. **Nghiệp vụ:** trên dữ liệu thử cách ly, kiểm tra job, upload CV, text extraction, screening bằng fake Gemini, results/ranking, báo cáo HR, Excel/PDF, Admin dashboard và Audit. Không gọi Gemini trả phí chỉ để regression cleanup.
12. **Giao diện:** desktop/mobile 1366/1024/390 px; thao tác Tab/Enter, trạng thái loading, lỗi và focus vẫn hoạt động, không có khoảng trống nút GitHub.

## 9. Commit/push — chỉ người dùng tự thực hiện

Script đầy đủ: [`scripts/commit-google-only-auth-cleanup.ps1`](../scripts/commit-google-only-auth-cleanup.ps1). **Chưa thực thi.** Script mặc định dừng vì bàn giao PARTIAL; đây không phải xác nhận code đã sẵn sàng push.

Sau khi đọc báo cáo, hoàn tất hoặc chủ động chấp nhận các hạn chế nghiệm thu còn lại, xử lý index theo quyết định của người dùng và ký file theo chính sách `AllSigned` của máy, người dùng có thể tự chạy:

```powershell
Set-Location C:\TTCN\project\AI-CV-Screening
.\scripts\commit-google-only-auth-cleanup.ps1 -ReviewedAcceptance
```

Script kiểm tra đúng repository/origin/branch/base, thao tác Git dở dang, staged changes có sẵn, danh sách file khớp chính xác báo cáo và đường dẫn nhạy cảm; stage từng file cụ thể; dừng nếu Git lỗi hoặc whitespace lỗi. Script dò dạng credential phổ biến trên phần thêm, hiển thị toàn bộ staged diff và yêu cầu nhập `COMMIT-AND-PUSH`. Bộ dò không thay thế việc người dùng tự kiểm tra secrets. Không `git add .`, `git add -A`, commit rỗng, force push hoặc merge; commit lỗi thì không push.

Trong lúc thực hiện đã phát hiện hai file được stage: `src/SmartCv.Web/Components/Shared/ExternalLoginButtons.razor` và `src/SmartCv.Web/Helpers/UserText.cs`. Agent không chạy `git add` và không bỏ stage các file đó. Staged diff khớp nội dung cleanup đã rà soát. Script sẽ dừng khi index có thay đổi có sẵn; người dùng tự xem và quyết định cách xử lý index trước khi dùng script.

Commit message:

```text
chore(auth): retain Google-only external authentication and remove unused provider code
```

## 10. Kiểm tra chất lượng và trạng thái Git bàn giao

Nhánh cuối: `chore/google-only-auth-cleanup`; HEAD vẫn bằng BASE_SHA `2b2c514b66b34c2cd378fc7e17f3b83e34fb7add`. Chưa commit/push/merge/rebase/tag hoặc tạo PR.

Đối chiếu với BASE_SHA: 12 file được theo dõi đã sửa, 3 file mới, không xóa file. Index có hai file nêu ở mục 9; 10 file được theo dõi còn lại chưa stage và ba file mới chưa được theo dõi. Agent giữ nguyên index có sẵn. Danh sách 15 đường dẫn khớp chính xác whitelist của script và mục 3.

Đã xem diff đầy đủ, stat, name-status, numstat, staged diff và nội dung cả ba file mới. `git diff --check`, `git diff --cached --check` và `git diff BASE_SHA --check` đều exit 0. Không có conflict marker, lỗi decode UTF-8, ký tự thay thế hoặc thay đổi line endings hàng loạt; không có file source bị cắt hay placeholder thay implementation. Không có binary, ZIP, log, test artifact, cấu hình thật, migration, ModelSnapshot hoặc đường dẫn A62 trong thay đổi.

Đã rà soát nội dung và dò mẫu credential phổ biến trên 15 file: không phát hiện secrets thật. File PowerShell được parser kiểm tra, không có lỗi cú pháp; chỉ kiểm tra tĩnh, không chạy script commit/push. Bộ dò tự động không thay thế bước người dùng tự xem staged diff trước khi quyết định commit.
