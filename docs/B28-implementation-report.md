# B28 — Trạng thái đăng nhập, phân quyền giao diện và điều hướng theo vai trò

Ngày thực hiện: 20/11/2026 · Kết luận: **CONDITIONAL** (xem mục I)

---

## A. Tài liệu

**Đã đọc trong `C:\TTCN\A27_B43_SmartCv.zip`** (giải nén vào thư mục tạm, không chép đè vào repo):

- `HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md`: hướng dẫn chung, thứ tự làm / gộp, bảng "Những chỗ mình phải tự quyết", phần A29 (bảng API chỉ Admin), A30, B27, **B28**, B29, B43 và "Lỗi thường gặp".
- Mã mẫu `B28/`: `ApiAuthenticationStateProvider.cs`, `WebAuthSetup.cs`, `Routes.razor`, `AuthGate.razor`, `NavMenu.razor`, `Forbidden.razor`, `RedirectToLogin.razor`, `ForbiddenPage.razor`, `Pages/_Imports.razor`, `JobEdit.razor`, test `ApiAuthenticationStateProviderTests.cs`, `snippets/Program.Web.B28.cs.txt`.
- Đối chiếu ranh giới: `B29/` (trang `/users`, `/change-password`), `B43/` (ẩn nút Admin trong trang, mở file qua API có token), `B27/` (bản mẫu khác với B27 đã gộp vào `main`).

**Hình ảnh:** ZIP **không có** file PNG/JPG/JPEG/WEBP/SVG/PDF/mockup nào (đã kiểm kê toàn bộ 130 file). Không có ảnh để tham khảo; thiết kế bám trực tiếp giao diện B27 và hệ thống "Deep Teal + AI Glow" đang có trong `app.css`.

**Khác với mã mẫu / hướng dẫn (có chủ đích):**

| Chỗ | Mẫu ZIP | B28 thực tế | Lý do |
|---|---|---|---|
| Xác minh vai trò | Provider tự gọi `/api/auth/me` thêm một lần | Dùng kết quả `/api/auth/me` mà `AuthSession` (B27) đã gọi khi khôi phục; phiên mới đăng nhập dùng phản hồi đăng nhập | Không gọi trùng, không tạo luồng xác thực thứ hai |
| API mất kết nối khi F5 | "tạm dùng thông tin đã lưu" (trao quyền theo dữ liệu trong trình duyệt) | Trạng thái **Unverified**: không có claim vai trò, không vẽ trang, màn hình "Thử lại" | Yêu cầu fail-closed: không trao quyền khi chưa xác minh |
| Trang Admin | `[Authorize(Roles = "Admin")]` | `[Authorize(Policy = AccessRules.AdminPolicy)]` = chính sách nghiệp vụ + vai trò Admin | `Roles` một mình bỏ qua chính sách mặc định (không chặn tài khoản phải đổi mật khẩu) |
| Chuyển hướng | `AuthGate` và `RedirectToLogin` cùng tự điều hướng | Một chỗ duy nhất: `AuthNavigator` | Tránh hai lần chuyển trang / nhấp nháy |
| 403 | Chữ "403" đỏ cỡ 56px | Thẻ teal nhẹ, mã 403 nhỏ, nút "Về Tổng quan" + "Mở <trang cha>" | Theo yêu cầu thiết kế: không lạm dụng đỏ |
| Ẩn nút Admin trong trang | Thuộc B43 | Làm ở B28 cho các thao tác có chính sách riêng ở A29 (Job, Prompt, Bộ CV) | Prompt nhiệm vụ yêu cầu mục VII. Phần còn lại của B43 (bỏ ô "Người thao tác", mở file qua API) **không** làm |
| Trang 404 | Chữ mẫu tiếng Anh | Thẻ tiếng Việt cùng phong cách 403, công khai | Đường dẫn lạ không được chuyển về /login |

---

## B. Git

| Mục | Giá trị |
|---|---|
| Worktree | `C:\TTCN\project\AI-CV-Screening-B28` |
| Nhánh đã tạo | `feature/B28-auth-state-ui` (từ `origin/main`, đã bỏ upstream tự gán để `git push` không thể trỏ nhầm `main`) |
| SHA nền | `2110250bf3d5bee643f34fd74d450ecf277b0c4d` (`origin/main`, đã gồm A29 #85, B27 #86, A30 #87) |
| Không đụng tới | Repo gốc `AI-CV-Screening` (vẫn ở `main`), `AI-CV-Screening-B27`, `AI-CV-Screening-A30` |
| add / commit / push / merge / rebase / stash / reset | **Không thực hiện** |

> ⚠ **CẦN XỬ LÝ TRƯỚC KHI COMMIT — nhánh của worktree đã bị đổi.** Lúc 09:41:13 (20/11) có một lệnh checkout **không phải do B28 thực hiện** chuyển worktree B28 từ `feature/B28-auth-state-ui` sang nhánh mới `feature/B29-account-management-ui` (theo dõi `origin/main`). Cả hai nhánh cùng ở commit `2110250`, nên toàn bộ thay đổi B28 (chưa commit) vẫn nguyên vẹn và **không có file B29 nào** trong worktree. Theo quyết định của người dùng, B28 **không tự sửa git**. Trước khi commit: kiểm tra không còn phiên nào đang làm B29 trong thư mục này, rồi `git switch feature/B28-auth-state-ui` (an toàn: cùng commit, thay đổi đi theo) và kiểm tra `git branch --show-current`.

**File thay đổi (53):** 34 sửa (`M`), 19 mới (`A`, gồm báo cáo này) chỉ trong `src/SmartCv.Web/`, `tests/SmartCv.Tests/` và báo cáo này. Danh sách chính xác: `C:\TTCN\project\SCApp-B28-Handoff\commit-manifest.tsv`. Không xóa, không đổi tên file.

**Bảo toàn:** không sửa `src/SmartCv.Api`, `src/SmartCv.Core`, `src/SmartCv.Infrastructure`, `database`, migration. B27: `AuthSession` chỉ **thêm** `HadSessionInCircuit` và `VerifyAsync()`; cơ chế lưu token (`ProtectedSessionTokenStore`), gắn Bearer (`ApiClientBase`), xử lý 401/403 giữ nguyên. A29/A30: không đổi chính sách nào; giao diện chỉ phản ánh.

---

## C. Kiến trúc

```
AuthSession (B27, Scoped/circuit) ── Changed ──► ApiAuthenticationStateProvider (B28, Scoped)
   │ token, /api/auth/me, 401, 403 mcp                 │ ClaimsPrincipal (không có token)
   │                                                    ▼
   │                                    AddCascadingAuthenticationState
   │                                                    ▼
   │                Routes.razor: AuthorizeRouteView ── [Authorize] mặc định (Pages/_Imports.razor)
   │                     ├─ Authorizing  → vòng quay, KHÔNG có menu
   │                     ├─ Authorized   → trang (chỉ lúc này trang mới được tạo và gọi API)
   │                     └─ NotAuthorized → NotAuthorizedContent
   │                            ├─ chưa đăng nhập   → RedirectToLogin → AuthNavigator
   │                            ├─ chưa xác minh    → "Thử lại" (AuthSession.VerifyAsync)
   │                            ├─ phải đổi mật khẩu→ hướng dẫn + Đăng xuất (B29 thay bằng trang đổi mật khẩu)
   │                            └─ thiếu quyền / vai trò lạ → AccessDenied (403)
   └─ Expired ──► AuthGate (MainLayout) ──► AuthNavigator.ToLogin()  (tải lại /login?expired=1)
```

- **`ApiAuthenticationStateProvider`**: lớp đọc thuần trên `AuthSession`; lần đầu chờ `EnsureRestoredAsync()` (Task chưa xong → `Authorizing`), sau đó dựng principal từ trạng thái hiện tại; nghe `AuthSession.Changed` → `NotifyAuthenticationStateChanged`; gỡ đăng ký khi Dispose. Không lưu, không đọc token, không log.
- **Claims** (cùng tên với API, `SmartCvClaims`): `sub`, `name`, `email`, `smartcv_session` (`verified`/`unverified`), và **chỉ khi đã xác minh và vai trò hợp lệ**: `role`, `smartcv_mcp`. `ClaimsIdentity(authenticationType: "SmartCv", nameType: "name", roleType: "role")` → `IsInRole` / `RequireRole` hoạt động.
- **Chính sách** (`WebAuthSetup`): mặc định = đã xác minh + Admin/HR + không phải đổi mật khẩu (giống A29); `SmartCv.Admin` = mặc định + Admin; `SmartCv.SignedIn` (cho trang đổi mật khẩu của B29). Bước HTTP cho qua (`PassThroughAuthorizationResultHandler`) vì token nằm trong circuit; prerender đã tắt nên HTTP chỉ trả khung trống. `Program.cs`: thêm `app.UseSmartCvWebAuth()` trước `UseAntiforgery()`.
- **`AccessRules`** (Helpers): một nơi duy nhất cho bảng quyền giao diện + `AccessLevel` (Anonymous / Unverified / PasswordChangeRequired / NoValidRole / Hr / Admin). Fail-closed khi trạng thái chưa có.
- **`AuthNavigator`**: chỗ duy nhất đưa tab về `/login` (một lần): lần đầu mở trang → chuyển trong circuit; phiên kết thúc giữa chừng (401, đăng xuất) → `forceLoad` để bỏ circuit cũ và mọi trạng thái Scoped; `replace` nên Back không xem lại được.
- **`AuthGate`**: chỉ còn (1) chọn khung có menu khi tài khoản dùng được ứng dụng, khung trống khi không; (2) bắt `Expired`. Không còn quyết định trang nào được mở.

**Vòng đời:** mở trang → `Authorizing` (vòng quay) → khôi phục + `/api/auth/me` → trang hoặc `NotAuthorized` · đăng nhập → `SignInAsync` → Changed → trang · 401 giữa chừng → `Expired` + Changed → một lần `forceLoad` `/login?expired=1&returnUrl=…` · đăng xuất → xóa phiên → `forceLoad` `/login?signedOut=1` · 403 `password_change_required` → Changed → màn hình đổi mật khẩu · 403 `access_denied` → **giữ phiên**, trang hiện lỗi.

---

## D. Quyền (đối chiếu `src/SmartCv.Api` — A29/A30)

### Trang

| Trang | API chính | Admin | HR | Ghi chú B28 |
|---|---|---|---|---|
| `/` Tổng quan | `GET /api/dashboard/*`, `GET /api/jobs` | đọc | đọc | HR: thay link "Tạo vị trí" bằng câu nhờ Admin |
| `/upload` | `POST /api/cvs` … | đọc/ghi | đọc/ghi | không đổi |
| `/candidates`, `/candidates/{id}`, `/cvs` | `/api/candidates`, `/api/cvs` | đọc/ghi | đọc/ghi | `POST /api/cvs/{id}/anonymize` chỉ Admin nhưng web **không có** nút này |
| `/results`, `/evaluations/{id}` | đổi giai đoạn, cờ, quyết định | đọc/ghi | đọc/ghi | không đổi |
| `/prompts` | `GET /api/prompts`, `POST /api/prompt-tests`, `POST …/ai-preview` | đọc/ghi | đọc + chạy thử / Prompt Test | HR chỉ xem prompt (xem dưới) |
| `/evaluation-sets` | `GET/PUT …/labels`, thêm/bỏ hồ sơ | đọc/ghi | đọc/ghi | Tạo bộ: chỉ Admin |
| `/jobs` | `GET /api/jobs` | đọc/ghi | **đọc** | HR không có Tạo / Sửa / Ngừng tuyển / Mở lại |
| `/jobs/new`, `/jobs/{id}` | `POST/PUT /api/jobs`, `/api/jobs/{id}/skills` | ✓ | **403** | `[Authorize(Policy = SmartCv.Admin)]` |
| `/runs`, `/runs/{id}` | `GET /api/runs` | đọc | đọc | không đổi |
| `/forbidden` | — | 403 | 403 | chưa đăng nhập → /login |
| `/login`, `/not-found`, `/Error` | — | công khai | công khai | `[AllowAnonymous]` |

Tài khoản **chưa xác minh / phải đổi mật khẩu / vai trò lạ**: không mở được trang nghiệp vụ nào (giống A29: `password_change_required` chặn mọi API nghiệp vụ). `GET /api/health/*` (controller) chỉ Admin nhưng web không dùng (huy hiệu API dùng `GET /api/health` công khai).

### Thao tác bị ẩn với HR (API A29 = `AuthPolicies.Admin`)

| Quyền (`AppPermission`) | API | Giao diện |
|---|---|---|
| `ManageJobs` | `POST /api/jobs`, `PUT /api/jobs/{id}`, `POST …/deactivate`, `…/activate`, nhóm `/skills` | Nút "+ Tạo vị trí", "Sửa vị trí", "Ngừng tuyển / Mở lại", tên vị trí không còn là link tới form; Tổng quan không có link tạo |
| `ManagePromptVersions` | `POST /api/prompts`, `POST /api/prompts/{id}/activate` | Ô prompt `readonly`, không có tên/ghi chú phiên bản, "Hoàn tác", "Lưu thành phiên bản mới", "Kích hoạt" |
| `PreviewAssembledPrompt` | `POST /api/prompts/assembled-preview` | Thẻ "Nội dung gửi Gemini" trong "Thử với hồ sơ…" |
| `CreateEvaluationSet` | `POST /api/evaluation-sets` | Nút "+ Tạo bộ CV" và form tạo bộ; HR thấy hướng dẫn nhờ Admin |

HR **vẫn** được: "Thử với hồ sơ…" (chấm thử `ai-preview`), chạy Prompt Test, xem độ chính xác, thêm/bỏ hồ sơ trong bộ, gán nhãn — đúng A29. Ngoài việc ẩn, mỗi handler còn kiểm tra quyền lần nữa (không gửi API nếu không đủ quyền). **API A29 vẫn là nơi quyết định cuối cùng**; test E2E xác nhận HR gọi API Admin nhận `403 access_denied` và **không** bị đăng xuất.

Quyền đổi giữa chừng: A30 đổi vai trò / khóa ⇒ đổi `SecurityStamp` ⇒ request kế tiếp 401 ⇒ phiên kết thúc, menu và nút Admin biến mất cùng circuit cũ; F5 lấy vai trò mới từ `/api/auth/me`.

---

## E. Thiết kế

- **Sidebar**: giữ nguyên Figma "SC v2" (Deep Teal, nhãn nhóm 11px in hoa, mục 44px, vạch mint ở mục đang mở). Mục menu lấy từ `AppNavigation` theo vai trò; mục `AdminOnly` ẩn với HR; nhóm rỗng bị ẩn. Theo A29, cả 8 trang trong menu đều dùng được với HR nên **menu HR = menu Admin**; khác biệt nằm ở thao tác trong trang. Không thêm mục "Tài khoản" (chưa có giao diện B29).
- **Không nhấp nháy**: menu chỉ vẽ khi trạng thái đã xác định và tài khoản dùng được ứng dụng; trong lúc kiểm tra chỉ có vòng quay giữa màn hình.
- **Di động (<768px)**: nút menu 44×44 có biểu tượng ☰/✕, `aria-expanded`, `aria-controls`; mở ra có khối tài khoản (tên, vai trò màu mint, nút Đăng xuất cao 44px); chọn mục → menu tự đóng. Thanh trên cùng vẫn có nút Đăng xuất (biểu tượng).
- **Trang 403**: thẻ tối đa 520px giữa vùng nội dung, biểu tượng khiên-khóa trên nền teal nhạt, "MÃ 403 · KHÔNG ĐỦ QUYỀN" 12px, tiêu đề 22–26px, nội dung 15px, chip "Đang đăng nhập · tên · vai trò", nút 44px "Về Tổng quan" + "Mở <trang cha>" (đường dẫn trong ứng dụng, không dùng lịch sử trình duyệt). Không có màu đỏ, không lộ chính sách/claim/API. Màn hình hẹp: nút xếp dọc full-width.
- **Màn hình chưa xác minh / phải đổi mật khẩu**: thẻ 460px kế thừa B27 (xanh thông tin / vàng cảnh báo).
- **HR chỉ xem**: ghi chú nền xám nhạt (Jobs) hoặc khung xanh thông tin có biểu tượng khóa (Prompt) — không dùng màu cảnh báo.
- **Accessibility**: `h1 tabindex=-1` cho `FocusOnNavigate`, `role="status"` cho trạng thái chờ, `aria-busy` cho nút Thử lại, nhãn rõ cho ô prompt chỉ đọc, vùng bấm ≥ 44px.

---

## F. Tích hợp

- **B27**: dùng nguyên `AuthSession`, `ProtectedSessionTokenStore`, `ApiClientBase`, `Login.razor`, `AuthText.SafeReturnUrl`. 5 test UI của B27 viết lại theo kiến trúc mới (cùng tiêu chí: chưa đăng nhập → /login đúng một lần, không menu, không API; phiên bị thu hồi → `expired=1`; phải đổi mật khẩu → hướng dẫn).
- **A28/A29**: tên claim và chính sách mặc định khớp `SmartCvClaims` / `AuthSetup`; `AuthorizationTests` của A29 không đổi và đều qua.
- **A30**: không tạo/sửa tài khoản. B28 chỉ phản ánh `MustChangePassword` và hiệu lực tức thì của đổi vai trò/khóa.
- **B29** (chưa làm): thêm `/users` với `[Authorize(Policy = AccessRules.AdminPolicy)]` + mục `AdminOnly: true` trong `AppNavigation`; trang đổi mật khẩu dùng `AccessRules.SignedInPolicy`; thay nhánh `PasswordChangeRequired` trong `NotAuthorizedContent` bằng chuyển tới `/change-password`.
- **B43** (chưa làm): bỏ ô "Người thao tác / Người gán nhãn / Người xử lý"; nút xem/xuất file qua API có token. **Không** mở file CV ẩn danh. Có thể dùng `AccessRules.Can(...)` cho các nút khác.
- Test UI tuần trước được đăng ký trạng thái Admin (`AddB28Auth()`) vì component nay fail-closed khi không có trạng thái đăng nhập.

---

## G. Bảo mật

- Không có phiên thứ hai: provider chỉ đọc `AuthSession`; mỗi circuit một provider (test 2 circuit không lẫn trạng thái).
- Không lộ JWT: không có trong claim (có test), không log; log mới chỉ ghi loại lỗi.
- Fail-closed: trạng thái chưa có / chưa xác minh / vai trò lạ / principal không do provider dựng → không quyền. Trang mới thêm vào `Pages/` tự được bảo vệ; test phản chiếu chặn trang thiếu `[Authorize]`.
- Không có open redirect: `returnUrl` qua `AuthText.SafeReturnUrl` (test `//evil.example` và `https://evil.example`).
- Không `AllowAnonymous` để lách lỗi: chỉ Login, NotFound, Error.
- Bước HTTP cho qua là có chủ đích và an toàn vì prerender tắt và mọi dữ liệu qua API kiểm tra token.
- Không sửa API/Core/Infrastructure/database/migration/User Secrets/khóa JWT; không tạo/reset/đổi tài khoản Admin; không chạm `SmartCvDb` (guard A29 của test vẫn bật, không đặt biến môi trường SQL).

---

## H. Kiểm thử

| Lệnh / nhóm | Kết quả |
|---|---|
| `dotnet restore SmartCv.slnx` | OK |
| `dotnet build SmartCv.slnx -c Release --no-restore` | OK, **0 cảnh báo** |
| `dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4` | **3233 PASS · 0 FAIL · 135 SKIP** (3368) |
| Trong đó B28 (`B28*Tests`) | **82 PASS** |
| B27 (login/session/bearer/E2E), A28, A29 `AuthorizationTests` | PASS (trong tổng trên) |

135 SKIP: test SQL Server opt-in (A11–A43, Dashboard, JobCrud…; không đặt biến môi trường để không chạm database thật) và 3 test OCR theo môi trường — không liên quan B28.

**Test B28 mới** (5 file): `B28AccessRulesTests` (trạng thái, bảng quyền, ma trận **mọi trang × 6 trạng thái** bằng phản chiếu + chính sách thật, menu không đường dẫn chết / không nhóm rỗng, wiring `Routes`/`Program`), `B28AuthStateProviderTests` (anonymous, vai trò từ `/me`, không token trong claim, Authorizing, API mất kết nối, thu hồi, đăng nhập/xuất, 401 giữa chừng chỉ một lần, `password_change_required`, vai trò lạ, hết hạn theo đồng hồ, Thử lại, Dispose, 2 circuit, `AuthNavigator` một lần chuyển + forceLoad + returnUrl an toàn), `B28RoleUiTests` (vẽ thật `Routes.razor`: HR `/jobs/new` → 403 và **không có request nào**, Admin mở form, chưa đăng nhập → /login không phải 403, chưa xác minh, đổi mật khẩu, vai trò lạ, 404, nút Job / Prompt / Bộ CV theo vai trò, menu, AuthGate hủy không phản ứng 401), `B28EndToEndTests` (API A28/A29/A30 thật trong TestServer: đăng nhập + F5 Admin/HR, HR gọi API Admin → 403 giữ phiên, đổi vai trò → phiên cũ bị thu hồi).

**Kiểm thử trình duyệt** (Chrome thật, Playwright .NET, headless; API thật A28/A29 trên Kestrel với database InMemory và 3 tài khoản tổng hợp, môi trường Staging — không User Secrets, không SQL, không Gemini): **138/139 PASS** ở 360, 375, 390, 768, 1024, 1366, 1920px — menu Admin/HR, active state, menu di động (44px, aria-expanded, tự đóng, khối tài khoản), HR `/jobs` không có nút Admin, HR `/jobs/new` và `/jobs/3` → 403 giữ nguyên địa chỉ, nút "Mở Vị trí tuyển dụng", Prompt chỉ đọc + không có thẻ "Nội dung gửi Gemini", không có "Tạo bộ CV", F5 giữ phiên, màn hình đang khôi phục không có menu, API mất kết nối → "Chưa xác minh" → Thử lại vào thẳng trang, đăng xuất + Back, thu hồi giữa chừng → `/login?expired=1&returnUrl=…`, mật khẩu tạm, chưa đăng nhập `/forbidden` → /login, returnUrl ngoài → về trong ứng dụng, không cuộn ngang. Nhật ký API: HR **không** có request ghi nào, **không** có `GET /api/jobs/3`, **không** có 403, không gọi `assembled-preview`.
1 mục "FAIL" là Chrome ghi "Failed to load resource: 404" cho **chính tài liệu** của bài thử đường dẫn lạ (`/khong-co-trang-nay` trả HTTP 404 có chủ đích) — không phải tài nguyên hỏng.
Ảnh trước/sau (B27 cùng nền ↔ B28) lưu ngoài repo: `C:\TTCN\project\SCApp-B28-Handoff\screenshots\` (HR mở `/jobs/new`: trước — vào được form tạo vị trí; sau — trang 403).

**NOT VERIFIED:** chạy với API thật trên SQL Server / `SmartCvDb` và tài khoản thật (không được phép dùng); `dotnet watch`/IIS; trình đọc màn hình thật (chỉ kiểm tra thuộc tính ARIA); Safari/Firefox. **Không** tuyên bố LIVE TEST PASS.

---

## I. Bàn giao

**Lỗi đã sửa**
1. HR gõ thẳng `/jobs/new`, `/jobs/{id}` vẫn mở form tạo/sửa vị trí (chỉ bị API chặn khi lưu) → nay 403, form không được tạo.
2. HR thấy và bấm được Tạo/Sửa/Ngừng tuyển, Lưu/Kích hoạt prompt, Tạo bộ CV, thẻ "Nội dung gửi Gemini" → chắc chắn 403 từ API → nay ẩn theo bảng quyền.
3. F5 khi API mất kết nối: B27 vẽ ứng dụng theo dữ liệu đã lưu (chưa xác minh) → nay fail-closed với nút Thử lại.
4. Không có trang 403 / trang 404 tiếng Việt.

**Rủi ro còn lại**
- **Nhánh worktree đang là `feature/B29-account-management-ui`** (mục B) — phải chuyển về `feature/B28-auth-state-ui` trước khi commit.
- Nếu vai trò bị đổi trong database **không** đổi `SecurityStamp` (sửa tay bằng SQL), tab đang mở giữ vai trò cũ tới lần F5 tiếp theo; API vẫn chặn đúng. A30 luôn đổi stamp nên không xảy ra qua giao diện quản trị.
- B43 vẫn cần cho nút xem/xuất file (link trực tiếp tới API bị 401 sau A29) và bỏ ô "Người thao tác".
- B29 sẽ sửa `NotAuthorizedContent` (nhánh đổi mật khẩu) và `AppNavigation` (mục Tài khoản) — xung đột nhỏ, đã ghi chú trong code.

**Chạy Web:** chạy API như thường (cổng của nhóm), rồi `dotnet run --project src/SmartCv.Web` (ApiBaseUrl trong appsettings). Không cần cấu hình mới.

**Điều kiện nghiệm thu (thử tay với tài khoản thật do nhóm cấp)**
1. Admin: menu 8 mục, `/jobs` có Tạo/Sửa/Ngừng tuyển, `/jobs/new` mở form, `/prompts` có Lưu/Kích hoạt.
2. HR: menu 8 mục, `/jobs` không có nút Admin, gõ `/jobs/new` → trang 403 có tên + "Nhân sự (HR)", `/prompts` chỉ đọc, `/evaluation-sets` không có "Tạo bộ CV"; F12 → Network không có 403.
3. Chưa đăng nhập `/forbidden`, `/jobs/new` → `/login?returnUrl=…`; `/abc` → "Không tìm thấy trang".
4. F5 giữ phiên; Đăng xuất → `/login?signedOut=1`, Back vẫn ở /login; Admin đổi vai trò HR (A30) → tab HR về `/login?expired=1`.

**Kết luận: CONDITIONAL** — code, 82 test B28 và toàn bộ regression (0 FAIL) cùng kiểm thử trình duyệt trên môi trường cô lập đã hoàn tất. Chưa đủ điều kiện commit/push cho tới khi (1) worktree được chuyển về nhánh `feature/B28-auth-state-ui` và (2) nhóm thử tay với API + SQL Server + tài khoản thật theo 4 bước trên.
