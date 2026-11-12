# Báo cáo kiểm tra và khắc phục lỗi runtime SCApp

Ngày kiểm tra: 12/11/2026. Nhánh: `fix/scapp-runtime-api-errors`.

| Lỗi | Nguyên nhân | File sửa | Kết quả test | Trạng thái |
|---|---|---|---|---|
| P1 — Ranking RunId 25 trả 500; Evaluation cũng lỗi | Database ứng dụng chưa có migration `20261112045258_ApplicationStages`; thiếu bốn cột A35, bảng lịch sử và các đối tượng bảo vệ | `database/runtime_apply_application_stages.sql`, `tests/SmartCv.Tests/RuntimeSchemaSqlTests.cs` | Tái hiện SQL 207; trên SQL test, ranking 24/25 chuyển từ 500 sang 200 sau script; dữ liệu cũ giữ nguyên | Đã xác định và kiểm chứng cách sửa; **chưa cập nhật database ứng dụng** |
| P2 — Health database báo OK trong khi schema không dùng được | Endpoint cũ chỉ mở kết nối, không kiểm tra migration/schema | `HealthController.cs`, `DatabaseSchemaDiagnostics.cs` | Database test trước A35: 503 và tên migration thiếu; sau A35: 200 | Đã sửa trong code |
| P2 — Chuyển trang tạo 500 ở ranking, Prompt, Evaluation | Request đã bị hủy nhưng SqlClient trả exception SQL/reader thay vì exception hủy | `SqlReadCancellationMiddleware.cs`, `Program.cs`, `HealthController.cs`, `SqlReadCancellationTests.cs` | 15 unit cases pass; browser chuyển trang 15 lần: trước có 21 HTTP 500, sau có 0; GET kiểm tra sau đó đều 200 | Đã sửa và kiểm chứng trên SQL/API/Web test |
| P2 — Results tiếp tục polling khi API lỗi kéo dài | Không giới hạn số lần lỗi liên tiếp | `Results.razor`, `ResultsUiTests.cs` | Test mới thất bại trước sửa; sau sửa dừng sau ba lần lỗi, giữ dữ liệu và cho Làm mới; timeout tạm thời vẫn phục hồi | Đã sửa |
| P3 — Prompt test không tìm thấy `#8` | Test renderer chèn dấu cách giữa các text frame, tạo `# 8` | `PromptTestFlowTests.cs` | 25 Prompt flow cases pass; giữ assert cũ và thêm kiểm tra RunId/PromptVersionId; browser xác nhận Prompt mới được giữ sau reload | Đã sửa test harness |
| P2 cần theo dõi — Một lượt suite trung gian trả 500 tại Evaluation | Lượt đó chưa lưu response exception đầy đủ, chưa đủ bằng chứng kết luận nguyên nhân | `RuntimeSchemaSqlTests.cs` bổ sung body vào thông báo assert khi thất bại | Không tái hiện trong hai lượt full kế tiếp và lượt full cuối; browser cuối cũng pass | Không coi là đã xác định nguyên nhân của lần lỗi riêng này |

## 1. Git, nền mã và môi trường

- Repository: `C:\TTCN\project\AI-CV-Screening`.
- Nhánh ban đầu và cuối: `fix/scapp-runtime-api-errors`.
- Commit nền và HEAD cuối: `7afa25f43d91c782c89be6e3daaf6da3a00e7b04` — `feat(B34): implement application stage management UI`.
- Working tree sạch trước khi sửa; không có conflict. Mọi thay đổi trong báo cáo được tạo trong nhiệm vụ này.
- Solution thực tế: `SmartCv.slnx`. `global.json` và SDK cài đặt: `10.0.403`; EF Core thực tế được restore: `10.0.15`, SqlClient: `6.1.7`. Không đổi package, SDK, connection string hay User Secrets của ứng dụng.
- Dùng output `artifacts/runtime-fix-build` để tránh ghi lên DLL của API/Web người dùng. Không kill tiến trình của người dùng. Helper chỉ kết thúc API/Web do chính helper khởi chạy.
- Database kiểm thử: SQL Server LocalDB, tên `SmartCv_Runtime_Test_<GUID>` mới cho mỗi lần SQL regression. Không drop/reset database; danh sách sáu database được giữ tại `artifacts/runtime-fix-tests/retained-test-databases.txt`.

## 2. Nguyên nhân gốc của `/api/runs/25/ranking`

Đã tái hiện trên API đang chạy ban đầu tại `https://localhost:7269`. RunId 25 tồn tại, loại Screening, Completed, Total/Processed = 3, Failed = 0. Ranking các run 23, 24, 25 trả 500; Evaluation 67 cũng trả 500.

Exception thực tế, đã lọc request header và dữ liệu không cần thiết:

```text
Microsoft.Data.SqlClient.SqlException: Invalid column name 'Stage'.
Invalid column name 'RejectReason'.
Invalid column name 'StageUpdatedAt'.
Invalid column name 'StageVersion'.
SmartCv.Api.Endpoints.RunEndpoints.RankingAsync
src/SmartCv.Api/Endpoints/RunEndpoints.cs:185
Error Number:207,State:1,Class:16
```

Luồng lỗi: `Results.LoadAsync` → `RunApiClient.GetRankingAsync` → `ApiClientBase`/HttpClient → `RunEndpoints.RankingAsync` → EF projection từ Evaluations qua Applications → SQL Server từ chối bốn cột chưa tồn tại. Exception không có inner exception riêng cho lỗi 207 này; stack đi qua `RelationalCommand.ExecuteReaderAsync`, `SingleQueryingEnumerable` và `ToListAsync`.

Kiểm tra chỉ đọc trên database ứng dụng xác nhận:

- `__EFMigrationsHistory` chỉ có `20261106132922_InitialCreate` và `20261106142155_SeedJobs`.
- Không có `Stage`, `RejectReason`, `StageUpdatedAt`, `StageVersion` trong Applications.
- Không có ApplicationEvents, hai index A35, tám CHECK constraint A35 và trigger `TR_ApplicationEvents_NoChange`.
- Kiểm tra lại metadata cuối vẫn cho cùng kết quả. Không suy luận nguyên nhân chỉ từ log Web.

Đã kiểm tra Entity, DbContext, configurations, migration A35, snapshot, ResultContracts và endpoint hiện có. Sau khi có schema đúng trên SQL test, projection, xử lý null, rank đồng điểm, StageCounts, StageVersion và chi tiết Evaluation đều chạy được. Không có bằng chứng cần sửa truy vấn ranking hoặc thay Stage bằng dữ liệu giả.

Không truy cập trực tiếp được terminal API qua công cụ phiên này; stack trên lấy từ response Developer Exception Page của API thực tế. Bằng chứng: `artifacts/runtime-fix-tests/ranking25-before-sanitized.txt`, `artifacts/runtime-database-audit.log`, `artifacts/runtime-database-final-audit.log`.

Ở lần kiểm tra HTTP cuối, các cổng API/Web 7269/5156/7258/5172 không còn lắng nghe; curl trả trạng thái kết nối `000`. Không tự khởi chạy worker trên database ứng dụng để tránh thay đổi dữ liệu. Vì vậy không có kết quả HTTP “sau triển khai” trên database ứng dụng.

## 3. SQL sửa schema và tác động dữ liệu

Không sửa/xóa migration cũ, model snapshot hay script A35 có sẵn. Script mới `database/runtime_apply_application_stages.sql` triển khai đúng phần Up của migration A35:

- Thêm Stage mặc định New, RejectReason nullable, StageUpdatedAt nullable, StageVersion có giá trị NEWID cho dữ liệu cũ.
- Tạo bảng ApplicationEvents, FK không cascade delete, hai index, tám CHECK constraint và trigger chặn UPDATE/DELETE lịch sử.
- Thêm đúng migration A35 vào history trong cùng transaction.
- Yêu cầu người triển khai điền tên database đích vào `@ExpectedDatabase`; bản nguyên trạng bị chặn bằng lỗi 51035.
- Kiểm tra SeedJobs, chặn schema A35 bị áp dụng dở/không có history, dùng transaction, XACT_ABORT và application lock; kiểm tra đối tượng bảo vệ trước commit.
- Tách từng lệnh DDL bằng dynamic batch để tránh SQL biên dịch tham chiếu các cột vừa thêm trước khi guard/DDL chạy. Một bản script thử ban đầu đã bị test cô lập bắt lỗi này và được sửa trước khi bàn giao.

Trên database test mới, đã chứng minh: guard không cho chạy khi chưa xác nhận đích; lỗi cố ý giữa migration rollback cả các cột mới; fingerprint dữ liệu Applications cũ, CVs, Jobs, Evaluations, Runs và PromptVersions không đổi; chạy lại script không đổi StageVersion/quyết định/lịch sử. UPDATE/DELETE dòng lịch sử tổng hợp bị trigger từ chối bằng lỗi 51000, số dòng vẫn giữ nguyên.

**Script chưa được chạy trên database ứng dụng.** Người dùng cần duyệt SQL, xác nhận database đích, chuẩn bị backup/khoảng triển khai theo quy trình hiện có, áp dụng migration rồi kiểm tra lại RunId 25. `database/check_runtime_schema.sql` chỉ đọc metadata và số liệu kỹ thuật, dùng được trước/sau triển khai. Không chạy Down, drop/recreate hoặc tự sửa migration history.

## 4. Các sửa lỗi runtime và Prompt

Readiness database trong Development kiểm tra pending migrations, cột/bảng A35, constraint/index/FK và trigger có cả UPDATE/DELETE. Thiếu schema trả 503 kèm danh sách đối tượng thiếu; có schema trả 200. Lỗi kết nối được log ở API; client không nhận nguyên exception. `/api/health` vẫn là liveness, không được diễn giải thành schema đã sẵn sàng. Endpoint database vẫn giữ chốt Development như trước.

Lỗi chuyển trang được truy vết thêm bằng log có RequestId và cờ hủy. 20 exception SQL trong probe trước sửa có `RequestAborted=True`, mã `0:11:0`, đôi lúc kèm 3980; ngoài ra có dạng InvalidOperationException từ continuation reader của SqlCommand. Middleware mới chỉ chuẩn hóa GET/HEAD đã bị hủy và khớp dấu hiệu driver thành OperationCanceledException, giữ exception gốc ở InnerException và giữ token. ASP.NET Core ghi nhận request bị hủy (499). Không trả 200 giả, không retry, không áp dụng cho POST/PATCH. Test bảo vệ lỗi 207, lỗi trộn schema/cancel, timeout -2, 3980 không có dấu hiệu hủy, lỗi không thuộc driver và request chưa bị hủy.

Đã đối chiếu hành vi reader với [source SqlClient 6.1.7](https://github.com/dotnet/SqlClient/blob/v6.1.7/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/SqlCommand.cs#L2456) và vấn đề [SqlClient cancellation diagnostics](https://github.com/dotnet/SqlClient/issues/2424). Kết luận áp dụng cho SCApp dựa trên log/tái hiện tại máy này. Không nâng driver hoặc bật retry toàn cục theo suy đoán.

Results giữ dữ liệu API xác nhận gần nhất, ngừng tự polling sau ba lỗi liên tiếp và hướng dẫn Làm mới. Thành công đặt lại bộ đếm; đổi run/job hoặc Làm mới tạo chu kỳ mới. Giữ nguyên cơ chế chống kết quả polling cũ ghi đè quyết định HR của B34.

Với Prompt regression, trước sửa các assert RunId = 8, PromptVersionId = 2 và một POST đã pass; renderer chỉ tạo chuỗi sai `# 8` do `string.Join(" ", ...)`. Chuyển thành `string.Concat(...)` phản ánh text liền kề trong DOM, giữ nguyên assert `#8`, `v2`, một POST; thêm assert trạng thái server và kiểm tra HTML `Lượt #7`. Không sửa component Prompt vì chưa có bằng chứng lỗi logic sản phẩm ở kịch bản này. Bằng chứng thất bại trước sửa: `prompt-root-cause.trx` trong thư mục evidence.

## 5. Toàn bộ file sửa/mới

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Api/Controllers/HealthController.cs` | Sửa | Readiness schema, logging, đóng connection trong finally, truyền hủy request đúng cách |
| `src/SmartCv.Api/Program.cs` | Sửa | Đăng ký middleware chuẩn hóa hủy SQL read |
| `src/SmartCv.Api/Middleware/SqlReadCancellationMiddleware.cs` | Mới | Phân biệt request đọc đã bị hủy với database failure thực; giữ nguyên nguyên nhân |
| `src/SmartCv.Infrastructure/Data/DatabaseSchemaDiagnostics.cs` | Mới | Kiểm tra migration và metadata bằng truy vấn chỉ đọc |
| `src/SmartCv.Web/Components/Pages/Results.razor` | Sửa | Giới hạn polling khi lỗi kéo dài, thông báo trạng thái và khởi động lại bằng Làm mới |
| `tests/SmartCv.Tests/PromptTestFlowTests.cs` | Sửa | Sửa cách lấy text của renderer, giữ và tăng kiểm tra regression Prompt |
| `tests/SmartCv.Tests/ResultsUiTests.cs` | Sửa | Regression polling lỗi liên tục và phục hồi thủ công |
| `tests/SmartCv.Tests/RuntimeSchemaSqlTests.cs` | Mới | SQL integration: tái hiện 207, guarded migration/rollback/bảo toàn dữ liệu, ranking/detail/HR409/trigger |
| `tests/SmartCv.Tests/SqlReadCancellationTests.cs` | Mới | 15 trường hợp hủy/lỗi thực/không retry; kiểm tra continuation của driver cài đặt |
| `database/check_runtime_schema.sql` | Mới | Audit chỉ đọc trước/sau triển khai |
| `database/runtime_apply_application_stages.sql` | Mới | SQL cập nhật A35 có guard, giao dịch và kiểm tra đầy đủ để người dùng duyệt |
| `docs/SCApp-runtime-error-fix-report.md` | Mới | Báo cáo này |

Helper chẩn đoán/browser, output build, TRX, ảnh và log nằm trong `artifacts/` vốn đã được Git ignore; không thêm dependency browser vào solution. Helper dùng cấu hình tạm riêng trong process test, không sửa appsettings/User Secrets. Không có file bị xóa, đổi tên hay ghi đè nguyên file mẫu.

## 6. API và Web đã xác minh

| Nhóm/kịch bản | Kết quả và môi trường |
|---|---|
| Jobs | GET danh sách/chi tiết trên API ứng dụng ban đầu 200; ID thiếu 404; test endpoint/selection pass. Không phát hiện API keyword search riêng trong mã hiện có |
| CV | Suite validator/upload/storage/file/extraction/OCR pass; browser thật trên SQL test upload một rồi 19 PDF demo, đọc và chấm đủ 20 |
| Applications | Suite gán job, chống trùng, validation pass; browser sử dụng luồng gán thật cho 20 hồ sơ trên test DB |
| Runs | Tạo/progress/Queued/Running/Completed và snapshot được test; browser Run27 xử lý 20/20, Failed = 0 |
| Ranking hợp lệ | SQL test Run24/25 trả 200 sau migration; đồng điểm hạng 1/1, điểm null không có hạng, candidate null, keyword-only đều hợp lệ |
| Ranking thiếu/rỗng/sai job | Run2147483647, Run26 rỗng, job không thuộc Run25 trả 404 đúng contract hiện có |
| Evaluation | SQL test các Evaluation của Run24/25 trả 200 sau migration; browser mở trang chi tiết thật; suite null/error/not-found pass |
| A35 | SQL test đổi đơn Rejected, version cũ 409, bulk OnHold, ba lần GET đồng bộ số đếm, ba event đúng số; trigger bảo vệ lịch sử. Suite rule/endpoint/persistence pass |
| B34 | 16 UI tests pass, gồm lỗi 409/thao tác hàng loạt/hủy/lọc/refresh và chống dữ liệu cũ; browser HR OnHold khi Run27 đang Running, giữ sau polling/F5 |
| API lỗi/timeout/mất kết nối | WebApiClientTests 17 pass; ResultsUiTests 51 pass, gồm lỗi poll kéo dài, timeout phục hồi, kết quả cũ khi đổi trang; probe 60 lần hủy GET và 60 lần đọc bình thường đều đọc lại thành công |
| Gemini | Service/model/flow tests dùng fake hoặc HTTP handler giả. Browser xác minh response có cờ simulated; BaseUrl test trỏ loopback không có dịch vụ, không gọi key thật |
| Prompt | Version/activation/test/progress/comparison/mismatch suites pass; browser chuyển từ PromptTest28 sang29 với prompt mới và giữ sau reload |
| Dashboard | Rà soát trang Home và số tổng hợp Results hiện có; không thấy một nhóm API Dashboard riêng. Không thêm chức năng mới |
| Responsive | Chromium desktop 1440px và mobile 390px; mobile không tràn ngang, bảng chuyển dạng card; không thay CSS/layout hiện có |

Các truy vấn API hiện có đã được rà soát về cancellation, validation, null và trạng thái HTTP. Không sửa hàng loạt các nhóm không có lỗi xác minh. Cảnh báo EF về query nhiều collection hoặc Take/First chưa OrderBy được ghi nhận trong helper; chưa có lỗi kết quả/hiệu năng đo được để biện minh thay query trong nhiệm vụ này. Cảnh báo HTTPS của helper do chạy HTTP loopback trên cổng tạm; không đổi HTTPS cấu hình ứng dụng.

## 7. Kết quả build và test

Output riêng bảo toàn DLL người dùng. Lệnh tương đương đã thực thi:

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/runtime-fix-build --no-restore
dotnet test SmartCv.slnx --artifacts-path artifacts/runtime-fix-build --no-restore --no-build --logger "trx;LogFileName=runtime-final.trx" --results-directory artifacts/runtime-fix-tests
```

SQL test mới được bật bằng `SMARTCV_RUNTIME_TEST_SQL_CONNECTION` trỏ kết nối LocalDB dành riêng cho tên `SmartCv_Runtime_Test_`; test luôn thay catalog bằng GUID mới, không dùng database được cấu hình làm dữ liệu test. Đọc PDF demo qua `SMARTCV_B34_DEMO_ZIP`. Không công bố connection string có thông tin đăng nhập.

| Lần kiểm tra | Passed | Failed | Skipped | Tổng |
|---|---:|---:|---:|---:|
| Baseline toàn solution | 1329 | 1 | 38 | 1368 |
| Toàn suite trước sửa cancellation, lượt xác minh lại | 1332 | 0 | 38 | 1370 |
| Cuối cùng, có SQL integration mới và 15 cancellation cases | **1347** | **0** | **38** | **1385** |
| Cancellation tests chạy riêng | 15 | 0 | 0 | 15 |

Build cuối: **0 warning, 0 error**. Suite cuối khoảng 71 giây. Test kiểm tra thứ tự migration và `HasPendingModelChanges()` pass. Không có migration/snapshot mới ngoài A35 đã tồn tại trên nhánh nền.

38 test SQL opt-in cũ được giữ nguyên Skipped: A11 = 6; A15 = 7; A16 = 10; A17 = 3; PromptSqlServerTests = 5; ApplicationStageSqlServerTests = 7. Không bật các fixture này vì chúng có lifecycle xóa database thử nghiệm, trái yêu cầu không tự xóa/reset database của phiên này. **Không tính Skipped là Passed.** SQL integration mới và browser SQL chạy thật như mô tả, không thay thế kết quả chưa chạy của các fixture cũ.

Một test mới ban đầu lỗi ở Evaluation 500 chưa có body diagnostic; không quy kết lần đó cho lỗi hủy request nếu thiếu stack. Đã giữ nguyên assert 200 và thêm body khi assert thất bại. Các lượt full sau đó pass; lỗi hủy request trong browser được tái hiện, lấy cờ hủy và sửa bằng bằng chứng độc lập. Khi lỗi Evaluation riêng tái xuất hiện, cần giữ TRX có body và log API để phân loại tiếp.

## 8. Bằng chứng SQL và browser cuối

SQL test cuối dùng `SmartCv_Runtime_Test_0fdeb83edb3445059d4483e61283d445`, được giữ nguyên sau test. Ranking25 trước script = 500/SQL207; readiness = 503. Sau script: ranking24 = 200, ranking25 = 200, readiness = 200, legacy fingerprint giữ nguyên, stale HR version = 409; chạy lại script giữ nguyên quyết định/lịch sử.

Browser cuối chạy API/Web build mới trên cổng tạm và database test đó:

- Upload 1 + 19 PDF từ bộ `cv-20-backend` trong ZIP, đổi job trên UI để kiểm tra circuit vẫn phản hồi; tạo Screening27.
- Trích xuất và fake AI hoàn thành 20/20, không CV thất bại. Không chạy Gemini thật, không tạo chi phí API.
- Lưu HR OnHold khi trạng thái run là **Running**; số đếm đồng bộ, tên HR test được hiển thị, kết quả giữ qua polling và reload.
- Mở ranking Run25, run không tồn tại, trang Evaluation.
- Tạo Prompt test từ28 sang29, xác nhận prompt mới trước POST và kiểm tra sau F5.
- Mobile 390 × 844 không tràn ngang; **0 page errors, 0 HTTP500 trong log API, 0 unhandled API errors**.

Probe chuyển trang riêng trước/sau gồm cùng 15 lần chuyển giữa Results, Prompt và Evaluation. Trước: 21 HTTP500, 21 unhandled requests; 20 log SQL chẩn đoán xác nhận Aborted=True và không có Aborted=False. Sau: 0 HTTP500, 0 unhandled requests, 38 request hủy được ghi nhận 499; ba GET kiểm tra cuối đều 200. Số request nền thay đổi theo timing prerender/interactive, không dùng số request 499 để suy ra số lỗi được sửa theo tỷ lệ một-một.

Evidence cục bộ (đều trong `artifacts/`, không stage):

- `runtime-fix-build.log`, `runtime-final-tests.log`, `runtime-fix-tests/runtime-final.trx`.
- `runtime-fix-tests/runtime-baseline.trx`, `prompt-root-cause.trx`, `poll-before.trx`, `sql-cancellation.trx`.
- `runtime-fix-tests/runtime-sql-evidence.json`, `ranking-generated-sql.txt` (SQL tham số hóa, không lưu giá trị ứng viên).
- `runtime-fix-tests/runtime-navigation-before.log`, `runtime-navigation-after.log`, `runtime-navigation-evidence.json`.
- `runtime-fix-tests/runtime-browser-evidence.json`, `runtime-results.png`, `runtime-mobile.png`, `runtime-trace-browser-helper.log`.
- Helper browser: `artifacts/runtime-browser/Program.cs`; chạy lại bằng `dotnet run --project artifacts/runtime-browser/RuntimeBrowser.csproj --no-restore -- <repo-path> <test-database> trace-browser`. Helper chặn tên database không đúng prefix/GUID và chỉ dùng cấu hình test.

## 9. Giới hạn và việc còn cần người dùng duyệt

1. Database ứng dụng vẫn thiếu A35; không thể xác nhận runtime thực tế hết 500 trước khi người dùng duyệt và triển khai migration. Không có thao tác ghi/migration/đổi cấu hình nào trên database ứng dụng trong phiên này.
2. API ứng dụng đã ngừng lắng nghe ở lần kiểm tra cuối. Cần chạy lại API/Web bản mới sau triển khai để gọi Run25 và kiểm tra polling/HR với schema thật. Không ghi thử trên ứng viên thật trong phiên này.
3. Một lỗi Evaluation 500 trung gian chưa có stack gốc, cần theo dõi như mục 7; không đánh dấu “đã sửa triệt để mọi lỗi”.
4. Chưa chạy các SQL fixtures bị skip, stress dài hạn, nhiều người dùng trên hạ tầng triển khai, hoặc batch lớn hơn 20 CV ở browser. Chưa chứng minh mọi lỗi từng gây mất kết nối UI đều đã hết ở mọi dung lượng/mạng.
5. Không kiểm tra Gemini key/quota/model thật hoặc timeout của dịch vụ thật; chỉ fake/mock. Không coi fake AI là kết quả đánh giá chất lượng tuyển dụng.
6. Nhánh xử lý cancellation được kiểm chứng với SqlClient 6.1.7. Test reader sử dụng reflection do driver không công khai constructors/continuation; khi nâng driver cần chạy lại và điều chỉnh theo hành vi thực nếu driver thay đổi. Không dựa vào chuỗi lỗi theo ngôn ngữ.
7. Các database test được giữ lại theo yêu cầu; không tự dọn bằng DROP. Người dùng có thể xem danh sách trước khi quyết định vòng đời dữ liệu thử nghiệm.

## 10. Git cuối và bảo toàn chức năng

`git status --short --branch --untracked-files=all`:

```text
## fix/scapp-runtime-api-errors
 M src/SmartCv.Api/Controllers/HealthController.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Web/Components/Pages/Results.razor
 M tests/SmartCv.Tests/PromptTestFlowTests.cs
 M tests/SmartCv.Tests/ResultsUiTests.cs
?? database/check_runtime_schema.sql
?? database/runtime_apply_application_stages.sql
?? docs/SCApp-runtime-error-fix-report.md
?? src/SmartCv.Api/Middleware/SqlReadCancellationMiddleware.cs
?? src/SmartCv.Infrastructure/Data/DatabaseSchemaDiagnostics.cs
?? tests/SmartCv.Tests/RuntimeSchemaSqlTests.cs
?? tests/SmartCv.Tests/SqlReadCancellationTests.cs
```

`git diff --stat` cho các file đã tracked:

```text
 src/SmartCv.Api/Controllers/HealthController.cs | 27 ++++++++++++++++++++-----
 src/SmartCv.Api/Program.cs                      |  3 +++
 src/SmartCv.Web/Components/Pages/Results.razor  | 13 +++++++++++-
 tests/SmartCv.Tests/PromptTestFlowTests.cs      |  6 +++++-
 tests/SmartCv.Tests/ResultsUiTests.cs           | 23 +++++++++++++++++++++
 5 files changed, 65 insertions(+), 7 deletions(-)
```

Git diff không tính bảy file mới chưa theo dõi trong bảng trên. Đã đọc lại toàn bộ file sửa/mới; `git diff --check` không có lỗi whitespace. File mới cũng được kiểm tra riêng bằng diff no-index, không stage. `git diff --cached --name-status` rỗng. Tổng cộng 12 file thuộc nhiệm vụ: 5 sửa, 7 mới.

Không có thay đổi ngoài phạm vi runtime fix/test/SQL kiểm tra/báo cáo. Không sửa migrations cũ, upload, OCR, Gemini, logic chấm điểm, DTO, Stage rules, modal/cột/filter B34 hoặc các tính năng B5–B16 khác. Không làm mất code cũ; A35 endpoint/persistence, B34 UI và regression hiện có được giữ và pass trong phần đã chạy. Những kiểm thử chưa chạy được nêu rõ, không suy rộng thành bảo đảm toàn hệ thống.

Không tạo/chuyển nhánh, pull, merge, rebase, reset, stash, clean, stage, commit, push hoặc tạo PR. HEAD giữ nguyên commit nền.

## 11. Kết luận

**CHƯA SẴN SÀNG để commit với xác nhận “đã khắc phục toàn bộ lỗi runtime trên database ứng dụng”.** Bản sửa code, SQL để duyệt và kiểm thử cô lập đã hoàn tất; còn bước người dùng duyệt/triển khai migration A35 và kiểm tra lại môi trường ứng dụng, cùng theo dõi lỗi Evaluation trung gian chưa có stack.

Đã dừng sau code → kiểm thử → báo cáo. Không thực hiện các bước Git hoặc triển khai database tiếp theo.
