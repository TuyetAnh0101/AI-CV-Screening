# A30 — Quản lý tài khoản, mật khẩu và an toàn Admin

Ngày thực hiện: 20/11/2026. Kết luận: **CONDITIONAL** — code sẵn sàng review; kiểm chứng SQL Server và nghiệm thu frontend trên môi trường cô lập còn thiếu. Chỉ thực hiện A30, không có nhiệm vụ A307 trong bộ ZIP.

## 1. Tài liệu ZIP và đối chiếu

Nguồn duy nhất: `C:\TTCN\A27_B43_SmartCv.zip`; SHA-256 `21C9B362E408ABC020F2A52019619AA5A8C3499904D8E3DDFC30121FDDD888F2`. Kiểm kê 134 file, 100 thư mục. **Không có ảnh, mockup, screenshot, SVG hoặc PDF; không có việc xem ảnh.** Danh sách đầy đủ được giữ cục bộ tại `artifacts/a30/zip-inventory.txt` (ignored).

Đã đọc đầy đủ `HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md`, gồm thứ tự A27 → A28 → A29 → A30, lỗi thường gặp, phần tự quyết, các hợp đồng B27/B28/B29/B43. Đã đọc các snippet `Program.Api.A27-A30.cs.txt`, `Program.Api.sau-A30.tham-khao.cs`, `appsettings.Api.A27-A30.json.txt`, `Program.Web.B28.cs.txt` và SQL `database/a27_a30_kiem_tra_tai_khoan.sql`. Chỉ áp dụng phần backend A30; không thực thi SQL hoặc phần Web trong mẫu.

Đã đọc và so sánh từng file trong `A27_B43_SmartCv/A30/` với source nền:

1. `src/SmartCv.Core/Contracts/AuthContracts.cs`
2. `src/SmartCv.Core/Auth/UserRules.cs`
3. `src/SmartCv.Api/SmartCvApi.cs`
4. `src/SmartCv.Api/Auth/AuthSetup.cs`
5. `src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs`
6. `src/SmartCv.Api/Auth/PasswordChangedRequirement.cs`
7. `src/SmartCv.Api/Auth/AuthMapping.cs`
8. `src/SmartCv.Api/Endpoints/AuthEndpoints.cs`
9. `src/SmartCv.Api/Endpoints/UserEndpoints.cs`
10. `tests/SmartCv.Tests/Api/AuthorizationTests.cs`
11. `tests/SmartCv.Tests/Auth/UserRulesTests.cs`

Đối chiếu được lưu tại `artifacts/a30/zip-comparison.tsv`. Các file hiện có chỉ được bổ sung phần cần thiết; không chép mẫu cũ đè lên source. `UserRules` mới kế thừa bề mặt hợp đồng mẫu và thêm giới hạn đầu vào. Test phân quyền được mở rộng trong `A29AuthorizationTests` đang có. Không tạo lại `PasswordChangedRequirement`: sử dụng điều kiện fail-closed đã có từ A29.

Khác biệt có chủ đích với ZIP:

- Thêm điều phối ghi bằng transaction/khóa SQL, kiểm tra lại actor và conditional update; mẫu SELECT rồi UPDATE chưa đủ bảo vệ concurrency.
- Thêm phiên bản tài khoản, If-Match tùy chọn, phân trang/lọc có giới hạn, validation JSON và mã lỗi ổn định. Danh sách vẫn là mảng để tương thích B29.
- Reset mật khẩu hành chính **chỉ áp dụng HR**. Reset mọi tài khoản Admin trả 409; Admin tự đổi mật khẩu qua xác minh mật khẩu hiện tại. Đây là hạn chế an toàn theo yêu cầu bảo vệ Admin của nhiệm vụ.
- Tên thay đổi đơn thuần không đổi stamp; role thay đổi có đổi stamp, giữ cách A29 lấy tên mới từ database. Email luôn bất biến.
- Không dùng các thao tác xóa/sửa Admin hoặc hash giả trong phần thử SQL mẫu. Không thêm DELETE, UI, gửi email hoặc bảng audit mới.

## 2. Git, worktree và nền triển khai

| Mục | Giá trị |
|---|---|
| Repository gốc | `C:\TTCN\project\AI-CV-Screening` |
| Worktree A30 | `C:\TTCN\project\AI-CV-Screening-A30` |
| Nhánh A30 | `feature/a30-account-management` |
| Nguồn | `origin/main`, sau `git fetch origin --prune` |
| SHA nền đầy đủ | `03b0ed4d510d64dde2c6dd3b38cbc92aefdbe51c` |
| Origin | `https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Handoff ngoài Git | `C:\TTCN\project\SCApp-A30-Handoff` |

Main đã chứa A27 (#83), A28 (#84), A29 (#85), B27 (#86). Đây là nhánh từ main, không phải stacked branch. Trước triển khai đã kiểm tra trạng thái, staged/untracked, nhánh trùng, worktree và trạng thái merge/rebase/cherry-pick. Tạo worktree/nhánh mới đúng tên, không đổi nhánh repository gốc, không mang thay đổi chưa commit sang A30.

Không sửa worktree B27. Đường dẫn B27 có ở kiểm tra đầu phiên nhưng không còn tồn tại ở kiểm tra cuối phiên; A30 không thực hiện thao tác xóa/di chuyển worktree đó. Không stage, commit, push, merge, rebase, cherry-pick hoặc tạo PR. Chỉ dùng `safe.directory` theo từng lệnh, không đổi Git config toàn cục.

`base-sha.txt` chứa đúng 40 ký tự, không BOM. `commit-manifest.tsv` có A/M, TAB thật, đường dẫn tương đối, không header, không trùng; gồm báo cáo này. Hai file nằm ngoài repository, không nằm trong manifest.

## 3. Kiến trúc tài khoản và phiên

Giữ nguyên `User`, role Admin/HR, schema A27, `PasswordRules`, `SeedAdminPlan`, `AuthOptions`, `AdminSeeder`, `UserConfiguration`, migrations và model snapshot. Đã đọc cơ chế JWT, claims, SessionRules, PasswordHasher, policies, ProblemDetails, fixtures A27/A28/A29 và ApplicationDatabaseGuard trước khi bổ sung A30.

`UserRules` chứa validation/chuẩn hóa và quyết định bảo vệ Admin thuần. `UserAccountService` xử lý nghiệp vụ. `AccountWriteCoordinator` bảo đảm thứ tự ghi. Endpoint chỉ đọc request, gọi service, trả DTO/ProblemDetails. Đăng ký service trong `AuthSetup`; `Program.cs` giữ nguyên.

A29 vẫn kiểm tra trạng thái tài khoản từ database ở **mỗi request JWT** (`requireFresh: true`). Không có khoảng chờ cache 30 giây cho API production. Sau thay đổi đã commit, A30 còn invalidate khóa cache tương thích cũ. Không thay JWT claims, thời hạn token, `LoginResponse`, `MeDto`, DefaultPolicy hoặc FallbackPolicy.

Chỉ `SignedIn` cho `/me` và `/change-password` cho phép MustChangePassword. API nghiệp vụ và quản trị khác vẫn yêu cầu mật khẩu chính thức. Login giữ anonymous. Việc đổi tên lấy tên mới từ DB theo A29; thay role/stamp/trạng thái bắt token đăng nhập lại.

## 4. API contract

Tất cả route dưới đây dùng Bearer JWT, `Cache-Control: no-store`. Không nhận password trên query string. Trường JSON theo camelCase.

| Method / route | Request | Response thành công | Policy |
|---|---|---|---|
| GET `/api/users` | Query tùy chọn: search, role, isActive, page, pageSize | 200 `UserDto[]` và header phân trang | Admin + đã đổi mật khẩu |
| POST `/api/users` | email, fullName, role, password tùy chọn | 201 `{ user, temporaryPassword }` | Admin + đã đổi mật khẩu |
| PUT `/api/users/{id}` | fullName, role, expectedVersion tùy chọn | 200 `UserDto` | Admin + đã đổi mật khẩu |
| POST `/api/users/{id}/lock` | Không cần body; If-Match tùy chọn | 200 `UserDto` | Admin + đã đổi mật khẩu |
| POST `/api/users/{id}/unlock` | Không cần body; If-Match tùy chọn | 200 `UserDto` | Admin + đã đổi mật khẩu |
| POST `/api/users/{id}/reset-password` | Không cần body; If-Match tùy chọn; mục tiêu HR | 200 `{ user, temporaryPassword }` | Admin + đã đổi mật khẩu |
| POST `/api/auth/change-password` | currentPassword, newPassword; If-Match tùy chọn | 200 `{ token, expiresAt, user: MeDto }` | SignedIn, cho phép MustChangePassword |

`UserDto`: userId, email, fullName, role, roleName, isActive, mustChangePassword, createdAt, lastLoginAt, version. Không có PasswordHash hoặc SecurityStamp. Version là digest SHA-256 của trạng thái revision có stamp ngẫu nhiên; không phải stamp hay hash mật khẩu. LastLoginAt không tham gia version để login không làm stale màn hình quản trị.

Danh sách mặc định page=1, pageSize=100, tối đa 200. Search tối đa 254 ký tự, role chỉ Admin/HR, isActive true/false. Sắp xếp active giảm dần, tên tăng dần, ID tăng dần. Trả `X-Total-Count`, `X-Page`, `X-Page-Size`. Trùng tham số, số âm/overflow hoặc bộ lọc sai trả 400. Count/trang là kết quả đọc thông thường, không cam kết snapshot giữa hai câu truy vấn khi có ghi đồng thời.

Mutation trả ETag chứa version. Client gửi `If-Match: "<version>"`; PUT cũng nhận expectedVersion. Nếu gửi cả hai phải giống nhau. Version cũ trả 409, sai định dạng trả 400. Version tùy chọn để giữ tương thích ZIP; frontend cần gửi version để chống ghi đè từ màn hình đã cũ. Không có route GET-by-ID hoặc DELETE; Location của create định danh tài khoản vừa tạo.

| HTTP | code / ý nghĩa |
|---|---|
| 400 | `validation_failed`, có `errors` theo field; gồm mật khẩu hiện tại sai/mật khẩu mới yếu |
| 401 | `authentication_required` khi service kiểm tra lại thấy phiên hết hiệu lực; middleware giữ mã A28/A29; có WWW-Authenticate |
| 403 | `access_denied`; hoặc `password_change_required` kèm `mustChangePassword: true` |
| 404 | `account_not_found` sau khi đã có quyền truy cập |
| 409 | `email_conflict`, `account_conflict`, `admin_safety`, `admin_password_reset_forbidden` |
| 413 / 415 | `request_too_large` / `invalid_content_type` |
| 429 | `credential_rate_limited`, kèm Retry-After |
| 503 | `account_unavailable`; lỗi kiểm tra phiên từ middleware giữ `auth_unavailable` |
| 500 | `account_error` cho lỗi không dự kiến; không đưa exception/SQL/stack trace ra response |

Create, reset và change-password chia quota 10 thao tác/phút theo UserId đã xác thực, riêng quota login/upload/AI. Quota hiện tại theo process, không phải distributed limiter.

## 5. Nghiệp vụ

- Tạo tài khoản: email trim/lowercase theo CandidateRules A27, tên chuẩn hóa và tối đa 150, role Admin/HR theo guide. Password tùy chọn; null/chuỗi rỗng sinh mật khẩu tạm CSPRNG 12 ký tự, có hoa/thường/số; mật khẩu được cung cấp phải đạt PasswordRules A27 (8–128, chữ và số, không trùng email, không khoảng trắng đầu/cuối). Mọi tài khoản mới active, MustChangePassword=true, UTC, stamp ngẫu nhiên. PasswordHasher băm trước lưu. Chỉ trả temporaryPassword khi hệ thống sinh; không có API đọc lại.
- Cập nhật: chỉ tên/role; email không thay đổi kể cả client gửi thêm field email. Không tự thay hash. Role đổi làm đổi stamp; tự đổi role bị từ chối. Đổi tên đơn thuần không thu hồi phiên.
- Đổi mật khẩu cá nhân: UserId lấy từ JWT, xác minh currentPassword bằng PasswordHasher, newPassword phải hợp lệ và khác mật khẩu hiện tại. Commit hash mới, stamp mới, MustChangePassword=false; trả JWT mới. Token cũ bị từ chối; không sửa LastLoginAt hoặc CreatedAt.
- Reset HR: Admin đã có mật khẩu chính thức mới được gọi. Sinh password tạm một lần, hash mới, stamp mới, MustChangePassword=true. HR đang khóa vẫn khóa. Tài khoản Admin không được reset qua route này.
- Khóa/mở: chỉ đổi active và stamp khi chuyển trạng thái. Mở khóa giữ hash và MustChangePassword; gọi lại cùng trạng thái là no-op, không xoay stamp liên tiếp.
- Không có xóa vật lý, reset identity, quên mật khẩu ẩn danh, refresh token, gửi email hoặc thao tác hàng loạt.

## 6. Bảo vệ Admin hiện có

**Trong lượt A30 không có kết nối SQL Server thật, không đọc hoặc ghi bản ghi Admin thật, không đăng nhập Admin thật.** Không đổi User Secrets/connection string; không chạy API production để thử đổi/reset/khóa tài khoản. Không chạy cleanup A29. Không thể tuyên bố đã đối chiếu fingerprint tài khoản thật bằng SQL vì không thực hiện truy vấn đó.

Bằng chứng trong phạm vi thực hiện:

- `AdminSeeder.cs`, startup `Program.cs`, User entity, AuthOptions, schema và migrations có diff rỗng so với SHA nền.
- Test giữ nguyên fingerprint mọi cột của **Admin giả ID 1 trên InMemory** sau tạo HR; khởi động seed nhiều lần với cấu hình khác/sai vẫn giữ nguyên tài khoản có sẵn.
- Fixture A30 mặc định dùng InMemory, email example.invalid, password ngẫu nhiên trong bộ nhớ; không có tài khoản thật hoặc dữ liệu tuyển dụng thật trong test.
- ApplicationDatabaseGuard của A29 được đọc và giữ nguyên, chặn SqlClient tới SmartCvDb trước mở mạng/thực thi lệnh. Không có biến opt-in SQL/inspection bật trong các lượt test.
- Nghiệp vụ không hardcode UserId=1 để quyết định quyền. Tự khóa/tự đổi role bị chặn; count Admin active được kiểm tra trong transaction cùng thao tác ghi. Không có DELETE. Reset hành chính mọi Admin bị chặn.

Bảo vệ Admin cuối cùng là quy tắc áp dụng mọi tài khoản; không khẳng định API tuyệt đối cấm sửa tên/role/khóa mọi Admin khác khi vẫn còn Admin active và request quản trị hợp lệ. Không có request như vậy đối với dữ liệu thật trong triển khai này.

## 7. Bảo mật mật khẩu, JWT và audit

Giữ PasswordHasher<User> của ASP.NET Core, không thuật toán tự chế/mã hóa ngược. Temporary password sinh bằng RandomNumberGenerator, không ghi plaintext xuống DB, repository hoặc log. Response credential một lần và token mới có no-store theo middleware có sẵn. JSON giới hạn 8 KiB, UTF-8, từ chối body null/array, JSON lỗi hoặc trùng key kể cả khác hoa thường. Không trả SQL exception hay stack trace.

Stamp thay khi đổi/reset password, đổi role, khóa hoặc mở khóa. Sau commit, token cũ không còn khớp DB và trả 401 ngay ở request tiếp theo; request đã được xác thực và đang chạy trước commit không bị hủy hồi tố. Mutation A30 còn kiểm tra lại actor dưới khóa trước ghi, tránh actor bị khóa/hạ quyền trong thời gian chờ.

Audit dùng structured logging đang có: loại thao tác, ActorId từ phiên tin cậy, TargetId, thời điểm UTC, cờ roleChanged. Không log email, password, hash, stamp hoặc token. Không tạo bảng audit mới. Lưu trữ/bảo toàn audit qua log sink và phân quyền vận hành là trách nhiệm deployment. Test capture log kiểm tra bí mật không xuất hiện.

A30 không thêm cấu hình secret. JWT key và SeedAdmin giữ cơ chế User Secrets/môi trường đã có từ A27/A28; không liệt kê/in giá trị. Không đặt mật khẩu Admin thay người dùng. Cấu hình seed không dùng để đồng bộ/reset thông tin tài khoản hiện có.

## 8. Concurrency và giới hạn

Mọi ghi A30 đi qua SemaphoreSlim trong host; SQL Server dùng transaction **Serializable** và `sp_getapplock` Exclusive, Transaction-owned, resource cố định `SCApp.A30.AccountSecurity`, timeout 10 giây. Khóa được chia sẻ giữa các API instance cùng catalog; dispose giải phóng/rollback khi lỗi. Theo [tài liệu SQL Server](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql), khóa transaction cần nằm trong transaction và kết quả âm phải được xử lý.

Sau khi giữ khóa: đọc lại actor active/role/stamp/MustChangePassword; đọc lại mục tiêu; so revision quan sát trước khóa và version client nếu có; đếm Admin active; sau đó conditional ExecuteUpdate với ID, role, active, MustChangePassword, tên/email/stamp/hash gốc (BIN2 cho chuỗi). Nếu không đúng một dòng thì 409. Cách này bảo vệ cả race với A28 rehash không tham gia applock, đồng thời giữ Email, CreatedAt và LastLoginAt. [EF Core ExecuteUpdate](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete) cho phép kiểm tra số dòng tác động để tự kiểm soát concurrency.

Unique email vẫn do index A27 bảo vệ; SQL 2601/2627 được chuyển 409. Deadlock 1205 trả 409; lỗi DB/lock timeout trả 503. Không tự retry mutation có credential một lần. Nếu mất kết nối ở commit, kết quả commit có thể chưa rõ: cần đọc lại trạng thái trước thao tác tiếp; không giả định 503 đồng nghĩa chưa ghi.

Test InMemory xác nhận quyết định, actor stale, trùng email, hai reset cùng version, đổi password đua reset/lock, hai Admin đua khóa/hạ quyền. InMemory chỉ có khóa trong một host, không chứng minh isolation/khóa thực tế SQL. Có test SQL opt-in với hai API instance riêng nhưng **NOT RUN**.

Giới hạn: khóa toàn bộ ghi tài khoản ưu tiên tính đúng, giảm throughput quản trị; không khóa API nghiệp vụ khác. If-Match không bắt buộc vì tương thích contract cũ, nên request tuần tự không gửi version vẫn có thể là thao tác mới hợp lệ. Tất cả writer quản trị A30 dùng chung khóa; câu SQL bên ngoài API không tự tuân theo quy tắc last Admin. Không có constraint SQL mới để bảo vệ trước thao tác quản trị trực tiếp ngoài ứng dụng.

## 9. Tích hợp A27/A28/A29 và frontend

| Yêu cầu | File chính | Nghiệm thu tự động |
|---|---|---|
| Validation/email/password/role | UserRules, UserRoles | A30UserRulesTests + regression A27 |
| Admin quản lý tài khoản | UserEndpoints, UserAccountService | A30AccountEndpointTests, A30RequestTests |
| MustChangePassword có đường đổi hợp lệ | AuthPasswordEndpoints, AuthSetup | HTTP luồng MCP → đổi → token mới, matrix A29 |
| Thu hồi JWT/cache | UserAccountService, A29 UserSessionValidator giữ nguyên | Token cũ sau đổi/reset/khóa/đổi role → 401 |
| Last Admin/concurrency | AccountWriteCoordinator, UserAccountService | A30ConcurrencyTests; SQL opt-in NOT RUN |
| Bảo toàn lịch sử/schema | Không sửa Infrastructure | Test lịch sử InMemory, full migration/model tests; SQL trigger NOT VERIFIED |
| Không lộ credential/lỗi | AccountApiSupport, rate policy | A30SecurityTests, A30RequestTests |

A27 không đổi schema; A28 giữ login/claims/DTO/session; A29 giữ fail-closed và các endpoint cũ. Matrix tăng 82 → 89 endpoint, kiểm tra thêm sáu route Admin và change-password SignedIn, giữ kỳ vọng cho endpoint cũ. Test A28 từng xác nhận A30 chưa có được đổi sang xác nhận endpoint A30 có bảo vệ, đúng phạm vi chức năng mới.

B27 đã có trong main nền, không sửa Web hoặc worktree frontend. Hợp đồng cho B27/B29:

1. Login hoặc `/me` có `mustChangePassword=true`: hiển thị luồng đổi, không tải nghiệp vụ. 403 `password_change_required` nay có thêm cờ mustChangePassword nhưng giữ code cũ.
2. POST change-password bằng token hạn chế; thành công trả token/expiresAt/user giống dạng LoginResponse. Lưu **token mới** trước request tiếp theo, cập nhật user/mustChangePassword=false; token cũ trả 401.
3. 401 do reset/khóa/đổi role/stamp: xóa phiên cũ và yêu cầu đăng nhập lại. 403 thiếu quyền không được coi là sai mật khẩu. 400 hiển thị errors theo field; 409 tải lại danh sách/version; 429 dùng Retry-After; 503 cho phép thử lại có kiểm tra trạng thái.
4. UI quản trị tương lai dùng version/If-Match, xử lý phân trang qua header, hiển thị temporaryPassword đúng lần trả về và không lưu lâu dài. Có thể bỏ password khi create để backend sinh.

Chưa thực hiện UI đổi mật khẩu/quản lý tài khoản hoặc nghiệm thu trình duyệt B27 với backend A30. B28/B29/B43 tiếp tục phụ trách guard/menu/UI/privacy theo nhiệm vụ của họ; A30 không làm thay.

## 10. SQL Server và dữ liệu thật

Không thực hiện SELECT, DML, DDL hoặc mở connection tới SQL Server thật trong A30. Không chạy `dotnet ef database update`, không tạo/xóa catalog, không reset IDENTITY, không chạy cleanup A29, không sửa Users/ApplicationEvents, không tắt trigger. Không thay connection string hoặc User Secrets để né lỗi.

Tình trạng **database hiện tại: NOT VERIFIED trong A30**. Báo cáo A29 đã ghi nhận Users/trigger sau hậu kiểm, nhưng đó là bằng chứng lịch sử, không phải kiểm tra mới. A30 không có migration mới; yêu cầu nền vẫn là schema A27 `20261119110208_AddUsers` và các migration đã có của main. Không khẳng định migration đã áp dụng trên môi trường đích hiện nay khi chưa truy vấn.

Người dùng có thể kiểm tra read-only trên đúng catalog sau khi tự xác minh server/catalog; không cần hiển thị hash, stamp hoặc thông tin đăng nhập:

```sql
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS CatalogName;
SELECT MigrationId FROM dbo.__EFMigrationsHistory
WHERE MigrationId = N'20261119110208_AddUsers';
SELECT COL_LENGTH(N'dbo.Users', N'SecurityStamp') AS SecurityStampBytes,
       COL_LENGTH(N'dbo.Users', N'MustChangePassword') AS MustChangePasswordBytes,
       COL_LENGTH(N'dbo.ApplicationEvents', N'UserId') AS EventUserIdBytes;
SELECT name, is_disabled FROM sys.triggers
WHERE name = N'TR_ApplicationEvents_NoChange';
```

Nếu nền A27 thiếu, dừng sử dụng API A30 trên môi trường đó; xem script migration của A27 và sao lưu/phê duyệt riêng trước khi người dùng tự cập nhật. Không cần/không tạo migration A30 chỉ để thêm service/endpoint. Migrations/snapshot, BIN2 CHECK, `UseSqlOutputClause(false)`, `CK_ApplicationEvents_Actor`, trigger/FK/index A35 không có diff.

Test SQL A30 đã chuẩn bị nhưng **NOT RUN**, yêu cầu đồng thời:

- Người dùng phê duyệt riêng và cung cấp catalog **đã tạo sẵn, rỗng**, tên `SmartCv_A30_Test_` + 32 ký tự GUID hex chữ thường.
- `SMARTCV_A30_SQL_APPROVED=1` và `SMARTCV_A30_TEST_SQL_CONNECTION` chỉ trong process test, không ghi Git.
- Guard từ chối SmartCvDb, master, tên không đúng mẫu, AttachDBFilename; kiểm tra sys.tables = 0 trước migrate. Test chỉ áp dụng schema lên catalog được cung cấp, không tạo/drop database, không xóa test catalog cũ.
- Test để nguyên dữ liệu giả sau chạy để review; không có cleanup. Cần catalog rỗng khác cho một lượt chạy lại.

Chỉ sau các điều kiện đó, người dùng có thể chạy:

```powershell
dotnet test SmartCv.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~A30SqlServerTests -- xUnit.MaxParallelThreads=4
```

Test này kiểm tra hai API instance độc lập, race last Admin/trùng email, reset JWT và bảo toàn lịch sử/trigger A35 trên catalog cô lập. **Không coi test InMemory là bằng chứng khóa/transaction/unique index/trigger thật của SQL Server.**

## 11. Kiểm thử và nghiệm thu

Đã kiểm tra ApplicationDatabaseGuard, ApiFactory/RegressionIdentity/A28TestSupport và opt-in trước baseline. Không có biến SQL/inspection opt-in bật; mọi lượt regression giữ tối đa 4 thread xUnit. SDK .NET 10.0.403.

| Lượt | PASS | FAIL | SKIP | Tổng / kết quả |
|---|---:|---:|---:|---|
| Baseline restore | — | — | — | PASS |
| Baseline Release build | — | — | — | PASS, 0 warning / 0 error |
| Baseline full | 3078 | 0 | 134 | 3212; 11 phút 12 giây |
| Restore cuối | — | — | — | PASS |
| Release build cuối | — | — | — | PASS, 0 warning / 0 error; 9,19 giây |
| Focused cuối A30 + matrix A29/route A28 | 75 | 0 | 1 | 76; 25 giây |
| Full regression lần đầu sau code | 3150 | 1 | 135 | 3286; 7 phút 33 giây; lỗi worker timing cũ |
| Worker chạy riêng, 3 lượt | 3 | 0 | 0 | Mỗi lượt 1 test PASS |
| **Full regression bàn giao** | **3151** | **0** | **135** | **3286; 5 phút 58 giây** |
| SQL A30 thực tế | — | — | 1 | **NOT RUN** |
| Browser B27/đăng nhập Admin thật | — | — | — | **NOT VERIFIED**, không thao tác dữ liệu thật |

Lỗi worker: `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop` đếm 6 ngoài khoảng 2–5 sau khoảng đợi 3,5 giây. Cùng lỗi đã ghi trong báo cáo A27/A28 (xem docs/A28-implementation-report.md, baseline). File worker test không sửa, blob giống SHA nền `f5d068578d08ab003cd9d8b685a50c722cc68efb`; không sửa assertion, tăng timeout hoặc skip. Đã chạy lại riêng 3 lần và full đều PASS; giới hạn timing vẫn tồn tại, không tuyên bố đã sửa flaky test ngoài phạm vi A30.

135 skip gồm **125 SQL integration**, **1 read-only DB inspection**, **5 thiếu tessdata**, **4 demo ZIP**. Tăng đúng một SQL opt-in A30 so với baseline; không tăng skip để che lỗi. Các migration/model, A27/A28/A29, A35/A39/Workflow có trong full suite; những test SQL bên trong vẫn NOT RUN theo opt-in, không được báo PASS SQL thật.

74 trường hợp test mới của A30: 73 PASS + 1 SQL NOT RUN. Focused 76 còn gồm hai kiểm tra route/policy kế thừa. Bao phủ:

- Chuẩn hóa email/tên, role phân biệt hoa thường, password và temporary password; giới hạn độ dài.
- Admin tạo đúng một HR/Admin theo quyền; email trùng kể cả locked không nâng quyền/mở khóa; hash xác minh được, không trả hash/stamp.
- Anonymous/HR/MustChangePassword bị chặn ở API quản trị; MCP vẫn đổi mật khẩu được và bị chặn khỏi nghiệp vụ.
- Sai currentPassword, newPassword yếu/trùng, hash hỏng: lỗi an toàn, không ghi trạng thái bảo mật.
- Reset HR, khóa/mở idempotent, role change, JWT cũ 401; self lock/demotion và reset Admin bị chặn.
- Hai create cùng normalized email; hai reset cùng version; lock/demote hai Admin; password đua reset/lock; actor mất quyền khi đang chờ; stale version trả 409.
- JSON lỗi/trùng/null, body quá lớn, sai content type/charset; filter/paging sai; rate limit 429; DB fault 503 và giải phóng gate.
- No-store, log redaction, lịch sử ứng tuyển không đổi trên InMemory, Admin giả ID 1 không thay đổi qua create/seed restart.
- **HTTP Kestrel loopback thật, DB InMemory:** login Admin giả → tạo HR → login bằng password tạm → 403 nghiệp vụ → đổi password → JWT cũ 401 → JWT mới gọi nghiệp vụ 200. Không phải API production/SQL thật.

Bằng chứng cục bộ (ignored):

- `artifacts/a30/baseline/a30-baseline.trx`, baseline-restore/build/test.log.
- `artifacts/a30/build-final.log`, restore-final.log.
- `artifacts/a30/focused-final/a30-focused-final.trx`.
- `artifacts/a30/regression-final/a30-regression-final.trx` (giữ cả failure).
- `artifacts/a30/worker-recheck/worker-1.trx`, worker-2.trx, worker-3.trx.
- `artifacts/a30/regression-delivery/a30-regression-delivery.trx` và regression-delivery.log.

Lệnh chạy từ worktree A30:

```powershell
dotnet restore SmartCv.slnx
dotnet build SmartCv.slnx -c Release --no-restore
dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4
```

Focused và thử HTTP an toàn trên dữ liệu giả, không cần SQL/User Secrets thật:

```powershell
dotnet test SmartCv.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~A30|FullyQualifiedName~A29AuthorizationTests.Endpoint_matrix|FullyQualifiedName~A29_and_A30" -- xUnit.MaxParallelThreads=4
dotnet test SmartCv.slnx -c Release --no-build --no-restore --filter FullyQualifiedName~Real_Kestrel_HTTP_create_login_force_change_and_new_token_work_without_real_accounts_or_SQL -- xUnit.MaxParallelThreads=4
```

Không bật các opt-in SQL/inspection cũ trong các lệnh regression trên. Để nghiệm thu thủ công qua UI/API triển khai, chỉ dùng tài khoản giả trên môi trường cô lập được phê duyệt; không đổi/reset/khóa Admin thật.

## 12. Git review và danh sách file chính xác

**23 file: 14 Added, 9 Modified, 0 Deleted/Renamed.** Không có thay đổi ngoài Core/Api/tests/docs. Không sửa csproj, dependency, appsettings, Web, Infrastructure hoặc migrations. Các file A30 không nằm trong nhánh/worktree B27.

| A/M | File |
|---|---|
| A | docs/A30-implementation-report.md |
| A | src/SmartCv.Api/Auth/AccountApiSupport.cs |
| A | src/SmartCv.Api/Auth/AccountCredentialRateLimitPolicy.cs |
| A | src/SmartCv.Api/Auth/AccountWriteCoordinator.cs |
| M | src/SmartCv.Api/Auth/AuthMapping.cs |
| M | src/SmartCv.Api/Auth/AuthSetup.cs |
| M | src/SmartCv.Api/Auth/ProblemAuthorizationResultHandler.cs |
| A | src/SmartCv.Api/Auth/UserAccountService.cs |
| M | src/SmartCv.Api/Endpoints/AuthEndpoints.cs |
| A | src/SmartCv.Api/Endpoints/AuthPasswordEndpoints.cs |
| A | src/SmartCv.Api/Endpoints/UserEndpoints.cs |
| M | src/SmartCv.Api/SmartCvApi.cs |
| M | src/SmartCv.Core/Auth/UserRoles.cs |
| A | src/SmartCv.Core/Auth/UserRules.cs |
| M | src/SmartCv.Core/Contracts/AuthContracts.cs |
| M | tests/SmartCv.Tests/Auth/A28AuthEndpointTests.cs |
| M | tests/SmartCv.Tests/Auth/A29AuthorizationTests.cs |
| A | tests/SmartCv.Tests/Auth/A30AccountEndpointTests.cs |
| A | tests/SmartCv.Tests/Auth/A30ConcurrencyTests.cs |
| A | tests/SmartCv.Tests/Auth/A30RequestTests.cs |
| A | tests/SmartCv.Tests/Auth/A30SecurityTests.cs |
| A | tests/SmartCv.Tests/Auth/A30SqlServerTests.cs |
| A | tests/SmartCv.Tests/Auth/A30UserRulesTests.cs |

Đã kiểm tra staged/unstaged/untracked, diff và phạm vi. `git diff --check` PASS; quét cả file untracked không có trailing whitespace/conflict marker. Không phát hiện private key/PAT/JWT thật trong file thay đổi; đã review các chỗ password/token/log: chỉ tên field, test tổng hợp hoặc credential trong bộ nhớ. Không đưa User Secrets, .env, CV, SQL dump, cleanup script, bin/obj/log/TRX vào manifest. `artifacts/`, bin/obj tiếp tục ignored, không sửa .gitignore. Git có thông báo LF → CRLF do cấu hình hiện có, không có lỗi whitespace và không đổi cấu hình line ending toàn cục.

Đối chiếu manifest hai chiều với status: 23 dòng A/M, không thiếu/thừa/trùng, không file ignored, report có mặt; SHA handoff khớp HEAD nền 40 ký tự. Staged diff rỗng. Repository gốc vẫn main sạch; A30 vẫn HEAD nền và các thay đổi chưa stage, đúng yêu cầu chờ review.

Khác biệt có chủ đích với ZIP:

- Giữ toàn bộ auth/endpoint/schema mới đã merge; không copy đè Program/AuthSetup/SmartCvApi cũ. Tái sử dụng A29 policies, không thêm PasswordChangedRequirement trùng chức năng.
- Tách service/coordinator/error reader/credential rate limiter, thay SELECT rồi UPDATE không điều kiện bằng transaction, applock, CAS và revalidation actor.
- Thêm version/ETag tùy chọn và paging/filter có giới hạn; giữ list array và wire format của login/change-password.
- Reset chỉ HR; từ chối reset mọi Admin để tránh reset tài khoản quản trị ngoài ý muốn. Guide cho thao tác rộng hơn nhưng yêu cầu bảo vệ Admin hiện tại ưu tiên cao hơn.
- Không thay stamp khi chỉ đổi tên; thay role/security mới thu hồi token. Không tự reset password khi mở khóa.
- Temporary password CSPRNG/PasswordHasher thật cho test; không dùng hash giả cố định để chứng minh login. Giữ sample UserRules surface, thêm giới hạn generator/control characters.
- Không chạy các công thức SQL thử/sửa Admin hoặc cleanup trong môi trường thật. SQL test chỉ opt-in catalog rỗng được cấp sẵn, không tự tạo/drop.

## 13. Kết luận và điều kiện bàn giao

**CONDITIONAL — code sẵn sàng review, build và regression cuối PASS; nghiệm thu SQL Server thực tế còn thiếu.** Đã thực hiện đúng A30, không có nhiệm vụ A307 riêng trong ZIP. Có đủ 7 API, hợp đồng backend cho B27/B29, bảo vệ self/last Admin, hashing, đổi stamp, kiểm tra phiên mới và xử lý lỗi. Không làm UI hoặc chức năng ngoài A30.

Không có thay đổi dữ liệu thật/Admin thật trong lượt này. Không thể tuyên bố đã thử password/lock/reset trên SQL thật hoặc hoàn tất browser integration. Các rủi ro/điều kiện cần xác nhận trước vận hành:

1. Chạy SQL A30 trên catalog rỗng cô lập đã được người dùng phê duyệt để xác nhận applock/Serializable/CAS/unique và A35 trigger bằng SQL thật. Chưa kiểm chứng quyền thực thi applock/transaction của login triển khai.
2. Kiểm tra schema nền A27 và cấu hình môi trường đích bằng read-only trước sử dụng. Không tự áp dụng migration lên SmartCvDb.
3. B27/B29 cần luồng đổi password, lưu token mới và version/paging; chỉ hợp đồng backend đã sẵn sàng, chưa nghiệm thu UI.
4. Applock serialize toàn bộ ghi tài khoản để giữ bất biến; phù hợp tác vụ quản trị ít, cần đo tải nếu tăng quy mô. Rate limit chưa phân tán; audit phụ thuộc log sink.
5. Flaky timing worker cũ vẫn có thể tái xuất hiện ở suite nặng dù lượt bàn giao PASS; không sửa/skip ngoài A30.

Người dùng review báo cáo, diff và manifest rồi tự quyết định stage/commit/push. **Agent chưa stage, commit, push, merge, rebase hoặc tạo PR; không thực hiện thêm thao tác sau bàn giao.**
