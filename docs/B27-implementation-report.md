# B27: Giao diện đăng nhập, phiên JWT và khắc phục HTTP 401

**Kết luận: CONDITIONAL.** Code, test tự động và kiểm thử trình duyệt trên API thật (dữ liệu tổng hợp) đều đạt. Chưa thử với `SmartCvDb` và tài khoản Admin thật (`LIVE TEST: NOT VERIFIED`). Bước này cần người dùng tự nhập mật khẩu, xem mục H.

---

## A. Tài liệu gốc

**Đã đọc trong `C:\TTCN\A27_B43_SmartCv.zip`:**
- `HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md`, đủ 1018 dòng: phần chung, thứ tự gộp, A27 đến A30, B27, B28, B29, B43, lỗi thường gặp.
- Mã mẫu B27, đủ 17 file: `Login.razor/.css`, `AuthLayout`, `AuthGate`, `UserMenu`, `MainLayout`, `App.razor`, `AuthSession`, `ProtectedSessionTokenStore`, `CircuitServicesAccessor`, `AuthTokenHandler`, `AuthApiClient`, `ServiceCollectionExtensions`, `AuthText`, 4 file test.
- Danh sách file và phần mô tả của B28, B29, B43 (để không làm lấn sang). Contract A28 trong repo: `AuthContracts.cs`, `AuthEndpoints.cs`, `ProblemAuthorizationResultHandler.cs`, `AuthSetup.cs`, `LoginRateLimitPolicy.cs`.

**Hình ảnh: ZIP KHÔNG CÓ ẢNH.** Gói chỉ có `.cs`, `.razor`, `.css`, `.md`, `.sql`, `.txt`, `.js`. Thiết kế dựa trên token Deep Teal + AI Glow đang có trong `wwwroot/app.css` và logo `images/brand/scapp-logo.png`.

**Không chép đè mã ZIP.** Mã mẫu dựng trên một bản repo cũ hơn, nên tôi chỉ dùng làm tài liệu tham khảo. Ví dụ: mẫu có `IPromptApi`/`FakePromptApi` và `App.razor lang="en"`, còn repo hiện không có hai interface đó và dùng `lang="vi"`.

**Những chỗ làm khác hướng dẫn gốc và lý do:**

| # | Hướng dẫn / mã mẫu | B27 làm | Lý do |
|---|---|---|---|
| 1 | `AuthTokenHandler` (DelegatingHandler) + `CircuitServicesAccessor` (AsyncLocal) gắn token | Gắn token lên **từng `HttpRequestMessage`** trong `ApiClientBase`. Phiên được gắn khi DI tạo client trong scope của circuit (`AddTypedClient` + `UseSession`) | Đúng yêu cầu "không dựa vào handler dùng chung". Handler của HttpClientFactory sống trong scope riêng và được dùng chung. AsyncLocal có thể "chảy" sang tác vụ nền. Không cần thêm `CircuitHandler` |
| 2 | Tab chưa đăng nhập vẫn gửi request rồi nhận 401 | Tab chưa đăng nhập **không gửi request**, trả lỗi cục bộ `FailureKind = SignedOut` | Đây chính là nguồn "hàng loạt 401" trong log |
| 3 | Khôi phục phiên tin luôn dữ liệu trong storage | Xác minh lại bằng `GET /api/auth/me` trước khi dùng phiên. 401 thì xóa phiên và hiện "phiên đã hết hạn". API tạm lỗi thì giữ phiên ở trạng thái "chưa xác minh" | Yêu cầu mục 7. Tài khoản có thể đã bị khóa hoặc đổi quyền |
| 4 | Gặp 401 là xóa phiên | Chỉ xóa nếu 401 thuộc **đúng token hiện tại** | Request cũ trả về muộn sau khi người dùng đăng nhập lại không được đá phiên mới ra |
| 5 | Không xử lý `password_change_required` (B29 mới có trang đổi mật khẩu) | `AuthGate` hiện hướng dẫn "Cần đổi mật khẩu trước khi tiếp tục" kèm nút Đăng xuất. Không gọi API nghiệp vụ, không tự đặt `MustChangePassword=false` | A29 đã chặn mọi API nghiệp vụ bằng 403 với tài khoản này. A30 (API đổi mật khẩu) chưa có |
| 6 | Đăng xuất và hết phiên chỉ điều hướng trong circuit | Đăng xuất / hết phiên: **tải lại trang** `/login` (`forceLoad`) | Bỏ circuit cũ nên mọi trạng thái Scoped (ô tìm kiếm của `CandidateHubState`, tên người thao tác của `HrActorState`, dữ liệu đã tải) không lọt sang người dùng kế tiếp trên cùng tab |
| 7 | Câu lỗi lấy từ thông báo của API | Câu lỗi chọn theo **mã trạng thái + `code` + loại lỗi phía Web** (`AuthText.LoginError`), không dùng chữ của API | Yêu cầu mục 6. Tách rõ 400, 401, 403 (`account_locked` / khác), 429, 503, 5xx, mất kết nối, quá thời gian |
| 8 | `AuthGate` bọc phần nội dung | Bọc **toàn bộ khung** (menu trái, thanh trên, nội dung) | Chưa xác minh phiên thì không vẽ menu và không tạo component nào, nên không có lời gọi API nào |
| 9 | Một thẻ ở giữa màn hình | Hai khối: giới thiệu Deep Teal và thẻ form. Màn hẹp thu thành dải logo | Mục 5 yêu cầu bố cục thương hiệu và form |
| 10 | Gắn handler cho mọi client, kể cả Health | `HealthApiClient` và `AuthApiClient` không bao giờ gửi Bearer | Yêu cầu mục 7. Đăng nhập trả 401 là "sai mật khẩu", không phải "hết phiên" |
| 11 | Đổi constructor client | Giữ nguyên constructor của mọi client | 61 chỗ trong test đang `new XxxApiClient(http, logger)` vẫn chạy như cũ |

---

## B. Git

| Mục | Giá trị |
|---|---|
| Nhánh | `feature/B27-login-ui` |
| Worktree | `C:\TTCN\project\AI-CV-Screening-B27` |
| SHA nền | `461f1376bd8bcd5df5584e0f0667641f3eaebd1e` (`origin/main` = A29 #85) |
| Trạng thái | Chưa `add`, chưa commit, chưa push |

**Nhánh đã có sẵn từ trước.** Lúc bắt đầu, nhánh và worktree B27 đã tồn tại: sạch, 0 commit riêng, nền `e2e8690` (A28). Trong lúc đó `origin/main` đã có A29. Sau khi người dùng chọn phương án này, tôi chạy `git merge --ff-only origin/main` trong worktree B27. Đây là fast-forward thuần, không mất gì, không tạo merge commit.

**A29 ở thư mục gốc:** `C:\TTCN\project\AI-CV-Screening` đang ở `feature/a29-api-authorization` (`01bf71b`), sạch, **không bị động tới**. `git diff 01bf71b 461f137` rỗng, tức nội dung A29 local trùng với bản đã gộp vào `main`.

**File thay đổi (25 file + báo cáo này). Không xóa file nào.**

| Loại | File |
|---|---|
| Sửa (7) | `Components/App.razor`, `Components/Layout/MainLayout.razor`, `Services/ApiClientBase.cs`, `Services/ApiResult.cs`, `Services/CandidateApiClient.cs`, `Services/EvaluationApiClient.cs`, `Services/ServiceCollectionExtensions.cs` (tất cả trong `src/SmartCv.Web/`) |
| Mới, Web (13) | `Components/Pages/Login.razor/.css`, `Components/Layout/AuthLayout.razor/.css`, `AuthGate.razor/.css`, `UserMenu.razor/.css`, `Services/AuthSession.cs`, `AuthApiClient.cs`, `ProtectedSessionTokenStore.cs`, `Helpers/AuthText.cs`, `LoginForm.cs` |
| Mới, test (5) | `tests/SmartCv.Tests/B27AuthSessionTests.cs`, `B27BearerTests.cs`, `B27EndToEndTests.cs`, `B27LoginUiTests.cs`, `B27AuthTestDoubles.cs` |
| Mới, tài liệu | `docs/B27-implementation-report.md` |

Không sửa API, Core, Infrastructure, `.gitignore`, `Program.cs`, database hay cấu hình. Không thêm package nào.

---

## C. Giao diện và trải nghiệm

**Bố cục** (`AuthLayout`, không menu nội bộ):
- **≥ 1024px:** hai cột. Bên trái là khối giới thiệu, rộng khoảng 44% nhưng tối đa 760px, nền `--sc-grad-sidebar` (giống menu trái). Khối này có logo trên ô sáng như sidebar, dòng nhãn, tiêu đề "Tìm đúng ứng viên nhanh hơn, quyết định vẫn thuộc về bạn.", đoạn mô tả, 3 ý chính về chức năng thật (đọc CV, xếp hạng theo kỹ năng, HR quyết định cuối), và một hình trừu tượng "so khớp hồ sơ" dựng bằng CSS. Hình này **không có chữ, không có số liệu giả**, và tự ẩn khi màn hình thấp hơn 780px. Bên phải là thẻ form.
- **< 1024px:** khối giới thiệu thu thành một dải logo cao khoảng 60px, form nằm ngay dưới. **< 576px:** bỏ dòng nhãn, lề 16px.

**Thẻ đăng nhập:**
- Biểu tượng khóa, tiêu đề "Đăng nhập", câu "Dùng tài khoản SCApp do quản trị viên cấp để tiếp tục."
- Ô Email và ô Mật khẩu. Ô mật khẩu có nút Hiện / Ẩn.
- Nút "Đăng nhập".
- Khung hỗ trợ: "Quên mật khẩu hoặc chưa có tài khoản? Liên hệ quản trị viên (Admin)…". **Không có** link "Quên mật khẩu" hay "Tạo tài khoản" trỏ tới trang không tồn tại.
- Dòng bảo mật: "Phiên đăng nhập được mã hóa và tự kết thúc khi bạn đóng tab trình duyệt."

**Kích thước đo trên Chrome thật:**

| Phần tử | Giá trị |
|---|---|
| Thẻ form | 420px (desktop/tablet); 328 / 343 / 358px ở màn 360 / 375 / 390 |
| Ô nhập | cao 44px |
| Nút | cao 46px |
| Tiêu đề | 28px (26px trên điện thoại) |
| Chữ trong ô nhập | 15px (16px trên điện thoại để iOS không tự phóng to) |
| Nhãn | 13px |
| Bo góc | thẻ 16px, ô nhập 10px |
| Khoảng cách | theo nhịp 8 / 12 / 16 / 24px |

Chỉ dùng token sẵn có (`--sc-*`, Inter). Không tạo bảng màu mới, không thêm animation trang trí.

**Các trạng thái của form:** email trống, email sai định dạng (dùng cùng luật `CandidateRules.NormalizeEmail` với API), mật khẩu trống hoặc quá 128 ký tự, sai thông tin (một câu chung), tài khoản bị khóa, không được phép đăng nhập (403 khác), đang đăng nhập (vòng quay và chữ "Đang đăng nhập…", ô nhập chỉ đọc), quá nhiều lần (nút khóa và đếm ngược theo `Retry-After`, mặc định 60 giây), dịch vụ tạm lỗi (503), lỗi máy chủ (5xx), mất kết nối, quá thời gian, phiên hết hạn (`?expired=1`), đã đăng xuất (`?signedOut=1`), tài khoản phải đổi mật khẩu.

**Khả năng tiếp cận:**
- `label for` gắn đúng ô. Ô email dùng `autocomplete="username"`, ô mật khẩu dùng `autocomplete="current-password"`.
- Nhấn Enter là gửi form (dùng `<form>` + `type=submit`).
- Lỗi theo ô có `aria-invalid` và `aria-describedby`. Lỗi chung nằm trong `role="alert"`; đếm ngược 429 nằm trên nút, không nằm trong vùng alert, để trình đọc màn hình không đọc lại mỗi giây.
- Con trỏ: vào trang thì ở ô Email. Sai định dạng thì về đúng ô lỗi. Sai mật khẩu thì về ô Mật khẩu.
- Thứ tự Tab: Email → Mật khẩu → Hiện/Ẩn → Đăng nhập. Viền focus dùng `--sc-focus`.
- Có `prefers-reduced-motion`.

**Sau khi đăng nhập:** thanh trên cùng có `UserMenu` gồm chữ viết tắt, họ tên, vai trò ("Quản trị viên" / "Nhân sự (HR)") và nút **Đăng xuất**. Dưới 768px ẩn tên; dưới 420px nút chỉ còn biểu tượng nhưng vẫn có `aria-label`. Các trang khác giữ nguyên.

Ảnh chụp (23 ảnh, không commit) nằm ở `artifacts/b27-screenshots/`: `login-360…1920`, `login-validation`, `login-wrong-password`, `login-expired`, `login-signed-out`, `login-rate-limited`, `must-change-password`, `after-login-cvs`, `page-*`, `shell-*`.

---

## D. Xác thực

- **Đăng nhập:** `POST /api/auth/login` qua `AuthApiClient`, gửi JSON UTF-8 và **không** có Bearer. Nhận `LoginResponse { token, expiresAt, user }` đúng contract A28.
  - `user` dùng `MeDto` của Core: `userId`, `email`, `fullName`, `role`, `mustChangePassword`, `lastLoginAt`.
  - Mật khẩu bị xóa khỏi form sau khi API trả lời (trừ lỗi dịch vụ hoặc mạng, để người dùng thử lại).
- **`AuthSession`** (`Services/AuthSession.cs`):
  - Đăng ký **Scoped**, nên mỗi circuit (tab) có một bản riêng. Không static, không singleton.
  - Giữ `Token` (internal, không hiện, không log), `ExpiresAt`, `User`, `IsVerified`, `EndReason`. Có hai sự kiện `Changed` và `Expired`.
- **Lưu:** `ProtectedSessionTokenStore` ghi `ProtectedSessionStorage`, khóa `smartcv.auth`, mã hóa bằng Data Protection.
  - Đã kiểm tra trên Chrome: giá trị lưu không chứa `eyJ` (dấu hiệu JWT) hay email.
  - Mỗi tab một phiên; đóng tab là hết phiên.
  - Lỗi giải mã hoặc JSON thì xóa và coi như chưa đăng nhập. Chưa có JS hoặc mất kết nối thì bỏ qua, không làm sập.
- **Khôi phục (F5):** `EnsureRestoredAsync` chạy **một lần** cho cả tab: đọc storage → token đã quá hạn thì bỏ → gọi `GET /api/auth/me` bằng token đó.
  - 200: nhận phiên, cập nhật vai trò và tên mới nhất.
  - 401: xóa phiên, hiện "phiên hết hạn".
  - Mất kết nối, 503: giữ phiên "chưa xác minh". API vẫn kiểm tra token ở mỗi request.
- **Tắt prerender** (`App.razor`): trang chỉ chạy sau khi circuit có JS và phiên đã rõ. Nhờ vậy không có lượt render ẩn danh nào gọi API.
- **Điều hướng:**
  - Chưa đăng nhập → `/login?returnUrl=<trang đang mở>` (thay mục lịch sử).
  - Đăng nhập xong → quay lại `returnUrl`, không cần tải lại.
  - `AuthText.SafeReturnUrl` chỉ nhận đường dẫn trong ứng dụng. `https://…`, `//…`, `\…`, `javascript:…`, ký tự điều khiển và `/login` đều về `/`, nên không có vòng lặp Login ↔ Dashboard.
- **Đăng xuất:** xóa bộ nhớ và storage rồi tải lại `/login?signedOut=1`. Bấm Back chỉ về được trang đăng nhập (đã thử trên Chrome).
- **MustChangePassword:** xem mục A, dòng 5. Đây là **phụ thuộc A30/B29** (API và trang đổi mật khẩu).

---

## E. HTTP 401

**Nguyên nhân gốc (đã xác minh trong code và log):**
1. SmartCv.Web **không có đăng nhập và không gắn `Authorization` vào bất kỳ request nào.** `ApiClientBase` gọi `Http.GetAsync/PostAsJsonAsync…` không có header. Trong khi đó A29 bắt mọi route phải có JWT, trừ `GET /api/health` và `POST /api/auth/login`. Điều này khớp đúng log: health 200, còn Dashboard, Jobs, Runs, CV, Candidates đều 401.
2. Prerender bật. Kể cả khi có token trong sessionStorage, lượt prerender (không có JS) vẫn chạy trang và gọi API ẩn danh.
3. Không có chốt đăng nhập, nên mỗi trang mở ra tự gọi vài API. Đó là lý do log có hàng loạt 401.

Không có endpoint nào bị A29 bảo vệ nhầm. Không có lỗi backend nào cần sửa.

**Trả lời 10 câu hỏi của mục 4:**

| # | Câu hỏi | Trả lời |
|---|---|---|
| 1 | Request có Bearer? | Trước: không. Sau: có, mọi client nghiệp vụ |
| 2 | Login lấy đúng JWT? | Trước chưa có login. Sau: lấy `token` từ A28 |
| 3 | Lưu và khôi phục JWT? | Có, xem mục D |
| 4 | Tự gắn cho mọi request protected? | Có, ở `ApiClientBase` |
| 5 | Request chạy trước khi khôi phục xong? | Không: client chờ `EnsureRestoredAsync`, và `AuthGate` không tạo trang trước đó |
| 6 | Lỗi do prerender? | Có (nguyên nhân 2), đã tắt |
| 7 | Gọi API lặp khi chưa đăng nhập? | Có (nguyên nhân 3), nay không gửi |
| 8 | Lỗi scope handler / circuit? | Tránh bằng thiết kế gắn token từng request |
| 9 | JWT hết hạn có xử lý đúng? | Có: phát hiện ngay ở phía Web, hoặc khi API trả 401, kết thúc phiên đúng một lần |
| 10 | Endpoint bị A29 bảo vệ nhầm? | Không |

**Cách sửa:** gắn token ở `ApiClientBase.SendAuthorizedAsync`. Client nào được gắn phiên đều dùng hàm này: `JobApiClient`, `DashboardApiClient`, `ApplicationApiClient`, `RunApiClient`, `EvaluationApiClient` (kể cả `CheckCvAsync` tự dựng request), `PromptApiClient`, `AiPreviewApiClient`, `CvApiClient` (kể cả upload multipart, vẫn stream từng file), `CandidateApiClient` (kể cả `WriteAsync` tự dựng request). Không gắn: `HealthApiClient`, `AuthApiClient`.

**Tránh vòng lặp và bão 401:**
- Chưa đăng nhập hoặc token quá hạn thì không gửi.
- 401 thì kết thúc phiên đúng một lần (kể cả nhiều 401 song song) và đưa về `/login?expired=1`. Không có retry ở đâu.
- 403 giữ nguyên là 403 và không xóa phiên.

**Đã thấy trả 200 sau khi đăng nhập** (Chrome + API thật trên Kestrel, InMemory, xem mục G; log ở `artifacts/b27-api-requests.tsv`): `GET /api/jobs?activeOnly=false`, `/api/dashboard/trend`, `/api/runs?page=1&pageSize=20`, `/api/cvs?page=1&pageSize=20`, `/api/cvs?search=java…`, `/api/cvs?candidate=none…`, `/api/candidates?page=1&pageSize=20`, `/api/auth/me`. Test end-to-end (TestServer, JwtBearer thật) thêm `/api/jobs?activeOnly=true`, `/api/runs/latest`, `/api/runs?type=Screening&take=50`: không còn 401/403.

Trong 43 request của lượt chạy Chrome cuối:
- **Không có request nghiệp vụ nào thiếu Bearer.**
- 401 chỉ có ở 5 lần cố ý nhập sai mật khẩu và 1 request ngay sau khi cố ý thu hồi phiên.

**Chưa kiểm tra được:**
- `GET /api/dashboard/summary` trả 200: request **đã qua xác thực và vào tới handler**, nhưng handler dùng SQL thô (`SqlQuery`) nên không chạy được trên InMemory của harness. Cần thử với SQL Server.
- Toàn bộ luồng trên `SmartCvDb` với tài khoản thật.

**Phát hiện ngoài phạm vi, không sửa:** trong `appsettings.json` của API, `CvUpload:Endpoint:Enabled = false`; chỉ `appsettings.Development.json` bật. Ở môi trường khác Development, các nhóm Dashboard, Runs, CV, Candidates **không được map** và trả 404. Cần lưu ý khi triển khai (A45). Đây không phải lỗi của B27.

---

## F. Bảo mật

- **Phân lập circuit:** `AuthSession` là Scoped, client được tạo trong scope của component, token đặt trên từng request. Test `Two_circuits_never_mix_tokens_even_under_parallel_load` chạy 40 request song song của hai phiên qua cùng HttpClientFactory, không lẫn token. Có thêm test `ValidateScopes` bảo đảm `AuthSession` không bao giờ được lấy từ root.
- **Không rò rỉ:**
  - Không log email, mật khẩu, token. Test bật log mức Trace để kiểm tra. `RedactLoggedHeaders` được đặt tường minh.
  - Không đưa token lên URL, `DefaultRequestHeaders` hay giao diện.
  - Storage chỉ chứa chuỗi đã mã hóa. `AuthSnapshot` không có trường mật khẩu.
- **Không bypass A29:** không sửa API, không cho route nào ẩn danh, không tắt JWT, không đụng database hay tài khoản Admin.
- **Ranh giới với nhánh khác:**
  - **B28** (AuthenticationStateProvider, AuthorizeRouteView, menu theo vai trò, trang 403): chưa làm. B28 nên bọc `AuthSession` thay vì tạo cơ chế thứ hai; `AuthGate` khi đó chỉ còn lo 401 giữa chừng.
  - **B29** (trang đổi mật khẩu, `/users`): chưa làm.
  - **B43:** các **link mở thẳng trên trình duyệt** không mang Bearer, nên sau A29 vẫn 401: "Xem file" ở Kho CV và trang ứng viên, "Xuất dữ liệu (JSON)", "Xem CV gốc" ở chi tiết hồ sơ, Bộ dữ liệu đánh giá và trang Tải CV, "Mở file gốc" trong hộp xem chữ. B27 không làm các link này hoạt động bằng cách mở công khai dữ liệu cá nhân. Đây là phụ thuộc B43 (tải qua API có token).

---

## G. Kiểm thử

| Hạng mục | Kết quả |
|---|---|
| `dotnet restore SmartCv.slnx` | PASS |
| `dotnet build SmartCv.slnx -c Release --no-restore` | PASS: 0 lỗi, **0 cảnh báo** |
| Test B27 (lọc `SmartCv.Tests.B27`) | **PASS 82 / 82** |
| Regression đầy đủ (`--no-build --no-restore -- xUnit.MaxParallelThreads=4`) | {{FULL}} |
| Kiểm thử trình duyệt (Chrome thật, Playwright .NET) | **PASS 55 / 55**, 0 lỗi console |
| Live test với `SmartCvDb` và tài khoản thật | **NOT VERIFIED** |

**Test B27 (82):**
- `B27AuthSessionTests` (luật thuần):
  - Địa chỉ quay lại an toàn (14 trường hợp), `LoginUrl`, chữ viết tắt, tên vai trò.
  - Validate form, không gửi trùng, mỗi mã lỗi một câu, 429 với và không có `Retry-After`, 503 / 5xx / mạng / quá thời gian không bị báo là sai mật khẩu, 400 theo ô, thành công mà thiếu token.
  - Khôi phục phiên: hợp lệ, bị thu hồi, quá hạn, API tạm lỗi, storage lỗi, chỉ chạy một lần. Đăng nhập / đăng xuất. Snapshot không có mật khẩu.
- `B27BearerTests` (DI thật của Web + máy chủ giả):
  - 18 lời gọi của 9 client nghiệp vụ, đủ GET / POST / PUT / PATCH / DELETE, đều có Bearer. Upload multipart vẫn stream và có Bearer.
  - Login và Health không có Bearer. Hai circuit không lẫn token. Tab chưa đăng nhập không gửi gì. Sau đăng xuất không gửi token cũ.
  - 401 kết thúc phiên một lần, không gửi lại, kể cả 401 song song. 401 muộn của token cũ không đá phiên mới.
  - 403 giữ nguyên và không đăng xuất. `password_change_required` được đánh dấu.
  - F5 khôi phục qua `/me`. Scope đúng. Log mức Trace không có mật khẩu, token hay email.
- `B27EndToEndTests` (API A28/A29 thật trong TestServer, InMemory, dữ liệu tổng hợp):
  - Admin và HR đăng nhập xong: 10 API của các trang từng bị 401 đều không còn 401/403.
  - Tài khoản bị thu hồi giữa chừng. F5 xác minh bằng `/me` thật. Sai mật khẩu và email không tồn tại cho cùng một câu.
  - Tài khoản khóa. 429 thật với `Retry-After`. Tài khoản phải đổi mật khẩu. HR gọi API của Admin bị 403. API không chạy.
- `B27LoginUiTests` (HtmlRenderer):
  - Nhãn, autocomplete, nút Hiện/Ẩn, Enter gửi form, không có link ảo.
  - `AuthLayout` không có menu. Thông báo `expired` / `signedOut`. Đã đăng nhập mà mở `/login` thì quay về an toàn.
  - `AuthGate` chuyển hướng và không vẽ nội dung. Khung ứng dụng khi chưa đăng nhập không gọi API nào. Khung khi đã đăng nhập có tên, vai trò, Đăng xuất. Màn hình "cần đổi mật khẩu". Prerender đã tắt.

Phần API của mục 10.1 đến 10.8 (login hợp lệ, sai, email sai, khóa, 429, 503, `/me` hợp lệ, hết hạn, thu hồi) đã có sẵn trong `A28AuthEndpointTests` / `A29AuthorizationTests`. B27 kiểm thêm chính các trường hợp đó từ phía Web.

**Kiểm thử trình duyệt:**
- Harness ở scratchpad, không commit: `SmartCv.Api` thật (A28 JWT + A29) trên Kestrel, môi trường Staging (không nạp User Secrets thật), InMemory, 3 tài khoản tổng hợp với mật khẩu ngẫu nhiên của lần chạy. Web B27 thật ở cổng riêng.
- Kết quả: 55 kiểm tra đạt. Chi tiết trong `artifacts/b27-browser-checks.log`; log request phía API (chỉ đường dẫn, mã trạng thái và có/không Bearer, không có giá trị token) trong `artifacts/b27-api-requests.tsv`.
- Các kiểm tra gồm:
  - Mọi trang chưa đăng nhập về `/login?returnUrl=…`.
  - 7 độ rộng 360 / 375 / 390 / 768 / 1024 / 1366 / 1920 không cuộn ngang.
  - Vị trí con trỏ, thứ tự Tab, validate, Hiện/Ẩn mật khẩu.
  - Sai mật khẩu; Enter đăng nhập và quay về `/cvs?search=java`.
  - sessionStorage chỉ chứa chuỗi mã hóa.
  - Dashboard, Jobs, Runs, Kho CV, Ứng viên không có lỗi xác thực.
  - F5 vẫn đăng nhập; Đăng xuất; Back sau đăng xuất.
  - Thu hồi phiên thì về `/login?…&expired=1`.
  - Tài khoản phải đổi mật khẩu; 429 khóa nút và đếm ngược.
  - Khung ứng dụng không cuộn ngang ở 360 / 390 / 768 / 1366.

**Lỗi đã sửa trong lúc làm:** đọc `code` của phản hồi 403 bằng stream đã đệm làm lần đọc sau (lấy thông báo lỗi) gặp stream ở cuối, nên `ErrorCode` bị mất. Đã sửa bằng cách đọc chuỗi từ bộ đệm. Có test giữ lại trường hợp này.

---

## H. Bàn giao

**Chạy để tự kiểm tra:**
1. API (bản có A29, ví dụ thư mục gốc hoặc chính worktree B27): `dotnet run --project src/SmartCv.Api --launch-profile https`, cổng 7269.
2. Web B27: trong `C:\TTCN\project\AI-CV-Screening-B27`, chạy `dotnet run --project src/SmartCv.Web --launch-profile https`, cổng 7258. `ApiBaseUrl` trong `appsettings.Development.json` đã là `https://localhost:7269/`.
3. Mở `https://localhost:7258/cvs?search=java` → trang đăng nhập → tự nhập tài khoản Admin. Đi qua Tổng quan → Vị trí → Lịch sử lượt chấm → Ứng viên (cả chế độ Hồ sơ CV) → F5 → Đăng xuất → Back.
4. Mở F12 → Application → Session Storage: khóa `smartcv.auth` là chuỗi mã hóa. Terminal API không còn dòng 401 cho các trang trên.

**Điều kiện tích hợp:**
- A29 đã ở `main` và là nền của nhánh này.
- Phải gộp B43 trước khi HR dùng thật, để các link xem / xuất file hoạt động (mục F).
- B28 bọc `AuthSession`. B29 thay màn hình "cần đổi mật khẩu" bằng trang đổi mật khẩu khi A30 có.

**Rủi ro còn lại:**
- Chưa chạy live với `SmartCvDb`. `dashboard/summary` chỉ mới thấy qua xác thực, chưa thấy 200 trên SQL Server.
- Khóa Data Protection của Web đổi (cài lại máy chủ) thì người dùng phải đăng nhập lại. Đây là hành vi mong muốn.
- Tắt prerender nên trang trống trong khoảng thời gian circuit kết nối (thường dưới 1 giây ở máy cục bộ).
- Ở môi trường khác Development, cờ `CvUpload:Endpoint:Enabled` của API phải bật (mục E).

**Kết luận: CONDITIONAL.** Đủ điều kiện để người dùng review và tự chạy live test ở bước 3. Nếu live test đạt thì có thể commit theo `artifacts/b27-commit-manifest.tsv` (base: `artifacts/b27-base-sha.txt`). Tôi **chưa** `git add`, commit, push hay tạo PR.
