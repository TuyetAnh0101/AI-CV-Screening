# A43 – Quyền của ứng viên với dữ liệu

Ngày: 18/11/2026, Asia/Saigon. **Hoàn tất phần triển khai và kiểm thử khả dụng: full regression 2694 passed, 0 failed, 121 skipped.** Hai expectation migration đã được cập nhật đúng phê duyệt. Không stage/commit/push/PR.

## 1. Căn cứ xác định nhiệm vụ

Đã mở trực tiếp C:/TTCN/A23_B25_SmartCv.zip trước khi kiểm tra Git/tạo nhánh. ZIP đọc được, 130.166 bytes, có **128 mục / 56 file**. SHA-256: 4AFD8D4D7DFD845FC11FBFCFB15305F7824AC129ABD1585317481CC57C8DD8FB.

Guide có đầy đủ mục **A43 – Quyền của ứng viên với dữ liệu**, không suy đoán từ tên ZIP. Phạm vi là Backend/API + database: xuất dữ liệu JSON, xử lý yêu cầu xóa và nhật ký yêu cầu. Tiêu chí chính: sau khi xóa, tìm theo email/số điện thoại của ứng viên không còn kết quả; file CV trả 410; điểm và lịch sử được giữ.

Phụ thuộc A24 và A39. Đã fetch và xác minh cả hai có trên origin/main trước khi tạo nhánh. Không lấy code chưa merge từ feature branch.

## 2. Tài liệu và mẫu đã đọc

Các đường dẫn dưới đây tính từ A23_B25_SmartCv/ trong ZIP:

- HUONG_DAN_A23_A24_A43_B23_B24_B25.md: phần chuẩn bị chung, bảng phụ thuộc/thứ tự A23 → A24 → A43, quyết định rút hồ sơ trước khi xóa, toàn bộ mục A43 (quy tắc, API, schema, các bước, các kịch bản thử và tiêu chí nghiệm thu), ghi chú liên quan.
- A43/src/SmartCv.Core/Contracts/CandidateContracts.cs.
- A43/src/SmartCv.Core/Entities/CandidateDataRequest.cs.
- A43/src/SmartCv.Core/Candidates/CandidateErasePlan.cs.
- A43/src/SmartCv.Infrastructure/Data/Configurations/CandidateDataRequestConfiguration.cs.
- A43/src/SmartCv.Api/Endpoints/CandidateEndpoints.cs: đã đọc cả phần A24 kế thừa và phần A43.
- A43/tests/SmartCv.Tests/Candidates/CandidateErasePlanTests.cs.
- database/a43_kiem_tra_xoa.sql.
- snippets/Program.A23-A43.cs.txt.

Inventory đầy đủ: artifacts/a43/zip-inventory.txt (ignore). Đã đối chiếu source thật của A24, A39, DbContext/configurations/migrations/snapshot, bộ lưu file và recovery journal, retention processor, access gate, TestSupport và các test liên quan. Không chép đè file mẫu lên source mới nhất.

## 3. Ảnh và dữ liệu thử

**ZIP không có ảnh PNG/JPG/JPEG/SVG/GIF/WebP hoặc PDF.** Không có mockup UI, sơ đồ ảnh hay ảnh dữ liệu test để mở xem. Không tuyên bố đã xem ảnh.

A43 chỉ backend; không tạo trang Blazor, CSS, component hay mock data production. Test dùng dữ liệu tổng hợp và file PDF giả trong thư mục tạm; không dùng CV thật, không chạy Gemini/OCR thật.

## 4. Git và SHA nền

| Mục | Giá trị |
|---|---|
| Repository duy nhất | C:/TTCN/project/AI-CV-Screening |
| Nhánh ban đầu | feature/a24-implementation |
| HEAD ban đầu | af75be08da3d6c7e2307bbbfe4f9e73beb8ec636 |
| Trạng thái trước fetch | Sạch; staged/unstaged/untracked/unmerged đều trống |
| Thao tác đang dở | Không có merge/rebase/cherry-pick/revert/sequencer/bisect |
| Nhánh A43 trước khi tạo | Không có local/remote |
| Remote | origin — https://github.com/TuyetAnh0101/AI-CV-Screening.git |
| Fetch | Thành công, origin/main cập nhật từ A23 #76 lên A24 #77 |
| Nhánh làm việc | feature/a43-implementation |
| Cách tạo | switch --no-track -c feature/a43-implementation origin/main |
| Upstream A43 | Chưa đặt |
| SHA nền 40 ký tự | 5596e325c3120ccc78ed63b76d61333f42988910 |
| HEAD bàn giao | 5596e325c3120ccc78ed63b76d61333f42988910 |

Nền đã có A24 #77 và B26 #75. A24 đã được commit trước lượt làm này; không mang thay đổi chưa commit sang A43. Chỉ dùng safe.directory theo từng lệnh Git, không sửa cấu hình global. Fetch/switch được chạy qua cơ chế escalation do .git bị giới hạn ghi trong sandbox. Không tạo worktree/clone/repository khác.

## 5. Ma trận yêu cầu và kết quả

| Yêu cầu | Triển khai / kiểm chứng |
|---|---|
| Xuất dữ liệu liên hệ và mọi CV | JSON chứa Candidate, CV metadata/nguồn/consent/retainUntil/text; test HTTP đủ 2 CV |
| Hồ sơ, các lần chấm, điểm/mức/nhận xét, kỹ năng/bằng chứng | Projection theo contract ZIP, không xuất storedPath/fileHash/raw AI/system prompt; test HTTP |
| Lịch sử quyết định và nhãn HR | Export events/actor/note/timestamps và HrLabels; test HTTP |
| Các yêu cầu trước | PreviousRequests; lần xuất thứ hai thấy nhật ký lần đầu; test HTTP |
| UTF-8 tiếng Việt, thời gian UTC, tải file JSON | File attachment, application/json, Unicode encoder, UTC converter, nosniff, no-store; test HTTP |
| Ghi nhật ký trước khi cung cấp export | Serialize trước, SaveChanges + commit một lần trước trả file; lỗi 500/503 không tạo nhật ký và không retry; test HTTP interceptor |
| confirm=true, handledBy bắt buộc, note giới hạn | CandidateErasePlan + bounded JSON reader; null/JSON lỗi/lặp field/actor 100–101/note 500–501/control chars được kiểm tra |
| Rút mọi hồ sơ chưa Rejected | Chuyển Rejected/CandidateWithdrew, thêm event với EventNote chuẩn; không viết lại lý do của hồ sơ đã Rejected; unit test mọi stage, SQL test opt-in |
| Ẩn danh tất cả CV theo A39 | Tái sử dụng CvAnonymizer, hash/placeholder và durable recovery storage; thêm lối batch dùng transaction của A43 |
| Xóa contact và ghi audit trong một giao dịch | SQL Serializable + CandidateWriteScope: withdraw, CV/evaluation/skill clearing, candidate delete, audit cùng transaction; SQL opt-in |
| File rollback và recovery | Chuẩn bị journal cho từng file; lỗi file thứ hai phục hồi cả hai khi SQL rollback đã xác nhận; test file thật tổng hợp |
| Không tự restore sau commit không xác định | Giữ journal, trả lỗi an toàn, không retry; A39 recovery đọc tombstone để reconcile; SQL opt-in |
| Giữ điểm, mức, kỹ năng, nhãn và lịch sử | Chỉ clear PII fields, không xóa evaluations/applications/events/labels; SQL opt-in |
| Lần xóa thứ hai trả 410 | Có requestId/handledAt, không tạo audit xóa mới; HTTP test với nhật ký đã xử lý và SQL opt-in hai host |
| Nhật ký không có FK đến Candidates | Entity/config/migration không có FK; unit kiểm tra migration; SQL opt-in kiểm tra audit còn sau xóa |
| Lọc nhật ký theo candidateId/type | Validate số dương/overflow/lặp/type, thứ tự mới nhất, giới hạn 200 như mẫu; test HTTP |
| Chi tiết ứng viên thêm DataRequests | Tái sử dụng DTO; chỉ cung cấp audit khi được phép, giữ thông tin A24 còn lại; test HTTP |
| Access gate | Giữ Development + CvUpload:Endpoint:Enabled; HR/Admin khi cấu hình authentication; test 401/403 và route tắt |
| Không làm sang UI/nhiệm vụ khác | Không sửa Web/scoring/Gemini/OCR/worker; full regression kiểm tra tương thích |
| Schema đúng phạm vi | Chỉ bảng CandidateDataRequests + 1 index + CHECK, không sửa bảng cũ; migration/model unit test qua |

## 6. Endpoint và DTO

| Method | Endpoint | Contract / status |
|---|---|---|
| GET | /api/candidates/{id}/data-export?handledBy= | 200 file JSON; 400 input; 401/403 nếu có auth; 404 không tồn tại; 500/503 lỗi an toàn |
| POST | /api/candidates/{id}/erase | Body confirm, handledBy, note; 200 EraseCandidateResultDto; 400/401/403/404/409/410/415/500/503 khi liên quan |
| GET | /api/data-requests?candidateId=&type= | 200 array DataRequestDto; 400 filter sai; 401/403 nếu có auth |
| GET, bổ sung | /api/candidates/{id} | Thêm DataRequests; giữ các trường và nghiệp vụ A24 hiện có |

Không thêm 429 giả hoặc rate limiter ngoài guide. Các giới hạn/rate limiter upload và AI hiện có được giữ nguyên.

DTO mới giữ tên/shape ZIP: DataRequestTypes, EraseCandidateRequest, EraseCandidateResultDto, DataRequestDto, CandidateDataExportDto, ExportCvDto, ExportApplicationDto, ExportEvaluationDto, ExportSkillDto, ExportHrLabelDto. Chúng được tách sang CandidateDataRightsContracts.cs để không ghi đè CandidateContracts A24. CandidateDetailDto thêm DataRequests.

Export thiếu handledBy dùng HR như mẫu; giá trị quá dài hoặc chứa ký tự điều khiển trả 400 thay vì tự cắt. Erase bắt buộc handledBy, confirm=true; note tối đa 500. JSON được kiểm tra qua helper A24 hiện có, giới hạn body 16 KiB. Các thao tác async nhận CancellationToken.

Nhật ký không tự sao chép tên/email ứng viên, không xuất CvIds nội bộ qua DataRequestDto. Note là nội dung HR nhập theo hướng dẫn không ghi thông tin cá nhân; không đưa request/body, contact, SQL hoặc raw exception vào application log.

## 7. Database, transaction và triển khai schema

Migration mới: **20261118131430_CandidateDataRequests** (migration + Designer + snapshot).

Schema mới:

- RequestId BIGINT IDENTITY, PK.
- CandidateId INT, chỉ là tham chiếu số, **không có FK**.
- RequestType NVARCHAR(10), CHECK Export/Erase với collation phân biệt hoa thường.
- HandledBy NVARCHAR(100), Note/ResultSummary NVARCHAR(500).
- CvIds NVARCHAR(MAX), giữ đầy đủ danh sách GUID, không cắt cụt.
- CreatedAt DATETIME2, default SYSUTCDATETIME.
- Một index (CandidateId, CreatedAt).

Migration chỉ CreateTable và CreateIndex; Down chỉ drop bảng mới. Không tạo migration trùng và không thay đổi bảng cũ. Model test xác nhận không còn pending model changes.

Đã sinh script idempotent artifacts/a43/migration.sql để review. **Chưa áp dụng migration vào database nào. Cần người phụ trách áp dụng migration đã duyệt trước khi chạy binary mới trên database hiện có**, vì phần DataRequests dùng bảng mới. Không sửa dữ liệu cũ hoặc reset database để né bước này.

Luồng xóa lấy exclusive A39 privacy lease trước khóa ghi A24/transaction. Middleware nhận metadata của endpoint để tránh giữ shared lease rồi tự deadlock khi nâng khóa. Đọc/export giữ shared lease đến hết response, nên xóa không vượt qua response PII đang trả.

Mọi thay đổi SQL của yêu cầu xóa nằm trong một transaction. File system và SQL không phải distributed transaction: dùng journal A39 để phục hồi trước commit hoặc reconcile sau commit không xác định. Chỉ trả thành công sau commit và cleanup file đã hoàn tất. Lỗi cleanup/commit không xác định yêu cầu kiểm tra/recovery, không tự retry write. Không giả lập thành công xóa bằng EF InMemory; provider/storage không hỗ trợ trả 503 mà không thay dữ liệu.

Các CV Pending, lượt chấm Queued/Running hoặc file có tham chiếu ngoài ứng viên trả 409 trước khi đổi dữ liệu, giữ an toàn worker/storage của A39. Trạng thái Interview/Shortlisted không bị chặn vì A43 rút hồ sơ trong giao dịch trước khi xóa.

## 8. Khác biệt so với mẫu ZIP

1. Giữ source A24 mới nhất, thêm partial endpoints/DTO riêng thay vì ghi đè toàn file.
2. Mẫu gọi chữ ký CvAnonymizer cũ và lồng transaction; đã tích hợp batch trong transaction hiện có của A43, không thay hành vi API A39 đơn CV.
3. Bỏ execution-strategy retry write của mẫu. Có khóa privacy/ghi xuyên tiến trình và xử lý commit không xác định.
4. Mẫu không kiểm tra đầy đủ outcome mỗi CV; bản này không báo 200 khi file/DB/audit/cleanup thất bại.
5. Dùng durable journals A39 và batch rollback/cleanup; không xóa file ngoài cơ chế phục hồi.
6. CvIds dùng NVARCHAR(MAX) thay vì 2000 và bỏ JoinIds cắt chuỗi, tránh mất GUID khi ứng viên có nhiều CV. Đây chỉ là khác biệt cột của bảng mới.
7. CHECK dùng collation phân biệt case; default constraint được đặt tên theo convention hiện tại.
8. Giữ access gate, thêm kiểm tra HR/Admin theo cách A39 khi có auth. Audit trong chi tiết ứng viên không lộ cho role không đủ quyền.
9. Validate null/JSON lỗi/repeated fields/query overflow/actor dài/control chars; không tự cắt actor.
10. Export serialize trước khi ghi audit/commit, dùng UTC converter khi xuất, thứ tự có ID để ổn định khi trùng timestamp. Che payload PII legacy ở CV đã đánh dấu anonymized.
11. SQL kiểm tra sau xóa vẫn read-only, thêm guard yêu cầu dữ liệu thử rõ ràng, CHARINDEX thay LIKE cho email và CvIds MAX.
12. Giữ 5 test logic mẫu; thêm HTTP, file recovery, migration và SQL opt-in. Không xóa/skip/giảm assertion; chỉ cập nhật hai expectation migration cũ được người giao việc phê duyệt rõ ràng.

## 9. Build và test

.NET SDK 10.0.403; EF runtime 10.0.15. Output build/test cách ly tại artifacts/a43/build để không đụng DLL ứng dụng đang chạy.

| Bước | Kết quả |
|---|---|
| Baseline restore | Thành công, không warning |
| Baseline build | 0 errors, 0 warnings |
| Baseline tests trên SHA nền | **2642 passed, 0 failed, 114 skipped, total 2756** |
| Focused ban đầu | **45 passed, 0 failed, 7 skipped, total 52** |
| Focused cuối | **52 passed, 0 failed, 7 skipped, total 59** |
| Final build | **0 errors, 0 warnings** |
| Full regression trước phê duyệt | **2692 passed, 2 failed, 121 skipped, total 2815** |
| Build sau cập nhật đã được duyệt | **0 errors, 0 warnings** |
| Test migration/seed/model liên quan sau cập nhật | **22 passed, 0 failed, 0 skipped, total 22** |
| Full regression sau cập nhật đã được duyệt | **2694 passed, 0 failed, 121 skipped, total 2815** |

EF CLI 10.0.14 có advisory thấp hơn runtime 10.0.15 khi sinh migration/script; lệnh vẫn thành công. Không tự đổi tool/package hoặc tắt cảnh báo để làm xanh. Việc chạy lại focused cùng tên TRX có thông báo overwrite file kết quả, không phải lỗi/warning compile.

Các lệnh chính đã chạy:

~~~powershell
dotnet restore SmartCv.slnx -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build
dotnet build SmartCv.slnx --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build
dotnet test SmartCv.slnx --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --logger 'trx;LogFileName=baseline.trx' --results-directory artifacts/a43/results
dotnet ef migrations add CandidateDataRequests --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output-dir Data/Migrations --configuration A43
dotnet ef migrations script 20261117045748_JobHeadcountClosingDate 20261118131430_CandidateDataRequests --idempotent --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --configuration A43 --output artifacts/a43/migration.sql
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --filter 'FullyQualifiedName~A43|FullyQualifiedName~CandidateErasePlan' --logger 'trx;LogFileName=focused-final.trx' --results-directory artifacts/a43/results
dotnet test SmartCv.slnx --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --logger 'trx;LogFileName=regression.trx' --results-directory artifacts/a43/results
~~~

EF design-time chỉ dùng connection giả SmartCv_A43_Design_NotUsed, key giả và worker tắt trong process tạo script; không in hoặc dùng connection/key thật. Không chạy database update.

HTTP tests đã chạy: 200 export/journal, 400 validation/null/JSON/filter, 401/403 auth, 404, 410, 500/503 khi audit save lỗi, không retry, 503 fail-closed khi InMemory không hỗ trợ xóa thật. File tests dùng file thật tổng hợp trong temp để kiểm tra phục hồi hai file, cleanup lỗi và giữ bản recovery. Model/migration inspection không mở kết nối database.

Bằng chứng: artifacts/a43/baseline-restore.log, baseline-build.log, baseline-tests.log, final-build.log, focused-final-tests.log, regression-tests.log và các TRX trong artifacts/a43/results. Tất cả được ignore.

### Hai lỗi migration đã được xử lý theo phê duyệt

Full regression lần trước có 2 lỗi mới sau khi thêm schema A43, không phải lỗi baseline:

1. Jobs.JobCrudMigrationTests.A20_index_seed_named_defaults_and_current_snapshot_are_preserved còn kỳ vọng A21 là migration cuối.
2. JobSeedDataTests.Migrations_preserve_existing_order_then_add_JobHeadcountClosingDate_and_model_has_no_pending_changes còn kỳ vọng đúng 6 migration nền.

Bộ duyệt tự động từng từ chối cập nhật test cũ. Sau đó người giao việc đã cho phép rõ ràng sửa có giới hạn đúng hai file này; lần thực hiện sau phê duyệt đã được công cụ cho phép. Không dùng cách khác để vượt lần từ chối.

Đã áp dụng:

- JobCrudMigrationTests.cs: giữ nguyên hằng Previous/Current của A21, toàn bộ assertion về nullable columns, index, seed, named default và pending model. Kỳ vọng ba migration cuối chính xác là A20 → A21 → 20261118131430_CandidateDataRequests.
- JobSeedDataTests.cs: giữ nguyên cả 6 migration nền và thứ tự, thêm duy nhất 20261118131430_CandidateDataRequests ở cuối danh sách so sánh chính xác.
- Không xóa/skip test, không đổi tên test, không bỏ hoặc giảm assertion seed/index/default/model.
- Không thay đổi logic sản phẩm để né lỗi. Đối chiếu SHA-256 của 22 file A43 đã có (ngoài báo cáo) trước và sau lượt tiếp tục: tất cả giữ nguyên.
- Build sau sửa: 0 errors, 0 warnings. Chạy nhóm JobCrudMigrationTests, JobSeedDataTests và A43SafetyTests: 22 passed, 0 failed, 0 skipped.

Patch artifacts/a43/migration-test-expectations.patch được giữ làm tài liệu đề xuất lịch sử, được ignore. Hai thay đổi thực tế đã có trong working tree và manifest, không còn chờ phép áp dụng.

Lệnh xác minh bổ sung:

~~~powershell
dotnet build SmartCv.slnx --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --verbosity minimal
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --filter 'FullyQualifiedName~JobCrudMigrationTests|FullyQualifiedName~JobSeedDataTests|FullyQualifiedName~A43SafetyTests' --logger 'trx;LogFileName=migration-compatibility-approved.trx' --results-directory artifacts/a43/results
dotnet test SmartCv.slnx --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a43/build --logger 'trx;LogFileName=regression-approved.trx' --results-directory artifacts/a43/results
~~~

Bằng chứng sau phê duyệt: artifacts/a43/approved-followup-build.log, migration-compatibility-approved.log, regression-approved.log; TRX tương ứng nằm trong artifacts/a43/results. Giữ nguyên kết quả lần regression 2 failed để đối chiếu, không ghi đè bằng kết quả mới.

## 10. Chưa kiểm chứng và giới hạn

**7 SQL test A43 chưa chạy** vì không có SMARTCV_A43_TEST_SQL_CONNECTION được cung cấp. Không tự dùng SQL Server thật hoặc bật các test có quyền ghi để thay thế. Vì vậy chưa tuyên bố đã kiểm chứng runtime SQL cho luồng xóa thành công, rollback SQL, lock xuyên process, concurrency/cancellation hoặc commit outcome.

Các test opt-in đã viết cho: xuất/xóa end-to-end và giữ điểm/nhãn/history; lỗi file thứ hai; lỗi SaveChanges của audit; hai host xóa đồng thời (200/410); shared reader chặn xóa/timeout không ghi; cancellation sau withdrawal; commit error giữ journal và A39 recovery reconcile. Fixture chỉ tạo catalog GUID mới với prefix SmartCv_A43_Test_, kiểm tra chưa tồn tại, migrate catalog mới và giữ lại để review. Không tạo/xóa database test trong lượt chạy này.

114 case có điều kiện skip sẵn trên baseline được giữ nguyên; không sửa skip để ép xanh. InMemory không phải bằng chứng transaction/locking SQL. Không kiểm browser/Playwright/responsive vì A43 không có UI.

Ghi chú HR là free text theo contract; không tự chép contact vào audit. Bảo đảm không có dữ liệu cá nhân do HR tự nhập trong note cần quy trình nhập dữ liệu theo guide. Recovery sau lỗi cần được xác minh/điều phối theo A39; không hứa worker tự chạy recovery khi AutoAnonymize đang tắt.

## 11. Lỗi đã phát hiện/xử lý và bảo toàn phạm vi

Các lỗi của mẫu được xử lý: API anonymizer không khớp, nested transaction, retry write không an toàn, cắt cụt CvIds, thiếu gate/validation, khả năng báo xóa thành công dù anonymizer thất bại, thiếu phối hợp rollback file/SQL và nguy cơ khóa shared/exclusive tự chặn.

Baseline lượt A43 không có test thất bại. Hai lỗi expectation migration của regression đầu đã được xử lý bằng hai thay đổi test được duyệt; không sửa logic sản phẩm. Kết quả kiểm thử sau cập nhật được ghi ở mục 9. Không sửa lỗi ngoài phạm vi A43.

Không triển khai A24/A25/B23/B24/B25. Các thay đổi CandidateDetailDto, CandidateEndpoints, middleware, DbContext và lối batch CvAnonymizer là tích hợp A43 được guide yêu cầu. Không sửa Web, scoring, Gemini, OCR, worker, thuật toán điểm hoặc dữ liệu lịch sử. Không chạy xóa/ẩn danh trên dữ liệu thật.

## 12. Manifest và Git cuối

Manifest cuối: **25 file — 16 A, 9 M, 0 D**. A là file mới chưa track (??); M là file sửa unstaged. Hai file test cũ được phê duyệt đã có trong manifest.

| Trạng thái | Đường dẫn relative |
|---|---|
| A | database/a43_kiem_tra_xoa.sql |
| A | docs/A43-implementation-report.md |
| A | src/SmartCv.Api/Endpoints/CandidateDataRightsEndpoints.cs |
| A | src/SmartCv.Api/Endpoints/CandidateRightsAccessFilter.cs |
| A | src/SmartCv.Core/Candidates/CandidateErasePlan.cs |
| A | src/SmartCv.Core/Contracts/CandidateDataRightsContracts.cs |
| A | src/SmartCv.Core/Entities/CandidateDataRequest.cs |
| A | src/SmartCv.Infrastructure/Candidates/CandidateErasureService.cs |
| A | src/SmartCv.Infrastructure/Data/Configurations/CandidateDataRequestConfiguration.cs |
| A | src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.cs |
| A | src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.Designer.cs |
| A | src/SmartCv.Infrastructure/Privacy/CvFileErasureBatch.cs |
| A | tests/SmartCv.Tests/Candidates/A43EndpointTests.cs |
| A | tests/SmartCv.Tests/Candidates/A43SafetyTests.cs |
| A | tests/SmartCv.Tests/Candidates/A43SqlServerTests.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateErasePlanTests.cs |
| M | src/SmartCv.Api/Endpoints/CandidateEndpoints.cs |
| M | src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs |
| M | src/SmartCv.Api/Program.cs |
| M | src/SmartCv.Core/Contracts/CandidateContracts.cs |
| M | src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs |
| M | src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs |
| M | src/SmartCv.Infrastructure/Privacy/CvAnonymizer.cs |
| M | tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs |
| M | tests/SmartCv.Tests/JobSeedDataTests.cs |

artifacts/a43-base-sha.txt chứa SHA nền 5596e325c3120ccc78ed63b76d61333f42988910. artifacts/a43-commit-files.txt có đúng 25 đường dẫn relative dùng /, mỗi dòng một file, không trùng. So sánh hai chiều với git status --porcelain=v1 --untracked-files=all: không thiếu/thừa.

Hai metadata được .gitignore dòng 66 (artifacts/) bảo vệ, không được Git track. Không đưa artifacts/logs/TRX/bin/obj/SQL script xuất để review hoặc patch lịch sử vào manifest.

Toàn bộ 2756 case baseline vẫn có mặt; thêm đúng 59 case A43. Regression cuối: 2694 passed, 0 failed, 121 skipped. Không đổi tên, xóa hoặc thêm skip vào test cũ. Bảy SQL case A43 tiếp tục opt-in như trước.

Đối chiếu SHA-256 với thời điểm trước lượt tiếp tục: 22 file A43 đã có ngoài báo cáo giữ nguyên. Chỉ hai file test đã được duyệt, báo cáo và metadata A43 được cập nhật trong lượt này; không sửa logic sản phẩm để né lỗi test.

Kết quả git status --short --branch:

~~~text
## feature/a43-implementation
 M src/SmartCv.Api/Endpoints/CandidateEndpoints.cs
 M src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Core/Contracts/CandidateContracts.cs
 M src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs
 M src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs
 M src/SmartCv.Infrastructure/Privacy/CvAnonymizer.cs
 M tests/SmartCv.Tests/JobSeedDataTests.cs
 M tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs
?? database/a43_kiem_tra_xoa.sql
?? docs/A43-implementation-report.md
?? src/SmartCv.Api/Endpoints/CandidateDataRightsEndpoints.cs
?? src/SmartCv.Api/Endpoints/CandidateRightsAccessFilter.cs
?? src/SmartCv.Core/Candidates/CandidateErasePlan.cs
?? src/SmartCv.Core/Contracts/CandidateDataRightsContracts.cs
?? src/SmartCv.Core/Entities/CandidateDataRequest.cs
?? src/SmartCv.Infrastructure/Candidates/CandidateErasureService.cs
?? src/SmartCv.Infrastructure/Data/Configurations/CandidateDataRequestConfiguration.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.Designer.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.cs
?? src/SmartCv.Infrastructure/Privacy/CvFileErasureBatch.cs
?? tests/SmartCv.Tests/Candidates/A43EndpointTests.cs
?? tests/SmartCv.Tests/Candidates/A43SafetyTests.cs
?? tests/SmartCv.Tests/Candidates/A43SqlServerTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateErasePlanTests.cs
~~~

Kết quả mở rộng từng file dùng đối chiếu manifest:

~~~text
## feature/a43-implementation
 M src/SmartCv.Api/Endpoints/CandidateEndpoints.cs
 M src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Core/Contracts/CandidateContracts.cs
 M src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs
 M src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs
 M src/SmartCv.Infrastructure/Privacy/CvAnonymizer.cs
 M tests/SmartCv.Tests/JobSeedDataTests.cs
 M tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs
?? database/a43_kiem_tra_xoa.sql
?? docs/A43-implementation-report.md
?? src/SmartCv.Api/Endpoints/CandidateDataRightsEndpoints.cs
?? src/SmartCv.Api/Endpoints/CandidateRightsAccessFilter.cs
?? src/SmartCv.Core/Candidates/CandidateErasePlan.cs
?? src/SmartCv.Core/Contracts/CandidateDataRightsContracts.cs
?? src/SmartCv.Core/Entities/CandidateDataRequest.cs
?? src/SmartCv.Infrastructure/Candidates/CandidateErasureService.cs
?? src/SmartCv.Infrastructure/Data/Configurations/CandidateDataRequestConfiguration.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.Designer.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261118131430_CandidateDataRequests.cs
?? src/SmartCv.Infrastructure/Privacy/CvFileErasureBatch.cs
?? tests/SmartCv.Tests/Candidates/A43EndpointTests.cs
?? tests/SmartCv.Tests/Candidates/A43SafetyTests.cs
?? tests/SmartCv.Tests/Candidates/A43SqlServerTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateErasePlanTests.cs
~~~

git diff --check: exit 0, output trống. Kiểm tra bổ sung tất cả file untracked bằng git diff --no-index --check so với /dev/null: không có chẩn đoán whitespace (exit 1 do file có nội dung mới không được nhầm là lỗi whitespace).

git diff --cached --name-status: exit 0, output trống. git ls-files -u: trống. Không có staged/unmerged.

Nhánh vẫn feature/a43-implementation; HEAD vẫn là SHA nền. Không git add/commit/push/merge/rebase/reset/stash/restore/clean hoặc tạo PR/worktree/clone.

Hoàn tất phần triển khai và kiểm thử khả dụng với 0 test thất bại. Giới hạn SQL opt-in/migration chưa áp dụng được giữ rõ ở mục 10. Bàn giao để người giao việc review và tự commit/push.
