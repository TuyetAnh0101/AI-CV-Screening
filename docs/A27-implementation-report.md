# A27 — Users table & first Admin seeding

Ngày thực hiện: 19/11/2026 (Asia/Saigon). Phạm vi: backend A27, chờ người dùng review và nghiệm thu SQL/User Secrets thực tế. Không có endpoint đăng nhập ở nhiệm vụ này.

## 1. Nguồn tài liệu và kiểm kê ZIP

- Đúng file người dùng chỉ định: `C:\TTCN\A27_B43_SmartCv.zip`.
- SHA-256: `21C9B362E408ABC020F2A52019619AA5A8C3499904D8E3DDFC30121FDDD888F2`.
- Đã kiểm kê **134 file, 100 directory entries**; riêng A27 có **11 file**. Inventory cục bộ: `artifacts/a27/zip-inventory.tsv`.
- **Không có ảnh/mockup hoặc file thiết kế trong ZIP**; không có ảnh để xem.
- Đã đọc đầy đủ guide 1.019 dòng, gồm phần chung, thứ tự triển khai/gộp, A27, các quyết định của tác giả, lỗi thường gặp và ranh giới A28–A30/B27–B43.

Các file đã đọc (đường dẫn tương đối trong `A27_B43_SmartCv/`):

```text
HUONG_DAN_A27_A28_A29_A30_B27_B28_B29_B43.md
A27/src/SmartCv.Core/Entities/User.cs
A27/src/SmartCv.Core/Entities/ApplicationEvent.cs
A27/src/SmartCv.Core/Options/AuthOptions.cs
A27/src/SmartCv.Core/Auth/PasswordRules.cs
A27/src/SmartCv.Core/Auth/UserRoles.cs
A27/src/SmartCv.Core/Auth/SeedAdminPlan.cs
A27/src/SmartCv.Infrastructure/Data/Configurations/ApplicationEventConfiguration.cs
A27/src/SmartCv.Infrastructure/Data/Configurations/UserConfiguration.cs
A27/src/SmartCv.Api/Auth/AdminSeeder.cs
A27/tests/SmartCv.Tests/Auth/PasswordRulesTests.cs
A27/tests/SmartCv.Tests/Auth/SeedAdminPlanTests.cs
snippets/Program.Api.A27-A30.cs.txt
database/a27_a30_kiem_tra_tai_khoan.sql
```

Chỉ áp dụng phần A27 của snippet. Không chép đè source hiện tại bằng file ZIP.

## 2. Git và source nền

| Thuộc tính | Giá trị |
|---|---|
| Repository duy nhất | `C:\TTCN\project\AI-CV-Screening` |
| Origin | `https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Nhánh | `feature/a27-users-table` |
| SHA nền đủ 40 ký tự | `7fb0f184e600bbbb32064e990db78430c047c128` |
| Commit nền | `feat(B25): implement evaluation sets UI and refresh SCApp design (#82)` |

Đã kiểm tra status, nhánh, remotes, log, diff thường/cached, untracked, unmerged, worktrees và các dấu hiệu merge/rebase/cherry-pick/revert/sequencer. Working tree ban đầu sạch; nhánh A27 chưa tồn tại. Có metadata worktree cũ của các nhiệm vụ khác, được giữ nguyên; không tạo clone/worktree/project khác.

Đã `fetch origin --prune`, sau đó `switch --no-track -c feature/a27-users-table origin/main`. Dùng `git -c safe.directory=...` riêng từng lệnh do tài khoản sandbox khác chủ repository; không sửa cấu hình Git toàn cục. Fetch/switch cần quyền ngoài sandbox để ghi `.git`, đã thực hiện thành công trong phạm vi được yêu cầu.

Không reset/stash/clean/restore; không stage/commit/push/merge/rebase/cherry-pick/PR.

## 3. Baseline trước triển khai

Source/assembly baseline được build và khởi chạy test trước khi thay đổi implementation.

| Kiểm tra | Kết quả |
|---|---|
| .NET SDK | 10.0.403, đúng `global.json` |
| `dotnet restore SmartCv.slnx` | Thành công khi chạy có quyền mạng; lần sandbox đầu lỗi NuGet TLS NU1301 |
| `dotnet build SmartCv.slnx --no-restore` | Debug bị khóa DLL bởi API/Web đang chạy: 6 errors MSB3027/MSB3021, 30 warnings retry; không dừng ứng dụng người dùng |
| `dotnet build SmartCv.slnx -c Release --no-restore` | **0 errors, 0 warnings** |
| Full tests Release baseline | **2.819 pass, 0 fail, 122 skip**, tổng 2.941 |

122 skip baseline gồm **117 SQL opt-in** và **5 demo ZIP/resource tests**. Không coi skip là pass. Lần thử Debug trong sandbox gặp lỗi quyền Windows EventLog và đã hủy; không dùng lần này làm baseline. Release tests chạy với quyền phù hợp để giữ nguyên logging của ứng dụng.

Log/TRX: `artifacts/a27/baseline-restore.log`, `baseline-build.log`, `baseline-release-build.log`, `baseline-release-test.log`, `baseline-release/baseline.trx`.

## 4. Users: entity và schema

| Cột | Kiểu SQL Server | Null/default |
|---|---|---|
| UserId | INT IDENTITY(1,1) | NOT NULL, PK_Users |
| Email | NVARCHAR(254) | NOT NULL, unique index UQ_Users_Email |
| FullName | NVARCHAR(150) | NOT NULL |
| PasswordHash | VARCHAR(500) | NOT NULL |
| Role | NVARCHAR(20) | NOT NULL, CK_Users_Role |
| IsActive | BIT | NOT NULL; entity mặc định true |
| CreatedAt | DATETIME2 | NOT NULL, SYSUTCDATETIME(); seeder ghi DateTime.UtcNow |
| LastLoginAt | DATETIME2 | NULL |
| MustChangePassword | BIT | NOT NULL; Admin tự cấu hình có giá trị false |
| SecurityStamp | VARCHAR(64) | NOT NULL; 32 byte RNG, biểu diễn hex 64 ký tự |

Setter `User.Email` trim và chuyển `ToLowerInvariant()` trước khi lưu qua entity. SeedAdminPlan còn kiểm tra định dạng/độ dài bằng `CandidateRules.NormalizeEmail`. Họ tên dùng `CandidateRules.NormalizeName` để gộp khoảng trắng.

`CK_Users_Role` chỉ nhận đúng `Admin`/`HR`: dùng BIN2 và kiểm tra độ dài byte để chặn cả sai hoa/thường lẫn khoảng trắng cuối mà so sánh SQL thường bỏ qua. Không thêm Manager. Không có API xóa tài khoản; quy tắc sử dụng là khóa bằng IsActive. FK chặn xóa tài khoản đã có lịch sử, không cascade.

Thêm `DbSet<User> Users => Set<User>()` vào DbContext hiện có; UserConfiguration được nạp bởi `ApplyConfigurationsFromAssembly`. Không tạo DbContext thứ hai, không thay đổi package references.

## 5. ApplicationEvents và bảo toàn A35

Chỉ thêm `int? UserId`, navigation `User? User`, index `IX_ApplicationEvents_UserId` và FK `FK_ApplicationEvents_Users` tới Users.UserId, optional, `DeleteBehavior.NoAction`.

Giữ nguyên toàn bộ cấu hình cũ:

- BIN2 trong CHECK FromStage/ToStage/RejectReason.
- CHECK RejectReason_Stage và `CK_ApplicationEvents_Actor`.
- `TR_ApplicationEvents_NoChange`, `UseSqlOutputClause(false)`.
- PK, identity, FK Applications với Restrict, index ApplicationId/CreatedAt descending, mọi giới hạn cột và UTC default.
- Chặn update/delete lịch sử trong SmartCvDbContext, cơ chế StageVersion hiện tại.

Không backfill UserId, không suy đoán từ Actor. Migration chỉ ALTER thêm cột nullable, nên lịch sử cũ có UserId NULL. Không UPDATE/DELETE lịch sử hoặc thay Actor/FromStage/ToStage/RejectReason/Note/CreatedAt. Gắn UserId từ token là A29, chưa triển khai.

## 6. Migration AddUsers và toàn bộ thay đổi snapshot

Migration: `20261119110208_AddUsers` (EF Core sinh bằng Infrastructure + startup project Api, configuration Release).

Up có đúng **5 operations**:

1. AddColumn ApplicationEvents.UserId INT NULL, không default/backfill.
2. CreateTable Users, 10 cột, PK_Users và CK_Users_Role.
3. CreateIndex IX_ApplicationEvents_UserId.
4. Create unique index UQ_Users_Email.
5. AddForeignKey FK_ApplicationEvents_Users, SQL NO ACTION.

Down có đúng **4 operations**: drop FK mới, drop Users, drop index UserId, drop cột UserId. Down sẽ mất dữ liệu tài khoản và liên kết A27 nếu áp dụng; chỉ dùng sau kế hoạch rollback đã duyệt. Không tự chạy Down trên database người dùng.

Không drop/recreate ApplicationEvents hoặc trigger; không sửa CHECK cũ; không đụng schema Cvs/Applications/EvaluationRuns/EvaluationSets hay dữ liệu ứng viên/kết quả chấm. Không sửa migration lịch sử.

Snapshot chỉ thêm **76 dòng**, không xóa dòng:

- ApplicationEvent: property UserId INT nullable và index UserId.
- Entity User: đủ 10 cột, identity, required/nullable/max length/unicode, UTC default, PK, email unique index, Role CHECK.
- ApplicationEvent: optional User relationship, NoAction, constraint name và navigation User.

Không có thay đổi ProductVersion hoặc metadata entity cũ. Designer của migration mới ghi toàn bộ model hiện tại theo cơ chế EF thông thường.

Đã sinh, đọc và kiểm tra script Up idempotent/Down, không thực thi:

```text
artifacts/a27/AddUsers-up-idempotent.sql
artifacts/a27/AddUsers-down.sql
```

`dotnet ef migrations has-pending-model-changes`: **No changes**. EF CLI hiện 10.0.14, runtime package đã resolve 10.0.15 theo wildcard có sẵn; CLI có cảnh báo lệch patch nhưng sinh script/model đúng, build không có warning. Không tự đổi tool/package ngoài A27.

## 7. SeedAdminPlan và AdminSeeder

| Trường hợp | Hành vi |
|---|---|
| Có Admin hoạt động | Trả ngay; không kiểm tra lại cấu hình seed, không đổi hash/stamp/tên/ngày tạo |
| Thiếu SeedAdmin hoặc thiếu Email/Password | Warning rõ, không tạo mặc định; API tiếp tục nếu schema/các cấu hình khác hợp lệ |
| Email sai/vượt giới hạn, password sai, tên quá dài | InvalidOperationException với quy tắc lỗi, dừng startup; không ghi giá trị cấu hình |
| Thiếu/trống FullName | Dùng “Quản trị hệ thống” đúng code mẫu ZIP; FullName đầu ra luôn bắt buộc |
| Email thuộc HR/tài khoản khóa | Warning xung đột an toàn, không tạo/nâng quyền/mở khóa |
| Cấu hình hợp lệ | Một Admin active, hash bằng PasswordHasher<User>, stamp ngẫu nhiên, CreatedAt UTC, LastLoginAt NULL |
| Khởi động lại | Không tạo trùng, không thay tài khoản cũ |

SeedAdminDecision không giữ password. Log warning/error không nội suy email/họ tên/password; log thành công chỉ có UserId và ghi sau commit SQL. Không lưu mật khẩu trong entity, không dùng mã hóa có thể giải ngược.

Program.cs chỉ thêm usings, bind AuthOptions, đăng ký scoped IPasswordHasher<User>/PasswordHasher<User>, và `await app.SeedAdminAsync()` sau Build. Giữ worker/scoring/Gemini/OCR/rate limiting/privacy/middleware/endpoints/Development gate/health/logging/OpenAPI như nền.

## 8. Concurrency và giới hạn

Trên SQL Server, seeder mở transaction ReadCommitted, lấy `sys.sp_getapplock` Exclusive với resource cố định `SCApp.A27.FirstAdmin`, owner Transaction, timeout 15 giây. Kiểm tra Admin, kiểm tra email và insert đều bên trong khóa. Hai seeders cùng catalog, kể cả cấu hình email khác nhau, dùng chung resource; tiến trình sau phải kiểm tra lại sau commit của tiến trình trước. Unique email là lớp bảo vệ bổ sung.

Khóa được giải phóng khi commit/rollback. Mã trả về âm làm startup dừng bằng lỗi an toàn; không âm thầm chạy seed thiếu khóa. Transaction được tạo bên trong EF execution strategy; khi SaveChanges lỗi, entity vừa thêm được detach trước khi rethrow để tránh Added entity tồn đọng trong lần retry.

Giới hạn: khóa chỉ phối hợp những bên dùng cùng resource trong cùng catalog; không thay quy tắc quản lý tài khoản A30 và không cấm Admin được tạo bằng thao tác SQL bên ngoài. InMemory chỉ dùng cho unit tests, không cung cấp bảo đảm concurrency SQL. Chưa xác nhận cơ chế khóa bằng SQL thực tế trong phiên này.

Tham khảo cơ chế khóa: [Microsoft sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql).

## 9. Tests A27

Focused A27 cuối: **53 pass, 0 fail, 4 skip SQL**, tổng 57 trường hợp được runner báo cáo.

- PasswordRules: valid/invalid, null/rỗng, thiếu chữ/số, min/max boundaries, khoảng trắng đầu/cuối, trùng email không phân biệt hoa/thường.
- SeedAdminPlan: normalized email/name, email sai/quá dài, tên thiếu mặc định/tên quá dài, thiếu cấu hình/thiếu credential, Admin ưu tiên trước config, email trùng, đúng hai role, thông báo không chứa password.
- Seeder InMemory: thiếu config warning, active Admin giữ nguyên, không nâng quyền HR/không mở khóa, invalid config không ghi dữ liệu, Admin hợp lệ/hash đúng/sai password, startup lại trên context mới giữ nguyên tài khoản, stamp ngẫu nhiên và UTC. Có test Program thật qua WebApplicationFactory bind options/DI/seeding trước HTTP health, với dữ liệu synthetic trong InMemory.
- Model/migration: 10 cột đúng contract, optional FK/NoAction, toàn bộ CHECK cũ không đổi, trigger/OUTPUT an toàn, Up/Down allowlist, actual model diff và snapshot khớp, script không có thay đổi ngoài A27, 6 cases kiểm tra guard SQL test.

SQL opt-in có 4 test methods skipped (concurrency theory chứa 2 data cases khi được bật):

1. Upgrade/Down/reapply giữ fingerprint lịch sử và các bảng cũ; UserId cũ NULL; EF insert tương thích trigger; trigger tiếp tục chặn update/delete.
2. Hai context/kết nối độc lập seeding đồng thời với cùng/khác email, có barrier để chồng lấn transaction, tạo đúng một Admin; restart không đổi hash/stamp/ngày tạo.
3. Lỗi hashing rollback transaction và giải phóng khóa, lần sau seed được.
4. SQL CHECK role chính xác, email unique, FK hợp lệ và không cascade xóa lịch sử.

Test không cần secrets thật; password hợp lệ được tạo synthetic trong RAM. Không có mật khẩu tài khoản thật hoặc file chứa mật khẩu trong repository.

## 10. Regression và điều chỉnh test hạ tầng

Build Release cuối: **0 errors, 0 warnings**.

| Lượt kiểm tra | Pass | Fail | Skip | Tổng |
|---|---:|---:|---:|---:|
| Full Release baseline | 2.819 | 0 | 122 | 2.941 |
| Focused A27 cuối | 53 | 0 | 4 | 57 |
| Full Release lần 1, parallel mặc định | 2.871 | 1 | 126 | 2.998 |
| Test worker lỗi, chạy lại riêng | 1 | 0 | 0 | 1 |
| Full Release lần 2, MaxParallelThreads=4 | **2.872** | **0** | **126** | **2.998** |
| Migration/model/A35/A39/Workflow V2 riêng | 153 | 0 | 29 | 182 |

126 skip cuối gồm **121 SQL opt-in** (117 cũ + 4 A27) và **5 demo/resource tests** thiếu bộ demo ZIP. Bộ SQL A27 có một theory 2 data cases; khi skip, runner báo một test cho theory. Không coi các skip là bằng chứng SQL đã pass.

Full lần 1 có một lỗi timing trong test cũ `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`: đếm 6, kỳ vọng 2–5. Duration từ TRX: baseline pass 4,517 giây; lần lỗi 5,599 giây; chạy riêng pass 4,751 giây. Test dùng `Task.Delay(3500)` và đếm poll theo thời gian thực nên nhạy với scheduling. Worker và test đó không có diff so với SHA nền; baseline không có lỗi này. Lỗi không lặp lại khi chạy riêng và trong full suite giới hạn 4 luồng; đây là bằng chứng timing không ổn định, **chưa chứng minh được lỗi đã có từ baseline dưới cùng tải**. Giữ đây là rủi ro kiểm thử còn lại, không khẳng định full parallel mặc định luôn xanh và không sửa kỳ vọng để ép xanh.

Lệnh full chạy lại (không tắt/bỏ test, không sửa cấu hình runner trong Git):

```powershell
dotnet test SmartCv.slnx -c Release --no-build --no-restore -- xUnit.MaxParallelThreads=4
```

[xUnit RunSettings](https://xunit.net/docs/config-runsettings) mô tả tùy chọn số luồng này. Thời gian full lần 1 khoảng 6 phút 11 giây; lần 2 khoảng 6 phút 1 giây. Các kiểm tra chức năng/model được chạy đều không còn lỗi trong lượt cuối; SQL và demo skip vẫn chưa được xác nhận.

Log/TRX: `artifacts/a27/full-regression.log`, `regression/full-regression.trx` (giữ nguyên lần lỗi), `worker-timing.log`, `worker-timing/worker-timing.trx`, `full-regression-repeat.log`, `regression-repeat/full-regression-repeat.trx`, `schema-regression.log`, `schema-regression/schema-regression.trx`, `focused-test.log`, `focused/a27-focused.trx`.

Các thay đổi test cũ có lý do trực tiếp từ A27:

- Hai assertion migration order vẫn giữ đủ thứ tự cũ, nối thêm AddUsers.
- Assertion FK A35 chọn đúng FK Applications; FK Users mới có assertion riêng, không bỏ kiểm tra Restrict.
- JobEndpointsTests và TestHostLifecycleTests dùng DbContext InMemory để startup mới không truy cập SQL giả; giữ nguyên assertion HTTP/lifecycle/options.
- JobCrudSqlFixture: migration-only tests vẫn ở A20/A21; synthetic history được insert bằng các cột A35 tồn tại lúc đó. Riêng HTTP test nâng schema trong catalog GUID đã guard tới hiện tại trước khi startup đọc Users. Không sửa SQL expectations cũ hoặc migration cũ. Phần fixture SQL này chưa được chạy thực tế vì không có opt-in connection.

## 11. SQL thực tế và phần nghiệm thu chưa xác nhận

**Chưa chạy SQL integration thực tế.** Không có biến connection test opt-in được chỉ định; không tự dùng connection của SmartCvDb để thay thế. Không tạo/xóa database, không chạy `dotnet ef database update`, không đổi connection string.

Chưa tự cấu hình/đọc mật khẩu User Secrets, chưa chạy API seeding vào SQL bằng secrets thật. Các kết quả tạo Admin/hash/idempotence đã xác nhận bằng InMemory và Program test synthetic; tính bảo toàn dữ liệu/trigger/concurrency SQL hiện có model/script tests và bộ integration opt-in, chưa có chứng cứ chạy SQL. Không tuyên bố đã đăng nhập Admin.

## 12. So sánh từng mẫu A27 với main và khác biệt có chủ đích

| Mẫu ZIP | Main nền / triển khai |
|---|---|
| User.cs | Chưa có; thêm 10 thuộc tính theo mẫu, normalization trong setter và RNG 32 byte thay Guid stamp |
| ApplicationEvent.cs | Main có bản A35; chỉ thêm UserId/User, giữ các cột lịch sử |
| AuthOptions.cs | Chưa có; chỉ SeedAdmin, không thêm LoginAttemptsPerMinute của A28 |
| PasswordRules.cs | Chưa có; giữ quy tắc 8–128/chữ/số/email/whitespace, không đưa khẳng định tiêu chuẩn NIST từ comment vào sản phẩm |
| UserRoles.cs | Chưa có; chỉ constants và validation hai vai trò; bỏ helpers hiển thị/chức năng UI chưa dùng |
| SeedAdminPlan.cs | Chưa có; tái dùng CandidateRules hiện tại, giữ default tên/missing-config theo mẫu, lỗi không echo giá trị cấu hình |
| UserConfiguration.cs | Chưa có; đúng schema, tăng độ chính xác Role CHECK với BIN2/độ dài |
| ApplicationEventConfiguration.cs | Main có BIN2, Actor CHECK, OUTPUT(false) mà ZIP thiếu; giữ nguyên tất cả, chỉ thêm FK/index mới |
| AdminSeeder.cs | Chưa có; thêm transaction application lock, early return Admin, log chỉ ID sau commit, overload dùng lại trong test |
| PasswordRulesTests.cs | Chưa có; mở rộng cases min/max/null/whitespace |
| SeedAdminPlanTests.cs | Chưa có; giữ default FullName và missing config, mở rộng schema/seeding/security/Program/SQL tests trong các file riêng |

Snippet Program chỉ áp dụng A27. SQL mẫu có cả A29/A30; thay bằng file `database/a27_kiem_tra_tai_khoan.sql` chỉ đọc A27, không SELECT hash/stamp/raw CV, không có nhãn/login/phân quyền.

## 13. File A/M/D và metadata cục bộ

Danh sách chính xác được chèn ở phần status cuối báo cáo. Không có D. Báo cáo này, file SQL kiểm tra và cả hai migration files mới đều nằm trong manifest.

- `artifacts/a27-base-sha.txt`: đúng SHA nền 40 ký tự, lấy từ HEAD ngay sau khi tạo nhánh.
- `artifacts/a27-commit-manifest.tsv`: A/M, TAB thật, một file mỗi dòng, không trùng; đối chiếu hai chiều với status cuối.
- Cả hai metadata files, scripts/logs/TRX sinh ra trong artifacts đều đã bị ignore bởi quy tắc `artifacts/` có sẵn. Không thay .gitignore, không đưa artifacts vào commit.

## 14. Chức năng ngoài phạm vi và rủi ro còn lại

Không JWT/token service/login/me, không RequireAuthorization, không đổi cơ chế auth/Development gate, không gắn UserId từ token, không đổi mật khẩu/quản lý tài khoản, không sửa sidebar/CSS/Blazor/B25. Các phần đó thuộc A28/A29/A30/B27/B28/B29/B43.

Rủi ro còn lại cần nghiệm thu: SQL thực tế (migration + trigger + đồng thời), khởi động bằng User Secrets trên catalog được duyệt, và test worker cũ nhạy với scheduling khi chạy cả suite. User Secrets chỉ tự nạp ở Development; seeder cần schema Users tồn tại trước startup. Lỗi `Invalid object name 'Users'` phải xử lý bằng đúng migration/order trên catalog đã kiểm tra, không đổi connection sang database khác.

## 15. Hướng dẫn người dùng cấu hình secrets và kiểm thử an toàn

Chỉ thực hiện sau khi review thay đổi, xác định đúng server/catalog, sao lưu và phê duyệt migration. Các lệnh dưới đây **chưa được agent thực thi**.

### 15.1. Kiểm tra schema/migration trước startup

Api đã có UserSecretsId; không cần init lại. Kiểm tra đúng cấu hình connection được API sử dụng mà không in toàn bộ connection string/password; trong SSMS dùng `SELECT @@SERVERNAME, DB_NAME()` để đối chiếu catalog. Không dùng `dotnet user-secrets list` trong phiên chia sẻ vì sẽ in bí mật.

Kiểm tra script cục bộ `artifacts/a27/AddUsers-up-idempotent.sql`, đối chiếu nền đang áp dụng tới ScappWorkflowV2, rồi áp dụng theo quy trình đã duyệt. Hoặc, sau sao lưu và phê duyệt riêng của người dùng:

```powershell
dotnet ef database update AddUsers --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --configuration Release --no-build
```

Lệnh sử dụng cấu hình hiện tại; không chạy trước khi kiểm tra tên catalog. File kiểm tra chỉ đọc `database/a27_kiem_tra_tai_khoan.sql` hiển thị server/catalog, schema/index/FK/trigger, số Admin và độ dài hash; không xuất hash/stamp/mật khẩu.

### 15.2. Người dùng tự nhập User Secrets, không đưa password vào command history

Đứng tại repository, trong terminal cá nhân không bật transcript/ghi màn hình. CLI `user-secrets set` hỗ trợ nhận JSON qua stdin (đã kiểm tra help cục bộ). Đoạn này chỉ giữ password trong RAM, không tạo file JSON trong repository:

```powershell
$OutputEncoding = [Text.UTF8Encoding]::new($false)
$a27SeedEmail = Read-Host 'Email Admin'
$a27SeedName = Read-Host 'Ho ten Admin (de trong de dung ten mac dinh)'
$a27SeedSecure = Read-Host 'Mat khau Admin: 8-128 ky tu, co chu va so' -AsSecureString
$a27SeedBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($a27SeedSecure)
try {
    $a27SeedJson = @{
        'Auth:SeedAdmin:Email' = $a27SeedEmail
        'Auth:SeedAdmin:FullName' = $a27SeedName
        'Auth:SeedAdmin:Password' = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($a27SeedBstr)
    } | ConvertTo-Json -Compress
    $a27SeedJson | dotnet user-secrets set --project src/SmartCv.Api
    if ($LASTEXITCODE -ne 0) { throw 'Khong luu duoc User Secrets.' }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($a27SeedBstr)
    $a27SeedSecure.Dispose()
    Remove-Variable a27SeedJson,a27SeedSecure,a27SeedBstr -ErrorAction SilentlyContinue
}
```

Không gửi mật khẩu qua chat/báo cáo/Git. User Secrets là kho cấu hình cục bộ Development, không phải file repo và không phải cơ chế quản lý secret production.

Sau schema hợp lệ, chạy API ở Development bằng profile/configuration đã được duyệt. Xác nhận log tạo đúng một Admin, truy vấn số lượng/role/active/UTC bằng script read-only; restart và đối chiếu không tăng số tài khoản. Không thử xóa Admin trong database đang dùng. Các kịch bản cấu hình sai/email HR/tài khoản khóa phải thử ở catalog cô lập, không sửa dữ liệu thật. Không kiểm tra login cho tới A28.

### 15.3. Chạy SQL integration opt-in

Người dùng chỉ định connection tới SQL instance dành cho test, Initial Catalog bắt đầu `SmartCv_A27_Test_`, không AttachDBFilename. Không in connection string hoặc đưa vào file repo. Cấp biến `SMARTCV_A27_TEST_SQL_CONNECTION` trong terminal riêng bằng cơ chế cấu hình cá nhân được duyệt, rồi chạy:

```powershell
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj -c Release --filter FullyQualifiedName~SmartCv.Tests.Auth.A27SqlServerTests
```

Fixture xác nhận catalog GUID mới chưa tồn tại trong master, tạo mới và kiểm tra DB_NAME trước migrate. Catalog được **giữ lại**, không drop/detach/tái sử dụng catalog cũ. Chỉ tên catalog được ghi vào `artifacts/a27/sql-catalogs/`; không ghi connection string. Không tự lấy connection production/development khi thiếu biến test.

Các SQL suites cũ có lifecycle riêng, một số có cleanup trong code có sẵn; không bật hàng loạt biến opt-in ngoài phạm vi đã xem xét. Phiên này không bật biến SQL nào và không chạy cleanup database.

## 16. Kiểm tra bàn giao và Git status cuối

Kết quả kiểm tra bàn giao được ghi dưới đây sau khi đối chiếu hai chiều status/manifest. Không stage/commit/push. Người dùng review báo cáo, diff, migration và phần nghiệm thu còn lại trước khi tự quyết định commit/push.


Kiểm tra cuối: **27 file = 16 A + 11 M + 0 D**. Manifest khớp status hai chiều, không thiếu/thừa/trùng. SHA metadata khớp HEAD và đúng 40 ký tự. git diff --check đạt; index rỗng; scan tất cả 27 file không có conflict marker, trailing whitespace, key/private-key dễ nhận diện; đã rà các dòng password/config mới, chỉ có test synthetic và hướng dẫn nhập tương tác. Không có secrets thật, bin/obj/artifacts hoặc dữ liệu CV trong tập file bàn giao. Artifacts được Git ignore. Không thay đổi file Web, endpoint, middleware, worker hoặc migration cũ. Scan tự động có giới hạn nhận diện, đã kết hợp review source mới.

Danh sách A/M/D chính xác (TAB thật; cùng nội dung manifest):

```text
A	database/a27_kiem_tra_tai_khoan.sql
A	docs/A27-implementation-report.md
A	src/SmartCv.Api/Auth/AdminSeeder.cs
M	src/SmartCv.Api/Program.cs
A	src/SmartCv.Core/Auth/PasswordRules.cs
A	src/SmartCv.Core/Auth/SeedAdminPlan.cs
A	src/SmartCv.Core/Auth/UserRoles.cs
M	src/SmartCv.Core/Entities/ApplicationEvent.cs
A	src/SmartCv.Core/Entities/User.cs
A	src/SmartCv.Core/Options/AuthOptions.cs
M	src/SmartCv.Infrastructure/Data/Configurations/ApplicationEventConfiguration.cs
A	src/SmartCv.Infrastructure/Data/Configurations/UserConfiguration.cs
A	src/SmartCv.Infrastructure/Data/Migrations/20261119110208_AddUsers.cs
A	src/SmartCv.Infrastructure/Data/Migrations/20261119110208_AddUsers.Designer.cs
M	src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs
M	src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs
M	tests/SmartCv.Tests/Applications/ApplicationStagePersistenceTests.cs
A	tests/SmartCv.Tests/Auth/A27MigrationTests.cs
A	tests/SmartCv.Tests/Auth/A27SqlServerTests.cs
A	tests/SmartCv.Tests/Auth/AdminSeederTests.cs
A	tests/SmartCv.Tests/Auth/PasswordRulesTests.cs
A	tests/SmartCv.Tests/Auth/SeedAdminPlanTests.cs
M	tests/SmartCv.Tests/JobEndpointsTests.cs
M	tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs
M	tests/SmartCv.Tests/Jobs/JobCrudSqlFixture.cs
M	tests/SmartCv.Tests/JobSeedDataTests.cs
M	tests/SmartCv.Tests/TestHostLifecycleTests.cs
```

Git status cuối:

```text
## feature/a27-users-table
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Core/Entities/ApplicationEvent.cs
 M src/SmartCv.Infrastructure/Data/Configurations/ApplicationEventConfiguration.cs
 M src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs
 M src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs
 M tests/SmartCv.Tests/Applications/ApplicationStagePersistenceTests.cs
 M tests/SmartCv.Tests/JobEndpointsTests.cs
 M tests/SmartCv.Tests/JobSeedDataTests.cs
 M tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs
 M tests/SmartCv.Tests/Jobs/JobCrudSqlFixture.cs
 M tests/SmartCv.Tests/TestHostLifecycleTests.cs
?? database/a27_kiem_tra_tai_khoan.sql
?? docs/A27-implementation-report.md
?? src/SmartCv.Api/Auth/AdminSeeder.cs
?? src/SmartCv.Core/Auth/PasswordRules.cs
?? src/SmartCv.Core/Auth/SeedAdminPlan.cs
?? src/SmartCv.Core/Auth/UserRoles.cs
?? src/SmartCv.Core/Entities/User.cs
?? src/SmartCv.Core/Options/AuthOptions.cs
?? src/SmartCv.Infrastructure/Data/Configurations/UserConfiguration.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261119110208_AddUsers.Designer.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261119110208_AddUsers.cs
?? tests/SmartCv.Tests/Auth/A27MigrationTests.cs
?? tests/SmartCv.Tests/Auth/A27SqlServerTests.cs
?? tests/SmartCv.Tests/Auth/AdminSeederTests.cs
?? tests/SmartCv.Tests/Auth/PasswordRulesTests.cs
?? tests/SmartCv.Tests/Auth/SeedAdminPlanTests.cs
```
