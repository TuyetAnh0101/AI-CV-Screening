# A25 — API lịch sử lượt chấm CV

Ngày: **17/11/2026**, Asia/Ho_Chi_Minh. Phạm vi chỉ A25; báo cáo để chủ dự án duyệt trước commit/push.

## 1. Git, nguồn và readiness

- RepoPath: `C:\TTCN\project\AI-CV-Screening`.
- Remote origin: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Nhánh: **`feature/a25-run-history-api`**, không upstream.
- **HEAD = SHA nền = origin/main đã fetch: `7373b52c6c11471d731a82a33b97e32e1b3812e0`**, commit `feat(A22): implement job skills and keyword management API (#68)`.
- Trước khi bắt đầu: nhánh A22, HEAD `822978516db202c0b2bdeba33bb245e595d1dd40`, working tree/index sạch, không merge/rebase/cherry-pick, `git ls-files -u` rỗng. Đã fetch origin --prune, kiểm tra nhánh local/remote và source main không có A25/RunHistory; không trùng nhánh A25. Tạo nhánh bằng `git switch --no-track -c feature/a25-run-history-api origin/main`. Các worktree/nhánh khác được giữ nguyên.
- Đã đọc đầy đủ `C:\TTCN\A25_Codex_Prompt_20261117.md`, guide ZIP (gồm A25/B26/B36, hướng dẫn chung/lỗi), toàn bộ **6 file C# mẫu A25**: RunHistoryContracts, RunContracts, RunHistoryQuery, VietnamTime, RunEndpoints, RunHistoryQueryTests; và `snippets/Program.A19-A25.cs.txt`.
- ZIP `C:\TTCN\A19_B26_SmartCv.zip`, 150.880 bytes, SHA-256 `3E0C98598DA639AC0B4B27D6CC400C794E5A772B8154437741954A198FAC1327`. Kiểm kê không có PNG/JPG/JPEG/SVG/WebP/GIF/mockup. Không tạo hình, sửa Figma hoặc frontend.
- Đã audit RunEndpoints/RunCompareEndpoints, contracts/entities/DbContext/constants, VietnamTime, RetentionRules/CvPrivacyScope/middleware, PromptEndpoints, ScoringOptions/ScoreCombiner, RunApiClient và các client/test A12/A13/A39/A40/A42/B7/B38.

**Readiness dev database:** chỉ SELECT metadata `SmartCvDb` trên `.\BB`, không đọc nội dung CV hoặc ghi dữ liệu. Hiện có đủ **6 migration** đến `20261117045748_JobHeadcountClosingDate` và hai cột Jobs.Headcount/ClosingDate; khác lần kiểm tra A22 trước đó. Evidence `artifacts/a25-live-schema-readonly.txt`. Không chạy migration A25 hoặc migration nào lên database thật.

## 2. Chức năng và hợp đồng API

Tất cả route thuộc nhóm `/api/runs` hiện có, chỉ map khi **Development + `CvUpload:Endpoint:Enabled=true`**. `CvPrivacyReadMiddleware` tiếp tục giữ privacy lease A39 qua serialization/response và đặt `Cache-Control: no-store`. Không tạo header HR giả, không mở Production trước M6.

| Method/path | Kết quả | Lỗi/hành vi |
| --- | --- | --- |
| `GET /api/runs` không có page | 200 JSON array `List<RunSummaryDto>` như B7 | Giữ mặc định take=20, clamp 1–100, sorting cũ; nhận thêm filter A25 |
| `GET /api/runs?page=1&pageSize=20` | 200 `RunPageDto` | 400 query sai; pageSize không có page cũng 400 |
| `GET /api/runs/{id}` | 200 positional record `RunProgressDto` + init fields mới | Giữ 404, Items/Percent/các field hiện có |
| `GET /api/runs/{id}/failures` | 200 `RunFailuresDto`, kể cả không có lỗi | 404 nếu không có run; không retry/requeue/chấm lại |

### Danh sách hai chế độ

Không có `page`: **giữ JSON array**, `GET /api/runs?type=Screening&take=3` tiếp tục deserialize bằng `RunApiClient.ListScreeningAsync`. Đã test **client B7 thật**, không đổi Web hoặc client. Legacy summary giữ nguyên các field, Percent và Jobs; không tự đổi thành page object khi gửi filter.

Có `page`:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 0,
  "totalPages": 0
}
```

Mỗi item có `runId`, `runType`, `status`, `promptVersionId`, `promptVersionName`, `modelName`, `temperature`, `keywordWeight`, `aiWeight`, `passThreshold`, `evaluationSetId/name`, `accuracyPercent`, `totalItems`, `processedItems`, `failedItems`, `createdAt`, `completedAt`, `durationSeconds`, `jobs[]`. JobCountDto giữ `jobId/title/count`.

- Filter kết hợp **AND**: `type=Screening|PromptTest`, `status=Queued|Running|Completed|Failed`, `jobId` nguyên dương, `from`, `to`.
- Không gửi type/status là mọi loại/trạng thái. Giá trị lạ, sai hoa/thường hoặc rỗng khi đã gửi bị 400. JobId không tồn tại trả mảng/trang rỗng, không 404. Jobs trong một run là toàn bộ Job của run đó; filter Job chọn run có chứa Job, không cắt Jobs xuống một phần.
- Ngày đúng `yyyy-MM-dd`, theo Việt Nam UTC+7 và tính cả hai đầu. Ví dụ ngày 17/11/2026 thành **[16/11 17:00 UTC, 17/11 17:00 UTC)**. Không phụ thuộc timezone máy; giữ nguyên VietnamTime hiện tại. Ngày sai, từ>đến, DateOnly biên không biểu diễn UTC trả 400 theo field.
- Các tham số đã biết lặp lại, kể cả khác hoa/thường như page/PAGE, bị 400. Integer sai kiểu/overflow/chuỗi ngày có giờ đều được parse chủ động, trả ProblemDetails tiếng Việt; không echo input hoặc stack trace. Query ngoài hợp đồng không làm đổi response shape, giữ cách bỏ qua query không dùng của runtime cũ.
- Page bắt đầu 1; pageSize mặc định 20, 1–100. `pageSize` đơn lẻ trả 400. `take` hợp lệ vẫn được validate khi có page nhưng không giới hạn pageSize/totalCount; chỉ áp dụng ở legacy array.
- Offset tính bằng long khi validate, từ chối nếu vượt Int32; Skip sau đó checked. `page=2147483647&pageSize=1` hợp lệ và trả items rỗng, không SQL overflow; cùng pageSize=100 trả 400. TotalPages dùng phép tính long, chống tràn tại totalCount=Int32.MaxValue.
- TotalCount áp dụng đủ filter **trước** Skip/Take. Không có dữ liệu: items=[], totalCount=0, totalPages=0. Trang vượt tổng: items=[], giữ count/pages đúng.
- Thứ tự **CreatedAt DESC, RunId DESC**, ổn định khi thời gian bằng nhau. DateTime JSON UTC `Z`. Duration null khi chưa có CompletedAt; timestamp ngược → 0, duration quá Int32 → Int32.MaxValue giây để tránh cast tràn, không ảnh hưởng timestamp gốc.
- SQL chỉ chọn cột cần dùng, CountAsync + page SELECT + **một GROUP BY Jobs cho cả trang**; không tải mọi run/CV vào RAM trước phân trang, không N+1.

Ví dụ lỗi validation:

```json
{
  "title": "Bộ lọc không hợp lệ",
  "status": 400,
  "detail": "page bắt đầu từ 1.",
  "errors": { "page": ["page bắt đầu từ 1."] }
}
```

### Chi tiết và tính chính xác của lịch sử

Giữ nguyên positional signature của `RunProgressDto`, không thay class/constructor từ ZIP. `promptVersionId`, `temperature` đã có sẵn trong positional record trên main, không khai báo trùng. Chỉ thêm init fields **SystemPrompt, EvaluationSetId, EvaluationSetName, AccuracyPercent**. Các phép `with`, Start Run/PromptTest và clients hiện có vẫn build/test.

- Model, temperature, weights và passThreshold lấy **cột đã lưu trên EvaluationRun**, không đọc ScoringOptions hiện hành. SystemPrompt lấy đúng PromptVersionId được run tham chiếu; không lấy prompt active mới. PromptTest trả đúng set/accuracy nullable; Screening không có set/accuracy thì null, không tạo 0 giả.
- Audit PromptEndpoints: chỉ có create/read/activate, **không có API sửa SystemPrompt/VersionName**; sửa nội dung phải tạo version mới. Test PUT vào phiên bản cũ trả 405, active prompt khác không đổi nội dung GET run cũ.
- Đây là lịch sử dựa trên tham chiếu phiên bản bất biến **qua API**, không có bản sao content riêng trong EvaluationRuns và không có constraint SQL chống DBA chỉnh PromptVersions. Không tuyên bố bất biến trước sửa DB thủ công.
- Tên Job và EvaluationSet là tên hiện tại qua FK, không phải chuỗi được snapshot tại lúc chạy; Job đổi tên vẫn có dữ liệu hợp lệ, các ID/run settings đã lưu không đổi. AccuracyPercent trả giá trị đã lưu, không tự tính lại từ labels khi đọc.
- **Khác ZIP:** cap thiếu MustHave không phải hằng số cố định trong runtime mới: `ScoringOptions.MissingMustHaveCap` nullable/configurable, appsettings hiện là 69. Cap và các trọng số bên trong Keyword **chưa lưu per-run**. Ngưỡng FitLevels **85/70/55** là hằng số code, cũng chưa lưu per-run. A25 không bịa snapshot hoặc lấy config mới để bổ sung các cột lịch sử không tồn tại; không rescore.
- SystemPrompt chỉ được bổ sung tại GET run detail; list/paged list/failures không trả text này hoặc ghi log. POST Start Run giữ logic/response construction hiện tại, chưa nạp prompt content vào response tạo mới.

### Hồ sơ có vấn đề và privacy

Lấy các evaluation `Status=Failed OR ScoreSource=KeywordOnly`. Không lấy Pending/Completed thông thường; mỗi evaluation một dòng. Nếu đồng thời Failed và KeywordOnly thì **Failed ưu tiên**, chỉ đếm một lần. Counts tính từ cùng danh sách, thứ tự Failed trước, sau đó EvaluationId tăng dần để ổn định và không phụ thuộc tên đã ẩn danh.

`RunFailureDto`: evaluationId/applicationId/cvId, fileName/candidateName, jobId/jobTitle, kind, errorMessage, cvExtractionStatus/cvExtractionError, createdAt UTC. Run tồn tại không có lỗi trả `{runId, failedCount:0, keywordOnlyCount:0, items:[]}`.

- CV đã `AnonymizedAt`: SQL CASE trả filename **“Hồ sơ đã ẩn danh”**, CandidateName=null, không materialize raw ErrorMessage, không lấy ExtractionError thô; trả thông báo lỗi đã ẩn an toàn, CvExtractionError=null. Cả GET progress vẫn giữ ErrorMessage=null theo A39.
- CV chưa bị retention: tên/file chỉ trả trong gate nội bộ, B26 phải tiếp tục áp dụng switch Ẩn danh B36 ở phía hiển thị. Để raw error không vượt qua switch này, **failures trả thông điệp có kiểm soát**, phân biệt trích xuất lỗi/chưa xong, HTTP 429/quota, HTTP 503/overload, timeout và fallback Failed/KeywordOnly. Không trả nguyên exception, provider URL, API key, đường dẫn hoặc CV text trong lỗi.
- CvExtractionError giữ field nhưng dùng hướng dẫn an toàn; nếu status đã Completed mà còn diagnostic cũ, không khẳng định lần trích xuất hiện tại thất bại. Raw extraction diagnostics không được SELECT thành chuỗi response.
- Raw AI/ExtractedText không nằm trong projection failures/list. Không sửa các điểm, CV, run, labels, event, prompt hoặc lịch sử khi đọc.
- **Contract cũ giữ nguyên:** ErrorMessage của GET progress cho CV chưa ẩn danh vẫn theo runtime trước A25; B7/B38 tiếp tục dùng Display.SafeError. A25 chỉ bổ sung policy thông điệp của route failures mới và giữ policy null cho CV đã anonymized. Không mở rộng thay đổi sang Ranking/Compare.
- Privacy lease A39 giữ nguyên cho `/api/runs/.../failures`; SQL test dùng hai connection kiểm chứng exclusive erasure lease phải chờ khi read đang diễn ra, và được nhả sau cancellation. Test không thực hiện erasure/xóa CV.

## 3. Kiểm thử và bằng chứng SQL

Môi trường: SDK **.NET 10.0.403**, runtime **10.0.14**, EF Core **10.0.15**, SQL Server **16.0.1000.6 Developer Edition**, `.\BB`. Không nâng tool/package hoặc thay config để vượt lỗi. Artifacts build riêng tránh khóa DLL của ứng dụng người dùng; không kill/tắt ứng dụng.

| Kiểm tra | Passed | Failed | Skipped | Bằng chứng |
| --- | ---: | ---: | ---: | --- |
| Restore | — | 0 | — | `artifacts/a25-baseline-restore.log` |
| Baseline build | — | 0 | — | 0 warning, 0 error; `a25-baseline-build.log` |
| Baseline full | **2.078** | **0** | **78** | 2.156 tổng, 1 phút 31 giây; SQL A19–A22 bật |
| Unit + HTTP focused | 67 | 0 | 0 | 7 giây; `a25-focused-http.log` |
| Focused cuối gồm SQL | **75** | **0** | **0** | 9 giây; `a25-focused-final.log`/TRX |
| Build cuối sau source changes | — | **0** | — | **0 warning, 0 error**; `a25-build.log` |
| Full regression cuối | **2.153** | **0** | **78** | **2.231** tổng, 1 phút 39 giây; tăng đúng 75 test A25 |

75 test mới gồm **34 pure unit**, **33 HTTP**, **3 guard an toàn SQL**, **5 SQL integration**. Unit mở rộng 10 case guide: mặc định/page3, type/status/number/date lỗi, repeated/empty/pageSize đơn lẻ, DateOnly extremes/leap/17:00 UTC, offset và TotalPages overflow, duration ngược/cực hạn, nullable metadata, record `with` và legacy take clamp.

HTTP: client B7 thật đọc array; page object camelCase/count/pages/last/after-last; filter riêng/kết hợp AND, Job membership/tie sorting/ranh giới ngày; 25 query invalid cases; snapshot Screening/PromptTest, set/accuracy, activation khác version, immutable prompt route, Failed/KeywordOnly/fallback, A39 residual PII, Start Run sau thay active prompt; no-store, 404, Production/Staging/Development gate. Production/Staging bật gate bị guard M6 hiện có từ chối khởi động; khi tắt gate routes 404 kể cả header HR giả.

SQL opt-in `SMARTCV_A25_TEST_SQL_CONNECTION`: guard prefix riêng `SmartCv_A25_Test_`, cấm AttachDbFilename; fixture luôn thay thành **GUID mới**, kiểm `DB_ID` chưa tồn tại trước CREATE và DB_NAME khi mở kết nối. Áp dụng **6 migration hiện có chỉ trên catalog mới**, không migration A25, `HasPendingModelChanges=false`. Không thay model/master, reuse/detach/drop hoặc cleanup catalog/CV nào. Workers đều tắt/bỏ khỏi test host, không Gemini.

| SQL integration | Kết quả đã kiểm chứng |
| --- | --- |
| HTTP Kestrel + filter/snapshot/failures/privacy | 6 run/10 evaluation, hai Job, Screening/PromptTest; ngày Việt Nam/ties, set/accuracy và prompt cũ đúng; Failed=2/KeywordOnly=1; residual PII/errors không lộ |
| Paging/performance | 256 runs; pageSize 1 và 100 đều **3 EF SELECT**: Count + OFFSET/FETCH + GROUP BY Jobs. Legacy take=3 dùng 2 SELECT, after-last dùng 2; không SystemPrompt/ExtractedText/raw AI/fileName trong SQL list |
| Cancellation tới EF reader | RequestAborted đến interceptor/reader, một query attempt, không retry; hash 13 bảng bằng nhau trước/sau |
| Native SQL cancellation | Quan sát session thực sự `WAITFOR` trong sys.dm_exec_requests trước HTTP abort; server dừng wait/query, token được hủy, đúng một attempt |
| Privacy lease hai connection | Exclusive lease chờ shared read; hủy request/lock wait sạch; lấy được exclusive lease mới sau đó; không thực hiện ẩn danh hóa/xóa dữ liệu |

Snapshot SHA-256 **13 bảng** trước/sau chuỗi API đọc: Jobs, Candidates, Cvs, Applications, ApplicationEvents, JobSkills, JobSkillAliases, PromptVersions, EvaluationSets, HrLabels, EvaluationRuns, Evaluations, EvaluationSkills. Hash/count bằng nhau, bảo vệ cả raw errors/scores/settings; artifacts chỉ lưu fingerprint/count, không lưu raw CV rows. Seed hoàn toàn tổng hợp trên catalog test, không dùng CV thật.

Evidence trong `artifacts/a25-sql/`: `*-catalog.json`, `*-before-history-reads.json`, `*-after-history-reads.json`, `*-http-and-sql.json`, `*-performance.json`, `*-before-cancellation.json`, `*-after-cancellation.json`, `*-cancellation.json`, `*-native-cancellation.json`, `*-privacy-lease.json`. Log/TRX: `a25-baseline-results/`, `a25-focused-results/`, `a25-full-results/`; summary cuối `a25-test-summary.json`.

Hiệu năng là số đo local, không cam kết SLA: focused đầu pageSize1 khoảng **179 ms**, pageSize100 khoảng **86 ms** (warm-up/cache ảnh hưởng). Đếm 3 là **EF SELECT**, chưa tính hai lệnh ADO lấy/nhả privacy app lock có sẵn, hằng số mỗi request. Không thêm index/migration hay benchmark production.

Lệnh tái kiểm:

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a25-build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/a25-build
$env:SMARTCV_A19_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A19_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A20_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A20_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A21_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A21_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A22_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A22_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A25_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A25_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --no-restore --artifacts-path artifacts/a25-build --filter 'FullyQualifiedName~RunHistory' --logger 'trx;LogFileName=a25-focused-final.trx' --results-directory artifacts/a25-focused-results
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a25-build --logger 'trx;LogFileName=a25-full.trx' --results-directory artifacts/a25-full-results
```

Không bật các opt-in SQL fixture cũ có thao tác dọn dữ liệu/catalog. **78 skipped có sẵn** gồm 73 SQL opt-in khác và 5 demo phụ thuộc tài liệu ngoài repo; không tính là pass. Đối chiếu từng test/outcome baseline với final và danh sách skipped trong test summary; không sửa/giảm assertion hay skip test cũ. Lỗi phát triển duy nhất ban đầu là test B7 gọi nhầm ApiResult.Value; đã sửa đúng contract `.Data`, build và focused pass, không phải lỗi baseline/API.


Fingerprint SQL cuối: `7DFC4288C18E7C1A67143111FE017E59E46754F351F03ECCB1021DA9646E4162`; bằng chứng `artifacts/a25-sql/SmartCv_A25_Test_a9bc2c409bb042259c59f4e6a3923f09-http-and-sql.json` và hai file before/after cùng catalog xác nhận dữ liệu giữ nguyên, 0 Gemini calls. **14 catalog A25 GUID mới** từ các vòng test được giữ lại; danh sách `artifacts/a25-final-test-catalogs.txt`.

Đối chiếu TRX: 2.156 test baseline không mất test hoặc đổi outcome, skipped membership giữ nguyên. A19 **44/44**, A20 **12/12**, A21 **52/52**, A22 **58/58**, B38 **66/66**, nhóm A12/A13/B7 chọn trong audit **102/102**. Các nhóm A35/A39/A40/A41/A42 có SQL opt-in skipped như baseline; chi tiết passed/failed/skipped ở `artifacts/a25-test-summary.json`.

## 4. Toàn bộ file M/A/D và khác biệt với mẫu

**2 M / 7 A (mới chưa stage) / 0 D = 9 file**, khớp manifest `artifacts/a25-commit-files.txt`, một relative path `/` mỗi dòng, UTF-8 không BOM, không duplicate/artifacts/secret. SHA nền đầy đủ một dòng UTF-8 không BOM ở `artifacts/a25-base-sha.txt`.

| Trạng thái | File | Nội dung |
| --- | --- | --- |
| M | `src/SmartCv.Api/Endpoints/RunEndpoints.cs` | Query dual-mode, detail init fields, failures có privacy; giữ Start/Latest/Estimate/Ranking và nhóm gate |
| M | `src/SmartCv.Core/Contracts/RunContracts.cs` | Thêm 4 init fields vào positional RunProgressDto, giữ signature/RunItemDto |
| A | `src/SmartCv.Core/Contracts/RunHistoryContracts.cs` | Page/item/failures DTO theo hợp đồng camelCase |
| A | `src/SmartCv.Core/Runs/RunHistoryQuery.cs` | Parse/validation thuần, ngày Việt Nam, page/take/offset/TotalPages/duration an toàn |
| A | `tests/SmartCv.Tests/Runs/RunHistoryQueryTests.cs` | 34 unit cases |
| A | `tests/SmartCv.Tests/Runs/RunHistoryEndpointTests.cs` | 33 HTTP cases + seed dùng chung, client B7 thật, errors/privacy/gate |
| A | `tests/SmartCv.Tests/Runs/RunHistorySqlFixture.cs` | Guard GUID catalog, Kestrel SQL, fingerprint/read recorder/cancellation fixture |
| A | `tests/SmartCv.Tests/Runs/RunHistorySqlServerTests.cs` | 3 safety guards + 5 SQL integration, hiệu năng/privacy/cancellation/read-only |
| A | `docs/A25-implementation-report.md` | Báo cáo và handoff B26 này |

Không sửa Program/VietnamTime, RunCompareEndpoints, migration/snapshot/schema/seed, source A19–A22, appsettings/package, Web/Razor/CSS/JS/Figma. SQL fixture riêng là cần thiết để kiểm relational SQL paging, cancellation và A39 session app-lock mà InMemory không kiểm tra được.

Khác ZIP có chủ đích: giữ positional record mới/gate/privacy/Start/Estimate/Ranking/Compare; parser chủ động có errors theo field và chống repeated/overflow/date extremes; pageSize không có page bị từ chối; duration clamp; failures order theo ID, bảo vệ tên/errors/extraction diagnostics; không copy file VietnamTime hoặc public routes cũ. Không làm theo lệnh drop/update dữ liệu thật, thay Gemini key, commit/push trong guide vì prompt A25 quy định rõ không được phép.

## 5. Handoff UX/UI B26 — chưa triển khai frontend

Tokens đã đọc từ `src/SmartCv.Web/wwwroot/app.css`: Inter local có dấu tiếng Việt; Deep Teal **#0a2e2d / #0f766e**, surface/neutral hiện có; `--sc-grad-ai` **#14b8a6 → #06b6d4 → #6366f1** chỉ dùng tiết chế ở phần AI. Dùng hierarchy bằng typography/khoảng trắng, viền mảnh và shadow nhẹ; không glow toàn bảng hoặc màu trạng thái chói.

### Trang lịch sử `/runs`

1. Header “Lịch sử lượt chấm”, mô tả ngắn và tổng `totalCount`. Bộ lọc loại lượt, Job (kể cả ngừng tuyển), trạng thái, từ/đến ngày Việt Nam; nút “Đặt lại”. Active filters thành chip có thể bỏ từng mục. URL chứa filter/page/pageSize để F5/back/link chia sẻ giữ trạng thái.
2. Tám cột: **Thời gian Việt Nam**, **Loại**, **Job/Bộ đánh giá**, **Trạng thái**, **CV đã xử lý/lỗi**, **Prompt/Model**, **Thời lượng**, **Mở chi tiết**. Row metadata phụ nhỏ: RunId, temperature, số Job; nhiều Job dùng chips gọn và “+N”, không ép chiều rộng. PromptTest hiển thị accuracy có nhãn; Screening/null dùng “—”, không 0 giả. CompletedAt=null thì thời lượng “Đang xử lý/Chưa hoàn tất”, không 0 giây giả.
3. Badge có chữ/icon: Chờ, Đang chấm, Hoàn tất, Lỗi; không dùng màu làm tín hiệu duy nhất. Chỉ dùng AI Glow cho nhãn/thông tin AI phù hợp. Số Failed ở run là counter đã lưu; số KeywordOnly lấy từ failures detail, không gọi `/failures` cho từng hàng để tránh N+1 phía client.
4. Phân trang dùng API **có page**, size 10/20/50, “Trang 2/5 · 87 lượt”. Đổi filter/size về page1; page quá tổng sau refresh đưa về trang hợp lệ có thông báo nhẹ. Giữ dữ liệu đang xem khi tải trang tiếp, skeleton đúng chiều cao, không nhấp nháy trắng.
5. Debounce filter phù hợp, CancellationToken/sequence guard bỏ response cũ khi đổi nhanh. Loading skeleton khác empty chưa có run (“Bắt đầu từ Tải CV”) và empty do filter (“Không có lượt phù hợp” + Bỏ lọc). API lỗi có nội dung an toàn và Thử lại, giữ filter; 400 gắn lỗi đúng trường, không làm mất form.

### Trang chi tiết `/runs/{id}`

1. Breadcrumb trở lại lịch sử có filter trước đó; title RunId/loại. Card tóm tắt trạng thái, Percent và processed/total/failed; ngày tạo/kết thúc Việt Nam, thời lượng. Running/Queued polling có cancellation khi rời trang, dừng khi Completed/Failed; tránh poll SystemPrompt dài không cần thiết hoặc cache vào analytics/localStorage.
2. Khối **“Cấu hình đã lưu”**: Prompt Version + ID, Model, Temperature, Keyword–AI weights, ngưỡng Đạt, Evaluation Set, Accuracy. Dữ liệu từ GET run; không lấy cấu hình hiện tại rồi gắn nhãn lịch sử. Chú thích cap/keyword component weights/85–70–55 chưa được snapshot; tên Job/Set có thể là tên hiện tại.
3. SystemPrompt là accordion chỉ đọc, text được encode (không render HTML), giới hạn chiều cao và có cuộn; Copy có nhắc nội dung nội bộ. Chỉ tải chi tiết theo quyền/gate, không nhúng prompt vào list hoặc URL, telemetry, LocalStorage. Nêu phiên bản được lưu qua FK, không khẳng định có chống sửa trực tiếp SQL.
4. Kết quả: Screening có link Ranking từng Job đúng runId/jobId; PromptTest liên kết khu vực đánh giá prompt đúng scope. Không đưa PromptTest vào ô chọn Screening B7. GET không có page vẫn dành cho client B7; B26 gọi page object.
5. **“Hồ sơ có vấn đề”** có hai tab: **Không chấm được** (Failed) và **Chỉ chấm từ khóa** (KeywordOnly); badge giải thích rằng KeywordOnly vẫn có điểm keyword, không phải AI đã chấm. Một evaluation không xuất hiện ở cả hai tab. Mỗi dòng/card có tên theo privacy, Job, loại, thông báo an toàn, trạng thái trích xuất và hướng dẫn khi cần tải lại tệp.
6. 404: “Không có lượt chấm #…” + quay về lịch sử/Thử lại; không dùng empty failures để suy ra không có run. Run không có lỗi hiển thị empty tích cực, không gắn nhãn mọi CV đều AI thành công khi còn pending. API lỗi tải failures có retry riêng, không làm mất cấu hình/tóm tắt đã tải.

### Privacy, responsive và accessibility

- Switch **Ẩn danh B36** áp dụng cả tên ứng viên và fileName trước render ở danh sách lỗi, tooltip, aria-label, toast, Copy và link label. Dùng helper anonymity hiện có, không chỉ ẩn bằng CSS. Dữ liệu A39 trả “Hồ sơ đã ẩn danh”/null không có nút khôi phục, switch tắt cũng không hiện lại. Không render raw errors; A25 failures đã trả thông điệp an toàn.
- 1440/1024 px: bảng thoáng, filters một hoặc hai hàng; 768 px: nhóm filter wrap và metadata gọn; 390/320 px: mobile cards với thứ tự thông tin thống nhất, filter xếp dọc, chips wrap, pager gọn, không buộc cuộn ngang tám cột.
- Target chạm ≥44px, label thực gắn input, focus Deep Teal rõ, contrast văn bản ≥4.5:1, badge có chữ, keyboard tới filter/tab/accordion/pager; errors dùng aria-describedby, cập nhật count/loading bằng aria-live=polite. Skeleton giữ layout, tôn trọng reduced motion, không shimmer/glow liên tục.
- B26 thuộc nhánh riêng `feature/b26-run-history` sau A25 merge. Không sửa frontend hoặc tạo mockup trong A25.

## 6. Giới hạn và chốt trước Git

- Chưa có M6; chốt Dev/feature-gate giữ fail-closed. Auth production nằm ngoài A25.
- Không có snapshot per-run của cap, trọng số nội bộ keyword, FitLevels, tên Job/Set; prompt content tham chiếu phiên bản bất biến qua API nhưng có thể bị sửa thủ công SQL. Đã mô tả đúng giới hạn, không thêm migration.
- Count/page là các SELECT liên tiếp; có thể quan sát thay đổi giữa hai request khi hệ thống đồng thời tạo/cập nhật run. Không cam kết snapshot nhất quán qua nhiều trang; sorting có RunId tie-break và UI refresh/poll phù hợp.
- Không benchmark production hoặc E2E giao diện B26. Năm SQL test thực và các suite còn lại được nêu đúng phạm vi; các SQL opt-in khác vẫn unverified nếu skipped.
- Không có lỗi còn mở ở build/focused/full cuối. Toàn bộ 2.156 test baseline giữ nguyên outcome; tăng đúng 75 test A25; danh sách 78 skipped giữ nguyên. Không quy skipped thành pass.

Artifacts ignored theo `.gitignore`: `a25-base-sha.txt`, `a25-commit-files.txt`, `a25-test-summary.json`, `a25-git-verification.json`, `a25-final-test-catalogs.txt` cùng log/TRX/evidence; không nằm trong manifest commit.

Trạng thái Git cuối: **feature/a25-run-history-api; HEAD/base/origin/main `7373b52c6c11471d731a82a33b97e32e1b3812e0`; 2 M / 7 mới chưa stage / 0 D; index rỗng; không upstream, không merge/rebase dở dang**.

Kiểm tra cuối gồm `git diff --check`, BOM/CRLF/EOF/trailing whitespace cả file mới, manifest khớp porcelain, index rỗng, đúng nhánh/HEAD/base và không merge/rebase. **Không stage/commit/push/merge/rebase/reset/stash/clean/restore/pull hoặc tạo PR, không chạy script commit của người dùng.** Không ghi/migrate SmartCvDb, xóa CV/run/Job hoặc catalog có sẵn, thay secrets/SQL settings, bật worker/retention, gọi Gemini thật hay tác động nhánh khác. **Dừng để chủ dự án review và tự thực hiện Git sau khi duyệt.**
