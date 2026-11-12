# Báo cáo full system audit và bug fix SCApp

Ngày thực hiện: 12/11/2026. Repository: `C:\TTCN\project\AI-CV-Screening`.

## A. Tổng quan

**Bản sửa code sẵn sàng để người dùng duyệt và commit. Môi trường ứng dụng chưa được xác nhận vận hành đầy đủ:** database cấu hình vẫn thiếu A35; API/Web người dùng không chạy trong thời gian kiểm tra; worker chấm điểm đang tắt trong cấu hình. Không áp dụng migration hoặc đổi cấu hình ứng dụng trong phiên này.

Đã xác nhận và sửa ba lỗi: worker trích xuất bỏ quên CV Pending khi database lỗi lúc startup; polling tiến độ trên Upload không dừng khi API lỗi kéo dài hoặc trả lỗi không nên retry; thời gian cập nhật tiến độ bị đổi dù retry thất bại. Đã bổ sung SQL integration cho concurrency/rollback và kiểm thử browser các luồng liên quan, giữ nguyên thiết kế và chức năng hiện có.

### Nhánh và nền mã

- Working tree ban đầu sạch: không staged, modified, untracked hay conflict. Nhánh ban đầu `fix/scapp-runtime-api-errors`, HEAD `11cdf362d721e4ca2cc3cc18daac19d49256c3df`.
- Origin đúng: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`. Đã chạy đúng `git fetch origin --prune` theo ủy quyền.
- GitHub xác nhận cả ba PR đã merge vào main; đã kiểm tra commit merge là ancestor của `origin/main`:

| Dependency | PR | Commit merge |
|---|---|---|
| A35 | [#49](https://github.com/TuyetAnh0101/AI-CV-Screening/pull/49) | `6554f3badc53c5dc06ec8166d9a6f68cd10698ca` |
| B34 | [#50](https://github.com/TuyetAnh0101/AI-CV-Screening/pull/50) | `7566dcef0985be3f8c3b65395957fe482a1f4f1f` |
| Runtime Fix | [#51](https://github.com/TuyetAnh0101/AI-CV-Screening/pull/51) | `c7d0c8ad7c782a42096e84e2b27ed2a34688bb9a` |

- Nhánh đích chưa tồn tại local và remote; đã tạo duy nhất `fix/scapp-end-to-end-stability-20261112` trực tiếp từ `origin/main`. Working tree sạch ngay sau switch.
- Commit nền và HEAD cuối: **`c7d0c8ad7c782a42096e84e2b27ed2a34688bb9a`**. Không commit trong phiên này.
- Không tìm thấy AGENTS.md áp dụng tại repository/các thư mục cha đã kiểm tra.

### Môi trường

- Solution thực tế `SmartCv.slnx`; SDK/global.json `10.0.403`; target `net10.0`; EF Core `10.0.15`, SqlClient `6.1.7`.
- Output riêng: `artifacts/full-audit-build`. Restore trước baseline vì thư mục này chưa có assets; không đổi dependency của solution.
- SQL integration chỉ dùng LocalDB và database tên `SmartCv_Runtime_Test_<GUID>` mới. Các database được giữ lại, không DROP/reset.
- Browser: Chromium headless qua Playwright; API/Web thật từ output audit, database test, fake AI. Không gọi Gemini thật hoặc đổi User Secrets.

## B. Danh sách lỗi và trạng thái

| STT | Chức năng | Lỗi | Root cause | File sửa | Kết quả | Trạng thái |
|---|---|---|---|---|---|---|
| 1 | Ranking/Evaluation — High | Schema ứng dụng chưa hỗ trợ code A35, gây SQL207 như log HTTP500 đã ghi nhận | Thiếu migration `20261112045258_ApplicationStages` trên database cấu hình | Không sửa migration; dùng SQL đã có, chuẩn bị bản chờ duyệt | SQL ứng dụng chỉ đọc xác nhận thiếu bốn cột; SQL test tái hiện API500 rồi 200 sau migration | Cần duyệt triển khai database |
| 2 | Kết nối — Medium, trạng thái môi trường | Không kết nối được API/Web ở lần audit hiện tại | Không có process SCApp người dùng/listener tại các cổng cấu hình | Không sửa code/config theo suy đoán | GET health/readiness/ranking/evaluation hiện tại không có kết nối | Cần chạy lại bản build ứng dụng để xác minh thực tế |
| 3 | Queue phân tích — Medium, cấu hình | Lượt mới sẽ giữ Queued khi worker tắt | `Scoring:WorkerEnabled=false` từ appsettings.json | Không tự bật worker | Xác nhận nguồn cấu hình; test riêng bật worker chỉ trên DB test và fake AI | Cần người dùng quyết định cấu hình, không phải nguyên nhân của SQL207 |
| 4 | Trích xuất — High | Pending CV không được tự nạp sau khi database phục hồi từ lỗi startup | Nạp pending một lần, catch/log rồi chờ hàng đợi trong bộ nhớ mãi | `CvExtractionWorker.cs`, `CvExtractionProcessorTests.cs` | Hai case startup lỗi một/hai lần thất bại trước sửa, pass sau sửa; mỗi CV đọc một lần | Đã sửa |
| 5 | Upload/progress — Medium | Polling vô hạn với server lỗi/offline; 400/401/403/409 vẫn bị gọi lại | Chỉ tăng khoảng chờ, không có điểm dừng | `Upload.razor`, `RunProgressUiTests.cs` | Dừng sau ba lỗi liên tiếp; lỗi không retry dừng ngay; manual retry phục hồi, không POST lại | Đã sửa |
| 6 | Upload/progress — Low | Hiển thị thời gian cập nhật mới khi retry vẫn thất bại | StartTracking gán UtcNow cho dữ liệu cũ trước xác nhận API | `Upload.razor`, `RunProgressUiTests.cs` | Assert timestamp thất bại trước sửa và pass sau sửa | Đã sửa |
| 7 | Test automation — Low | Helper test có thể bấm nhầm khi đoạn hướng dẫn chứa tên nút Retry | Tìm text đầu tiên rồi lấy onclick gần nhất, không giới hạn trong button | `RunProgressUiTests.cs` | Chọn button theo render subtree; assert phục hồi và số GET/POST giữ nguyên; full suite pass | Đã sửa helper, không thay assert để lấy kết quả xanh |

Không phát hiện Critical mới có đủ bằng chứng. Không tuyên bố mọi lỗi tiềm ẩn đều đã được loại bỏ.

## C. Chi tiết khắc phục và bằng chứng

### C1. HTTP500: code, schema, cấu hình và runtime được phân biệt

**Schema:** cấu hình hiệu lực từ file, User Secrets và environment của phiên chẩn đoán trỏ `localhost\BB`, database `SmartCvDb`; connection string lấy từ `secrets.json`. Chỉ in tên server/database và metadata, không in thông tin đăng nhập hoặc API key.

SQL Server `16.0.1000.6`; database ONLINE, MULTI_USER. History có đúng:

```text
20261106132922_InitialCreate
20261106142155_SeedJobs
```

Thiếu migration A35; thiếu Stage, RejectReason, StageUpdatedAt, StageVersion; không có ApplicationEvents, hai index A35, tám CHECK constraint A35, FK lịch sử và trigger NoChange. Truy vấn chỉ đọc thử bốn cột trên database cấu hình trả **bốn lỗi SQL207**. Run25 có thật, Completed, 3/3 hồ sơ, Failed=0; Evaluation67/68/69 có thật. Không ghi thử trên các hồ sơ này.

**API/Web hiện tại:** không có listener 7269/5156/7258/5172 hoặc process SCApp người dùng được tìm thấy; các GET tới API cấu hình đều báo không kết nối. Không có exception HTTP mới/inner exception mới từ một API người dùng đang chạy. Không tự khởi động worker trên database ứng dụng để tái hiện vì có thể thay đổi dữ liệu/call AI thật.

**Bản build thường:** API/Web trong `src/*/bin/Debug/net10.0` có ProductVersion `1.0.0+11cdf362d721e4ca2cc3cc18daac19d49256c3df`, thời gian build khoảng 17:03 ngày 12/11. Đã kiểm tra tree commit này giống `origin/main` trước khi sửa: không có diff. Khác SHA với commit squash #51 chưa chứng minh binary thiếu Runtime Fix. Binary thường chưa chứa các sửa mới chưa commit của đợt audit này; helper dùng output audit riêng. Vì ứng dụng không chạy, không thể đọc environment hoặc xác nhận binary được một process người dùng thực sự nạp.

**Luồng đã đối chiếu:** Results → RunApiClient/ApiClientBase → `/api/runs/{id}/ranking` → RunEndpoints.RankingAsync → EF projection qua Applications → SQL Server. Log đã ghi ở đợt trước chỉ tới `RunEndpoints.cs:185`, SQL207 cho bốn cột A35. Mã hiện tại vẫn cần các cột này. Không có bằng chứng connection string đang trỏ nhầm database; database đang cấu hình thực sự chưa được nâng schema.

**Bằng chứng hiện tại trên SQL cô lập:** `RuntimeSchemaSqlTests` tạo schema trước A35, seed dữ liệu tổng hợp có Run24/25, gọi API thật bằng TestServer: ranking25=500/SQL207 và readiness=503. Sau script A35: ranking24/25=200, readiness=200, Evaluation=200, dữ liệu cũ có fingerprint không đổi. Query SQL đã phát sinh được ghi không kèm giá trị tham số ứng viên. Lỗi không bị chuyển thành 200 giả.

Huy hiệu “Đã kết nối API” dùng `/api/health` để kiểm tra liveness, không kiểm tra schema/Gemini/worker. Nó có thể xanh trong khi database readiness=503. Đã giữ nguyên chức năng này và giải thích phạm vi, không coi đổi thông báo là sửa lỗi Backend.

### C2. Worker trích xuất phục hồi từ lỗi startup

Trước sửa, RequeuePendingAsync chỉ catch và log lỗi rồi trả về. ExecuteAsync tiếp tục chờ Channel trong bộ nhớ, còn CV Pending lưu từ phiên trước không có tác nhân xếp lại. Test seed Pending CV, gây lỗi resolve database ở một/hai lần đầu rồi cho database dùng được: trước sửa CV vẫn Pending sau thời hạn kiểm tra.

Sau sửa, worker thử lại việc đọc/nạp danh sách pending với khoảng chờ 1, 2, 4… tối đa 30 giây. Mỗi lần tạo scope mới; log exception và số lần lỗi; hỗ trợ hủy khi shutdown. Chỉ retry bước đọc ID pending, không replay OCR hoặc Gemini. Queue sẵn có chống xếp trùng ID. Hai regression xác nhận CV được xử lý một lần; test worker/processor cũ giữ nguyên và pass.

Rủi ro: khi database chưa phục hồi, bước nạp lúc startup vẫn chờ và log có backoff; không báo thành công giả. Kiểm thử transient startup dùng lỗi database được mô phỏng qua DI, không gây mất kết nối database ứng dụng để thử.

### C3. Polling và timestamp Upload

Trước sửa, 500 hoặc offline khiến PollLoop chạy mãi; cả 400/401/403/409 cũng tiếp tục được GET. StartTracking đồng thời gán thời gian mới cho run cũ ngay khi retry, dù API chưa phản hồi thành công.

Sau sửa: ngừng sau ba lỗi liên tiếp, dừng ngay với 400/401/403/409; giữ xử lý 404 và Completed/Failed hiện có. Giữ dữ liệu gần nhất, cho retry chủ động; thành công đặt lại bộ đếm. Thời gian chỉ đổi khi GET/POST đã xác nhận dữ liệu. Không thay khoảng chờ thành công, bộ lọc, giao diện hay logic gửi quyết định HR.

Regression gồm 500, mất kết nối, bốn status không retry, timestamp, retry phục hồi và kiểm tra không phát sinh POST. Test kết nối mất tạm thời cũ vẫn tự phục hồi ở lần tiếp theo trước giới hạn. Không giảm assert cũ; sửa helper để chỉ chọn onclick trong chính button, tránh bấm text hướng dẫn chứa “Thử lại ngay”.

### C4. Tính toàn vẹn và concurrency trên SQL Server

Hai SQL test mới dùng database GUID riêng, giữ lại sau test:

- Ép hai request HR cùng đọc phiên bản cũ rồi cùng tới SaveChanges, không gửi ExpectedStageVersion để tránh chỉ kiểm tra xung đột lúc đọc. Kết quả đúng một 200, một 409, một event được commit; Unchanged không thêm event; đổi hai lý do loại và mở lại sang Interview tạo đúng lịch sử.
- Sau quyết định, 32 GET ranking đồng thời và 32 GET Evaluation đồng thời đều thành công, giai đoạn/số đếm đúng, điểm AI giữ nguyên. Chuỗi kiểm tra cuối ghi khoảng 3354 ms trên LocalDB test; đây không phải SLA hay stress test sản xuất.
- Bulk bị lỗi SQL51050 cố ý ở lệnh ghi thứ hai, sau khi lệnh đầu đã chạy: HTTP500 được giữ đúng cho lỗi server, toàn bộ Stage/Reason/Timestamp/Version và lịch sử rollback. MaxBatchSize=1 chỉ dùng trong nhánh test fault injection, không thay cấu hình ứng dụng.

Thiết kế test fault injection ban đầu thử thêm trigger vào Applications làm EF OUTPUT gặp SQL334 trước điểm lỗi mong muốn. Đã chuyển sang chèn lỗi tại command thứ hai để kiểm chứng rollback thực, giữ assert về điểm lỗi và toàn vẹn dữ liệu. Không sửa production model/constraint để vượt test.

## D. Database và phương án cần phê duyệt riêng

### Trạng thái và backup

Database ứng dụng vẫn thiếu A35 sau audit. Toàn bộ truy vấn trên `localhost\BB/SmartCvDb` trong phiên này là SELECT; không backup, migrate, sửa dữ liệu hay migration history. Số CV theo trạng thái ở hai lần kiểm tra giữ 60 Completed, 1 Failed.

Metadata msdb ghi nhận backup full COPY_ONLY gần nhất lúc `2026-11-06 21:27:47`. Đó chưa phải bằng chứng file còn đọc được hoặc đủ mới cho lần triển khai này. Chưa chạy VERIFYONLY/restore thử trên backup ứng dụng. Thư mục backup mặc định do instance báo:

```text
C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\Backup
```

Đã xác nhận sqlcmd có sẵn và chạy được **SELECT chỉ đọc** tới đúng server/database; lịch sử vẫn hai migration. Quyền ghi thư mục backup của SQL Server service và dung lượng trống chưa được kiểm chứng.

### Script đã kiểm chứng trên test

Dùng nguyên `database/check_runtime_schema.sql` và `database/runtime_apply_application_stages.sql` đã merge ở #51; không viết lại A35 hoặc sửa migration/snapshot.

- Guard tên database, dependency SeedJobs, schema áp dụng dở, transaction/XACT_ABORT/application lock.
- Kiểm tra đầy đủ columns/table/index/check/FK/trigger trước commit.
- Test guard chưa duyệt bị chặn, lỗi giữa DDL rollback, chạy lại giữ nguyên dữ liệu/lịch sử/StageVersion; trigger chặn sửa/xóa lịch sử.

Đã tạo bản để duyệt tại `artifacts/full-audit-tests/application-stages-SmartCvDb.pending-approval.sql`, chỉ thay `@ExpectedDatabase` thành `SmartCvDb`. **Chưa thực thi bản này.** SHA256:

```text
Nguồn: 1F38CAA4121DAEA0B33756E6F57EBEF6DE42FA219A9E8EB975F67018A947484D
Bản chờ duyệt: 47734E7951BD6CCF3EACD630F0664DDAA3554A696FAD5CD8F668209616988892
```

### Lệnh đề xuất sau khi người dùng phê duyệt — chưa chạy

Người triển khai xác nhận instance/database và khoảng bảo trì trước. Chọn file backup mới, không ghi đè backup có sẵn. SQL Server service cần quyền ghi thư mục đích.

```powershell
# CHỈ SAU KHI ĐƯỢC DUYỆT. Chạy tại gốc repository.
$scBackupDirectory = 'C:\Program Files\Microsoft SQL Server\MSSQL16.BB\MSSQL\Backup'
$scBackupFile = Join-Path $scBackupDirectory ('SmartCvDb_before_A35_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.bak')
$scSqlBackupFile = $scBackupFile.Replace("'", "''")
sqlcmd -S 'localhost\BB' -d master -E -b -Q "BACKUP DATABASE [SmartCvDb] TO DISK=N'$scSqlBackupFile' WITH COPY_ONLY, CHECKSUM, STATS=5;"
if ($LASTEXITCODE -ne 0) { throw 'Backup failed; stop deployment.' }
sqlcmd -S 'localhost\BB' -d master -E -b -Q "RESTORE VERIFYONLY FROM DISK=N'$scSqlBackupFile' WITH CHECKSUM;"
if ($LASTEXITCODE -ne 0) { throw 'Backup verification failed; stop deployment.' }
sqlcmd -S 'localhost\BB' -d SmartCvDb -E -b -i database/check_runtime_schema.sql
if ($LASTEXITCODE -ne 0) { throw 'Schema audit failed; stop deployment.' }

# Có thể tái tạo bản chờ duyệt nếu artifacts không còn; đọc lại diff trước khi chạy.
$scSource = Get-Content database/runtime_apply_application_stages.sql -Raw -Encoding utf8
$scApproved = $scSource.Replace("DECLARE @ExpectedDatabase sysname = N'REVIEW_DATABASE_NAME_BEFORE_EXECUTION';", "DECLARE @ExpectedDatabase sysname = N'SmartCvDb';")
New-Item -ItemType Directory -Path artifacts/full-audit-tests -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'artifacts/full-audit-tests/application-stages-SmartCvDb.pending-approval.sql'), $scApproved)
# DỪNG để đọc/duyệt SQL đã tạo và xác nhận đích trước lệnh bên dưới.
sqlcmd -S 'localhost\BB' -d SmartCvDb -E -b -i artifacts/full-audit-tests/application-stages-SmartCvDb.pending-approval.sql
if ($LASTEXITCODE -ne 0) { throw 'Migration failed; investigate before proceeding.' }
sqlcmd -S 'localhost\BB' -d SmartCvDb -E -b -i database/check_runtime_schema.sql
```

Cú pháp backup và VERIFYONLY đã đối chiếu [Microsoft BACKUP](https://learn.microsoft.com/en-us/sql/t-sql/statements/backup-transact-sql) và [RESTORE VERIFYONLY](https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-verifyonly-transact-sql). VERIFYONLY kiểm tra backup đọc được, không thay thế hoàn toàn thử restore vào môi trường được duyệt riêng.

Kết quả dự kiến: history thêm A35; đủ bốn cột, ApplicationEvents, hai index, tám CHECK, FK và trigger; các hồ sơ cũ có New/RejectReason null/StageUpdatedAt null/StageVersion hợp lệ. Không thêm lịch sử HR giả hoặc sửa điểm AI. Nếu script lỗi, kiểm tra rollback và dừng; không Down/drop/recreate hoặc sửa trực tiếp history.

Sau triển khai, chạy lại API/Web bản mới và gọi readiness=200, ranking25=200, Evaluation67/68/69=200; kiểm tra lại polling/reload. **Worker vẫn tắt** cho đến quyết định riêng của người dùng. Trước khi bật cần xem hàng Queued/Pending, model/quota/chi phí Gemini và mục đích môi trường. Không bật fake AI lên database ứng dụng để tạo điểm giả.

## E. Kiểm thử và phạm vi audit

### Kết quả

Baseline: build 0 warning/0 error; **1345 Passed, 0 Failed, 40 Skipped / 1385**. Hai test opt-in chưa bật lúc baseline là Runtime SQL và demo ZIP; không tính là pass.

Final: build **0 warning, 0 error**; **1358 Passed, 0 Failed, 38 Skipped / 1396**, khoảng 69 giây. Có thêm 11 test cases: 7 progress/timestamp, 2 worker startup recovery, 2 SQL concurrency/rollback.

| Nhóm kiểm thử | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit Tests | 776 | 0 | 0 |
| Integration Tests | 82 | 0 | 38 |
| API Tests | 258 | 0 | 0 |
| UI Tests | 242 | 0 | 0 |
| Full Regression | **1358** | **0** | **38** |

Các nhóm được phân theo test class để không cộng trùng: UI/component/workspace; SQL/persistence/worker/processor/host integration; endpoint API; phần còn lại unit. Một class có thể gồm cả unit helper và kiểm thử tích hợp; danh sách từng case và quy tắc phân nhóm lưu trong `test-classification.csv`, số nhóm ở `test-group-counts.json`. Dòng Full Regression là tổng bốn nhóm, không cộng lần nữa.

38 skipped là SQL fixtures cũ: A11=6, A15=7, A16=10, A17=3, Prompt SQL=5, A35 SQL=7. Không bật fixture có EnsureDeleted/lifecycle xóa database vì ràng buộc an toàn phiên này. Không đổi test cũ hoặc coi skipped là passed. Ba SQL facts an toàn đã chạy thật: RuntimeSchemaSqlTests và hai FullAuditSqlTests, database đều giữ lại. Migration order/model `HasPendingModelChanges()` pass.

Trước sửa, tám regression đầu thất bại đúng triệu chứng, timestamp test riêng thất bại với expected/actual thời gian khác nhau. Các lỗi của fixture/helper test phát sinh khi xây dựng kiểm thử mới đã được giải thích và sửa ở C3/C4; assert về nghiệp vụ được giữ. Focused sau sửa **44 Passed, 0 Failed, 0 Skipped**. Lỗi Evaluation 500 không rõ stack từng ghi ở báo cáo trước không tái hiện trong baseline/full cuối đợt này; đã thêm 32 Evaluation GET song song trên SQL và kiểm tra trang chi tiết thật, không quy kết lỗi cũ nếu chưa có stack của lần đó.

Lệnh cuối đã thực thi (output riêng, không dừng tiến trình người dùng):

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/full-audit-build --no-restore
dotnet test SmartCv.slnx --artifacts-path artifacts/full-audit-build --no-restore --no-build --logger "trx;LogFileName=final.trx" --results-directory artifacts/full-audit-tests
```

Trong process test đã bật `SMARTCV_RUNTIME_TEST_SQL_CONNECTION` cho LocalDB test và `SMARTCV_B34_DEMO_ZIP` cho ZIP demo. Không thay connection string/User Secrets của ứng dụng.

### API thực tế đã khảo sát

Đối chiếu source và `/openapi/v1.json` của build test: **42 operations**, danh sách đầy đủ tại `artifacts/full-audit-tests/api-endpoint-inventory.json`. Không giả định có API Candidates hay Dashboard riêng.

| Nhóm | Endpoint/thao tác có thật và kiểm chứng |
|---|---|
| Health | GET health, database, gemini. Liveness/schema phân biệt rõ; Gemini không gọi key thật; không thử quota/model thật |
| Jobs | GET list/chi tiết, activeOnly, thiếu ID; validation và null qua suite, SQL test 200/404 |
| CV | POST upload/extract; GET metadata/file. Suite loại file, signature, size, trùng, storage/stream/cancel và extraction/OCR; browser upload đủ giới hạn 20 file, file gốc/detail trả thành công |
| Applications | POST gán, GET theo job/CV, DELETE bỏ gán. Suite validation/trùng/tài nguyên đã dùng; SQL GET thiếu filter400; browser dùng luồng gán thật |
| Scoring preview | GET keyword-score, POST ai-preview; suite trạng thái CV, HTTP/error mapping và fake/mock AI |
| Runs | POST/list/latest/detail/ranking; snapshot/progress/Queued/Running/Completed/Failed; browser restart giữa chừng tiếp tục run cũ |
| Ranking/Evaluation | SQL 200 sau A35, null/đồng điểm/keyword-only/rỗng/không tồn tại/sai job; 32+32 GET đồng thời; browser giữ stage và số đếm |
| Stages | GET options/events, PATCH đơn, POST bulk; validation400, version409, SaveChanges concurrency, rollback và trigger bất biến |
| Prompt | GET list/active/detail, POST tạo/activate. Không có PUT sửa phiên bản: sửa nội dung tạo phiên bản mới; browser tạo A/B và kích hoạt thật trên SQL test |
| Evaluation sets | GET/list/create/applications/labels/template/import/delete-label; suite request/CSV/validation/transactions, browser lưu nhãn HR thật trên test DB |
| Prompt tests/compare | POST test, GET list/detail/compare, GET prompts/compare; suite timeout/null/config/mismatch/accuracy, browser hai run cùng set, so sánh và snapshot |

Đã rà soát DTO, validation, null, query EF, cancellation, timeout/retry, logging, transaction và concurrency trong các nhóm hiện có. Không sửa hàng loạt những phần không có lỗi xác minh. Các test dùng AI giả/handler HTTP giả không chứng minh Gemini model/key/quota thực tế dùng được.

### Sáu luồng E2E bằng API/Web/SQL thật trong môi trường test

Browser dùng `SmartCv_Runtime_Test_39f68552fb1446bd8b0b1f97e747b940`; dữ liệu upload là 20 PDF demo trong ZIP, đổi tên file thành runtime-demo-NN.pdf. Không ghi tên/nội dung ứng viên thật vào báo cáo.

1. **CV:** upload một rồi 19 PDF, circuit tiếp tục nhận thao tác đổi Job; lưu file, gán đúng 20 hồ sơ, trích xuất, lấy được file gốc và Evaluation detail. Giới hạn API/Web cùng 20 file, 2 MB/file; các lỗi size/type/trùng/per-file được suite kiểm tra. Chưa thử browser với 20 file đều sát 2 MB.
2. **AI/progress:** Screening27 đi Queued → Running. Lưu HR OnHold khi đang Running. Ngắt **API helper** ở checkpoint 1/20; Results giữ dữ liệu và dừng sau ba lỗi; khởi động lại API cùng database, Làm mới, chấm tiếp tới 20/20 Completed, Failed=0 bằng fake AI. Không POST lại để tạo run mới.
3. **HR:** giai đoạn giữ sau polling/F5 và restart Web; modal có sáu lý do, thiếu lý do bị chặn; Escape hủy không thêm event. Một HR khác đổi trong khi modal mở tạo 409 thật, UI tải trạng thái đã lưu; thao tác sau đó loại MissingSkills thành công. Bulk hai hồ sơ OnHold, số đếm/lọc giai đoạn/search rỗng/clear đều đúng.
4. **Prompt:** tạo A/B bằng editor Web; chạy A, chọn B chạy bằng UI, reload giữ run mới; hai run 1027/1028 Completed, API và bảng so sánh có đủ hai phiên bản. Kích hoạt B bằng UI; Screening27 vẫn lưu PromptVersionId=1 như khi tạo. Khoảng trống ID sau restart không được dùng làm bằng chứng mất run.
5. **Runtime offline/phục hồi:** giữ Web hoạt động khi API tắt, ngừng polling lỗi, khởi động lại API và tải đúng run; sau đó restart riêng Web, URL/run/giai đoạn đã lưu vẫn giữ. Chỉ dừng các process helper do phiên kiểm thử tạo, không dừng process người dùng.
6. **Nhiều CV/responsive:** đủ 20 CV và stage counts nhất quán. Desktop1440, mobile390 không tràn ngang; tablet768 dùng vùng bảng cuộn ngang. Kiểm tra focus native vào cột HR khiến vùng bảng tự cuộn và control nằm trong viewport, không tràn document. Escape modal và các control có label được thử; chưa phải audit WCAG toàn diện.

Kết quả browser cuối: **0 page errors, 0 API HTTP500, 0 unhandled API errors**. Các log Web mất kết nối trong đoạn cố ý tắt API là kết quả được mong đợi. Không che exception của server hoặc thay dữ liệu trả về để test qua.

### Bằng chứng và giới hạn

Evidence trong `artifacts/` vốn đã ignore, không stage:

- `full-audit-baseline-build.log`, `full-audit-baseline-tests.log`, `full-audit-final-build.log`, `full-audit-final-tests.log`.
- `full-audit-tests/baseline.trx`, `confirmed-before.trx`, `timestamp-before.trx`, `focused-after.trx`, `final.trx`.
- `application-readonly-audit.jsonl`, `application-final-readonly-audit.jsonl`, `sqlcmd-readonly-check.txt`, `git-foundation.json` trong thư mục full-audit-tests.
- `browser-schema-migration-evidence.json`, `full-audit-browser-evidence.json`, `concurrent-sql-evidence.txt`, `api-endpoint-inventory.json`, `tablet-accessibility.json`.
- `runtime-results.png`, `runtime-mobile.png`, `runtime-tablet.png`, `tablet-stage-focus.png`, `runtime-trace-browser-helper.log`.
- SQL fact RuntimeSchemaSqlTests còn ghi kết quả mới nhất vào `artifacts/runtime-fix-tests/runtime-sql-evidence.json` và `ranking-generated-sql.txt`; bản dùng cho browser được giữ riêng như trên.
- Database của FullAuditSqlTests được liệt kê ở `full-audit-tests/retained-sql-databases.txt`; database của RuntimeSchemaSqlTests tại `runtime-fix-tests/retained-test-databases.txt`. Không tự dọn các database này; một fixture thử ban đầu còn trigger gây lỗi tổng hợp để kiểm tra, không dùng làm môi trường ứng dụng.
- Helper: `artifacts/full-audit-diagnostics` và `artifacts/full-audit-browser`. Helper browser kiểm tra prefix/GUID database; chỉ fake AI, URL AI loopback không có dịch vụ, storage riêng. Một lần gọi helper thiếu tên database đã bị guard từ chối trước khi khởi chạy service; sửa đối số rồi mới chạy E2E.

Chưa kiểm chứng runtime của API/Web người dùng sau triển khai, Gemini thật, backup có thể restore được, load nhiều người dùng kéo dài, nhiều máy/SQL Server triển khai hoặc mọi loại scan OCR trên dữ liệu thực. OCR/text extraction suite chạy với dữ liệu test và tessdata sẵn có; bộ 20 PDF không thay thế mọi tình huống ảnh scan. Các cảnh báo EF về query nhiều collection/Take/First và cảnh báo HTTPS của helper được ghi nhận, không có lỗi kết quả/performance đã đo để biện minh sửa query/HTTPS của ứng dụng.

## F. File thay đổi và Git cuối

| File | Loại | Mục đích và kiểm chứng |
|---|---|---|
| `src/SmartCv.Api/Background/CvExtractionWorker.cs` | Sửa | Retry nạp pending sau lỗi startup với backoff/cancellation; hai regression recovery và processor/worker cũ pass |
| `src/SmartCv.Web/Components/Pages/Upload.razor` | Sửa | Dừng polling lỗi, retry thủ công, giữ timestamp đã xác nhận; UI regressions và luồng browser cũ/mới pass |
| `tests/SmartCv.Tests/CvExtractionProcessorTests.cs` | Sửa | Hai case database startup lỗi tạm rồi phục hồi, không OCR lại hai lần |
| `tests/SmartCv.Tests/RunProgressUiTests.cs` | Sửa | Bảy case polling/status/timestamp; helper click đúng button, giữ các assert cũ |
| `tests/SmartCv.Tests/FullAuditSqlTests.cs` | Mới | Hai SQL facts giữ database: concurrency ở SaveChanges, 32+32 GET, lịch sử/Unchanged/reason/reopen, bulk rollback thực |
| `docs/SCApp-full-system-audit-report.md` | Mới | Báo cáo này, kết quả, giới hạn và lệnh triển khai cần duyệt |

Tổng: **4 file sửa, 2 file mới**. Helpers/logs/TRX/ảnh/SQL chờ duyệt ở artifacts không nằm trong danh sách thay đổi Git; không commit artifacts.

`git status --short --branch`:

```text
## fix/scapp-end-to-end-stability-20261112...origin/main
 M src/SmartCv.Api/Background/CvExtractionWorker.cs
 M src/SmartCv.Web/Components/Pages/Upload.razor
 M tests/SmartCv.Tests/CvExtractionProcessorTests.cs
 M tests/SmartCv.Tests/RunProgressUiTests.cs
?? docs/SCApp-full-system-audit-report.md
?? tests/SmartCv.Tests/FullAuditSqlTests.cs
```

`git diff --stat` (không bao gồm file mới chưa theo dõi):

```text
 src/SmartCv.Api/Background/CvExtractionWorker.cs  | 47 +++++++----
 src/SmartCv.Web/Components/Pages/Upload.razor     |  8 +-
 tests/SmartCv.Tests/CvExtractionProcessorTests.cs | 53 ++++++++++++
 tests/SmartCv.Tests/RunProgressUiTests.cs         | 99 +++++++++++++++++++++--
 4 files changed, 182 insertions(+), 25 deletions(-)
```

`git diff --check`: không lỗi whitespace. Hai file mới cũng được kiểm tra riêng bằng no-index. `git diff --cached --name-status`: rỗng. Đã đọc lại từng file sửa/mới; không có conflict, file xóa hoặc đổi tên. HEAD giữ nguyên commit nền.

Không sửa appsettings, secrets, connection strings, package/SDK, migration/snapshot, SQL nguồn A35, RunEndpoints, ResultContracts, Gemini/scoring, storage/upload stream, CSS/Design System hay modal/filter/ranking B34. Không làm mất code A6/A7/A8/B5 hoặc các tính năng đã merge. Riêng A7 worker và Upload được sửa có phạm vi như bảng, được kiểm thử; không ghi đè bằng file ZIP mẫu.

Đã tạo đúng một nhánh được ủy quyền; không tự merge/pull/rebase/reset/stash/clean/stage/commit/push hoặc tạo PR. Không chạy migration/ghi dữ liệu trên database ứng dụng, không DROP database, không xóa bảng hoặc sửa history để vượt lỗi.

## G. Kết luận

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT BẢN SỬA CODE** trên `fix/scapp-end-to-end-stability-20261112`: ba lỗi sản phẩm đã có bằng chứng trước/sau, tests đã chạy không còn failed, SQL và E2E test đạt trong phạm vi nêu trên.

**CHƯA XÁC NHẬN môi trường ứng dụng đã hết HTTP500 hoặc sẵn sàng vận hành đầy đủ.** Cần phê duyệt riêng backup/migration A35, quyết định worker/Gemini, khởi chạy bản mới và kiểm tra Run25/Evaluation thật. Những bước này chưa được thực hiện và không được thay bằng kết quả fake/mock/test database.

Đã dừng sau audit → sửa lỗi có bằng chứng → kiểm thử → báo cáo. Không thực hiện thao tác Git hoặc triển khai database tiếp theo.
