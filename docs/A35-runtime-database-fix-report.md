# Báo cáo sửa lỗi HTTP 500 do database thiếu migration A35

Ngày thực hiện: 12/11/2026 · Nhánh `fix/A35-runtime-database-migration` · Repository `C:\TTCN\project\AI-CV-Screening`

> **Tóm tắt.** HTTP 500 tại `GET /api/runs/25/ranking` là do `SmartCvDb` chưa có migration A35 (`20261112045258_ApplicationStages`). Mã nguồn đúng, không cần migration mới.
> Script triển khai đã được kiểm thử trên một bản sao khôi phục từ dữ liệu thật: API trả 200, dữ liệu cũ không đổi.
> Trong quá trình này đã phát hiện và sửa một lỗi của script khi chạy bằng `sqlcmd` (làm hỏng trigger mà vẫn báo thành công).
> Ngoài ra, `SmartCvDb` **không còn bản backup nào dùng được** trên đĩa; đã tạo bản backup mới và đã thử khôi phục thành công.
> **Giai đoạn 1 (kiểm tra chuẩn bị) đã xong** (mục O): đích, backup, script, tiến trình, ảnh hưởng dữ liệu và công cụ triển khai đều đạt.
> **Giai đoạn 2 (mục P), sau khi anh/chị phê duyệt:** lúc 19:04:17 đã áp dụng A35 lên `SmartCvDb`, sau một bản backup cuối đã kiểm tra. Hậu kiểm database **đạt**: đủ 17 đối tượng, dữ liệu cũ không đổi, 0 quyết định HR.
> **Kiểm chứng trên hệ thống thật (19:22, worker tắt, chỉ GET):** HTTP 500 **đã hết**.
> - Readiness, Ranking Run 25 và Evaluation 67–69 đều trả 200.
> - Toàn bộ 69 Evaluation và 21 bảng xếp hạng đều trả 200.
> - Dữ liệu API trùng SQL; không có lỗi trong log.
>
> **Còn chờ:** kiểm tra giao diện chỉ đọc. Web không chạy lúc kiểm tra, nên cần anh/chị khởi động Web.

## A. Nhánh và commit nền

| Mục | Kết quả |
|---|---|
| Trạng thái ban đầu | Nhánh `fix/scapp-end-to-end-stability-20261112` @ `36139f7`; working tree sạch, không staged/untracked/conflict |
| Thay đổi chưa commit | Không có. Cây của `36139f7` **trùng hoàn toàn** cây của `8f30584` (#52 squash) nên chuyển nhánh không mất gì |
| Stash có sẵn | `stash@{0}` của `fix/A10-test-lifecycle`, không đụng tới |
| `git fetch origin --prune` | Đã chạy; remote xóa nhánh `fix/scapp-end-to-end-stability-20261112` (đã merge) |
| A35 / B34 / Runtime Fix / Stability trong `origin/main` | `6554f3b` (#49), `7566dce` (#50), `c7d0c8a` (#51), `8f30584` (#52): cả bốn đều là ancestor của `origin/main` (`git merge-base --is-ancestor`). Máy không có `gh`, nên bằng chứng là lịch sử Git |
| Nhánh mới | Chưa có ở local/remote → tạo `fix/A35-runtime-database-migration` từ `origin/main`, không tracking |
| **Commit nền** | **`8f305849dfe39a3ac11986a8ed7e5597ef89dd3e`**; HEAD giữ nguyên, không commit |

## B. Nguyên nhân chính xác HTTP 500

**Hệ thống đang chạy lúc kiểm tra** (của anh/chị, không bị dừng hay khởi động lại):

- API pid 19608: `dotnet run --project src/SmartCv.Api --launch-profile https`, cổng 7269/5156, binary `1.0.0+c7d0c8a`.
- Web pid 3608: `dotnet watch`, cổng 7258/5172, binary `1.0.0+c7d0c8a`. **API và Web cùng một bản build**, không lệch phiên bản.
- Kết nối hiệu lực: User Secrets `ConnectionStrings:SmartCvDb` → `localhost\BB` / `SmartCvDb`, Windows auth. Không có biến môi trường ghi đè; appsettings không chứa connection string. Không in thông tin đăng nhập.

**Bằng chứng trên API thật (chỉ gọi GET):**

| Endpoint | HTTP | Nguyên nhân |
|---|---|---|
| `/api/health` | 200 | Chỉ kiểm tra tiến trình còn sống, không kiểm tra schema |
| `/api/health/database` | **503** | `SchemaOutOfDate`, pending `20261112045258_ApplicationStages`, thiếu 17 đối tượng |
| `/api/runs/25/ranking` | **500** | `SqlException` **Number 207**: `Invalid column name` × 4 (`Stage`, `RejectReason`, `StageUpdatedAt`, `StageVersion`) tại `RunEndpoints.cs:185` |
| `/api/evaluations/67`, `68`, `69` | **500** | Cùng lỗi 207 tại `EvaluationEndpoints.cs:33` |
| `/api/applications/43/events` | **500** | `SqlException` **Number 208**: thiếu bảng `ApplicationEvents` |
| `/api/jobs`, `/api/runs`, `/api/runs/latest`, `/api/runs/25`, `/api/applications?jobId=1`, `/api/evaluation-sets`, `/api/evaluation-sets/1/applications`, `/api/prompt-tests`, `/api/prompts`, `/api/applications/stage-options` | 200 | Các truy vấn này không đọc cột/bảng A35 |

**Kết luận nguyên nhân:** mã A35 (merge ở #49) đọc và ghi 4 cột cùng bảng lịch sử của migration `ApplicationStages`, nhưng `SmartCvDb` mới có 2 migration (`InitialCreate`, `SeedJobs`).

Đây **không phải lỗi mã**:
- Truy vấn đúng với model.
- Endpoint không nuốt exception.
- `Program.cs` cố ý không tự migrate khi khởi động.

Không sửa mã để che lỗi. Theo mã nguồn, thao tác gán CV vào Job (`ApplicationEndpoints.cs:110`, INSERT có `StageVersion`) cũng sẽ lỗi cho đến khi có migration. Điều này **suy ra từ mã**; không thử ghi trên database ứng dụng.

**Lỗi `sqlcmd` trước đây không phải lỗi SQL Server.** CMD không coi dấu nháy đơn là dấu bao chuỗi, nên `-S 'localhost\BB'` trở thành tên máy chủ có cả dấu nháy. Cú pháp đúng cho từng shell nằm ở mục M và trong `database/README.md`.

## C. Database và migration thiếu

| Nhóm | Đối tượng | `SmartCvDb` hiện tại | Bản sao sau migration |
|---|---|---|---|
| Lịch sử | `20261112045258_ApplicationStages` | Thiếu | Có (ProductVersion 10.0.15) |
| Cột `Applications` | `Stage` nvarchar(20) NOT NULL DEFAULT `N'New'`; `RejectReason` nvarchar(30) NULL; `StageUpdatedAt` datetime2 NULL; `StageVersion` uniqueidentifier NOT NULL DEFAULT `NEWID()` | Thiếu cả 4 | Đủ, đúng kiểu/default |
| Bảng | `ApplicationEvents` (PK `EventId` bigint identity) | Thiếu | Có |
| Index | `IX_Applications_Job_Stage`, `IX_ApplicationEvents_Application_CreatedAt` | Thiếu | Có, enabled |
| CHECK (8) | `CK_Applications_Stage`, `_RejectReason`, `_RejectReason_Stage`; `CK_ApplicationEvents_FromStage`, `_ToStage`, `_RejectReason`, `_RejectReason_Stage`, `_Actor` | Thiếu | Có, enabled, trusted |
| FK | `FK_ApplicationEvents_Applications` (NO ACTION) | Thiếu | Có, trusted |
| Trigger | `TR_ApplicationEvents_NoChange` (INSTEAD OF UPDATE, DELETE) | Thiếu | Có, `uses_quoted_identifier=1`, thông điệp đúng Unicode |

**Migration A35 hiện tại đúng, không tạo migration mới:**
- 12 lệnh DDL/history trong `runtime_apply_application_stages.sql` trùng **từng lệnh** với script do EF sinh (`database/a35_application_stages.sql`), so sánh cả hai chiều.
- Cấu hình EF (`ApplicationConfiguration`, `ApplicationEventConfiguration` với `HasTrigger`) khớp schema.
- Kiểm thử thứ tự migration và model trong suite đều đạt.

## D. Các lỗi phát hiện thêm

| # | Mức | Lỗi | Bằng chứng | Xử lý |
|---|---|---|---|---|
| D1 | **Medium** | Chạy `runtime_apply_application_stages.sql` bằng `sqlcmd` theo lệnh trong báo cáo audit trước (bản sao không BOM, không `-f 65001`, không `-I`) vẫn báo `ApplicationStages ready` và ghi lịch sử migration. Tuy vậy trigger bị tạo **sai**: thông điệp tiếng Việt hỏng (84 ký tự rác thay cho 58 ký tự đúng) và `uses_quoted_identifier=0`, khác migration EF. `DatabaseSchemaDiagnostics` vẫn báo "ready". Trigger vẫn chặn sửa/xóa (lỗi 51000), nên đây là lệch schema và lỗi hiển thị, không mất chức năng bảo vệ. | Probe: `sqlcmd` đọc file UTF-8 không BOM theo **Windows-1252** dù console đang ở 65001. Tái hiện bằng script gốc trên DB LocalDB tổng hợp. So khớp code point thông điệp: bản cũ ≠ migration, bản sửa = migration | **Đã sửa:** thêm BOM UTF-8 cho file; thêm 2 kiểm tra dừng **trước transaction** (sai bảng mã, `QUOTED_IDENTIFIER OFF`); thêm hậu kiểm trigger (SET options + thông điệp). Ghi `-I -f 65001` vào script và README; `check_runtime_schema.sql` hiển thị thêm `MessageText` |
| D2 | **High** (bảo vệ dữ liệu) | msdb ghi nhận 2 backup ngày 06/11, nhưng **cả hai file không còn** (`sys.dm_os_file_exists` = 0; thư mục Backup rỗng). Trước phiên này, `SmartCvDb` không có backup nào khôi phục được | `app-backup-files-before.txt` | Đã tạo backup mới, kiểm tra CHECKSUM, khôi phục thử thành công (mục G). Không sửa mã |
| D3 | Low | `database/README.md` không nhắc tới các script A35/runtime, cũng không có cú pháp `sqlcmd` cho CMD/PowerShell | Lỗi CMD của anh/chị | Đã bổ sung |
| D4 | Thông tin (đính chính) | Báo cáo trước ghi worker chấm điểm **tắt**. Thực tế `launchSettings.json` đặt `Scoring__WorkerEnabled=true` cho cả profile `http` và `https`, nên API chạy bằng `dotnet run --launch-profile https` **có bật worker** (gọi Gemini thật khi có lượt `Queued`) | Đọc `src/SmartCv.Api/Properties/launchSettings.json` | Không đổi cấu hình. Hiện có 0 lượt Queued/Running. Liên quan tới thứ tự triển khai (mục M, bước 0) |
| D5 | Thông tin | Binary đang chạy là `c7d0c8a` (#51), **chưa có #52** (phục hồi worker trích xuất, polling Upload) | `ProductVersion` của DLL | Nên khởi động lại từ `main` sau khi migration |
| D6 | Thông tin | `SmartCvDb` dùng recovery model FULL nhưng chưa từng có log backup. Không khôi phục được tới một thời điểm bất kỳ; muốn quay lại chỉ có cách restore nguyên bản backup | Audit chỉ đọc | Không đổi; đã ghi vào rủi ro và phương án khôi phục |
| D7 | **Cần lưu ý ngay** | File chờ duyệt cũ `artifacts/full-audit-tests/application-stages-SmartCvDb.pending-approval.sql` (SHA256 `47734E79…`) sinh từ script **cũ**: không BOM, không có kiểm tra | Kiểm tra file | **Không dùng file này.** Chưa xóa, chờ anh/chị quyết định. Bản mới ở mục M |

Không phát hiện lỗi mã nào khác có bằng chứng: EF snapshot, startup, readiness, Ranking/Evaluation, UI B34, cancellation/polling và vòng đời worker đều đã kiểm. Không tái cấu trúc hay sửa chức năng không liên quan.

## E. File đã sửa

| File | Loại | Mục đích |
|---|---|---|
| `database/runtime_apply_application_stages.sql` | Sửa | Lưu UTF-8 có BOM. Thêm hướng dẫn chạy (`-I -f 65001`, cú pháp CMD/PowerShell). Thêm 2 kiểm tra trước transaction: sai bảng mã, `QUOTED_IDENTIFIER OFF` (lỗi 51035, không thay đổi gì). Thêm hậu kiểm trigger so với migration EF. **Phần DDL giữ nguyên** |
| `database/check_runtime_schema.sql` | Sửa | Vẫn chỉ đọc, chỉ ký tự ASCII. Dòng trigger hiển thị thêm `uses_quoted_identifier`, `uses_ansi_nulls`, `MessageText` (Unicode OK / CORRUPTED) |
| `database/README.md` | Sửa | Liệt kê 4 script A35/runtime; cú pháp `sqlcmd` cho CMD và PowerShell; cảnh báo BOM |
| `tests/SmartCv.Tests/RuntimeScriptDeploymentTests.cs` | Mới | 2 test luôn chạy (BOM; vị trí kiểm tra; thông điệp trigger khớp migration) và 1 SQL test opt-in (giả lập cách `sqlcmd` đọc Windows-1252, `QUOTED_IDENTIFIER OFF` → dừng, không đổi schema; chạy đúng → trigger khớp migration từng ký tự) |
| `docs/A35-runtime-database-fix-report.md` | Mới | Báo cáo này |

Không sửa: mã ứng dụng, migration/snapshot, appsettings, User Secrets, connection string, package/SDK, test cũ.

## F. Kết quả kiểm tra SQL Server (chỉ đọc trên `SmartCvDb`)

| Mục | Giá trị |
|---|---|
| Instance | `localhost\BB` = `DESKTOP-I9O3636\BB`, SQL Server 16.0.1000.6 Developer (64-bit) |
| Database | ONLINE, MULTI_USER, FULL, `SQL_Latin1_General_CP1_CI_AS`, không read-only |
| Quyền | Login Windows hiện tại là sysadmin, có quyền BACKUP. Service SQL chạy bằng `.\Lenovo`; Agent đang Stopped |
| Kích thước | Data 8 MB (đã dùng 7,25 MB), log 8 MB (đã dùng 1,13 MB); ổ C: còn trống khoảng 72,9 GB |
| Số dòng | Applications 44 · Candidates 12 · Cvs 61 (60 Completed, 1 Failed) · EvaluationRuns 21 (17 Completed, 4 Failed) · Evaluations 69 · EvaluationSkills 362 · EvaluationSets 1 · HrLabels 10 · Jobs 5 · JobSkills 29 · JobSkillAliases 52 · PromptVersions 2 |
| Run 25 | Screening, Completed, 3/3, Failed 0; Evaluations 67/68/69 thuộc Run 25 (điểm 31.33 / 31.33 / 38.00) |
| Lượt đang chờ | 0 Queued/Running |
| Fingerprint dữ liệu | SHA-256 trên mọi cột cũ của 12 bảng, sắp theo PK; chỉ in hash. **Không đổi** từ đầu tới cuối phiên |

Mọi truy vấn trên `SmartCvDb` là SELECT/DMV. Thao tác duy nhất chạm tới nó là `BACKUP ... COPY_ONLY` (mục G); thao tác này không đổi dữ liệu/schema và không cắt chuỗi backup.

## G. Tình trạng backup

| Bước | Kết quả |
|---|---|
| Backup cũ | 2 bản ngày 06/11 trong msdb, **file không còn** (D2) |
| Backup mới | `C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\Backup\SmartCvDb_before_A35_20261112_182452.bak`. Tên có timestamp, đã kiểm tra chưa tồn tại trước khi ghi. `COPY_ONLY, CHECKSUM`, 946 trang, khoảng 7,46 MB |
| `RESTORE VERIFYONLY ... WITH CHECKSUM` | `The backup set on file 1 is valid.` |
| Khôi phục thử | Restore vào **database mới** `SmartCvDb_A35Rehearsal_20261112_182452` (`WITH MOVE` sang file mới, không `REPLACE`) → ONLINE, MULTI_USER |
| `DBCC CHECKDB` bản sao | Không có lỗi |
| So với `SmartCvDb` | Fingerprint 12 bảng **trùng 100%** |

Giới hạn:
- Backup phản ánh dữ liệu lúc 18:24:52. Ngay trước khi triển khai phải tạo bản mới (mục M, bước 1).
- File backup và bản sao chứa dữ liệu ứng viên thật. Chúng nằm trên cùng instance/máy, thư mục Backup chỉ service SQL truy cập được. Cần anh/chị quyết định thời gian giữ.

## H. Kết quả migration trên database thử nghiệm

**H1. Tái hiện D1 bằng script gốc** (LocalDB, DB tổng hợp `SmartCv_Runtime_Test_7a0bd21e…`, dựng bằng `migration_InitialCreate.sql` + `migration_SeedJobs.sql`).

Chạy đúng lệnh báo cáo trước đề xuất → exit 0, `ApplicationStages ready`, history = 3. Nhưng trigger có `uses_quoted_identifier=0`, thông điệp lỗi 51000 dài 84 ký tự rác thay vì 58.

**H2. Script đã sửa** (LocalDB, DB tổng hợp `SmartCv_Runtime_Test_06513974…`):

| Trường hợp | Kết quả |
|---|---|
| Bản sao không BOM, thiếu `-f 65001` | 51035 "decoded with the wrong code page…", **không thay đổi gì** |
| Có BOM, thiếu `-I` | 51035 "QUOTED_IDENTIFIER is OFF…", không thay đổi gì |
| Lệnh chuẩn `-I -f 65001` | `ApplicationStages ready`, trigger `uses_quoted_identifier=1`, thông điệp trùng migration EF từng code point |
| Chạy lại | No-op, trạng thái không đổi |
| DB đã triển khai sai theo cách cũ (H1) | Hậu kiểm phát hiện, lỗi 51035 "trigger differs…", không tự sửa, DB giữ nguyên |

**H3. Diễn tập trên bản sao dữ liệu thật** `SmartCvDb_A35Rehearsal_20261112_182452` (`localhost\BB`, cùng phiên bản và collation với ứng dụng):

| Bước | Kết quả |
|---|---|
| Lỗi giữa DDL (chèn `THROW 51036` trước `CREATE TABLE`) | Exit 1; history 2, 0 cột A35, không có bảng; fingerprint **không đổi** → rollback thật |
| Thiếu `-I` | 51035, không đổi |
| Áp dụng (`-I -f 65001`) | Exit 0 trong **309 ms**; 17 đối tượng đúng (mục C); fingerprint dữ liệu cũ **không đổi** |
| Dữ liệu A35 sinh ra | 44/44 hồ sơ `Stage='New'`, `RejectReason` NULL, `StageUpdatedAt` NULL; 44 `StageVersion` khác nhau, 0 GUID rỗng; **0 dòng lịch sử** (không tạo event HR giả) |
| Ràng buộc/trigger (16 phép thử, mỗi phép trong transaction rồi rollback) | **16/16 PASS**: stage hợp lệ; Rejected + lý do; stage lạ/sai hoa thường (BIN2); Rejected thiếu lý do; lý do khi không Rejected; lý do lạ; 5 CHECK của lịch sử; FK với hồ sơ không tồn tại; trigger chặn UPDATE và DELETE (51000) |
| FK chặn xóa hồ sơ đã có lịch sử | **Bỏ qua (không tính PASS):** mọi hồ sơ thật đều có Evaluation/HrLabel nên không cô lập được FK này. FK đã được xác nhận qua metadata (NO ACTION, trusted) |
| Chạy lại | No-op; fingerprint và hash trạng thái stage không đổi |
| Sau các lần ghi HR qua API và UI (mục J) | Fingerprint dữ liệu cũ vẫn không đổi; chỉ cột/bảng A35 thay đổi |

**H4. Kiểm thử tự động** (LocalDB, giữ lại database): `RuntimeSchemaSqlTests` (500/207 → script → 200, 409, trigger, chạy lại), `FullAuditSqlTests` (tranh chấp ở SaveChanges, 32+32 GET song song, rollback bulk) và test mới: đều **Passed**.

## I. Kết quả build/test

Lệnh đã chạy, đúng theo yêu cầu:

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/a35-runtime-build
dotnet test SmartCv.slnx --artifacts-path artifacts/a35-runtime-build --no-build --logger "trx;LogFileName=final.trx" --results-directory artifacts/a35-runtime-tests
```

Ghi chú tham số:
- `dotnet build` tự restore cho thư mục output mới, không cần đổi lệnh.
- Lệnh test chỉ thêm `--logger`/`--results-directory` để lưu bằng chứng.
- Bật hai biến môi trường cho phần opt-in an toàn:
  - `SMARTCV_RUNTIME_TEST_SQL_CONNECTION` = LocalDB, tiền tố `SmartCv_Runtime_Test_`, database được giữ lại;
  - `SMARTCV_B34_DEMO_ZIP` = `C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip`, chỉ đọc.

| Hạng mục | Kết quả |
|---|---|
| Build | **0 warning, 0 error** |
| Test toàn bộ | **1361 Passed, 0 Failed, 38 Skipped / 1399** (khoảng 78 giây). Trước nhánh này là 1396; thêm 3 test mới |

| Nhóm (phân theo tên lớp test) | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit/khác | 808 | 0 | 0 |
| API (TestServer) | 265 | 0 | 0 |
| UI (bUnit/Web) | 270 | 0 | 0 |
| SQL integration | 18 | 0 | 38 |

Quy tắc phân nhóm: tên lớp chứa `Sql` → SQL; chứa Ui/Page/Component… → UI; chứa Endpoint/Api/Health/Middleware… → API; còn lại → Unit. Danh sách từng test ở `artifacts/a35-runtime-tests/final-test-outcomes.csv`.

**38 Skipped = chưa kiểm thử được trong phiên này**, không tính là pass: A11 = 6, A15 = 7, A16 = 10, A17 = 3, Prompt SQL = 5, A35 `ApplicationStageSqlServerTests` = 7. Các fixture này tự tạo rồi **tự xóa** database GUID của chúng. Vì yêu cầu không tự xóa database, chúng không được bật. Phần A35 tương ứng đã được bao phủ bằng H2–H4. Nếu anh/chị đồng ý, có thể chạy chúng trên LocalDB.

**Chứng minh test bắt được lỗi:**
- Trước khi sửa (tạm đặt lại script gốc, SHA256 `1F38CAA4…`): **3/3 test mới Failed**. Test SQL báo đúng triệu chứng `Assert.Throws() Failure: No exception was thrown`, tức script đọc sai bảng mã vẫn được áp dụng âm thầm.
- Sau khi khôi phục bản sửa (SHA256 `433B59A0…`, trùng byte): Passed.
- Chạy tập trung sau khi sửa: **71 Passed, 0 Failed, 7 Skipped**.

Không sửa hoặc xóa test cũ.

## J. Các API đã kiểm thử

**Trước khi sửa, trên API thật:** xem mục B (GET only).

**Sau migration, trên bản sao** (API và Web từ `artifacts/a35-runtime-build`, cổng 5391/5392, `Gemini:UseFake=true`, worker tắt, storage riêng; API đã được xác nhận nối tới bản sao trước mọi lần ghi): **24/24 PASS**

| Nhóm | Kết quả |
|---|---|
| `GET /api/health/database` | 200, `OK`, pending 0, missing 0 |
| `GET /api/runs/25/ranking` | 200, 3 hồ sơ, `StageCounts.New=3`, mọi hồ sơ `New` và có `StageVersion` |
| `GET /api/evaluations/67`, `68`, `69` | 200 (31.33 / 31.33 / 38.00) |
| `GET /api/runs`, `/latest`, `/25`, `/api/jobs`, `/api/applications/stage-options` | 200 |
| Run/Job không tồn tại | 404 |
| `GET /api/applications/{id}/events` ngay sau migration | 200, rỗng |
| `PATCH` New→Reviewing; dùng lại `StageVersion` cũ; Rejected thiếu lý do; Rejected(MissingSkills); lặp lại quyết định | 200 Changed; **409**; **400**; 200; 200 `Unchanged` (không thêm event) |
| `POST /api/applications/stage` (bulk OnHold) | 200, changed = 2 |
| Lịch sử | 2 event, mới nhất trước |
| Ranking sau quyết định | Rejected 1, OnHold 2, New 0; **điểm keyword/AI/final và hạng giữ nguyên** |

**Giao diện trên bản sao** (Playwright .NET + Chrome headless, 1440×1000): **21/21 PASS**
- Trang Kết quả của Run 25 hiển thị 3 dòng, không có cảnh báo lỗi.
- Ô giai đoạn khớp API; số đếm giai đoạn đúng; bộ lọc "Dự phòng" còn 2 dòng.
- Đổi giai đoạn qua hộp thoại (nhập tên HR) → API lưu và thêm 1 event.
- **409 thật:** một HR khác đổi hồ sơ qua API trong khi trang đang mở; UI báo "Hồ sơ vừa được đổi…", không ghi đè quyết định của người kia và tải lại giai đoạn đã lưu.
- F5 giữ nguyên giai đoạn.
- Trang chi tiết Evaluation 67/68/69 hiển thị bình thường.
- 0 page error, 0 console error, 0 phản hồi 5xx; log API diễn tập: 0 exception.

Chưa kiểm chứng: API/Web **thật** sau migration (vì chưa migration), Gemini thật, tải nhiều người dùng.

## K. Rủi ro còn tồn tại

1. ~~`SmartCvDb` vẫn thiếu A35.~~ **Đã triển khai lúc 19:04 và kiểm chứng HTTP thật lúc 19:22 (mục P).** Còn chờ kiểm tra giao diện chỉ đọc vì Web chưa chạy.
2. Backup lúc 18:24:52 sẽ cũ dần, nên phải backup lại ngay trước khi triển khai.
3. Khi triển khai, `ALTER TABLE` giữ khóa Sch-M trên `Applications`. Thời gian rất ngắn (309 ms với 44 dòng), nhưng API đang chạy với worker bật (D4), nên nên dừng API/Web trong lúc triển khai.
4. Sau khi migration thành công, quay lại chỉ có cách **restore cả database** về thời điểm backup (D6). Mọi dữ liệu ghi sau đó sẽ mất. `Down` migration xóa lịch sử HR nên không dùng.
5. 38 SQL fixture tự xóa database chưa chạy (mục I).
6. File chờ duyệt cũ không có kiểm tra (D7) vẫn nằm trong `artifacts/`. Nếu chạy file đó theo lệnh cũ, trigger sẽ hỏng như H1. Nếu dùng script mới với lệnh cũ, script sẽ dừng an toàn.
7. Bản sao diễn tập, file backup và 11 DB tổng hợp vẫn được giữ (mục L). Bản sao và backup chứa dữ liệu ứng viên thật.
8. Chưa kiểm chứng Gemini thật; worker đang bật trong launch profile.

## L. Trạng thái Git cuối cùng

```text
## fix/A35-runtime-database-migration
 M database/README.md
 M database/check_runtime_schema.sql
 M database/runtime_apply_application_stages.sql
?? docs/A35-runtime-database-fix-report.md
?? tests/SmartCv.Tests/RuntimeScriptDeploymentTests.cs
```

- `git diff --stat` (file đã theo dõi): 3 files, 47 insertions(+), 4 deletions(-).
- `git diff --check`: sạch. Không có gì được stage; HEAD = `8f305849`.
- Không add/commit/push/pull/merge/rebase/reset/stash/clean; không tạo PR.
- Cảnh báo "LF will be replaced by CRLF" chỉ là thông báo `core.autocrlf` cho bản làm việc; index vẫn LF như các file cũ.

**Dữ liệu ngoài Git do phiên này tạo (giữ lại, chưa xóa):**

- `localhost\BB`: `SmartCvDb_A35Rehearsal_20261112_182452` (bản sao dữ liệu thật đã migration, có 4 event diễn tập + 1 event UI + 1 event "HR khác").
- LocalDB: 11 DB tổng hợp `SmartCv_Runtime_Test_*`, liệt kê trong `artifacts/a35-runtime-tests/retained-test-databases.txt` và `artifacts/runtime-fix-tests/`, `artifacts/full-audit-tests/`.
- File backup ở mục G.
- Bằng chứng trong `artifacts/a35-runtime-tests/` (đã ignore): audit chỉ đọc, fingerprint, kết quả diễn tập, kết quả API/browser, ảnh chụp, TRX `before-fix`/`focused-after`/`final`, log build/test.

## M. Những bước cần phê duyệt

**Chưa thực hiện bước nào dưới đây.** Kết quả kiểm tra chuẩn bị ở mục O.

Các bước ghi đều chạy qua runbook `artifacts/a35-runtime-tests/deploy-a35.ps1`:
- Mỗi bước được gọi riêng và tự kiểm tra lại điều kiện của mình.
- Runbook **không có** lệnh restore, drop hay xóa.
- Gặp bất kỳ dòng `STOP:` nào thì dừng, không chạy tiếp.

| # | Việc | Lệnh (PowerShell, tại gốc repository) | Runbook dừng khi |
|---|---|---|---|
| 0 | Dừng SCApp | Lúc 18:54 **không có** API/Web nào chạy. Nếu anh/chị đã mở lại: Ctrl+C ở cửa sổ `dotnet run` (API) và `dotnet watch` (Web) | Còn tiến trình `SmartCv.Api/Web`, cổng 7269/5156/7258/5172 hoặc phiên/transaction trên `SmartCvDb` |
| 1 | Kiểm tra trước (chỉ đọc) | `.\artifacts\a35-runtime-tests\deploy-a35.ps1 -Step Preflight` | Sai server/database, lịch sử ≠ 2 migration, đã có một phần A35, file duyệt bị đổi hash hoặc mất BOM |
| 2 | Backup cuối | `.\artifacts\a35-runtime-tests\deploy-a35.ps1 -Step Backup` | File đích đã tồn tại, BACKUP hoặc VERIFYONLY lỗi, dữ liệu đổi trong lúc backup |
| 3 | Áp dụng A35 | `.\artifacts\a35-runtime-tests\deploy-a35.ps1 -Step Apply -ConfirmDatabase SmartCvDb` | Không có backup cuối ≤ 120 phút, dữ liệu đổi sau backup, SCApp đang chạy, script trả lỗi (**không tự chạy lại**) |
| 4 | Hậu kiểm DB (chỉ đọc) | `.\artifacts\a35-runtime-tests\deploy-a35.ps1 -Step Verify` | Thiếu đối tượng, trigger sai SET option/thông điệp, hồ sơ không ở `New`, có event lạ, dữ liệu cũ khác fingerprint trước khi áp dụng |
| 5 | Khởi động lại | Xem các phương án bên dưới | API không lên hoặc lỗi khi khởi động |
| 6 | Kiểm tra HTTP (GET) | `.\artifacts\a35-runtime-tests\post-deploy-http-checks.ps1 -BaseUrl https://localhost:7269 -Database SmartCvDb -ExpectAllNew` | Readiness ≠ 200; Ranking 25 / Evaluation 67–69 ≠ 200; điểm API ≠ điểm SQL; endpoint cũ không còn 200 |
| 7 | Kiểm tra giao diện (chỉ đọc) | `dotnet artifacts/a35-rehearsal-browser/out/A35RehearsalBrowser.dll "$PWD/artifacts/a35-runtime-tests/phase2/ui-readonly" SmartCvDb readonly https://localhost:7258 https://localhost:7269` | Trang Kết quả, Chi tiết Evaluation, Prompts, Home hoặc Upload lỗi; có console error hoặc 5xx |

**Lệnh tương đương chạy tay trong CMD** (nếu không dùng runbook; giữ nguyên `-I -f 65001`):

```cmd
sqlcmd -S "localhost\BB" -d "master" -E -b -Q "BACKUP DATABASE [SmartCvDb] TO DISK = N'C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\Backup\SmartCvDb_before_A35_final_<yyyyMMdd_HHmmss>.bak' WITH COPY_ONLY, CHECKSUM, STATS = 25;"
sqlcmd -S "localhost\BB" -d "master" -E -b -Q "RESTORE VERIFYONLY FROM DISK = N'<file .bak vừa tạo>' WITH CHECKSUM;"
sqlcmd -S "localhost\BB" -d "SmartCvDb" -E -b -I -f 65001 -i "artifacts\a35-runtime-tests\application-stages-SmartCvDb.pending-approval.sql"
sqlcmd -S "localhost\BB" -d "SmartCvDb" -E -b -I -f 65001 -W -s "|" -i "database\check_runtime_schema.sql"
sqlcmd -S "localhost\BB" -d "SmartCvDb" -E -b -I -W -s "|" -i "artifacts\a35-runtime-tests\post-deploy-readonly-checks.sql"
```

Các file sẽ được dùng ở Giai đoạn 2:

| File | Vai trò | Kiểm tra |
|---|---|---|
| `application-stages-SmartCvDb.pending-approval.sql` | Script áp dụng | SHA256 `2E28750C88B9F388ED29E5CE2857F0D2D35321C51F1867B70540559BFD071F03`. Chỉ khác nguồn `433B59A0…` một dòng `@ExpectedDatabase`; chỉ khác file đã chạy thành công trên bản sao ở tên database |
| `post-deploy-readonly-checks.sql` | Hậu kiểm, chỉ SELECT | Tự từ chối nếu không phải `SmartCvDb` |
| **Không dùng** `artifacts/full-audit-tests/application-stages-SmartCvDb.pending-approval.sql` | Bản cũ (`47734E79…`) | Không BOM, không có kiểm tra (D1/D7) |

**Phương án khởi động lại (bước 5)** — anh/chị chọn một:
- **(a) Anh/chị tự chạy lại trong terminal của mình, như trước** — nên chọn để theo dõi log:
  ```powershell
  dotnet run --project src/SmartCv.Api --launch-profile https
  dotnet watch --project src/SmartCv.Web --launch-profile https
  ```
  Hai lệnh build vào `src/*/bin` từ mã hiện tại. Mã ứng dụng trùng `main` (`8f30584`, đã có #52); nhánh này không sửa mã ứng dụng.
- **(b) Tôi chạy đúng hai lệnh trên ở chế độ nền**, ghi log vào `artifacts/a35-runtime-tests/phase2/`, và chỉ dừng chúng khi anh/chị yêu cầu.

Cả hai cách dùng profile `https`, tức **worker chấm điểm bật và gọi Gemini thật** khi có lượt `Queued` (D4). Hiện có 0 lượt chờ; bước kiểm tra không tạo lượt chấm. Nếu muốn tắt worker khi chạy lại, cần anh/chị quyết định.

**Nghiệp vụ HR trên `SmartCvDb`:** chỉ kiểm tra **đọc**:
- Giai đoạn hiển thị, số đếm, bộ lọc;
- `GET /events`;
- Chi tiết Evaluation.

Luồng ghi (đổi giai đoạn, bulk, 409, lý do loại, F5) đã kiểm chứng trên bản sao (mục J, 24/24 API và 21/21 UI) và **không** thử trên quyết định HR thật. Nếu anh/chị muốn thử ghi trên `SmartCvDb`, cần chỉ định một hồ sơ thử nghiệm riêng.

**Phục hồi theo điểm thất bại** — không tự restore đè, không xóa database:

| Thất bại tại | Trạng thái database | Việc làm |
|---|---|---|
| Bước 1 | Không đổi | Sửa nguyên nhân (ví dụ dừng app), chạy lại Preflight |
| Bước 2 | Không đổi; có thể còn một file `.bak` dở | Dừng, không áp dụng. Kiểm tra quyền và dung lượng. Không xóa file khi chưa được duyệt |
| Bước 3, script trả lỗi | **Tự rollback** (XACT_ABORT + TRY/CATCH; đã kiểm chứng ở H3) | Không chạy lại. Chạy `check_runtime_schema.sql` và fingerprint để xác nhận vẫn 2 migration, dữ liệu như cũ. Gửi `phase2/apply-output.txt` để phân tích. Ứng dụng quay về trạng thái cũ (vẫn 500) |
| Bước 3, mất kết nối giữa chừng | SQL Server rollback transaction khi phiên kết thúc | Như trên, và kiểm tra không còn phiên hay khóa treo |
| Bước 4 | Migration **đã commit** | Dừng, chưa mở app cho người dùng, báo cáo. Ưu tiên sửa tiến (forward fix) với phê duyệt. Không `Down`/drop |
| Bước 5–7 | Database đã đúng (bước 4 đạt) | Kiểm tra log, cấu hình, bản build. Không đụng database |

Phương án cuối cùng, **chỉ khi anh/chị phê duyệt riêng**: restore bản backup cuối. Trước hết restore ra database khác để đối chiếu (như mục G); chỉ ghi đè `SmartCvDb` sau khi đã so sánh. Ghi đè sẽ mất mọi dữ liệu ghi sau thời điểm backup, và không có point-in-time (D6). Lệnh tham khảo, **chưa chạy**:

```sql
-- 1) Restore ra database khác để đối chiếu (không ảnh hưởng SmartCvDb)
RESTORE DATABASE [SmartCvDb_RestoreCheck_<ts>] FROM DISK = N'<backup cuối>'
  WITH MOVE N'SmartCvDb' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\DATA\SmartCvDb_RestoreCheck_<ts>.mdf',
       MOVE N'SmartCvDb_log' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\DATA\SmartCvDb_RestoreCheck_<ts>_log.ldf',
       CHECKSUM, RECOVERY;
-- 2) CHỈ khi được duyệt riêng: ghi đè SmartCvDb
ALTER DATABASE [SmartCvDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [SmartCvDb] FROM DISK = N'<backup cuối>' WITH REPLACE, CHECKSUM, RECOVERY, STATS = 25;
ALTER DATABASE [SmartCvDb] SET MULTI_USER;
```

**Các quyết định khác cần anh/chị:**
1. Phê duyệt Giai đoạn 2 (bước 0–7).
2. Chọn phương án khởi động lại (a) hoặc (b), và có giữ worker bật không.
3. Có cần thử ghi HR trên một hồ sơ thử nghiệm của `SmartCvDb` không.
4. Commit/push.
5. Có chạy 38 SQL fixture tự xóa database không.
6. Kế hoạch dọn dẹp dữ liệu tạm. Hiện giữ nguyên theo yêu cầu: bản sao diễn tập, 11 DB LocalDB, backup, file SQL cũ.

## N. Kết luận

**A. Code và script đã sẵn sàng commit chưa? — SẴN SÀNG ĐỂ ANH/CHỊ DUYỆT VÀ COMMIT.**
- Nguyên nhân 500 đã xác định chính xác: migration A35 chưa áp dụng.
- Script triển khai đã sửa lỗi D1, có test thất bại trước khi sửa và đạt sau khi sửa.
- Build 0 warning/0 error; 1361 test Passed, 0 Failed; 38 Skipped được nêu rõ.
- Diễn tập trên bản sao dữ liệu thật đạt mọi kiểm tra trong phạm vi đã nêu.

**B. Database ứng dụng đã được cập nhật và HTTP 500 thực tế đã hết chưa?**
- **Database: ĐÃ CẬP NHẬT** (12/11/2026 19:04:17). `SmartCvDb` có 3 migration, đủ 17 đối tượng A35, dữ liệu cũ không đổi (mục P).
- **HTTP 500 thực tế: ĐÃ KHẮC PHỤC ở mức API** (kiểm tra trên API thật lúc 19:22, worker tắt, chỉ GET):
  - `/api/health/database` 200; `/api/runs/25/ranking` 200; `/api/evaluations/67`, `68`, `69` 200; `/events` 200.
  - Toàn bộ 69 Evaluation và 21 bảng xếp hạng đều 200.
  - Dữ liệu API trùng SQL; không có Error/500 trong log (mục P6–P8).
- **Giao diện:** kiểm tra chỉ đọc trên hệ thống thật **chưa thực hiện**, vì Web không chạy lúc kiểm tra. Luồng giao diện đã đạt trên bản sao (mục J).

## O. Giai đoạn 1 — Kiểm tra chuẩn bị triển khai (12/11/2026, 18:45–18:57)

Toàn bộ thao tác trên `SmartCvDb` trong giai đoạn này là SELECT/DMV/`RESTORE VERIFYONLY`. **Không có thay đổi dữ liệu hay schema.**

| # | Yêu cầu | Kết quả |
|---|---|---|
| 1 | Git | Vẫn ở `fix/A35-runtime-database-migration` @ `8f305849`. Giữ nguyên 3 file sửa + 2 file mới; không stage, không tạo nhánh. `stash@{0}` cũ không đụng tới |
| 2 | Database đích | `@@SERVERNAME = DESKTOP-I9O3636\BB` (instance `BB`, SQL 16.0.1000.6), `DB_NAME() = SmartCvDb` (db_id 30, tạo 06/11/2026 20:36:54), ONLINE, MULTI_USER, FULL. User Secrets của API → `localhost\BB` / `SmartCvDb`, không có biến môi trường ghi đè. Lịch sử: đúng 2 migration |
| 3 | Backup | `SmartCvDb_before_A35_20261112_182452.bak` vẫn tồn tại. Hoàn tất **18:24:53**, COPY_ONLY, có checksum, `is_damaged = 0`, 7,46 MB. `RESTORE VERIFYONLY ... WITH CHECKSUM` chạy lại: hợp lệ. msdb ghi nhận restore thử vào bản sao lúc 18:25:12 (`replace = 0`); `DBCC CHECKDB` bản sao trước đó không lỗi. Fingerprint `SmartCvDb` lúc 18:54 **trùng** fingerprint ngay sau backup, tức dữ liệu không đổi kể từ backup. Giai đoạn 2 vẫn tạo backup cuối mới |
| 4 | Script A35 | Nguồn `433B59A0…`; bản cho `SmartCvDb` `2E28750C…`. Cả hai có BOM, 2 kiểm tra trước transaction và hậu kiểm trigger; chỉ khác nhau một dòng `@ExpectedDatabase`. So với file đã chạy thành công trên bản sao: chỉ khác tên database. Chạy thử bản `SmartCvDb` lên bản sao → **51035 "Database has not been explicitly approved"**, bản sao không đổi (history, event, hash giai đoạn). Bản cũ `47734E79…`: không BOM, không kiểm tra → **không dùng** |
| 5 | UTF-8 / `QUOTED_IDENTIFIER` / `-I -f 65001` / transaction / rollback | Đã kiểm chứng trước đó (H1–H3, I) với đúng script này: thiếu `-f 65001` khi file không BOM → 51035, không đổi; thiếu `-I` → 51035, không đổi; lỗi giữa DDL trên dữ liệu thật → rollback toàn bộ, fingerprint không đổi; lệnh chuẩn → thành công, trigger trùng migration EF từng ký tự; chạy lại → no-op. `sqlcmd` dùng: `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE` 16.0.1000.6 |
| 6 | Tiến trình cần dừng | Lúc 18:54 **không có** `SmartCv.Api.exe`/`SmartCv.Web.exe`, `dotnet run`/`dotnet watch` SCApp nào, và không có listener 7269/5156/7258/5172. Trên `SmartCvDb`: 0 phiên, 0 transaction (phiên user duy nhất trên instance là `SQLServerCEIP` ở `master`). Worker chấm điểm và worker trích xuất chạy **bên trong** tiến trình API, không có tiến trình riêng. **Không dừng:** VS Code/C# Dev Kit (pid 25976), dịch vụ MSSQL của VS Code (19828, 24092), các node MSBuild rảnh từ bản build kiểm thử, dịch vụ SQL Server, LocalDB. Nếu anh/chị chạy lại API/Web trước Giai đoạn 2, cần dừng: API `dotnet run --project src/SmartCv.Api --launch-profile https` (con `SmartCv.Api.exe`) và Web `dotnet watch --project src/SmartCv.Web --launch-profile https` (con `SmartCv.Web.exe`) |
| 7 | Ảnh hưởng dữ liệu | Xem bảng bên dưới |
| 8 | Lệnh triển khai, xác minh, phục hồi | Mục M. Công cụ đã chuẩn bị và kiểm chứng, xem bên dưới |

**Ảnh hưởng tới dữ liệu (mục 7).** Script chỉ **thêm**: 4 cột, 1 bảng, 2 index, 8 CHECK, 1 FK, 1 trigger và 1 dòng `__EFMigrationsHistory`. Không có UPDATE/DELETE/DROP trên dữ liệu đang có. 44 hồ sơ hiện có nhận `Stage = New`, `StageVersion` mới, `RejectReason`/`StageUpdatedAt` NULL; không tạo lịch sử HR. Không truy cập file CV trên đĩa. Bằng chứng fingerprint (SHA-256 mọi cột cũ, so với `SmartCvDb` gốc):

| Nhóm dữ liệu | Bảng (số dòng) | Bản sao sau restore | Sau lỗi giữa DDL | Sau A35 | Sau chạy lại | Sau 6 quyết định HR (API + UI) | `SmartCvDb` lúc 18:54 |
|---|---|---|---|---|---|---|---|
| CV | Cvs 61, Candidates 12 | trùng | trùng | trùng | trùng | trùng | trùng |
| Điểm AI | Evaluations 69, EvaluationSkills 362 | trùng | trùng | trùng | trùng | trùng | trùng |
| Lịch sử Prompt | PromptVersions 2, EvaluationRuns 21 | trùng | trùng | trùng | trùng | trùng | trùng |
| Nhãn HR (A15) | HrLabels 10, EvaluationSets 1 | trùng | trùng | trùng | trùng | trùng | trùng |
| Hồ sơ (cột cũ) | Applications 44 | trùng | trùng | trùng | trùng | trùng | trùng |
| Job | Jobs 5, JobSkills 29, JobSkillAliases 52 | trùng | trùng | trùng | trùng | trùng | trùng |

Thay đổi hành vi theo thiết kế A35, áp dụng sau khi triển khai:
- Lịch sử quyết định chỉ được thêm (trigger chặn sửa/xóa).
- Hồ sơ đã có quyết định HR không thể bỏ gán khỏi Job (FK NO ACTION, và `ApplicationEndpoints` kiểm tra event).
- Trong lúc chạy script, `Applications` bị khóa Sch-M khoảng 0,3 giây; ứng dụng đã dừng nên không ảnh hưởng.

**Công cụ cho Giai đoạn 2** (trong `artifacts/`, không đưa lên Git):

| Công cụ | Kiểm chứng ở Giai đoạn 1 |
|---|---|
| `deploy-a35.ps1` | `-Step Preflight` chạy trên `SmartCvDb`: **đạt mọi kiểm tra** (đích, lịch sử, script, 71 GB trống, SCApp dừng, 0 phiên). Các câu truy vấn của `Verify` và kiểm tra tuổi backup của `Apply` chạy thử chỉ đọc: bản sao trả 0 đối tượng thiếu; `SmartCvDb` trả 17 (khớp readiness); tuổi backup 18:24 đọc được (30 phút). `Backup`/`Apply` **chưa chạy** |
| `post-deploy-readonly-checks.sql` | Chạy trên bản sao (với dòng kiểm tra tên DB điều chỉnh): ra đủ kết quả. Bản gốc từ chối database khác `SmartCvDb` (51098) |
| `post-deploy-http-checks.ps1` | Chạy trên API của bản sao: **22/22 PASS**, gồm so sánh điểm API với SQL |
| Trình duyệt chế độ `readonly` | Chạy trên Web/API của bản sao: **22/22 PASS**; SQL xác nhận **không ghi gì** (event, hash giai đoạn, Prompt, CV giữ nguyên). Chế độ `readwrite` từ chối mọi database không phải bản diễn tập |

Các tiến trình diễn tập (cổng 5391/5392) đã dừng theo đúng PID do tôi tạo; log API diễn tập không có exception. Dữ liệu tạm vẫn được giữ theo yêu cầu.

**Kết luận Giai đoạn 1:** sẵn sàng triển khai về mặt kỹ thuật. **`SmartCvDb` chưa thay đổi**; Giai đoạn 2 chỉ bắt đầu khi anh/chị phê duyệt riêng.

## P. Giai đoạn 2 — Triển khai A35 lên `SmartCvDb` (12/11/2026)

Thực hiện sau khi anh/chị phê duyệt có điều kiện, bằng runbook `artifacts/a35-runtime-tests/deploy-a35.ps1`. Nhật ký đầy đủ: `artifacts/a35-runtime-tests/phase2/runbook.log`.

### P1. Các bước và trạng thái

| Bước | Thời điểm | Trạng thái | Kết quả |
|---|---|---|---|
| Kiểm tra lại Git và script | 19:03 | **Đã làm** | Nhánh và HEAD `8f305849` như Giai đoạn 1; không stage. Nguồn `433B59A0…` và bản cho `SmartCvDb` `2E28750C…` trùng hash Giai đoạn 1. `@ExpectedDatabase = N'SmartCvDb'`. Script chỉ ghi một dòng history `20261112045258_ApplicationStages`, không có lệnh DROP/DELETE/TRUNCATE/UPDATE (4 dòng khớp từ khóa là `ON DELETE NO ACTION`, `INSTEAD OF UPDATE, DELETE` và chuỗi trong truy vấn kiểm tra). **Không dùng** bản cũ `47734E79…` |
| 1. Preflight | 19:03:46 | **Đạt** | `DESKTOP-I9O3636\BB` / `SmartCvDb`; đúng 2 migration; script nguyên vẹn; 71 GB trống; không có API/Web/worker, cổng SCApp hay phiên/transaction trên `SmartCvDb`; 0 lượt Queued/Running; 0 CV Pending. **Môi trường không đổi so với Giai đoạn 1** (fingerprint trùng lúc 18:54) |
| 2. Backup cuối | 19:03:56 | **Đạt** | Xem P2 |
| 3. Áp dụng A35 | 19:04:17 | **Đạt** | `sqlcmd -S localhost\BB -d SmartCvDb -E -b -I -f 65001 -i …pending-approval.sql`, chạy **một lần**, exit 0, `ApplicationStages ready`. Backup cuối có tuổi 1 phút; dữ liệu không đổi giữa backup và lúc áp dụng |
| 4. Hậu kiểm database | 19:04:22 | **Đạt** | Xem P3, P4 |
| 5. Khởi động lại API/Web | 19:09–19:12 | **Chưa ổn định** | Anh/chị đã khởi động. Binary API/Web build lúc 19:09:56 / 19:10:20 từ `8f305849`. Event log Windows lúc 19:09:58 xác nhận worker **TẮT** (`RunScoringWorker`: "Tiến trình chấm điểm đang TẮT"); không có lỗi, exception hay crash nào từ 19:05. Tuy nhiên lúc 19:12:15 không còn tiến trình API/Web, không có cổng 7269/5156/7258/5172 và kết nối bị từ chối, nên chưa kiểm tra HTTP được. Kiểm tra lại database lúc 19:12:40 (chỉ đọc): **đạt**, fingerprint trùng 19:04, 0 event, không có lượt chấm mới.<br>**Lần khởi động thứ hai, 19:17:32, có sai lệch:**<br>• API (pid 22920) chạy bằng `dotnet run --project src/SmartCv.Api --launch-profile https` **thiếu** `-- --Scoring:WorkerEnabled=false`.<br>• Event log không có cảnh báo "đang TẮT", nên **worker nhiều khả năng đang BẬT**.<br>• Đã dừng kiểm chứng và báo anh/chị; không đụng tới tiến trình.<br>• Kiểm tra chỉ đọc lúc 19:18:23: 0 lượt Queued/Running, 0 CV Pending, 0 event, dữ liệu trùng 19:04, chưa có lời gọi Gemini nào được kích hoạt |
| 5b. Khởi động lại API, worker tắt | 19:21:14 | **Đạt** | API pid 12736 chạy bằng `dotnet run --project src/SmartCv.Api --launch-profile https -- --Scoring:WorkerEnabled=false`; exe nhận đúng `--Scoring:WorkerEnabled=false`; event log 19:21:14 ghi "Tiến trình chấm điểm đang TẮT". **Web không chạy** trong lần kiểm tra này: không có `SmartCv.Web.exe`, cổng 7258 từ chối kết nối |
| 6. Kiểm tra HTTP thật (GET) | 19:22–19:23 | **Đạt** | Xem P6, P7 |
| 7. Kiểm tra giao diện thật (chỉ đọc) | — | **Chưa làm** | Cần Web đang chạy |

Không có thao tác nào ngoài các bước trên:
- Không restore, DROP, DELETE, TRUNCATE hay reset.
- Không sửa tay migration history.
- Không xóa hay ghi đè backup nào.
- Không PATCH/POST/DELETE qua API.
- Không khởi động API/Web, không gọi Gemini.

### P2. Backup cuối

| Mục | Giá trị |
|---|---|
| File | `C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\Backup\SmartCvDb_before_A35_final_20261112_190356.bak` (tên mới, đã kiểm tra chưa tồn tại) |
| Thời điểm | 19:03:56 (bắt đầu = kết thúc) |
| Tùy chọn | `COPY_ONLY`, `CHECKSUM`; `is_damaged = 0`; 946 trang, 7,46 MB |
| `RESTORE VERIFYONLY ... WITH CHECKSUM` | `The backup set on file 1 is valid.` |
| Dữ liệu trong lúc backup | Fingerprint trước và sau backup trùng nhau |
| Backup cũ | `SmartCvDb_before_A35_20261112_182452.bak` vẫn còn, không bị ghi đè. Hai bản ngày 06/11 vẫn chỉ còn trong lịch sử msdb (file đã mất từ trước, D2) |

### P3. Migration history và schema

| | Trước (19:03) | Sau (19:04) |
|---|---|---|
| `__EFMigrationsHistory` | `20261106132922_InitialCreate`, `20261106142155_SeedJobs` | thêm `20261112045258_ApplicationStages` (ProductVersion 10.0.15) |
| Cột `Applications` | Không có | `Stage` nvarchar(20) NOT NULL DEFAULT `N'New'`; `RejectReason` nvarchar(30) NULL; `StageUpdatedAt` datetime2 NULL; `StageVersion` uniqueidentifier NOT NULL DEFAULT `newid()` |
| `ApplicationEvents` | Không có | Có |
| 8 CHECK | Không có | Đủ, enabled, trusted |
| 2 index A35 | Không có | `IX_Applications_Job_Stage`, `IX_ApplicationEvents_Application_CreatedAt` |
| FK | Không có | `FK_ApplicationEvents_Applications`, NO ACTION, trusted |
| Trigger | Không có | `TR_ApplicationEvents_NoChange` (INSTEAD OF UPDATE, DELETE), `uses_quoted_identifier = 1`, `uses_ansi_nulls = 1`, thông điệp Unicode đúng |
| Đối tượng A35 còn thiếu | 17 | **0** |

### P4. Dữ liệu trước/sau

Fingerprint gồm số dòng chính xác và SHA-256 trên mọi cột cũ, sắp theo PK; chỉ so hash, không in dữ liệu ứng viên.

| Bảng | 18:25 (sau backup đầu) | 19:04 (trước áp dụng) | 19:04 (sau áp dụng) | Dữ liệu cũ không đổi |
|---|---:|---:|---:|---|
| Cvs (CV) | 61 | 61 | 61 | Có |
| Candidates | 12 | 12 | 12 | Có |
| Evaluations (điểm AI) | 69 | 69 | 69 | Có |
| EvaluationSkills | 362 | 362 | 362 | Có |
| PromptVersions (Prompt) | 2 | 2 | 2 | Có |
| EvaluationRuns (lượt chấm/lịch sử Prompt) | 21 | 21 | 21 | Có |
| HrLabels | 10 | 10 | 10 | Có |
| EvaluationSets | 1 | 1 | 1 | Có |
| Jobs | 5 | 5 | 5 | Có |
| JobSkills | 29 | 29 | 29 | Có |
| JobSkillAliases | 52 | 52 | 52 | Có |
| Applications (cột cũ) | 44 | 44 | 44 | Có |

Dữ liệu A35 mới sinh ra:
- 44/44 hồ sơ `Stage = New`; `RejectReason` và `StageUpdatedAt` đều NULL.
- 44 `StageVersion` khác nhau, không GUID rỗng.
- **`ApplicationEvents` = 0 dòng**, không có quyết định HR giả.

Run 25 vẫn Completed 3/3. Evaluations 67/68/69 vẫn 31.33 / 31.33 / 38.00.

### P5. Khởi động API/Web với worker tắt (phương án a — anh/chị tự chạy)

`launchSettings.json` (profile `http`/`https`) gán biến môi trường `Scoring__WorkerEnabled=true` cho tiến trình. Vì vậy đặt `$env:Scoring__WorkerEnabled` trong terminal **không có tác dụng**: launch profile sẽ ghi đè nó.

Trong ASP.NET Core, tham số dòng lệnh được ưu tiên hơn biến môi trường. Do đó cần truyền `--Scoring:WorkerEnabled=false` **sau dấu `--`**. Đây là ghi đè tạm thời cho lần chạy này; không sửa file cấu hình.

```powershell
# Terminal 1 — API (tại C:\TTCN\project\AI-CV-Screening)
dotnet run --project src/SmartCv.Api --launch-profile https -- --Scoring:WorkerEnabled=false
# Terminal 2 — Web
dotnet watch --project src/SmartCv.Web --launch-profile https
```

Hai dòng trên chạy được như nhau trong CMD.

Xác nhận trong log API phải có dòng:
`Tiến trình chấm điểm đang TẮT (Scoring:WorkerEnabled = false)`
Nếu thấy `Tiến trình chấm điểm đã chạy (kiểm tra mỗi … giây)`, hãy Ctrl+C ngay.

Khi worker tắt và chỉ có thao tác đọc, không phát sinh lời gọi Gemini: Web không gọi `/api/health/gemini` hay `ai-preview`, và các bước kiểm tra không tạo lượt chấm hay chấm thử.

**Sau khi khởi động (sẽ chạy, chỉ GET/đọc):**
- `post-deploy-http-checks.ps1 -BaseUrl https://localhost:7269 -Database SmartCvDb -ExpectAllNew`
- Trình duyệt chế độ `readonly` trên `https://localhost:7258`
- `deploy-a35.ps1 -Step Verify` lần nữa, để xác nhận các bước kiểm tra không ghi gì

### P6. Kiểm tra HTTP trên hệ thống thật (19:22, chỉ GET)

Công cụ: `post-deploy-http-checks.ps1 -BaseUrl https://localhost:7269 -Database SmartCvDb -ExpectAllNew`. Kết quả: **23/23 PASS**.

| Endpoint | Trước migration (18:2x) | Sau migration (19:22) |
|---|---|---|
| `GET /api/health/database` | **503** `SchemaOutOfDate`, thiếu 17 đối tượng | **200** `OK`, `database = SmartCvDb`, pending 0, missing 0 |
| `GET /api/runs/25/ranking` | **500** SQL 207 | **200**, 3 hồ sơ, `StageCounts.New = 3`, mọi hồ sơ `New` và có `StageVersion` |
| `GET /api/evaluations/67`, `68`, `69` | **500** SQL 207 | **200**; `finalScore` 31.33 / 31.33 / 38.00, trùng SQL |
| `GET /api/applications/43`, `44`, `45` `/events` | **500** SQL 208 | **200**, 0 event (không có quyết định HR giả) |
| `GET /api/jobs`, `/api/runs`, `/api/runs/latest`, `/api/runs/25`, `/api/applications?jobId=1`, `/api/applications/stage-options`, `/api/evaluation-sets`, `/api/evaluation-sets/1/applications`, `/api/prompt-tests`, `/api/prompts` | 200 | 200 |
| Run/Job không tồn tại | — | 404 (đúng) |

### P7. Quét toàn bộ và đối chiếu API với SQL (19:22–19:23, chỉ GET)

Công cụ: `post-deploy-sweep.ps1`. Chỉ in ID, số lượng và PASS/FAIL, không in dữ liệu ứng viên.

| Kiểm tra | Kết quả |
|---|---|
| `GET /api/evaluations/{id}` cho **toàn bộ 69** Evaluation | **200 × 69**, không còn 500 |
| `GET /api/runs/{id}/ranking` cho **toàn bộ 21** lượt chấm | **200 × 21** |
| Run 25: 3 Evaluation × 12 trường (ApplicationId, CvId, điểm keyword/AI/final, ScoreSource, FitLabel, PredictedPass, YearsExperience, Status, Stage, StageVersion) | **0 sai khác** với SQL |
| Header Run 25 | API `Completed/3/3/0` = SQL `Completed/3/3/0` |
| Jobs; Prompt (tổng / đang dùng) | API 5 = SQL 5; API 2/1 = SQL 2/1 |

Lần quét đầu báo sai khác ở `predictedPass`. Nguyên nhân là lỗi trong **công cụ so sánh của tôi**: bit SQL `0` bị định dạng thành `0.00`, còn `false` của API thành `0`. Giá trị thật trùng nhau (`false` = `0` cho cả 3 Evaluation). Đã sửa bộ so sánh và chạy lại: 0 sai khác.

**Không ghi dữ liệu:** sau toàn bộ kiểm tra, fingerprint 12 bảng trùng lúc 19:04. `ApplicationEvents` = 0; 44 hồ sơ vẫn `New`, `StageUpdatedAt` NULL; RunId lớn nhất vẫn 25; 0 lượt Queued/Running.

### P8. Log API/Web

API ghi cảnh báo và lỗi vào Windows Application event log. Từ 19:05 đến lần kiểm tra cuối:

| Thời điểm | Mức | Nội dung |
|---|---|---|
| 19:09:58, 19:21:14 | Warning | `RunScoringWorker`: "Tiến trình chấm điểm đang TẮT (Scoring:WorkerEnabled = false)" — đúng như yêu cầu |
| 19:22:04 | Warning | EF Core 10103 trên `GET /api/prompt-tests`: `First/FirstOrDefault` không có `OrderBy`. Xem P10 |

**Không có Error, Critical, crash, exception hay HTTP 500 nào.** Lần khởi động 19:17:32 (thiếu tham số tắt worker) không ghi cảnh báo "TẮT"; tiến trình đó đã được anh/chị dừng lúc khoảng 19:21. Log console của Web (mức Information) nằm trong terminal của anh/chị, tôi không đọc được.

### P9. Build và regression test

- Không chạy lại. Từ lần test đầy đủ lúc 18:41 (build 0 warning/0 error; **1361 Passed, 0 Failed, 38 Skipped**; mục I), **không có file biên dịch nào thay đổi**: chỉ có báo cáo, sửa lúc 19:05. Mã ứng dụng trùng `main` (`git diff 8f305849 -- src` rỗng).
- Chạy lại sẽ chỉ tạo thêm database LocalDB giữ lại, nên không làm.
- API/Web anh/chị chạy được build lúc 19:09:56 / 19:10:20 từ `8f305849` (`ProductVersion 1.0.0+8f305849…`), đã có #52.

### P10. Quan sát thêm (không phải lỗi A35, không sửa)

- **EF Core 10103 trên `/api/prompt-tests`:** sinh từ `HrLabels.Where(h => h.EvaluationSetId == setId).Select(…).FirstOrDefault()` trong `PromptTestQueries.cs:87-90` (và `PromptCompareQueries.cs:167-170`). Khóa chính `HrLabels` là `(EvaluationSetId, ApplicationId)`, nên mỗi hồ sơ trong một bộ đánh giá có tối đa một nhãn. Kết quả vì vậy xác định; cảnh báo là **dương tính giả**. Không sửa.
- **`GET /api/prompt-tests` có thể ghi:** `LoadSummariesAsync` tính lại `AccuracyPercent` và lưu nếu giá trị đã lưu khác (hành vi B15 có sẵn). Trong lần kiểm tra này **không có thay đổi** (fingerprint `EvaluationRuns` gồm mọi cột, trùng 19:04). Lưu ý: GET này không hoàn toàn chỉ đọc. Nếu muốn đổi thiết kế, nên làm thành task riêng.

### P11. Thay đổi Git và lệnh commit/push đề xuất

`git status --short --branch` (nhánh `fix/A35-runtime-database-migration`, HEAD `8f30584`, không stage):

| File | Trạng thái | Thay đổi |
|---|---|---|
| `database/runtime_apply_application_stages.sql` | Sửa | +16/−2. BOM UTF-8; kiểm tra bảng mã và `QUOTED_IDENTIFIER` trước transaction; hậu kiểm trigger. DDL không đổi |
| `database/check_runtime_schema.sql` | Sửa | +5/−2. Hiển thị SET options và `MessageText` của trigger |
| `database/README.md` | Sửa | +26. Script A35/runtime; cú pháp `sqlcmd` CMD/PowerShell |
| `tests/SmartCv.Tests/RuntimeScriptDeploymentTests.cs` | Mới | 121 dòng. 2 test luôn chạy và 1 SQL test opt-in |
| `docs/A35-runtime-database-fix-report.md` | Mới | Báo cáo này |

Không có file nào trong `artifacts/` (backup, fingerprint, kết quả kiểm tra, runbook): thư mục này nằm trong `.gitignore`. Không có database hay dữ liệu ứng viên trong 5 file trên.

**Trước khi push**, báo cáo hiện có tên máy `DESKTOP-I9O3636` (3 chỗ) và tên tài khoản Windows chạy dịch vụ SQL (1 chỗ, mục F). Không có email, tên ứng viên hay tên file CV. Nếu không muốn đưa các thông tin này lên GitHub, tôi có thể thay bằng chỗ giữ chỗ trước khi anh/chị commit.

Lệnh đề xuất (anh/chị tự chạy, PowerShell, tại gốc repository):

```powershell
git add database/runtime_apply_application_stages.sql database/check_runtime_schema.sql database/README.md tests/SmartCv.Tests/RuntimeScriptDeploymentTests.cs docs/A35-runtime-database-fix-report.md
git diff --cached --stat
git commit -F - @'
fix(A35): make ApplicationStages runtime deployment safe under sqlcmd

- runtime_apply_application_stages.sql: save as UTF-8 with BOM; stop before any
  change when the file is decoded with the wrong code page or QUOTED_IDENTIFIER
  is OFF; verify the trigger matches the EF Core migration after deployment
- check_runtime_schema.sql: show trigger SET options and message encoding
- database/README.md: document A35/runtime scripts and sqlcmd -I -f 65001
- RuntimeScriptDeploymentTests: regression tests for BOM, guards and
  sqlcmd default (Windows-1252) decoding
- docs: A35 runtime database fix and deployment report

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
'@
git push -u origin fix/A35-runtime-database-migration
```

Sau khi push, tạo PR vào `main`. Nhóm đang squash-merge, nên nhánh khác phụ thuộc vào nhánh này cần rebase/merge lại sau khi PR được merge.
