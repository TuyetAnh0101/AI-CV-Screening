# Báo cáo A39 — Ẩn danh hóa CV và xử lý hết hạn lưu

Ngày: **13/11/2026**, múi giờ Asia/Saigon.

**Kết luận: SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT.** Worker vẫn mặc định tắt. Chưa triển khai, bật tự động hoặc thay đổi database/file CV của ứng dụng thật.

## 1. Nhánh, commit nền và tài liệu

- Repository/remote đã xác minh: `TuyetAnh0101/AI-CV-Screening`, `origin` đúng GitHub repository yêu cầu.
- Đầu vào sạch trên `feature/a38-consent-retention`: không staged/modified/untracked/conflict. Không tạo stash hoặc thay đổi code A38 để lấy dependency.
- Đã `fetch origin --prune`, xác nhận A38 merge qua **PR #59**, A36 đã có qua #58.
- Nhánh A39 mới duy nhất, tạo trực tiếp từ `origin/main`: **`feature/a39-cv-anonymization-retention`**. Nhánh này được tạo ở bước khảo sát trước khi chờ người dùng cung cấp prompt; tiếp tục đúng nhánh đó, không tạo lại hoặc dùng nhánh A38.
- HEAD/commit nền: **`e15ab5fae1ee79c162197bd1b057f79acbabb0a7`**. HEAD không đổi sau triển khai.
- Solution: `SmartCv.slnx`; SDK theo `global.json`: **10.0.403**. Dùng output riêng `artifacts/a39-build`, kết quả test `artifacts/a39-tests`.
- Đã đọc `A39_Codex_Prompt.md` người dùng cung cấp và mục A39 trong ZIP, source/tests/snippets/SQL đi kèm. Không thực hiện các lệnh Git hoặc SQL ghi dữ liệu trong hướng dẫn ZIP.
- A38 có đủ entity/options/rules/migration; migrations A38 được áp dụng trên các **catalog test mới**. Không kiểm tra bằng cách cập nhật `SmartCvDb` thật.
- A42 chưa có trong nền: không triển khai text quality/estimate/duplicate enrichment thay A42. `/text` chỉ bổ sung tombstone 410 cho CV ẩn danh; CV thông thường vẫn 404 cho tới A42.

## 2. Đối chiếu ZIP và checklist triển khai

| Yêu cầu / khác biệt mẫu | Kết quả |
|---|---|
| Manual anonymize, idempotency, 404/409 | Hoàn thành; kiểm tra tất cả Application thuộc CV, không lọc một Job. |
| Auto due, batch, preview và worker | Hoàn thành; có phục hồi journal, default dryRun và worker tắt. |
| Source mẫu xóa file trước SaveChanges | Thay bằng SQL transaction chưa commit + private recovery copy/journal; phục hồi khi lỗi. Không tuyên bố SQL/file atomic. |
| Source mẫu sửa tracked FileHash | Dùng ExecuteUpdate trong transaction, giữ alternate key/unique constraint hiện có; không drop constraint hoặc sửa A38. |
| Mẫu chỉ kiểm tra due trước vòng lặp | Kiểm tra lại deadline/stage/busy ngay dưới khóa tại thời điểm xử lý từng CV. |
| Mẫu AutoAnonymize=true | Đổi thành mặc định false; worker còn bị chặn ngoài Development hoặc khi CV gate tắt. |
| Endpoint mẫu map không có chốt | Dùng Development + CV feature gate; nếu có auth scheme, yêu cầu đăng nhập và role HR/Admin. |
| Mẫu trả ex.Message trong Errors | Chỉ trả ID, trạng thái và hướng dẫn cố định; không log actor text, nội dung, đường dẫn hay raw exception mới. |
| API đọc sau ẩn danh | Wipe dữ liệu + tombstone projections + shared privacy lease bao phủ cả serialization/file streaming. |
| 7 case mẫu RetentionDueTests | Giữ nguyên nội dung mẫu; 7/7 pass. |

Đã đối chiếu tất cả tám file tham chiếu A39 trong ZIP với repository. Bảng đối chiếu cục bộ ở `artifacts/a39-reference-comparison.json`; chỉ test thuần mẫu giống nguyên văn, source được tích hợp có chọn lọc. Không ghi đè nguyên `CvEndpoints.cs` hoặc `EvaluationSetEndpoints.cs`.

Hai ảnh `cv-anh-ro.png`, `cv-anh-mo.png` trong cùng ZIP đã được xem ở khảo sát A38/A39: là CV demo rõ/mờ, không phải UI mockup. Không thiết kế UI hoặc sửa OCR vì các ảnh này. Full regression có chạy upload ảnh demo của A38; không suy ra độ chính xác OCR/AI từ việc upload thành công.

## 3. Dữ liệu xóa và dữ liệu giữ

Khi thành công, giữ dòng CV/CvId, đổi tên thành **Hồ sơ đã ẩn danh**, đặt `StoredPath=(da-xoa)`, thay FileHash bằng chuỗi `ANON` + GUID và padding đủ 64 ký tự. Namespace này không trùng chuỗi SHA256 hex thật. Ghi `AnonymizedAt` UTC đúng một lần.

Làm trống:

- `Cvs.ExtractedText`, `ExtractionError`, `CandidateId`.
- `Evaluations.Reasoning`, `AiRawResponse`, cả `ErrorMessage` vì lỗi cũ có thể chứa dữ liệu cá nhân.
- `EvaluationSkills.Evidence`.
- Xóa file gốc. Xóa Candidate chỉ khi không còn CV nào tham chiếu; Candidate dùng chung với CV khác được giữ.

Giữ nguyên trong thao tác A39:

- UploadedAt, SourceChannel, ConsentConfirmedAt, RetainUntil, CvId.
- KeywordScore/AiScore/FinalScore, ScoreSource/FitLabel/PredictedPass, YearsExperience, skill codes và IsMatched.
- Application Stage/RejectReason/StageVersion, ApplicationEvents và HrLabels.
- Số lượng bản ghi, trạng thái Run/Evaluation và accuracy đã lưu; không gọi lại AI, không tạo lịch sử hoặc nhãn giả.

Các chỉ số dẫn xuất từ nội dung, như trạng thái CV còn sẵn sàng để chấm lại hay một số ReviewFlags, có thể phản ánh việc nội dung không còn. Không lưu lại nội dung nhận diện để duy trì các chỉ số dẫn xuất đó. Điểm và rank theo điểm được giữ.

**Ngoại lệ được giữ theo đặc tả:** HR notes, tên người ghi lịch sử/nhãn có thể chứa PII do người dùng nhập. A39 không sửa/xóa lịch sử để làm chúng biến mất. Đã tạo `docs/huong-dan-hr.md` vì file này chưa có trong repository: nhắc HR không nhập thông tin cá nhân, và báo quản trị khi ghi chú cũ có PII. Không tuyên bố mọi PII trong ghi chú đã bị loại bỏ.

## 4. API và contracts

| Endpoint | Kết quả |
|---|---|
| POST `/api/cvs/{id}/anonymize` | Body actor tùy chọn; 200 Done/AlreadyDone, 404 không có CV, 409 bị chặn/busy, 500 chưa xử lý/phục hồi xong. |
| GET `/api/retention/settings` | Months, AutoAnonymize, CheckIntervalHours, BatchSize; không trả secrets/storage paths. |
| POST `/api/retention/run?dryRun=true&asOf=...` | Mặc định chỉ đọc; preview ngày tương lai được phép; không khôi phục/xóa file hoặc ghi SQL khi dryRun. |
| POST `/api/retention/run?dryRun=false` | Xử lý thật theo UTC hiện tại hoặc ngày quá khứ; ngày tương lai trả 400. |
| GET `/api/cvs/{id}` sau ẩn danh | Tên trung tính, tên ứng viên/nội dung/error null, fileAvailable=false, có timestamp. |
| GET `/file`, GET `/text`, POST `/extract` sau ẩn danh | 410. |
| AI/keyword preview CV ẩn danh | 409, không gọi Gemini. |
| Protected read khi privacy lock/kết nối chưa sẵn sàng | 503 có thông báo cố định, không trả dữ liệu cũ để giả thành công. |

`actor` được trim, tối đa 100 ký tự, không ký tự điều khiển; không ghi actor text vào log mới. Không coi actor do client tự khai là danh tính đã xác thực.

`asOf`: nhận `yyyy-MM-dd` hoặc ISO UTC có `Z`/`+00:00`; thời gian offset khác UTC, chuỗi sai hoặc future real-run đều 400. Timestamp server/DTO là UTC.

`RetentionRunResultDto` có RanAt, DryRun, DueCount, AnonymizedCount, FailedCount, SkippedCount, CvIds, Errors; thêm **RecoveryCount, RecoveredCount, RecoveryCvIds**. Phân biệt CV đến hạn với CV cần khôi phục/dọn bản sao sau lần xử lý bị ngắt. `CvIds` là preview IDs khi dryRun, IDs thực sự xử lý xong khi chạy thật; không đếm request thất bại như thành công.

Không sửa giao diện SCApp. DTO/status là đầu vào cho B39/B42 sau này.

## 5. Security, concurrency và tính nhất quán SQL/file

### Điều kiện và khóa

- Manual bị chặn nếu bất kỳ Job có Shortlisted/Interview.
- Auto chỉ nhận CV chưa ẩn danh, có deadline <= now, không còn New/Reviewing/Shortlisted/Interview ở bất kỳ Job nào. Null deadline không tự xử lý.
- Chặn thêm khi OCR còn Pending, hoặc CV có Evaluation thuộc Run Queued/Running. Không xóa nội dung trong khi các processor hợp lệ còn có thể ghi lại kết quả.
- Chặn file path được CV khác tham chiếu. Traversal, reparse point, lỗi permission/I/O không bị coi như file đã mất; không mark success.
- Mỗi CV có transaction Serializable. Cvs và toàn bộ Application/CvId range được đọc với UPDLOCK/HOLDLOCK trước kiểm tra/xóa. Không chỉ dựa vào danh sách due đã đọc trước đó.
- `CvPrivacyScope` dùng SQL Server application lock theo database, owner Session. API đọc/thao tác liên quan dùng Shared lease; anonymization/recovery dùng Exclusive lease trước các khóa khác. Điều phối giữa các SQL sessions, không dựa vào mutex RAM của một process.
- Shared lease giữ qua việc tạo response, serialize JSON và stream file. Anonymization không vượt qua một response đang dùng dữ liệu cũ. Các thao tác stage, tạo Run và re-extract cũng lấy lease trước khi kiểm tra trạng thái.
- Chờ khóa tối đa 5 giây: erasure bị chặn trả 409; read tạm chưa sẵn sàng trả 503. Không tự retry quyết định ghi của HR.
- Hủy acquisition có thể được SqlClient báo bằng SqlException; chuẩn hóa sang OperationCanceledException khi token thực sự đã hủy. Session có kết quả acquisition/release không chắc chắn không được đưa trở lại pool như một session an toàn.
- Response cá nhân có `Cache-Control: no-store`. Không thể thu hồi dữ liệu client đã nhận từ trước, dữ liệu nằm trong backup cũ hoặc truy vấn trực tiếp ngoài API.

### File recovery

1. Kiểm tra eligibility dưới khóa; thực hiện SQL wipe trong transaction **chưa commit**.
2. Storage tạo bản phục hồi riêng trong `StorageRoot/.a39-recovery`, ghi journal theo CvId, flush dữ liệu/journal rồi mới xóa file gốc.
3. Commit SQL; sau đó purge recovery copy/journal. Chỉ trả Done khi các bước của lần gọi hoàn tất.
4. SQL/I/O thất bại trước commit: rollback SQL và khôi phục file gốc nếu đã chuẩn bị/xóa file.
5. Nếu commit outcome chưa xác định: giữ bản phục hồi, báo lỗi; không đoán trạng thái rồi khôi phục sai. Lần xử lý sau đọc lại marker dưới khóa.
6. Scanner thật xử lý journal trước, độc lập với deadline/stage hiện tại. Marker null -> phục hồi file, không xóa lại CV đó trong cùng batch. Marker đã có -> dọn bản sao còn sót. DryRun chỉ liệt kê, không phục hồi/purge.

**Không có distributed transaction SQL/filesystem.** Nếu cleanup sau commit lỗi, SQL và file gốc đã được xử lý nhưng private recovery copy có thể còn; API trả 500/FailedCount, không báo hoàn tất. Marker lúc đó phản ánh SQL đã ẩn danh và file gốc không còn được cung cấp; recovery fields/journal phản ánh việc dọn bản sao chưa xong. Lần quét/nhắc lại sẽ xử lý trạng thái này.

Batch giới hạn tổng số recovery + CV mới theo BatchSize, mặc định 100, validate 1–200. Recovery CV không bị xóa lại trong cùng lượt. Từng CV có transaction riêng; lỗi một CV không giả thành công hoặc làm mất dữ liệu CV khác. Query due lọc trong SQL, các UPDATE dùng subquery/set operations thay vì tải từng Evaluation/Skill để sửa. Vòng lặp per-CV có chủ đích để cô lập transaction/file failures; không tải vô hạn hoặc query từng Job.

### Worker và quyền truy cập

- `AutoAnonymize=false` ở options và appsettings. Không tự bật trong Development/production/user secrets.
- Worker chỉ chạy khi được bật rõ ràng, Development và CV feature gate bật. Lần đầu sau một phút, sau đó PeriodicTimer, mặc định 24 giờ; interval validate 1–720 giờ để tránh timer vượt giới hạn.
- TimeProvider phục vụ kiểm thử lịch; scheduler lỗi một lượt vẫn chờ lượt sau, shutdown hủy sạch.
- Endpoint retention/anonymize tối thiểu cùng chốt CV: Development + `CvUpload:Endpoint:Enabled`. Ngoài Development không map; cấu hình CV sai ở Production vẫn fail closed như code cũ.
- Có auth scheme: anonymous 401, role không phải HR/Admin 403. Không vô hiệu hóa validation, constraints hoặc cơ chế truy cập cũ. Trước M6, chỉ vận hành Development trong mạng/máy tin cậy; đây không phải hệ thống xác thực production hoàn chỉnh.

## 6. Kiểm thử và bằng chứng

| Nhóm / lần chạy | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Baseline trước sửa | 1631 | 0 | 53 |
| 7 case ZIP RetentionDueTests | 7 | 0 | 0 |
| A39PrivacyTests: rules/storage/HTTP/security/config | 20 | 0 | 0 |
| A39WorkerTests: đồng hồ giả, lịch, lỗi, shutdown, gate | 4 | 0 | 0 |
| A39SqlTests: SQL thật cô lập | 24 | 0 | 0 |
| Focused A39 cuối | **55** | **0** | **0** |
| Full regression cuối | **1699** | **0** | **43** |

Các dòng tập con **không cộng dồn**. Full cuối có 1742 case. Số total so với baseline còn tăng do theory SQL được bật và mở rộng các data rows vốn bị skip trong baseline; không xóa/làm yếu test cũ.

Build cuối: **0 warning, 0 error**. Source không thay đổi nghiệp vụ sau focused/full; lượt build cuối sau khi test kết thúc xác nhận lại output sạch.

Các nhóm regression đáng chú ý trong full: A35 endpoint 57 + persistence 7 pass; A38 consent 20 pass và migration/SQL class 9 pass (8 case SQL + 1 model unit); A36 rules 15 + endpoint 2 pass; A40 endpoint 22 + ReviewFlags 19 pass; B34 UI 16, B35 decision 11, B36 privacy 16, PromptTestFlow 25 pass. Có chạy các test PDF demo A37/A40/B34 và ảnh upload A38 bằng dữ liệu demo, không gọi Gemini thật.

**43 skipped đều là SQL opt-in cũ chưa bật:** A11 6, A14/PromptSql 5, A15 7, A16 10, A17 3, A35 SQL 7, A36/LabelSuggestion SQL 1, RuntimeSchema 1, RuntimeScriptDeployment 1, FullAudit SQL 2. Một số fixture cũ tự xóa catalog test; không bật chúng vì yêu cầu A39 không cho tự xóa database test. Các opt-in runtime rộng khác không được chọn cho đợt A39. Không ghi nhận skipped là passed hoặc khẳng định các bài đó đã kiểm chứng.

### Các tình huống đã kiểm tra

- Trước/bằng/sau deadline, null hạn, đã ẩn danh, nhiều Job, manual blocker.
- Manual 200, 404, 409; gọi lặp giữ cùng timestamp, không tạo history/label.
- Sentinel PII synthetic trong text, tên file, reasoning, raw JSON, evidence/error; không còn trong DB/API sau erasure, trong khi điểm, StageVersion, history và labels còn nguyên.
- CV dùng chung Candidate: giữ parent khi CV khác còn tham chiếu, xóa parent sau CV cuối.
- `/file`, `/text`, `/extract` 410; AI/keyword preview 409; metadata tên trung tính/fileAvailable=false.
- Ranking, evaluation detail, compare, application list, evaluation sets, CSV template, label suggestions và PromptTest reads; thêm test tombstone với legacy columns còn marker để kiểm chứng projection guard.
- Hai PromptTest đã lưu: compare không lộ nội dung, accuracy và điểm không bị tính lại bằng AI.
- Hai request anonymize đồng thời: một Done, một AlreadyDone, cùng timestamp.
- Due snapshot cũ sau HR đổi Reviewing hoặc deadline tương lai: service recheck chặn, file còn.
- OCR Pending hoặc AI Queued/Running: 409, file còn. Queue/processor OCR bỏ qua tombstone để không đọc lại nội dung đã xóa.
- SQL update fault sau khi statement thực sự chạy: rollback DB trước thao tác file; commit fault sau chuẩn bị file: phục hồi original và DB.
- Lỗi storage trước/sau xóa original, lỗi cleanup sau commit, retry, scanner phục hồi cả CV active hoặc đã ẩn danh.
- DryRun không ghi/xóa/phục hồi; preview tương lai được, real-run tương lai/UTC sai bị 400; batch giới hạn, lỗi từng CV độc lập.
- Fingerprint trước erasure bị Duplicate; cùng bytes sau erasure upload thành CV mới, CV cũ vẫn giữ dòng/timestamp.
- Privacy lease: read đang giữ khóa làm erasure 409; exclusive work làm read 503; hủy acquisition không để session pool chặn lần xử lý sau.
- Worker với đồng hồ giả: chờ một phút, lỗi lần đầu rồi thử kỳ sau, shutdown; default off/Production/gate off không mở scope.
- Guard khi CV gate tắt và Production; auth scheme với anonymous/Viewer/HR/Admin; cấu hình batch/timer sai dừng startup.

### Môi trường SQL và lệnh

Tất cả A39 SQL dùng `(localdb)\MSSQLLocalDB`, catalog mới `SmartCv_A39_Test_<GUID>`, synthetic fixtures và storage tạm. Validate prefix, không AttachDBFilename. Catalog được giữ lại, không EnsureDeleted/DROP. Full còn bật A38/A40 SQL tương ứng với GUID mới và fixture giữ lại; không sử dụng SmartCvDb.

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a39-build
dotnet build SmartCv.slnx --artifacts-path artifacts/a39-build --no-restore
dotnet test SmartCv.slnx --artifacts-path artifacts/a39-build --no-restore --no-build --logger "trx;LogFileName=full-regression-final.trx" --results-directory artifacts/a39-tests
```

SQL opt-in đã đặt cho A38/A39/A40 vào catalog test có tiền tố đúng; các fixture tự tạo GUID. Demo ZIP được truyền qua `SMARTCV_B34_DEMO_ZIP`, `SMARTCV_A40_DEMO_ZIP`, `SMARTCV_A38_DEMO_ZIP`. Worker của ứng dụng không được bật; test scheduler bật riêng instance có đồng hồ giả/database InMemory rỗng.

Bằng chứng cục bộ (ignored):

- `artifacts/a39-baseline-build.log`, `artifacts/a39-baseline-test.log`, `artifacts/a39-tests/baseline.trx`.
- `artifacts/a39-final-build.log`.
- `artifacts/a39-focused-final.log`, `artifacts/a39-tests/a39-focused-final.trx`.
- `artifacts/a39-full-regression-final.log`, `artifacts/a39-tests/full-regression-final.trx`.
- `artifacts/a39-tests/retained-sql-databases.txt`: catalog A39 của các lượt test, kể cả lượt trung gian, được giữ để đối chiếu.

Các lỗi trong quá trình phát triển đã sửa: assumption về auth provider có đăng ký service nhưng chưa có scheme; cleanup missing recovery directory; SqlClient cancellation trả SqlException. Giữ nguyên assert cuối thay vì nới lỏng cho xanh. Có một lượt build/test bị chạy chồng gây MSB3021/MSB3027 do DLL lock; đã chờ test kết thúc và build lại tuần tự thành công, không kill tiến trình người dùng. Log lượt khóa DLL: `artifacts/a39-build-lock-attempt.log`.

Không có browser/live Web E2E trong A39; dùng TestServer, component regression hiện có và SQL thật cô lập. Không sửa UI, không suy ra production đã hoạt động chỉ từ test synthetic.

## 7. Danh sách chính xác file M/A/D

**19 file M, 14 file A, 0 file D — tổng 33 file.** Không có migration, snapshot, package, ConnectionStrings hoặc User Secrets thay đổi.

| Trạng thái | File | Mục đích |
|---|---|---|
| A | `src/SmartCv.Core/Contracts/RetentionContracts.cs` | DTO manual/run và trạng thái recovery minh bạch. |
| A | `src/SmartCv.Core/Storage/ICvErasureStorage.cs` | Hợp đồng recovery file, không phá interface storage/upload cũ. |
| M | `src/SmartCv.Core/Options/RetentionOptions.cs` | Auto default off, interval và batch. |
| M | `src/SmartCv.Core/Privacy/RetentionRules.cs` | Rule manual/auto; giữ nguyên phép gia hạn A38. |
| A | `src/SmartCv.Infrastructure/Privacy/CvAnonymizer.cs` | SQL locking/wipe, idempotency, Candidate sharing và recovery. |
| A | `src/SmartCv.Infrastructure/Privacy/RetentionProcessor.cs` | Bounded due scan, reconcile journal, per-CV outcomes. |
| A | `src/SmartCv.Infrastructure/Privacy/CvPrivacyScope.cs` | Cross-session privacy lease, cancellation/pool cleanup. |
| M | `src/SmartCv.Infrastructure/Storage/LocalCvFileStorage.cs` | Private recovery copy/journal, restore/purge, path protection. |
| M | `src/SmartCv.Infrastructure/Extraction/CvExtractionProcessor.cs` | Không xử lý tombstone từ queue cũ. |
| M | `src/SmartCv.Infrastructure/Prompts/PromptTestQueries.cs` | Che tên/file/reasoning/error cho CV ẩn danh trong PromptTest. |
| M | `src/SmartCv.Infrastructure/Prompts/PromptCompareQueries.cs` | Không đưa nhận diện cũ vào compare/warnings. |
| A | `src/SmartCv.Api/Background/RetentionWorker.cs` | Schedule, gate, default off, TimeProvider, cancellation. |
| M | `src/SmartCv.Api/Background/CvExtractionWorker.cs` | Không requeue CV đã ẩn danh khi khởi động. |
| A | `src/SmartCv.Api/Endpoints/RetentionEndpoints.cs` | Settings/run/manual, quyền truy cập, validation/status. |
| A | `src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs` | Bảo vệ response/mutations đang chạy và no-store. |
| M | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` | Metadata scrub; file/extract 410; giữ A38 upload. |
| M | `src/SmartCv.Api/Endpoints/ApplicationEndpoints.cs` | Không trả tên/file cũ trong application list. |
| M | `src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs` | Chặn tombstone trước/sau AI preview, không trả nội dung cũ. |
| M | `src/SmartCv.Api/Endpoints/ScoringPreviewEndpoints.cs` | Chặn keyword preview CV đã ẩn danh. |
| M | `src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs` | Scrub tên/email/reasoning/raw/evidence/error, giữ scores/stage. |
| M | `src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs` | Che nhận diện trong labels/candidates/template, giữ nhãn và notes. |
| M | `src/SmartCv.Api/Endpoints/EvaluationSetSuggestionsEndpoints.cs` | Loại CV ẩn danh khỏi pool đề xuất, giữ logic A36. |
| M | `src/SmartCv.Api/Endpoints/PromptTestEndpoints.cs` | Không tạo lượt chấm lại trên CV đã ẩn danh. |
| M | `src/SmartCv.Api/Endpoints/RunEndpoints.cs` | Che nhận diện/errors, chặn chấm lại tombstone, giữ rank/điểm. |
| M | `src/SmartCv.Api/Endpoints/RunCompareEndpoints.cs` | Không trả reasoning/evidence cũ khi compare. |
| M | `src/SmartCv.Api/Program.cs` | DI, TimeProvider, option validation, worker, routes/middleware. |
| M | `src/SmartCv.Api/appsettings.json` | Auto false, interval 24, batch 100; không đổi cấu hình khác. |
| A | `tests/SmartCv.Tests/Privacy/RetentionDueTests.cs` | 7 case nguyên bản ZIP. |
| A | `tests/SmartCv.Tests/A39PrivacyTests.cs` | 20 case rule/storage/API guard/auth/config. |
| A | `tests/SmartCv.Tests/A39WorkerTests.cs` | 4 case scheduler deterministic, lỗi và gate. |
| A | `tests/SmartCv.Tests/A39SqlTests.cs` | 24 case SQL/API/concurrency/recovery/PII invariants. |
| A | `docs/huong-dan-hr.md` | Hướng dẫn ghi chú không chứa PII, giới hạn xóa và quy trình báo lỗi. |
| A | `docs/A39-implementation-report.md` | Báo cáo và điều kiện vận hành. |

Các file ngoài mẫu ZIP cần thiết để đáp ứng prompt bổ sung: storage recovery interface, privacy scope/middleware, các projection và OCR guards, TimeProvider/tests, hướng dẫn HR. SQL sentinel tests chứng minh các bề mặt đọc và trạng thái legacy/tombstone; concurrency tests chứng minh lý do cần lease. Không sửa test cũ hoặc source UI/Figma/AppShell/OCR engine/Gemini/scoring algorithm.

## 8. Rủi ro còn lại và điều kiện vận hành cần duyệt

1. **Chưa bật AutoAnonymize trên ứng dụng thật.** Trước bật cần người vận hành duyệt chính sách, quyền, backup/restore, schema A38, storage root ngoài wwwroot và ACL chỉ tài khoản dịch vụ/quản trị phù hợp.
2. Recovery copy chứa dữ liệu CV trong vùng riêng cho đến khi restore/purge xong. Cần bảo vệ vùng này, theo dõi FailedCount/RecoveryCount và xử lý lỗi ACL/disk. Copy không có journal hoàn chỉnh, journal hỏng hoặc CvId không còn trong DB phải được quản trị kiểm tra; không tự xóa một backup không xác minh được.
3. Mất kết nối đúng lúc commit, mất điện/kernel crash và filesystem hỏng thực tế chưa được thử bằng cách phá môi trường. Đã giả lập commit failure, ngắt giữa các pha, I/O failures và cancellation; không khẳng định hai hệ lưu trữ có atomic commit.
4. Privacy lease là khóa toàn database cho các luồng liên quan. Shared reads vẫn song song; erasure tuần tự. Response/file download kéo dài có thể hoãn erasure, gặp timeout trả 409/503. Chưa benchmark tải production hoặc nhiều OS process; đã kiểm tra nhiều SQL sessions và request song song.
5. Không thu hồi client data/backup/log cũ ngoài phạm vi A39. Ghi chú HR/audit identity được giữ theo đặc tả, có thể chứa PII nhập tay. Không coi A39 là chứng nhận xóa mọi bản sao hoặc cơ chế pháp lý hoàn chỉnh.
6. Chưa kiểm tra thực tế SmartCvDb, ACL/encryption/backup hiện tại hoặc triển khai M6 authentication/CSRF. Production routes vẫn đóng; không mở Development ra mạng công khai để thay thế authorization.
7. Chưa browser E2E, chưa stress ở max batch/dung lượng trên storage production, chưa xác minh Linux filesystem thực tế. Các kết quả pass thuộc môi trường test đã mô tả.
8. 43 SQL opt-in còn skipped nêu rõ ở phần 6. Chưa kết luận tất cả các chức năng ngoài các luồng đã chạy đều không còn lỗi.

Kế hoạch triển khai: duyệt code và báo cáo → xác minh schema A38 bằng truy vấn chỉ đọc → duyệt backup/storage/quyền → nghiệm thu synthetic trên môi trường tách biệt → deploy với Auto=false → kiểm tra preview → phê duyệt riêng mới bật lịch. Không chạy SQL sửa RetainUntil trên SmartCvDb để thử worker.

## 9. Git cuối và manifest

Manifest: `artifacts/a39-commit-files.txt`, UTF-8, mỗi dòng một đường dẫn tương đối, đúng 33 file M/A ở trên. Không chứa chính manifest hoặc artifacts. Các log/TRX/reference comparison, dữ liệu demo và database test không thuộc danh sách commit.

`git status --short --branch --untracked-files=all`:

```text
## feature/a39-cv-anonymization-retention...origin/main
 M src/SmartCv.Api/Background/CvExtractionWorker.cs
 M src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs
 M src/SmartCv.Api/Endpoints/ApplicationEndpoints.cs
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs
 M src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs
 M src/SmartCv.Api/Endpoints/EvaluationSetSuggestionsEndpoints.cs
 M src/SmartCv.Api/Endpoints/PromptTestEndpoints.cs
 M src/SmartCv.Api/Endpoints/RunCompareEndpoints.cs
 M src/SmartCv.Api/Endpoints/RunEndpoints.cs
 M src/SmartCv.Api/Endpoints/ScoringPreviewEndpoints.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Options/RetentionOptions.cs
 M src/SmartCv.Core/Privacy/RetentionRules.cs
 M src/SmartCv.Infrastructure/Extraction/CvExtractionProcessor.cs
 M src/SmartCv.Infrastructure/Prompts/PromptCompareQueries.cs
 M src/SmartCv.Infrastructure/Prompts/PromptTestQueries.cs
 M src/SmartCv.Infrastructure/Storage/LocalCvFileStorage.cs
?? docs/A39-implementation-report.md
?? docs/huong-dan-hr.md
?? src/SmartCv.Api/Background/RetentionWorker.cs
?? src/SmartCv.Api/Endpoints/RetentionEndpoints.cs
?? src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
?? src/SmartCv.Core/Contracts/RetentionContracts.cs
?? src/SmartCv.Core/Storage/ICvErasureStorage.cs
?? src/SmartCv.Infrastructure/Privacy/CvAnonymizer.cs
?? src/SmartCv.Infrastructure/Privacy/CvPrivacyScope.cs
?? src/SmartCv.Infrastructure/Privacy/RetentionProcessor.cs
?? tests/SmartCv.Tests/A39PrivacyTests.cs
?? tests/SmartCv.Tests/A39SqlTests.cs
?? tests/SmartCv.Tests/A39WorkerTests.cs
?? tests/SmartCv.Tests/Privacy/RetentionDueTests.cs
```

`git diff --stat` (chỉ 19 tracked files; 14 untracked được liệt kê ở bảng/manifest, chưa nằm trong diff stat):

```text
 src/SmartCv.Api/Background/CvExtractionWorker.cs   |   2 +-
 src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs    |   6 +-
 src/SmartCv.Api/Endpoints/ApplicationEndpoints.cs  |   4 +-
 src/SmartCv.Api/Endpoints/CvEndpoints.cs           |  20 +++--
 src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs   |  18 ++--
 .../Endpoints/EvaluationSetEndpoints.cs            |  12 +--
 .../Endpoints/EvaluationSetSuggestionsEndpoints.cs |   2 +-
 src/SmartCv.Api/Endpoints/PromptTestEndpoints.cs   |   2 +-
 src/SmartCv.Api/Endpoints/RunCompareEndpoints.cs   |   6 +-
 src/SmartCv.Api/Endpoints/RunEndpoints.cs          |  14 +--
 .../Endpoints/ScoringPreviewEndpoints.cs           |   6 +-
 src/SmartCv.Api/Program.cs                         |   9 ++
 src/SmartCv.Api/appsettings.json                   |   5 +-
 src/SmartCv.Core/Options/RetentionOptions.cs       |   3 +
 src/SmartCv.Core/Privacy/RetentionRules.cs         |  12 +++
 .../Extraction/CvExtractionProcessor.cs            |   2 +-
 .../Prompts/PromptCompareQueries.cs                |   4 +-
 .../Prompts/PromptTestQueries.cs                   |   6 +-
 .../Storage/LocalCvFileStorage.cs                  | 100 ++++++++++++++++++++-
 19 files changed, 185 insertions(+), 48 deletions(-)
```

`git diff --check`: exit code 0, không có output/lỗi whitespace.

`git diff --cached --name-status`: không có output; không có staged files.

## 10. Kết luận

Hoàn thành backend A39 và các kiểm thử nêu trên. Build cuối thành công; focused 55/55 và full regression 1699 pass, 0 fail, 43 skipped. Các invariants điểm/rank theo điểm, HR stage/history/labels/accuracy đã được kiểm chứng trên SQL synthetic; A38 upload/retention và các nhóm regression liên quan tiếp tục pass trong phạm vi đã chạy.

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT.** Không đồng nghĩa đã được phép bật xử lý phá hủy trên dữ liệu thật. Không stage, commit, push, merge, tạo PR, sửa database thật hoặc xóa CV thật. Dừng sau báo cáo.
