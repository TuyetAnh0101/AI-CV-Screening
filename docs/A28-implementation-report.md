# A28 — JWT authentication và đăng nhập SCApp

Ngày: 19/11/2026 (Asia/Saigon). Phạm vi: A28. Trạng thái bàn giao: **hoàn thành có điều kiện, sẵn sàng review source**; full regression đã pass, còn nghiệm thu cấu hình/tài khoản thật và SQL integration ghi dữ liệu trên catalog cô lập. Không triển khai A29/A30 hoặc UI B27/B28/B29/B43.

## 1. Git và nền A27

| Thuộc tính | Kết quả |
|---|---|
| Repository | `C:\TTCN\project\AI-CV-Screening` |
| Origin | `https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Nhánh mới | `feature/a28-jwt-auth` |
| Nguồn | `origin/main`, không stacked |
| SHA nền 40 ký tự | `e8df732cdb818f59324ed88b9e57b6e87ac05a44` |
| Commit nền | `feat(A27): implement users table and first admin seeding (#83)` |

Working tree ban đầu sạch, không staged/untracked/unmerged, không merge/rebase/cherry-pick/revert/sequencer đang dở; không có nhánh A28 local trùng tên. Đã kiểm tra worktrees hiện có và giữ nguyên. Đã fetch origin --prune, không pull/merge.

A27 đã commit/push, merge bằng squash; remote nhánh A27 đã bị xóa. Commit A27 local `99974dcddaabaf23e88cb9be8d3f0446b2460aea` không là ancestor trực tiếp của squash commit, nhưng **tree của hai bên đều là `9874303d199c3d4b72ecebec3436c34f7ed50dae`**, diff HEAD/origin/main rỗng. Vì vậy tạo A28 từ main đã chứa đầy đủ A27.

Không tạo clone/worktree/project khác, không reset/clean/restore/stash, không đổi Git config toàn cục. Dùng safe.directory riêng từng lệnh vì tài khoản sandbox khác chủ repository. Không stage/commit/push/PR/merge.

## 2. ZIP và tài liệu đã đọc

Đúng ZIP `C:\TTCN\A27_B43_SmartCv.zip`, SHA-256 `21C9B362E408ABC020F2A52019619AA5A8C3499904D8E3DDFC30121FDDD888F2`. Đã kiểm kê 134 file và 100 directory entries; **13 file trong A28**. ZIP không có ảnh/mockup/screenshot/file thiết kế; không có ảnh để xem. Inventory cục bộ: `artifacts/a28/zip-inventory.tsv`.

Guide đầy đủ đã đọc trong A27 với cùng checksum; ở A28 đọc lại phần chung, thứ tự triển khai, A28, các quyết định, frontend và lỗi thường gặp. Đã đọc toàn bộ 13 mẫu A28:

```text
A28/src/SmartCv.Core/Contracts/AuthContracts.cs
A28/src/SmartCv.Core/Options/JwtOptions.cs
A28/src/SmartCv.Core/Auth/SessionRules.cs
A28/src/SmartCv.Core/Auth/SmartCvClaims.cs
A28/src/SmartCv.Api/Auth/AuthSetup.cs
A28/src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs
A28/src/SmartCv.Api/Auth/ClaimsPrincipalExtensions.cs
A28/src/SmartCv.Api/Auth/JwtTokenService.cs
A28/src/SmartCv.Api/Auth/UserSessionValidator.cs
A28/src/SmartCv.Api/Auth/AuthMapping.cs
A28/src/SmartCv.Api/Endpoints/AuthEndpoints.cs
A28/tests/SmartCv.Tests/Api/JwtTokenServiceTests.cs
A28/tests/SmartCv.Tests/Auth/SessionRulesTests.cs
```

Đã đối chiếu thêm:

- `HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md`.
- `snippets/Program.Api.A27-A30.cs.txt`, `snippets/appsettings.Api.A27-A30.json.txt`.
- `database/a27_a30_kiem_tra_tai_khoan.sql` và SQL read-only A27 trong repo.
- B27 `Services/AuthSession.cs`, `Services/AuthApiClient.cs`; B28 `Services/ApiAuthenticationStateProvider.cs`; B29 `Services/AuthApiClient.cs`, cùng mô tả B27/B28/B29/B43 trong guide.
- Source A27: User, UserRoles, PasswordRules, SeedAdminPlan, AuthOptions, UserConfiguration, AdminSeeder, DbContext, AddUsers và A35 metadata/tests.

Các file mẫu A28 chưa tồn tại ở main; tích hợp thành file mới có điều chỉnh. Program/appsettings/AuthOptions tích hợp vào bản hiện tại, không chép đè file ZIP.

## 3. Tình trạng database và A27 thực tế

**Đã kiểm tra SQL Server thật theo hướng chỉ đọc**, qua Microsoft.Data.SqlClient và cấu hình Development hiện có (không có override connection từ command line):

| Nội dung | Kết quả |
|---|---|
| Data source cấu hình | `localhost\BB` |
| Catalog xác nhận bằng DB_NAME | `SmartCvDb` |
| AddUsers | `20261119110208_AddUsers` đã áp dụng |
| Migrations | Đủ 9 migration; EF migrations list không báo pending |
| Users | Đủ 10 cột, identity/nullability/type/length khớp A27 |
| ApplicationEvents.UserId | Có, nullable |
| Admin hoạt động | **0** |
| Jwt:Key trong cấu hình hiện tại | **Thiếu** |

NVARCHAR trong sys.columns báo độ dài byte: Email 508, FullName 300, Role 40, tương ứng 254/150/20 ký tự. PasswordHash VARCHAR(500), SecurityStamp VARCHAR(64); LastLoginAt nullable, các cột còn lại NOT NULL như contract.

Evidence: `artifacts/a28/database-readiness-dotnet.json`, `database-readonly/database-readonly.trx`, `database-migrations-inspection.log`. Lần kiểm tra ban đầu dùng System.Data.SqlClient của PowerShell cũ lỗi tương thích ArgumentException; đã thay bằng Microsoft.Data.SqlClient của project và xác nhận thành công, không coi lần đầu là lỗi database.

Agent không login/seeding/ghi dữ liệu trên SmartCvDb, không chạy database update, không tạo/xóa/reset database. Không sửa connection string để né lỗi. Việc có migration không chứng minh Admin đã seed: count hiện tại là 0. Người dùng cần cấu hình JWT key và SeedAdmin bằng User Secrets trước khi chạy A28 thực tế.

A28 không có migration mới. Không sửa entity/UserConfiguration/DbContext, migration lịch sử hoặc snapshot. `dotnet ef migrations has-pending-model-changes` báo **No changes**. A35 CHECK BIN2, Actor CHECK, trigger, OUTPUT(false), FK/index và lịch sử được giữ nguyên source/model; chưa chạy test ghi SQL để kiểm tra trigger trong phiên A28 này.

## 4. Baseline và ma trận nghiệm thu

SDK 10.0.403. Restore thành công. Build Release baseline: **0 lỗi, 0 cảnh báo**. Full baseline theo đúng `xUnit.MaxParallelThreads=4`: **2.871 pass, 1 fail, 126 skip / 2.998**.

Lỗi baseline: `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`, count 6 ngoài khoảng 2–5, giống lỗi timing đã ghi trong A27. Source worker và test đó không đổi. Giữ TRX/log baseline, không sửa assertion hoặc skip test.

| Yêu cầu | Triển khai | Nghiệm thu |
|---|---|---|
| Login Admin/HR, email chuẩn hóa | AuthEndpoints, AuthMapping, AuthContracts | HTTP login/LastLoginAt/DTO |
| Sai email/password không lộ tài khoản | PasswordHasher, LoginDummyHash | 401 giống nhau, inactive chỉ 403 sau đúng password |
| JWT HS256, 8 giờ | JwtOptions, JwtTokenService, AuthSetup | Claims/issuer/audience/lifetime/signature/algorithm/type |
| Me và trạng thái phiên | UserSessionValidator, SessionRules | Đọc DB mới, stamp/active/deleted/cache expiry |
| Rate limit giữ tương thích | LoginRateLimitPolicy | 5/phút/IP, 429/Retry-After, IP độc lập, upload vẫn dùng thông báo cũ |
| Lỗi request/DB/config | AuthProblems và validation | 400/413/415/503, không stack/secret |
| Startup/endpoint cũ | Program, test factory | DI, lifecycle, health, privacy gates, full regression |
| Bảo toàn schema | Không đổi model/migration | Pending model false, A27/A35/Workflow tests |

Ma trận cục bộ: `artifacts/a28/requirements.tsv`.

## 5. Kiến trúc authentication

- Dùng package **Microsoft.AspNetCore.Authentication.JwtBearer 10.0.14 thật**, cùng patch với ASP.NET OpenAPI hiện có, không dùng JWT giả như môi trường thử của tác giả ZIP.
- JwtOptions quản lý Key/Issuer/Audience/ExpiresHours. Key bắt buộc, tối thiểu 32 ký tự và 32 byte UTF-8; issuer/audience không trống; lifetime 1–24 giờ, mặc định 8. Key không có default hoặc giá trị trong appsettings.
- HMAC-SHA256; ký bằng framework JsonWebTokenHandler. Claims: `sub`, `email`, `name`, `role`, `smartcv_stamp`, `smartcv_mcp`, `iss`, `aud`, `iat`, `nbf`, `exp`.
- Validation chữ ký/key, issuer, audience chính xác (không bỏ qua slash cuối), expiry/lifetime, signed token, chỉ HS256 và type JWT. Clock skew 1 phút theo ZIP. MapInboundClaims=false; NameClaimType=name, RoleClaimType=role.
- Token không chứa password/hash/key. Stamp có trong token đúng contract ZIP để kiểm tra phiên; MeDto không trả stamp.
- OnTokenValidated kiểm tra đủ claims không trùng, UserId dương, role Admin/HR và mcp true/false; đọc trạng thái active/stamp qua store. Cache tối đa 30 giây, có Invalidate hook để A30 dùng sau. DB lỗi không cache thành phiên hợp lệ.
- `/me` đọc lại DB và kiểm tra active/stamp lần nữa, nên không trả tài khoản đã khóa/xóa/đổi stamp ngay cả khi cache trước đó còn hạn. Role/name/mcp trả giá trị hiện tại từ DB.
- Thứ tự middleware: HTTPS hiện có → no-store cho auth → Authentication → Authorization → RateLimiter → middleware privacy/cancellation hiện có.
- Không refresh token, logout/revocation endpoint hoặc storage session mới. Kiểm tra SecurityStamp là yêu cầu A28 trong ZIP. Đổi key/config JWT cần restart API vì issuer và validator dùng cùng options snapshot.

## 6. Luồng login và bảo mật ghi dữ liệu

1. Rate limiter xử lý trước handler, tách riêng login khỏi upload/chấm thử.
2. Chỉ nhận JSON UTF-8, body tối đa 4 KiB; charset không hỗ trợ trả 415, JSON null/lỗi có response an toàn cả ở Development, tránh DeveloperExceptionPage lộ stack.
3. Email qua CandidateRules.NormalizeEmail; required/format/length kiểm tra trước query. Password bắt buộc và tối đa 128 ký tự; không trim hoặc áp lại quy tắc tạo password khi login.
4. Tìm User; không tồn tại vẫn VerifyHashedPassword với dummy hash tạo ngẫu nhiên trong RAM, cùng cấu hình PasswordHasher. Không có tài khoản/password mặc định.
5. Sai password hoặc email hợp lệ không tồn tại: cùng 401. Chỉ trả 403 locked khi password đúng. Không cập nhật LastLoginAt khi thất bại.
6. SuccessRehashNeeded: dùng PasswordHasher chuẩn để nâng hash. LastLoginAt UTC.
7. Trên SQL Server, cập nhật LastLoginAt/hash có điều kiện theo UserId, active, hash và stamp đã đọc (so sánh BIN2). Nếu tài khoản bị đổi password/khóa/stamp giữa verify và update, trả 401 và không ghi đè credential mới. Không cần đổi schema hoặc concurrency token A27.
8. Sau persistence thành công mới phát token và trả LoginResponse/MeDto.

InMemory dùng SaveChanges cho unit tests. Nhánh conditional update SQL có test opt-in riêng, **chưa chạy thực tế**. DB exceptions trực tiếp hoặc bị provider bọc trong inner exception trả 503 generic; cancellation vẫn được truyền đúng. Không log password/token/key/hash/SQL exception detail; log auth chỉ trạng thái chung/UserId. Auth responses có Cache-Control: no-store.

## 7. API contract cho B27/B28/B29

### POST /api/auth/login — anonymous, application/json

```json
{ "email": "<email do người dùng nhập>", "password": "<nhập riêng, không ghi vào Git>" }
```

200 có đúng cấu trúc:

```text
token: string
expiresAt: UTC ISO 8601
user:
  userId: int
  email: string
  fullName: string
  role: Admin | HR
  mustChangePassword: bool
  lastLoginAt: UTC ISO 8601 | null
```

### GET /api/auth/me — Bearer, policy SignedIn

200 trả đúng 6 field của user phía trên. Không password/hash/stamp/key. Missing/bad/expired/revoked account token: 401; DB không kiểm tra được: 503. 401 middleware kèm WWW-Authenticate: Bearer, không đưa lỗi crypto chi tiết ra response.

### Mã lỗi mới có cấu trúc

ProblemDetails dùng status/title/detail và extension `code`; validation có thêm errors theo field.

| HTTP | code | Trường hợp |
|---:|---|---|
| 400 | invalid_request | Email/password thiếu/sai giới hạn, JSON sai/null |
| 401 | invalid_credentials | Email không có tài khoản hoặc password sai; credential thay đổi trong lúc login |
| 401 | authentication_required | Me thiếu/bad/expired token, tài khoản/stamp không còn hợp lệ |
| 403 | account_locked | Password đúng nhưng tài khoản bị khóa |
| 403 | access_denied | Authorization result handler, chuẩn bị dùng bởi policy sau này |
| 413 | request_too_large hoặc invalid_request | Body login quá giới hạn; tùy server phát hiện trước/trong lúc đọc |
| 415 | invalid_content_type | Content-Type không phải JSON hoặc charset không phải UTF-8 |
| 429 | login_rate_limited | Vượt limit; có Retry-After |
| 503 | auth_unavailable | Không thể đọc/cập nhật/kiểm tra phiên từ DB |

Frontend xử lý theo status/code, không suy đoán từ câu tiếng Việt. 400 có errors cho từng field; 401 xóa phiên/đăng nhập lại theo B27; 403 locked hiển thị lý do; 429 đợi Retry-After; 503 là mất dịch vụ, không báo sai password. B27/B28 dùng LoginResponse/MeDto không đổi tên field. `mustChangePassword=true` vẫn được login và gọi me đúng mốc A28; **restriction đổi mật khẩu toàn hệ thống thuộc A30, chưa triển khai**.

## 8. Tương thích privacy A39/A43 và ranh giới A29

Không thêm authorization hàng loạt, không FallbackPolicy/Admin policy, không gỡ Development gate, không đổi Actor/UserId của endpoint nghiệp vụ, không quản lý/đổi password, không sửa Web/CSS/sidebar.

**Ảnh hưởng có chủ đích từ quy tắc bảo mật đã tồn tại:** RetentionEndpoints.AuthorizedOperator và CandidateRightsAccessFilter có từ trước: khi có bất kỳ authentication scheme, chúng yêu cầu người dùng authenticated HR/Admin. A28 đăng ký Bearer nên các filter đó bắt đầu yêu cầu token ở retention/settings/run, anonymize, candidate export/erase và data-requests. Giữ nguyên source/filter và giới hạn HR/Admin; không tắt để làm test xanh. Các Developer gates khác giữ nguyên, không coi A28 là A29 đầy đủ.

Regression lần đầu phát hiện 29 cases A43 và 4 cases A39 gọi anonymous nay nhận 401. Đã sửa **fixture**, giữ mọi assertion:

- Client nghiệp vụ A39/A43 dùng tài khoản HR tổng hợp, có User thật trong InMemory/catalog test và JWT ký bằng key test ngẫu nhiên; không bypass middleware.
- Các test xác thực riêng 401/403/HR/Admin vẫn dùng client/mock scheme riêng và giữ nguyên kỳ vọng.
- Fixture có clock lịch sử dùng TimeProvider.System riêng khi ký token để không tạo token đã hết hạn.
- Khi SQL fixture có interceptor mô phỏng lỗi/cancellation, tạo identity tổng hợp bằng context tới cùng catalog test, ngoài interceptor nghiệp vụ; không làm hỏng mục tiêu fault injection.

SQL fixture thích ứng chưa chạy thực tế vì chưa có SQL test opt-in. Khi B27 chưa merge, giao diện B25 chưa gửi token nên các thao tác privacy trên sẽ nhận 401. Cần phối hợp B27 ngay sau A28; không đổi filter bảo mật hoặc làm UI giả trong nhánh này. Trước sử dụng CV thật cần hoàn tất A29 và các bước M6 liên quan.

## 9. Kiểm thử và evidence

| Lượt | Pass | Fail | Skip | Tổng |
|---|---:|---:|---:|---:|
| Full baseline trước A28 | 2.871 | 1 timing cũ | 126 | 2.998 |
| Startup/DI/A27/health smoke | 16 | 0 | 0 | 16 |
| Focused A28 trước bổ sung kiểm tra charset | 79 | 0 | 3 | 82 |
| Focused A28 bàn giao, gồm charset không hỗ trợ | **80** | **0** | **3** | **83** |
| A28 + compatibility A39/A43 trước bổ sung wrapper cases | 133 | 0 | 3 | 136 |
| A28 + A27/A35/A39/A43/Workflow/migration/worker cuối | 256 | 0 | 15 | 271 |
| SQL thật, inspection chỉ đọc riêng | 1 | 0 | 0 | 1 |
| Full regression lần đầu | 2.915 | 33 fixture chưa auth | 129 | 3.077 |
| Full regression sau sửa fixture, trước kiểm tra charset | 2.951 | 0 | 129 | 3.080 |
| **Full regression bàn giao cuối, MaxParallelThreads=4** | **2.952** | **0** | **129** | **3.081** |

Focused gồm: Admin/HR/mcp login; LastLoginAt; rehash; DTO whitelist; wrong/unknown/locked credentials; JSON/size/content-type; JWT signature/issuer/audience/slash/type/algorithm/unsigned/expired/future/no-expiry/tamper; missing/duplicate claims; session cache expiry/invalidate; wrapped DB read/save errors; fail-closed me nhưng health vẫn chạy; cấu hình sai dừng startup; no-store/log redaction; IP độc lập/IPv4-mapped/không tin X-Forwarded-For; login/upload rate limit không ghi đè nhau; legacy privacy filters và không có policy/route A29/A30.

Có HTTP qua TestServer và **Kestrel loopback thật**, dùng InMemory và tài khoản tổng hợp; JwtBearer/IdentityModel/PasswordHasher thật. Không coi đó là login tài khoản thật trên SQL.

SQL integration **NOT RUN**: 2 test methods opt-in kiểm tra login/me/UTC/hash/stamp/history trên catalog mới và race đổi password trong lúc rehash. Read-only inspection là test thứ ba: skip trong full suite nhưng đã chạy riêng thành công. Skip cuối cần phân biệt 123 SQL integration opt-in (121 cũ + 2 mới), 1 inspection đã chạy riêng và 5 demo/resource tests thiếu ZIP demo. Không tính skip là pass.

Build Release cuối chạy tuần tự: **0 errors, 0 warnings**. Có một lượt build chạy trùng test host gây khóa DLL ở output tests (8 MSB3027/MSB3021 errors, 40 retry warnings); đã giữ log `build-overlap-failed.log` và chạy lại sau khi test host kết thúc, không dừng API/Web người dùng. Full regression bàn giao sau kiểm tra charset chạy **6 phút 17 giây**, không lỗi; không cần đổi assertion test polling worker. Evidence cuối: `release-build.log`, `focused-final/focused-final.trx`, `regression-delivery/full-regression-delivery.trx`.

NuGet audit `dotnet list ... package --vulnerable --include-transitive --format json` hoàn tất exit 0, không báo package có advisory vulnerability; không phải chứng minh không có lỗ hổng. Build/model check và dependency audit artifacts được giữ cục bộ. CLI EF 10.0.14 cảnh báo runtime EF 10.0.15 theo dependency wildcard có sẵn; không đổi package EF/tool ngoài phạm vi.

Các lỗi phát hiện trong quá trình triển khai đã sửa: JSON null/lỗi lộ stack ở Development; callback rate limit chung trong mẫu có thể ghi đè upload; provider exception bị bọc; fixture dùng auth/clock cũ. Không thay assertions để bỏ qua lỗi. Lỗi worker baseline vẫn giữ nguyên source và được kiểm tra riêng trong lượt focused cuối.

Evidence trong `artifacts/a28/`: baseline-restore/build/test + baseline.trx; focused/focused.trx; auth-compatibility; schema-regression (lần phát hiện lỗi); final-focused-regression; regression (lần 33 lỗi); regression-final (trước charset); focused-final và regression-delivery (bàn giao cuối); database-readonly; database-readiness-dotnet.json; dependency-audit.json; model-check.log. Không ghi secrets thật vào các artifact này.

## 10. Khác biệt với ZIP và lý do

- Branch theo yêu cầu người dùng `feature/a28-jwt-auth`, khác tên ví dụ a28-jwt-login.
- Dùng package JWT thật; thêm validation issuer/audience/bytes/type/claims, options validation fail rõ thay Math.Max âm thầm sửa rate limit.
- Tách LoginRateLimitPolicy với OnRejected riêng theo cơ chế [IRateLimiterPolicy](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.ratelimiting.iratelimiterpolicy-1.onrejected?view=aspnetcore-10.0), giữ GlobalLimiter/OnRejected upload và AI đang có.
- Tách AuthProblems/LoginDummyHash để xử lý lỗi thống nhất, dummy không dùng password hằng, cùng PasswordHasher options; không log reason phân biệt user không tồn tại/sai password.
- JSON/client errors 400/413/415, DB 503, cache-control no-store; code lỗi là extension tương thích ngược DTO.
- SQL conditional persistence bảo vệ credential đổi đồng thời; không sửa A27 concurrency metadata hoặc migration.
- Cache dùng TimeProvider để kiểm thử expiry, me kiểm tra stamp mới từ DB. ActorName helper theo contract A28 nhưng chưa dùng ghi lịch sử; truncate không cắt đôi surrogate pair.
- Giữ các filter bảo mật A39/A43 thật; tích hợp fixture có JWT thay vì nới filter/đổi assertions.
- Không paste token thật vào jwt.io như ví dụ ZIP. Kiểm tra cục bộ và bằng me/framework validation.

## 11. Cấu hình và nghiệm thu do người dùng thực hiện

**Chưa có JWT key và Admin hoạt động trên database hiện tại.** Agent không tự tạo key/password Admin cho môi trường thật, không đọc/in giá trị password Admin. Các đoạn dưới chỉ là hướng dẫn; agent chưa thực thi.

### 11.1. Development User Secrets

Project Api đã có UserSecretsId. Trong terminal cá nhân, không bật transcript/ghi màn hình, tại repository. Sinh 48 byte ngẫu nhiên rồi pipe JSON vào CLI, không in key hoặc tạo file chứa key trong repo:

```powershell
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$a28Bytes = New-Object byte[] 48
$a28Rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $a28Rng.GetBytes($a28Bytes)
    @{ 'Jwt:Key' = [Convert]::ToBase64String($a28Bytes) } |
        ConvertTo-Json -Compress | dotnet user-secrets set --project src/SmartCv.Api
    if ($LASTEXITCODE -ne 0) { throw 'Khong luu duoc JWT secret.' }
}
finally {
    [Array]::Clear($a28Bytes, 0, $a28Bytes.Length)
    $a28Rng.Dispose()
    Remove-Variable a28Bytes,a28Rng -ErrorAction SilentlyContinue
}
```

Đặt `Auth:SeedAdmin:Email`, `Auth:SeedAdmin:FullName`, `Auth:SeedAdmin:Password` bằng quy trình nhập password `Read-Host -AsSecureString` và JSON stdin trong [báo cáo A27](A27-implementation-report.md). Không đặt password trực tiếp trong command history, không dùng user-secrets list trong phiên chia sẻ. FullName bỏ trống sẽ dùng tên mặc định của A27. Password 8–128 ký tự, có chữ và số, không trùng email.

User Secrets là cấu hình Development cục bộ, không phải vault production. Production dùng kho secret của hạ tầng/biến `Jwt__Key`, cùng Issuer/Audience giữa issuer và validator; quản lý rotation/access riêng, HTTPS và proxy tin cậy. Không commit .env hoặc key; không tự bật API Development ra production trước A29. Rate limit hiện ở RAM mỗi tiến trình; proxy/multi-instance cần cấu hình trusted forwarded headers và giới hạn tại ingress khi triển khai, không tin X-Forwarded-For tùy ý.

### 11.2. Schema và chạy API

Database vừa kiểm tra đã có AddUsers, **không cần chạy lại migration cho catalog này**. Trên môi trường khác, kiểm tra connection/catalog, migration history và backup trước. Nếu thiếu AddUsers, người dùng phê duyệt rồi tự chạy:

```powershell
dotnet ef migrations list --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --configuration Release
# Chỉ sau kiểm tra đúng catalog, sao lưu và phê duyệt riêng:
dotnet ef database update AddUsers --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --configuration Release
```

Không đổi connection sang database khác để né Invalid object name 'Users'. A27 seeder vẫn cần schema hợp lệ khi startup. Chưa có Admin và thiếu SeedAdmin: warning, không tạo mặc định; config seed sai: dừng startup; thiếu Jwt:Key: options error rõ và dừng startup. Gemini/options/worker gates cũ vẫn có hiệu lực.

Sau cấu hình và review, dùng profile Development/cổng HTTPS hiện có để chạy API Release. Đóng phiên API cũ theo quyết định của người dùng trước khi chạy bản mới cùng cổng; agent không dừng ứng dụng đang dùng.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/SmartCv.Api --configuration Release
```

### 11.3. Login và me không in token/password

Ví dụ PowerShell, điền URL HTTPS đã cấu hình bằng Read-Host. Tài khoản và password do người dùng tự nhập; response chỉ hiển thị user/expiresAt:

```powershell
$a28Base = (Read-Host 'URL HTTPS cua API').TrimEnd('/')
$a28Email = Read-Host 'Email'
$a28Secure = Read-Host 'Mat khau' -AsSecureString
$a28Bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($a28Secure)
try {
    $a28Json = @{ email = $a28Email; password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($a28Bstr) } | ConvertTo-Json -Compress
    $a28Login = Invoke-RestMethod -Method Post -Uri "$a28Base/api/auth/login" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($a28Json))
    $a28Token = $a28Login.token
    $a28Login.user | Format-List
    'ExpiresAt UTC: ' + $a28Login.expiresAt
    Invoke-RestMethod "$a28Base/api/auth/me" -Headers @{ Authorization = "Bearer $a28Token" } | Format-List
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($a28Bstr)
    $a28Secure.Dispose()
    Remove-Variable a28Json,a28Bstr,a28Secure,a28Login -ErrorAction SilentlyContinue
}
```

Admin hợp lệ: 200, role Admin, LastLoginAt có UTC, me 200. HR hợp lệ: cùng behavior với role HR. **A28 chưa có API tạo HR**: dùng HR đã được cấp hợp lệ, hoặc chạy test HR tổng hợp/InMemory/SQL cô lập; không đổi role Admin cuối cùng hay tạo HR bằng lệnh tùy tiện trên SmartCvDb để thử.

Kiểm tra thêm: valid email không tồn tại/sai password cùng 401; request thiếu field/JSON lỗi 400; locked+password đúng 403; me không token/bad token 401; lần login thứ 6/phút/IP 429 với Retry-After; lỗi DB mô phỏng 503 generic. Không khóa/xóa tài khoản hoặc ngắt SQL đang dùng để thử lỗi; dùng fixture cô lập. Không dán token thật lên website kiểm tra JWT. Có thể đọc payload cục bộ để xem sub/name/role/exp, nhưng decode payload không phải xác thực; me/framework tests mới kiểm tra chữ ký.

### 11.4. Tests và SQL opt-in

```powershell
dotnet restore SmartCv.slnx
dotnet build SmartCv.slnx -c Release --no-restore
dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj -c Release --filter FullyQualifiedName~A28 -- xUnit.MaxParallelThreads=2
```

SQL ghi dữ liệu chỉ bật với `SMARTCV_A28_TEST_SQL_CONNECTION` do người dùng cấu hình riêng, Initial Catalog bắt đầu `SmartCv_A28_Test_`, không AttachDBFilename. Không in connection string/credential. Fixture kiểm tra catalog GUID mới chưa tồn tại, xác nhận DB_NAME rồi migrate trên catalog mới; giữ lại catalog/evidence, không DROP/reset/reuse catalog cũ. Chưa có biến này trong phiên hiện tại nên 2 tests NOT RUN.

Inspection chỉ đọc có flag riêng `SMARTCV_A28_INSPECT_APP_DB=1`, filter `Inspect_configured_Development_database_without_seeding`; không bật flag này trong full suite chung. Nó đọc cấu hình Development nhưng không startup API/seeding, không SELECT hash/stamp/tài khoản cá nhân.

## 12. Sẵn sàng review và việc cần trước A29

Source A28 và hợp đồng B27 đã có thể review. Nghiệm thu môi trường thật còn điều kiện: người dùng cấu hình key và Admin seed; xác nhận login/me trên SQL với credential riêng; chạy 2 SQL integration trên catalog cô lập. B27 cần gắn Bearer cho các privacy routes đã có filter. A29 tiếp tục policy toàn API/Development gate/Actor từ token; A30 mới giới hạn mustChangePassword và quản lý tài khoản, đồng thời đổi stamp/invalidate cache.

Không tuyên bố đã đăng nhập Admin thật/SQL thành công. Baseline worker timing là rủi ro có sẵn; không sửa ngoài A28. Auth cache 30 giây và rate limiter theo từng tiến trình là giới hạn đã nêu. Danh sách file/status/manifest và kết quả full cuối được chèn sau bước kiểm tra bàn giao.


## 13. Danh sách file và trạng thái Git cuối

**28 file: 19 Added, 9 Modified, 0 Deleted.** TAB thật; cùng nội dung manifest:

```text
A	docs/A28-implementation-report.md
M	src/SmartCv.Api/appsettings.json
A	src/SmartCv.Api/Auth/AuthMapping.cs
A	src/SmartCv.Api/Auth/AuthProblems.cs
A	src/SmartCv.Api/Auth/AuthSetup.cs
A	src/SmartCv.Api/Auth/ClaimsPrincipalExtensions.cs
A	src/SmartCv.Api/Auth/JwtTokenService.cs
A	src/SmartCv.Api/Auth/LoginDummyHash.cs
A	src/SmartCv.Api/Auth/LoginRateLimitPolicy.cs
A	src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs
A	src/SmartCv.Api/Auth/UserSessionValidator.cs
A	src/SmartCv.Api/Endpoints/AuthEndpoints.cs
M	src/SmartCv.Api/Program.cs
M	src/SmartCv.Api/SmartCv.Api.csproj
A	src/SmartCv.Core/Auth/SessionRules.cs
A	src/SmartCv.Core/Auth/SmartCvClaims.cs
A	src/SmartCv.Core/Contracts/AuthContracts.cs
M	src/SmartCv.Core/Options/AuthOptions.cs
A	src/SmartCv.Core/Options/JwtOptions.cs
M	tests/SmartCv.Tests/A39PrivacyTests.cs
M	tests/SmartCv.Tests/A39SqlTests.cs
A	tests/SmartCv.Tests/Auth/A28AuthEndpointTests.cs
A	tests/SmartCv.Tests/Auth/A28SqlServerTests.cs
A	tests/SmartCv.Tests/Auth/A28TestSupport.cs
A	tests/SmartCv.Tests/Auth/A28TokenAndSessionTests.cs
M	tests/SmartCv.Tests/Candidates/A43EndpointTests.cs
M	tests/SmartCv.Tests/Candidates/A43SqlServerTests.cs
M	tests/SmartCv.Tests/TestSupport.cs
```

Git status cuối:

```text
## feature/a28-jwt-auth
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Api/SmartCv.Api.csproj
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Options/AuthOptions.cs
 M tests/SmartCv.Tests/A39PrivacyTests.cs
 M tests/SmartCv.Tests/A39SqlTests.cs
 M tests/SmartCv.Tests/Candidates/A43EndpointTests.cs
 M tests/SmartCv.Tests/Candidates/A43SqlServerTests.cs
 M tests/SmartCv.Tests/TestSupport.cs
?? docs/A28-implementation-report.md
?? src/SmartCv.Api/Auth/AuthMapping.cs
?? src/SmartCv.Api/Auth/AuthProblems.cs
?? src/SmartCv.Api/Auth/AuthSetup.cs
?? src/SmartCv.Api/Auth/ClaimsPrincipalExtensions.cs
?? src/SmartCv.Api/Auth/JwtTokenService.cs
?? src/SmartCv.Api/Auth/LoginDummyHash.cs
?? src/SmartCv.Api/Auth/LoginRateLimitPolicy.cs
?? src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs
?? src/SmartCv.Api/Auth/UserSessionValidator.cs
?? src/SmartCv.Api/Endpoints/AuthEndpoints.cs
?? src/SmartCv.Core/Auth/SessionRules.cs
?? src/SmartCv.Core/Auth/SmartCvClaims.cs
?? src/SmartCv.Core/Contracts/AuthContracts.cs
?? src/SmartCv.Core/Options/JwtOptions.cs
?? tests/SmartCv.Tests/Auth/A28AuthEndpointTests.cs
?? tests/SmartCv.Tests/Auth/A28SqlServerTests.cs
?? tests/SmartCv.Tests/Auth/A28TestSupport.cs
?? tests/SmartCv.Tests/Auth/A28TokenAndSessionTests.cs
```

Metadata chỉ lưu cục bộ: artifacts/a28-base-sha.txt (40 ký tự lấy từ HEAD khi tạo nhánh), artifacts/a28-commit-manifest.tsv (không header, một dòng mỗi file). Cả hai được ignore bởi artifacts/ có sẵn. Không stage các file này. Manifest được đối chiếu hai chiều với status, không thiếu/thừa/trùng; báo cáo này nằm trong manifest.

Không có thay đổi Infrastructure/model/migration/snapshot, Web/CSS, middleware cũ, worker hoặc filter privacy cũ. Mọi thay đổi test A39/A43 chỉ cập nhật fixture authenticated tương ứng auth đã cấu hình, không thay assertions. Các kiểm tra cuối bao gồm git diff --check, conflict/secret/build-data scan, index rỗng, SHA nền và danh sách file; evidence cục bộ: artifacts/a28/handoff-audit.json.
