# A29 — Phân quyền toàn bộ API và Audit Trail

> Báo cáo tiếp tục sau khi phiên làm việc trước bị ngắt do giới hạn sử dụng của Claude (không phải lỗi mã nguồn).
> Công việc được **tiếp tục từ trạng thái dở**, không làm lại từ đầu. Không commit, push, merge hay tạo PR.

## A. Tổng quan

**Mục tiêu A29** (theo `HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md`, mục A29):

1. Một chỗ khai báo mọi route: `SmartCvApi.MapSmartCvApi()`, nhóm nghiệp vụ dưới `RequireAuthorization()`, có `FallbackPolicy`.
2. Chỉ `POST /api/auth/login` và `GET /api/health` mở anonymous.
3. Các thao tác quản trị gắn `AuthPolicies.Admin` (bảng `AdminRoutes`).
4. Người thao tác (`Actor`, `UserId`) lấy từ token/tài khoản hiện tại, bỏ qua giá trị client gửi.
5. AI giả chỉ ở Development; API chạy được ở Production.
6. Test 401/403 trên mọi route.

**Đã hoàn thành**

| Hạng mục | Trạng thái |
|---|---|
| `MapSmartCvApi` + `RequireAuthorization()` + `DefaultPolicy`/`FallbackPolicy` | Xong |
| Bỏ chốt `IsDevelopment()` quanh các API nghiệp vụ (giữ feature flag `CvUpload:Endpoint:Enabled`) | Xong |
| 16 route chỉ Admin (14 theo ZIP + 2 route chẩn đoán `/api/health/database`, `/api/health/gemini`) | Xong |
| Actor/UserId từ tài khoản: đổi giai đoạn (một/nhiều), cờ thủ công, nhãn HR (JSON/CSV), thành viên bộ đánh giá, ẩn danh hóa, xuất/xóa dữ liệu ứng viên | Xong |
| `ApplicationEventDto.UserId` (NULL cho lịch sử cũ) | Xong |
| Phiên đăng nhập kiểm tra lại DB mỗi request: khóa, xóa, đổi SecurityStamp, đổi vai trò, bật MustChangePassword → 401 ngay | Xong |
| MustChangePassword chỉ được gọi `/api/auth/me`, mọi API nghiệp vụ 403 `password_change_required` | Xong (sớm hơn A30, chặt hơn ZIP A29) |
| Ma trận 82 endpoint từ runtime, test tự động | Xong |
| **Phiên này:** sửa khóa đọc A39 ngoài Development (`CvPrivacyReadMiddleware`) | Xong |
| **Phiên này:** fail-safe toàn tiến trình test chặn catalog `SmartCvDb` | Xong |
| Báo cáo, `artifacts/a29-base-sha.txt`, `artifacts/a29-commit-manifest.tsv` | Xong |

**Chưa hoàn thành / ngoài phạm vi**

- SQL integration A29 trên catalog cô lập: **NOT RUN** (chưa có cấu hình opt-in và phê duyệt).
- Kiểm tra tay trên API thật (đăng nhập Admin, đổi giai đoạn, xem `UserId` trong DB, chạy Production): **NOT RUN** — cần người dùng thực hiện theo mục "Thử" của hướng dẫn.
- Web gửi Bearer token (B27), menu theo vai trò (B28), tải file/xuất dữ liệu qua API kèm token (B43): ngoài phạm vi A29.
- API quản lý tài khoản, `change-password` (A30): ngoài phạm vi; test xác nhận chưa có route `/api/users` hay `change-password`.

## B. Git

| Mục | Giá trị |
|---|---|
| Nhánh | `feature/a29-api-authorization` (đã xác nhận, không đổi nhánh) |
| SHA nền | `e2e8690f196fe3b4a474a59add6317a056e4763a` — `feat(A28): implement JWT authentication and login (#84)`; trùng `origin/main` |
| Staged | Không có |
| Thao tác Git dở (merge/rebase/cherry-pick) | Không có |
| `git diff --check` | Sạch (chỉ cảnh báo LF→CRLF của Git, không phải lỗi khoảng trắng) |
| Conflict marker | Không có |
| Migration / schema | Không thay đổi |
| Commit / push / merge / PR | **Không thực hiện** |

Danh sách file chính xác nằm trong `artifacts/a29-commit-manifest.tsv` (được tóm tắt ở mục I). Thư mục `artifacts/` bị `.gitignore`, chứa bằng chứng database và script cleanup — **không stage**.

## C. Authorization

### C.1. Chính sách

| Policy | Định nghĩa (`AuthSetup.AddSmartCvAuth`) | Dùng cho |
|---|---|---|
| `DefaultPolicy` = `FallbackPolicy` | Đã xác thực + vai trò `Admin` hoặc `HR` + **không** có `smartcv_mcp=true` | Mọi route trong `MapGroup("").RequireAuthorization()`, `MapControllers().RequireAuthorization()` và mọi route lỡ khai báo ngoài nhóm |
| `Admin` | `DefaultPolicy` + vai trò `Admin` | 16 route quản trị |
| `SignedIn` | Đã xác thực + vai trò `Admin`/`HR` (cho phép mật khẩu tạm) | Chỉ `GET /api/auth/me` |
| `AllowAnonymous` | — | `POST /api/auth/login`, `GET /api/health` (và `/openapi` chỉ ở Development) |

### C.2. JWT và vòng đời phiên

- JwtBearer thật kiểm tra chữ ký HMAC-SHA256, hạn, Issuer, Audience (A28).
- `OnTokenValidated` → `UserSessionValidator.ValidateAsync(requireFresh: true)`: đọc lại `Users` theo `UserId` **mỗi request** (không dùng cache) và từ chối nếu tài khoản bị khóa/xóa, `SecurityStamp` khác, `Role` khác claim, `MustChangePassword` khác claim. Do đó khóa hoặc hạ quyền có hiệu lực **ngay ở request kế tiếp**, kể cả khi bên ghi quên đổi stamp.
- Claim `name`/`email` được thay bằng giá trị hiện tại trong DB → `ActorName()` là ảnh chụp tên tài khoản đáng tin, không phải trường client.
- Lỗi DB khi kiểm tra phiên → 503 an toàn (không lộ lỗi SQL); `/api/health` vẫn 200.
- Đánh đổi: thêm một truy vấn theo khóa chính `Users` mỗi request đã xác thực.

### C.3. 401 / 403

| Tình huống | Kết quả | Mã `code` |
|---|---|---|
| Không token, token rác, sai chữ ký, hết hạn, sai Issuer/Audience, không ký, thiếu `sub`/`role`, `role` trùng lặp/không hợp lệ, `sub` không hợp lệ | 401 + `WWW-Authenticate: Bearer` | `authentication_required` |
| Tài khoản khóa/xóa, stamp/vai trò/MustChangePassword đổi sau khi cấp token | 401 | `authentication_required` |
| HR gọi route Admin | 403 | `access_denied` |
| Tài khoản `MustChangePassword=true` gọi API nghiệp vụ (cả Admin lẫn HR) | 403 | `password_change_required` |
| Đăng nhập quá giới hạn | 429 | `login_rate_limited` (A28) |

Mọi phản hồi `/api/*` có `Cache-Control: no-store`.

### C.4. Ma trận endpoint (82 route `/api/*`, sinh từ runtime)

Số route được đếm trực tiếp từ `EndpointDataSource` của host test (Development, `CvUpload:Endpoint:Enabled=true`) và khóa cứng bằng `Assert.Equal(82, …)`. Bảng dưới được sinh từ output của test `Endpoint_matrix_covers_every_Minimal_API_and_Controller_and_blocks_before_binding_or_side_effects` (file `artifacts/a29/endpoint-matrix-runtime.tsv`), không chép tay.

- 2 public, 80 protected: 63 HR/Admin, 16 chỉ Admin, 1 `/api/auth/me`.
- Với **mọi** route protected, test gọi HTTP thật không token → 401 + `no-store`; đánh giá policy cho Admin (được), HR (được trừ 16 route Admin), Admin có mật khẩu tạm (bị chặn trừ `/me`); với route Admin còn gọi HTTP bằng token HR → 403 `access_denied`; cuối cùng kiểm tra không có bản ghi nghiệp vụ nào bị tạo (chặn trước binding/side effect).
- So với ZIP (hơn 60 route): repo có thêm các route của A36, A40–A43, SCApp v2 (cờ thủ công, thành viên bộ, gợi ý nhãn, data-requests…) nên là 82. Khi `CvUpload:Endpoint:Enabled=false` (mặc định `appsettings.json`), chỉ còn auth, health và đọc job.
- Cột "Test riêng" liệt kê test hành vi bổ sung ngoài ma trận; "—" nghĩa là route chỉ được kiểm bởi ma trận (401/policy) và các test nghiệp vụ hiện có.

| # | Method | Route | Truy cập | Policy | Vai trò được phép | Chưa đăng nhập | HR | Admin | MustChangePassword | Audit | Test riêng (ngoài ma trận) | Trạng thái |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | DELETE | `/api/applications/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 2 | DELETE | `/api/evaluation-sets/{id:int}/labels/{applicationId:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 3 | DELETE | `/api/evaluation-sets/{id:int}/members/{applicationId:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 4 | DELETE | `/api/jobs/{id:int}/skills/{code}` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 5 | GET | `/api/applications` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 6 | GET | `/api/applications/ai-preview/quota` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 7 | GET | `/api/applications/flag-reasons` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 8 | GET | `/api/applications/stage-options` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 9 | GET | `/api/applications/{id:int}/events` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | Single_and_bulk… (UserId NULL cho lịch sử cũ) | PASS |
| 10 | GET | `/api/applications/{id:int}/flags` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 11 | GET | `/api/applications/{id:int}/keyword-score` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 12 | GET | `/api/auth/me` | Protected | SignedIn | Admin, HR (kể cả mật khẩu tạm) | 401 | Được | Được | Được (chỉ /me) | — | Must_change_password_allows_me…, A28AuthEndpointTests | PASS |
| 13 | GET | `/api/candidates` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 14 | GET | `/api/candidates/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 15 | GET | `/api/candidates/{id:int}/data-export` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | CandidateDataRequests.HandledBy từ token | A29AuditTests.Candidate_export…, A43EndpointTests | PASS |
| 16 | GET | `/api/cvs` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | A29SafetyTests.Privacy_read_lease… (khóa đọc A39) | PASS |
| 17 | GET | `/api/cvs/{id:guid}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 18 | GET | `/api/cvs/{id:guid}/candidate-suggestion` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 19 | GET | `/api/cvs/{id:guid}/file` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 20 | GET | `/api/cvs/{id:guid}/text` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 21 | GET | `/api/dashboard/summary` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | DashboardEndpointTests.A29_requires_authentication… (Prod/Staging) | PASS |
| 22 | GET | `/api/dashboard/trend` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 23 | GET | `/api/data-requests` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 24 | GET | `/api/evaluation-sets` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 25 | GET | `/api/evaluation-sets/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 26 | GET | `/api/evaluation-sets/{id:int}/applications` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 27 | GET | `/api/evaluation-sets/{id:int}/label-suggestions` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 28 | GET | `/api/evaluation-sets/{id:int}/labels/template` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 29 | GET | `/api/evaluations/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 30 | GET | `/api/health` | Public | AllowAnonymous | Mọi người | 200 (không phải 401) | 200/4xx nghiệp vụ | 200/4xx nghiệp vụ | Không ảnh hưởng | — | Real_JwtBearer… (health vẫn 200), Database_session_failure… | PASS |
| 31 | GET | `/api/health/database` | Protected | Admin+Default | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 32 | GET | `/api/health/gemini` | Protected | Admin+Default | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 33 | GET | `/api/jobs` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | Real_JwtBearer_rejects_invalid_tokens (12), Account_changes_revoke…, Must_change_password… | PASS |
| 34 | GET | `/api/jobs/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 35 | GET | `/api/prompt-tests` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 36 | GET | `/api/prompt-tests/compare` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 37 | GET | `/api/prompt-tests/{runId:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 38 | GET | `/api/prompts` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 39 | GET | `/api/prompts/active` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 40 | GET | `/api/prompts/compare` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 41 | GET | `/api/prompts/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 42 | GET | `/api/retention/settings` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 43 | GET | `/api/runs` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 44 | GET | `/api/runs/estimate` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 45 | GET | `/api/runs/latest` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 46 | GET | `/api/runs/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 47 | GET | `/api/runs/{id:int}/compare` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 48 | GET | `/api/runs/{id:int}/failures` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 49 | GET | `/api/runs/{id:int}/ranking` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 50 | PATCH | `/api/applications/{id:int}/stage` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | ApplicationEvents: Actor + UserId từ token | Single_and_bulk…, Failed_validation…, Legacy_Actor…, Two_accounts…, Kestrel_HTTP_login… | PASS |
| 51 | POST | `/api/applications` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 52 | POST | `/api/applications/flags/{flagId:int}/reopen` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | ApplicationFlagEvents từ token | A29AuditTests.Manual_flag… | PASS |
| 53 | POST | `/api/applications/flags/{flagId:int}/resolve` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | ApplicationFlagEvents/ResolvedBy từ token | A29AuditTests.Manual_flag… | PASS |
| 54 | POST | `/api/applications/stage` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | ApplicationEvents: Actor + UserId từ token | Single_and_bulk…, Failed_validation…, Legacy_Actor… | PASS |
| 55 | POST | `/api/applications/{id:int}/ai-preview` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 56 | POST | `/api/applications/{id:int}/flags` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | ApplicationFlagEvents/CreatedBy từ token | A29AuditTests.Manual_flag… | PASS |
| 57 | POST | `/api/auth/login` | Public | AllowAnonymous | Mọi người | 400 (không phải 401) | 200/4xx nghiệp vụ | 200/4xx nghiệp vụ | Không ảnh hưởng | — | Kestrel_HTTP_login…, A28AuthEndpointTests | PASS |
| 58 | POST | `/api/candidates` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 59 | POST | `/api/candidates/{id:int}/erase` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | CandidateDataRequests.HandledBy + ApplicationEvents (rút hồ sơ) Actor + UserId | A43EndpointTests (Actor từ token) | PASS |
| 60 | POST | `/api/candidates/{id:int}/merge` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | Log có UserId (không có bảng audit) | — | PASS |
| 61 | POST | `/api/cvs/upload` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | CvUploadEndpointTests.Production_with_endpoint_enabled… | PASS |
| 62 | POST | `/api/cvs/{id:guid}/anonymize` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | Người ẩn danh hóa = tên tài khoản | — | PASS |
| 63 | POST | `/api/cvs/{id:guid}/extract` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 64 | POST | `/api/evaluation-sets` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 65 | POST | `/api/evaluation-sets/{id:int}/labels/import` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | HrLabels.LabeledBy từ token | A29AuditTests.Labels_CSV_and_members… | PASS |
| 66 | POST | `/api/evaluation-sets/{id:int}/members` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | EvaluationSetMembers.AddedBy từ token | A29AuditTests.Labels_CSV_and_members… | PASS |
| 67 | POST | `/api/jobs` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | JobCrudEndpointTests.A29_protects_Job_writes… (Prod/Staging), Kestrel_HTTP_login… (HR→403) | PASS |
| 68 | POST | `/api/jobs/{id:int}/activate` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 69 | POST | `/api/jobs/{id:int}/deactivate` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 70 | POST | `/api/jobs/{id:int}/skills` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 71 | POST | `/api/prompt-tests` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 72 | POST | `/api/prompts` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 73 | POST | `/api/prompts/assembled-preview` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 74 | POST | `/api/prompts/{id:int}/activate` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 75 | POST | `/api/retention/run` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | Log có UserId | — | PASS |
| 76 | POST | `/api/runs` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 77 | PUT | `/api/candidates/{id:int}` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 78 | PUT | `/api/cvs/{id:guid}/candidate` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | — | — | PASS |
| 79 | PUT | `/api/evaluation-sets/{id:int}/labels` | Protected | Default | Admin, HR | 401 | Được | Được | 403 password_change_required | HrLabels.LabeledBy từ token | A29AuditTests.Labels_CSV_and_members… | PASS |
| 80 | PUT | `/api/jobs/{id:int}` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 81 | PUT | `/api/jobs/{id:int}/skills/{code}` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |
| 82 | PUT | `/api/jobs/{id:int}/skills/{code}/aliases` | Protected | Default+Admin | Admin | 401 | 403 access_denied | Được | 403 password_change_required | — | — | PASS |

Quyết định ngoài bảng ZIP (cần nhóm review):

- `GET /api/health/database`, `GET /api/health/gemini` (Controller chẩn đoán, chỉ trả dữ liệu ở Development) → **chỉ Admin**, vì lộ tên server/catalog và trạng thái khóa Gemini. `GET /api/health` vẫn public.
- `MustChangePassword` bị chặn ở API nghiệp vụ ngay từ A29 (ZIP đặt việc này ở A30). Admin thật hiện có `MustChangePassword = 0` (đã kiểm bằng SELECT, mục E) nên không bị khóa.
- `GET /api/jobs`, `GET /api/jobs/{id}` trước A29 là public; nay cần HR/Admin đúng theo ZIP ("chỉ login và health mở").
- `POST /api/prompt-tests` (tốn hạn mức Gemini) giữ cho HR theo ZIP.

## D. Audit

| Thao tác | Ghi ở đâu | Nguồn người thao tác |
|---|---|---|
| Đổi giai đoạn một hồ sơ / nhiều hồ sơ | `ApplicationEvents.Actor` + **`ApplicationEvents.UserId`** | Tên tài khoản hiện tại + `sub` |
| Xóa dữ liệu ứng viên (rút hồ sơ) | `CandidateDataRequests.HandledBy`; sự kiện rút hồ sơ có `Actor` + `UserId` | Token |
| Xuất dữ liệu ứng viên | `CandidateDataRequests.HandledBy` | Token (bỏ `?handledBy=`) |
| Cờ thủ công mở/đã xử lý/mở lại | `ApplicationFlags` + `ApplicationFlagEvents.Actor` | Token |
| Nhãn HR (JSON, CSV) | `HrLabels.LabeledBy` | Token (bỏ `labeledBy`) |
| Thêm thành viên bộ đánh giá | `EvaluationSetMembers.AddedBy` | Token (bỏ `addedBy`) |
| Ẩn danh hóa thủ công | Người thực hiện ẩn danh hóa | Token (bỏ `actor`) |
| Gộp ứng viên, quét hạn lưu | Log ứng dụng có `UserId` | Token |

- Các trường `actor`, `labeledBy`, `handledBy`, `addedBy` vẫn còn trong DTO để Web cũ biên dịch được; API **không dùng** để xác định người thực hiện. Test gửi `actor = "FORGED"`, `userId = 999`, `role = "Admin"` và xác nhận bị bỏ qua.
- Lịch sử cũ giữ `UserId = NULL`, nội dung không đổi (test InMemory; test SQL thật là opt-in, NOT RUN).
- Validation lỗi, thao tác không đổi gì (no-op), xung đột phiên bản → **không** thêm dòng lịch sử.
- Hai tài khoản thao tác song song → mỗi sự kiện mang đúng `UserId` của mình.
- Trigger A35 `TR_ApplicationEvents_NoChange` không bị sửa; đang bật trên SmartCvDb (đã kiểm bằng SELECT).
- `ApplicationEventDto` trả `UserId`, không trả `SecurityStamp` hay hash.

## E. Sự cố 219 tài khoản thử

### E.1. Diễn biến (từ bằng chứng trong `artifacts/a29`)

| Thời điểm (giờ VN) | Sự kiện | Bằng chứng |
|---|---|---|
| 20:10 | Kiểm tra read-only SmartCvDb (test A28 opt-in, chỉ SELECT): 1 Admin hoạt động | `database-readiness.json` |
| 20:17 | Build lần 2 (đã thêm fixture đăng nhập cho regression) | `build-second.log` |
| 20:18:27 – 20:20:48 | **219 tài khoản `Synthetic regression Admin`, email `<guid>@example.invalid`, UserId 2–220 được ghi vào SmartCvDb** | `database-incident-readonly.txt` |
| 20:18 – 20:20 | Regression lần 1: 206 dòng `[FAIL]`, gần như toàn bộ "Expected …, Actual: Unauthorized" | `regression-first.log` |
| 20:22 | Kiểm tra read-only: 219 dòng, 0 sự kiện tham chiếu | `database-incident-readonly.txt` |
| — | Người dùng phê duyệt xóa đúng 219 bản ghi thử | Phiên trước |
| 20:38 – 20:39 | Lần chạy script đầu dừng `51002 Synthetic cohort differs; no changes` (không ghi gì); lần sau xóa 219, còn 1 | `database-incident-remediation.txt`, `database-incident-cleanup.txt` |
| 20:40 | Hậu kiểm: còn 1 user, giữ user gốc, 1 Admin, 0 user thử, 0 sự kiện tham chiếu, trigger bật | `database-incident-final-verification.txt` |

Script `remove-confirmed-synthetic-users.sql` (đã đọc, **không chạy lại**): kiểm tra `DB_NAME()`, khóa bảng, chỉ chọn dòng khớp đồng thời ID 2–220, tên, vai trò, độ dài/mẫu email, cửa sổ thời gian; đúng 219 dòng; không bị tham chiếu; không có khóa ngoại lạ; so sánh toàn bộ các dòng còn lại trước/sau; không reseed identity. Script không chứa dữ liệu cá nhân thật (chỉ mẫu `@example.invalid`).

### E.2. Hậu kiểm lại trong phiên này (SELECT-only, đã thực hiện thật)

Xác minh trước khi đọc: `@@SERVERNAME = DESKTOP-I9O3636\BB`, `DB_NAME() = SmartCvDb`. Kết quả:

| Kiểm tra | Kết quả |
|---|---|
| Tổng `Users` | 1 (min = max = UserId 1) |
| Admin đang hoạt động | 1 (UserId 1, `IsActive=1`, `MustChangePassword=0`) |
| Dòng giống dữ liệu thử (`Synthetic%`, `Tài khoản tổng hợp`, `@example.invalid`) | 0 |
| `IDENT_CURRENT('dbo.Users')` | 220 — identity **không** bị reset |
| `ApplicationEvents` có `UserId` 2–220 / có `UserId` bất kỳ | 0 / 0 |
| Trigger `TR_ApplicationEvents_NoChange` | `is_disabled = 0` |

Sau full regression của phiên này, truy vấn trên được chạy lại và cho kết quả giống hệt (mục F.3).

### E.3. Nguyên nhân

**Đã xác nhận:**

- Dữ liệu do fixture xác thực regression tạo (tên `Synthetic regression Admin` là hằng `StartupGatedApiFactory.RegressionActor`), trong đúng cửa sổ regression lần 1.
- Token của fixture bị API từ chối (401 hàng loạt) ⇒ tài khoản **không** nằm trong database InMemory của host mà nằm trong SmartCvDb: fixture phiên bản đó đã tự dựng một `SmartCvDbContext` SQL Server riêng thay vì dùng DbContext của host.
- Thiếu cơ chế bảo vệ: không có kiểm tra tên catalog trước khi ghi, không có chặn ở tầng kết nối; User Secrets của máy dev chứa `Server=localhost\BB;Database=SmartCvDb`.
- Kiểm tra thực nghiệm trong phiên này: `IConfiguration` của host test (Development và Production) **không** trỏ SmartCvDb (`UseSetting` của `ApiFactory` thắng User Secrets) và DbContext là InMemory.

**Chưa xác minh được (giả thuyết):** transcript của phiên trước không còn trên máy nên không xem được mã chính xác của fixture lúc 20:17. Vì cấu hình host không lộ SmartCvDb, giả thuyết hợp lý nhất là fixture đã đọc chuỗi kết nối trực tiếp từ appsettings + User Secrets (giống mẫu của test kiểm tra read-only A28 chạy lúc 20:10), ngoài cấu hình đã được thay của host.

### E.4. Biện pháp phòng ngừa (đang có trong mã)

1. **Fixture đã sửa (phiên trước):** `RegressionIdentity` chỉ dùng chính store của host (InMemory cùng `DatabaseRoot`), và `ValidateTarget` từ chối mọi SQL Server không phải catalog test dạng `SmartCv_<Tên>_Test_<32 hex>` hoặc có `AttachDBFilename` — **trước khi mở kết nối**. `ClientWithSyntheticHrAsync` gọi cùng kiểm tra.
2. **Fail-safe toàn tiến trình test (phiên này):** `tests/SmartCv.Tests/ApplicationDatabaseGuard.cs` đăng ký `[ModuleInitializer]` nghe `SqlClientDiagnosticListener` và **ném lỗi đồng bộ** khi:
   - mở kết nối có `Initial Catalog = SmartCvDb` (không phân biệt hoa thường) — trước khi ra mạng;
   - kết nối đã mở rơi vào SmartCvDb (catalog trống → DB mặc định của login);
   - chạy lệnh trên kết nối đang ở SmartCvDb (ví dụ script có `USE SmartCvDb`).
   Áp dụng cho **mọi** test, fixture, script, bất kể chuỗi kết nối đến từ đâu. Ngoại lệ duy nhất: scope `AllowReadOnlyApplicationInspection()` của test kiểm tra read-only A28 (vốn đã yêu cầu `SMARTCV_A28_INSPECT_APP_DB=1` và chỉ có SELECT).
3. Test chứng minh: `A29SafetyTests` (mở sync/async tới `SmartCvDb`/`smartcvdb`/` SMARTCVDB ` bị chặn với thông báo của guard thay vì `SqlException`; catalog test, `master`, `SmartCvDb_ModelOnly` không bị chặn; scope ngoại lệ kết thúc đúng; host test Development/Production không trỏ SmartCvDb), `A29AuthorizationTests.Regression_fixture_refuses_SQL_before_opening_a_connection_or_writing`, `Regression_fixture_identity_is_created_only_in_the_current_InMemory_host`.
4. Mọi test SQL ghi dữ liệu vẫn chỉ chạy khi có biến opt-in riêng và tự tạo catalog GUID mới.

### E.5. Rủi ro còn lại

- Guard chặn theo **tên** `SmartCvDb`. Nếu nhóm tạo một database ứng dụng với tên khác (ví dụ bản sao `SmartCvDb_Prod`) trên cùng máy, guard không nhận ra; fixture regression vẫn chặn vì yêu cầu mẫu catalog test.
- Script/công cụ chạy **ngoài** tiến trình test (sqlcmd, SSMS, `dotnet ef`) không được guard bảo vệ.
- Identity `Users` hiện ở 220: tài khoản tạo tiếp theo sẽ có `UserId = 221` (cố ý không reseed để không tái sử dụng mã lịch sử).

## F. Tests

### F.1. Build

`dotnet restore SmartCv.slnx` → thành công. `dotnet build SmartCv.slnx -c Release --no-restore` → **0 lỗi, 0 cảnh báo** (`artifacts/a29/continue-build.log`).

### F.2. Focused A29 (phiên này)

`--filter FullyQualifiedName~SmartCv.Tests.Auth.A29` → **Passed 55, Failed 0, Skipped 1** (Skipped = `A29SqlServerTests`, opt-in). Log: `artifacts/a29/continue-focused-a29.log`.

### F.3. Full regression (phiên này)

`dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4`
→ **Passed 3000, Failed 0, Skipped 130, Total 3130** (4 phút 30 giây). Log: `artifacts/a29/continue-regression.log`, TRX: `artifacts/a29/continue-regression/`.

| Mốc | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Baseline trước A29 (phiên trước, `baseline-test-unrestricted.log`) | 2952 | 0 | 129 | 3081 |
| Regression lần 1 phiên trước (fixture lỗi, gây sự cố E) | — | 206 dòng FAIL | — | — |
| Regression lần 2 phiên trước | 2944 | 39 | 129 | 3112 |
| **Regression phiên này** | **3000** | **0** | **130** | **3130** |

- 130 Skipped = các test SQL Server/OCR opt-in (thêm 1 so với baseline: `A29SqlServerTests`). Không skip thêm test nào để làm xanh.
- Không sửa assertion nào theo hướng nới quyền; các test cũ được cập nhật chỉ ở chỗ hành vi đổi có chủ đích theo ZIP (mục F.5).
- **Hậu kiểm SmartCvDb sau regression (SELECT-only, đã chạy thật):** kết quả giống hệt mục E.2 — 1 user, `IDENT_CURRENT = 220` (không có user mới nào được tạo, kể cả tạm thời), 0 dòng giống dữ liệu thử, trigger bật. Bằng chứng: `artifacts/a29/continue-db-readonly-after-regression.txt`.

### F.4. SQL integration

| Test | Trạng thái |
|---|---|
| `A29SqlServerTests.Real_SQL_preserves_legacy_history_attributes_HTTP_events_and_keeps_A35_trigger_and_conflicts` | **NOT RUN** — cần `SMARTCV_A29_TEST_SQL_CONNECTION` trỏ catalog `SmartCv_A29_Test_*` và phê duyệt; test tự tạo catalog GUID mới và giữ lại để review |
| Các test SQL opt-in khác (A11, A14–A17, A19–A25, A27, A28, A35, A38–A43, Runtime) | NOT RUN (Skipped, không có biến opt-in) |

### F.5. Lỗi đã sửa

| Lỗi | Phiên | Sửa |
|---|---|---|
| 3 lỗi biên dịch `IEndpointRouteBuilder.Logger` sau khi đổi `Map*` sang `IEndpointRouteBuilder` | Trước | Dùng `ILoggerFactory` từ `ServiceProvider` |
| Fixture regression ghi tài khoản vào SmartCvDb, 204 test 401 | Trước | Fixture dùng store của host + `ValidateTarget` |
| `Accepts<LoginRequest>` sinh endpoint 415 không có `AllowAnonymous` → login sai content-type trả 401 thay vì 415 | Trước | Bỏ metadata `Accepts`, login tự kiểm tra content-type |
| 39 test cũ kỳ vọng hành vi trước A29 (chốt khởi động "M6", actor từ body, job public) | Trước | Cập nhật theo hành vi mới; không nới quyền |
| **`CvPrivacyReadMiddleware` bỏ qua khóa đọc A39 ngoài Development**, trong khi A29 đã mở API CV/ứng viên ở mọi môi trường → ở Production, đọc CV có thể chạy song song với ẩn danh hóa/xóa | **Phiên này** | Bỏ điều kiện môi trường; test `A29SafetyTests.Privacy_read_lease_is_attempted_in_every_environment_and_fails_closed` |
| Comment `CvUploadEndpointOptions.Enabled` còn mô tả chốt khởi động M6 đã bỏ | Phiên này | Cập nhật comment |

## G. Ảnh hưởng đến frontend (401 trên SCApp)

| Lời gọi người dùng thấy | Route | Sau A29 | Đánh giá |
|---|---|---|---|
| `GET api/dashboard/summary`, `GET api/dashboard/trend` | HR/Admin | 401 khi không token | **Đúng thiết kế** |
| `GET api/jobs?activeOnly=false/true` | HR/Admin (trước A29 là public) | 401 khi không token | **Đúng thiết kế** (ZIP: chỉ login và health mở) |
| Kho CV `GET api/cvs` | HR/Admin | 401 khi không token | **Đúng thiết kế** |
| Danh sách ứng viên `GET api/candidates` | HR/Admin | 401 khi không token | **Đúng thiết kế** |

Kết luận:

1. Các 401 trên là **hành vi bảo mật đúng** của A29, không phải cấu hình sai: không route nào trong số đó nằm trong danh sách được mở anonymous.
2. Không có route nào đáng lẽ public mà bị chặn nhầm: chỉ `POST /api/auth/login` và `GET /api/health` là public theo ZIP, và cả hai đều mở (test xác nhận).
3. Nguyên nhân 401 trên giao diện: `SmartCv.Web` **chưa có** trang đăng nhập hay mã gắn `Authorization: Bearer` (tìm trong `src/SmartCv.Web` không có `Bearer`/`Authorization`/login). Đây là **dependency của B27**, không phải lỗi backend.
4. Pipeline không gây 401 sai: `UseSmartCvAuth` (Authentication → Authorization → RateLimiter) đứng trước `CvPrivacyReadMiddleware` và `MapSmartCvApi`; với token HR hợp lệ, `GET /api/jobs`, `/api/dashboard/summary`, `/api/cvs`, `/api/candidates` qua được phân quyền (test HTTP thật). Tài khoản `MustChangePassword=true` sẽ nhận **403** (không phải 401).
5. Không bỏ `RequireAuthorization`, không thêm `AllowAnonymous` để dữ liệu hiển thị lại.

Yêu cầu tích hợp cho nhóm B:

- **B27:** trang đăng nhập gọi `POST /api/auth/login`, lưu token (ZIP: `ProtectedSessionStorage`), `DelegatingHandler` gắn `Authorization: Bearer <token>` cho mọi `HttpClient` gọi API; 401 → về `/login`; 403 `password_change_required` → trang đổi mật khẩu (sau A30).
- **B28:** ẩn menu/trang Admin theo `role` từ `/api/auth/me`; HR gọi route Admin sẽ nhận 403 `access_denied`.
- **B43:** đổi các link mở thẳng API ("Xem file" `GET /api/cvs/{id}/file`, "Xuất dữ liệu" `GET /api/candidates/{id}/data-export`, mẫu CSV) sang tải qua `HttpClient` có token — trình duyệt mở link trực tiếp sẽ không có header và nhận 401. Bỏ các ô nhập "người thao tác" vì API đã bỏ qua.
- Thứ tự gộp theo ZIP: **B43 cùng ngày hoặc trước A29**, và B27 trước A29, để giao diện không bị 401 sau khi gộp.

## H. Kết luận

**Trạng thái: CONDITIONAL — đủ điều kiện review mã; chưa nên commit/push cho tới khi các điều kiện dưới được chấp nhận.**

Đã đạt: build Release 0 lỗi/0 cảnh báo; focused A29 55/0/1; full regression 3000/0/130; ma trận 82 route khớp ZIP; Actor/UserId không tin client; SmartCvDb không bị chạm trong phiên này (đã kiểm bằng SELECT trước và sau).

Điều kiện còn phải nghiệm thu:

1. Nhóm review bảng `AdminRoutes` và 3 quyết định ngoài ZIP (health chẩn đoán chỉ Admin; chặn MustChangePassword từ A29; giữ prompt-tests cho HR).
2. Người dùng quyết định có chạy `A29SqlServerTests` trên catalog cô lập `SmartCv_A29_Test_*` hay không (hiện NOT RUN).
3. Kiểm tra tay theo mục "Thử" của hướng dẫn A29 (health không token, đổi giai đoạn bằng Admin rồi xem `UserId = 1`, chạy Production) — NOT RUN.
4. Phối hợp thứ tự gộp với B27/B43 để giao diện không bị 401 sau khi gộp A29.
5. Người dùng review `artifacts/a29-commit-manifest.tsv` rồi tự quyết định stage/commit/push.

## I. Danh sách file (theo `artifacts/a29-commit-manifest.tsv`)

| Trạng thái | File | Mục đích |
|---|---|---|
| M | `src/SmartCv.Api/Auth/AuthSetup.cs` | A29: Default/Fallback/Admin policy, no-store cho /api |
| M | `src/SmartCv.Api/Auth/ClaimsPrincipalExtensions.cs` | A29: ActorName bỏ ký tự điều khiển |
| M | `src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs` | A29: 403 password_change_required |
| M | `src/SmartCv.Api/Auth/UserSessionValidator.cs` | A29: kiểm tra phiên mới mỗi request, tên/email từ DB |
| M | `src/SmartCv.Api/Controllers/HealthController.cs` | A29: chẩn đoán health chỉ Admin |
| M | `src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/ApplicationEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/ApplicationStageEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/AuthEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/CandidateDataRightsEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/CandidateEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/CandidateRightsAccessFilter.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/CvUploadEndpointOptions.cs` | A29 (phiên này): sửa comment lỗi thời |
| M | `src/SmartCv.Api/Endpoints/DashboardEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/EvaluationSetMemberEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/JobEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/JobSkillEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/ManualFlagEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/PromptEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/PromptTestEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/RetentionEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/RunEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Endpoints/ScoringPreviewEndpoints.cs` | A29: Actor/UserId từ token, bỏ chốt Development |
| M | `src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs` | A29 (phiên này): khóa đọc A39 ở mọi môi trường |
| M | `src/SmartCv.Api/Program.cs` | A29: thay các Map* bằng MapSmartCvApi; OpenAPI AllowAnonymous |
| M | `src/SmartCv.Core/Auth/SessionRules.cs` | A29: UserSessionState thêm Role/MustChange/FullName/Email |
| M | `src/SmartCv.Core/Contracts/StageContracts.cs` | A29: ApplicationEventDto.UserId |
| M | `src/SmartCv.Infrastructure/Candidates/CandidateErasureService.cs` | A29: UserId cho sự kiện rút hồ sơ |
| M | `tests/SmartCv.Tests/A39PrivacyTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/A39SqlTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/A40EndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Applications/ApplicationStageEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Auth/A28AuthEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Auth/A28SqlServerTests.cs` | A29 (phiên này): scope ngoại lệ read-only cho guard |
| M | `tests/SmartCv.Tests/Auth/A28TestSupport.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Auth/AdminSeederTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Candidates/A43EndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/CvUploadEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Dashboard/DashboardEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/EvaluationSetEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Jobs/JobCrudEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Jobs/JobSkillEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/Runs/RunHistoryEndpointTests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/ScappWorkflowV2Tests.cs` | A29: cập nhật test cũ theo hành vi mới (401/403, Actor từ token) |
| M | `tests/SmartCv.Tests/TestSupport.cs` | A29: ApiFactory authenticated + ValidateTarget |
| A | `docs/A29-implementation-report.md` | A29: báo cáo |
| A | `src/SmartCv.Api/SmartCvApi.cs` | A29: một chỗ map mọi route, nhóm RequireAuthorization |
| A | `tests/SmartCv.Tests/ApplicationDatabaseGuard.cs` | A29 (phiên này): fail-safe chặn catalog SmartCvDb |
| A | `tests/SmartCv.Tests/Auth/A29AuditTests.cs` | A29: audit nhãn/cờ/xuất dữ liệu/Kestrel |
| A | `tests/SmartCv.Tests/Auth/A29AuthorizationTests.cs` | A29: ma trận 82 route, JWT, phiên, audit |
| A | `tests/SmartCv.Tests/Auth/A29SafetyTests.cs` | A29 (phiên này): test fail-safe và khóa đọc A39 |
| A | `tests/SmartCv.Tests/Auth/A29SqlServerTests.cs` | A29: SQL thật opt-in (NOT RUN) |
| A | `tests/SmartCv.Tests/Auth/RegressionAuthenticationHandlerProvider.cs` | A29: fixture token regression, chỉ InMemory/catalog test |

Tổng: 56 file (48 Modified, 8 Added, 0 Deleted).

Không đưa vào commit: toàn bộ `artifacts/` (log, TRX, `endpoint-matrix-runtime.tsv`, bằng chứng database chứa tên máy chủ, script cleanup `remove-confirmed-synthetic-users.sql`, `integrate-maps.ps1`).
