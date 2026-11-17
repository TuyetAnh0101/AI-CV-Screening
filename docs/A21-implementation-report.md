# A21 — Báo cáo API quản lý Job trước khi commit

Ngày: 17/11/2026, Việt Nam. **READY FOR USER REVIEW** trong phạm vi kiểm thử dưới đây. Không triển khai migration lên SmartCvDb. Chưa stage, commit, push, merge hoặc tạo PR.

## 1. Git, phụ thuộc A20 và nguồn yêu cầu

- Repository/working tree: `C:\TTCN\project\AI-CV-Screening`; remote origin `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Ban đầu working tree sạch trên `feature/a20-job-title-index`, HEAD `2e67fa85627daaafd10b68d2895660eb884dc9d7`; không staged/untracked/conflict, không merge/rebase/cherry-pick dở dang. Không cần worktree khác.
- Đã fetch/prune, xác minh A20 merge vào origin/main qua PR #66: **`14dad000c4fcc258163f5da8135756df3a29cca8`**, `feat(A20): replace Job title alternate key with unique index (#66)`.
- Đã đọc trực tiếp source origin/main: JobConfiguration dùng `HasIndex(x => x.Title).IsUnique().HasDatabaseName("UQ_Jobs_Title")`; snapshot cùng mapping; migration `20261117042418_JobTitleUniqueIndex` có đúng Up/Down đổi constraint/index. Không chỉ dựa vào tên PR/báo cáo A20.
- Đã tạo `feature/a21-job-crud-api` từ origin/main bằng `git switch --no-track -c`; không upstream trỏ main. HEAD vẫn bằng SHA nền 40 ký tự trên, lưu tại `artifacts/a21-base-sha.txt`. Không merge/cherry-pick A20, không giấu/ghi đè thay đổi đồng đội hoặc sửa global Git config.

Đã đọc đầy đủ prompt `C:\TTCN\A21_Codex_Prompt_20261117.md`, SHA256 `646440631573940B157D248B5F3CF7532BF8537B5CF6772147F35EA449E9657E`.

Đã mở trực tiếp ZIP `C:\TTCN\A19_B26_SmartCv.zip`, SHA256 `3E0C98598DA639AC0B4B27D6CC400C794E5A772B8154437741954A198FAC1327`: đọc toàn bộ guide tuần 3 và đủ 8 mẫu A21 (Job, JobDto, JobRequests, JobRules, VietnamTime, JobConfiguration, JobEndpoints, JobRulesTests). Đã kiểm kê: **không có PNG/JPG/SVG/WebP/mockup**. Không chép đè mẫu lên source thật; không bịa ảnh thiết kế.

Đã đối chiếu A8 inactive assignment, A35 stages/events, B38 Job contract, A19 VietnamTime, A20 migrations/tests, seed/named defaults, access gates và scoring hiện tại.

### Cập nhật test A20 đã được duyệt

Prompt mục 4 yêu cầu xin duyệt khi sửa file A20. Đã chuẩn bị `artifacts/a21-a20-test-compatibility-proposal.md`, gửi câu hỏi và nhận trả lời **“Cho phép cập nhật test theo đề xuất”** trước khi áp dụng:

- A20 metadata test chấp nhận thêm check Headcount và kiểm A20 ở vị trí thứ năm, giữ nguyên assertions unique index/Up/Down/seed/FK/no-pending.
- A20 SQL rename tests chỉ project/attach JobId và Title, để tiếp tục kiểm schema tại migration 4/5 vốn chưa có cột A21. Không tắt tests hoặc sửa migration target/guard/rollback.
- Seed migration-order test thêm A21 ở cuối, giữ nguyên năm migration trước.

Không sửa logic hoặc migration A20, không sửa VietnamTime của A19.

## 2. API, DTO và validation đã hoàn thành

| Endpoint | Hành vi |
| --- | --- |
| GET `/api/health` | Giữ nguyên health không cần database |
| GET `/api/jobs` | Mặc định activeOnly=true; Skills/Aliases và các field cũ giữ nguyên |
| GET `/api/jobs?activeOnly=false` | Gồm inactive, active xếp trước, hòa theo JobId tăng dần |
| GET `/api/jobs/{id}` | Gồm cả Job inactive, 404 kiểu cũ nếu không có |
| POST `/api/jobs` | 201 + Location `/api/jobs/{id}` + JobDto; Job mới active; 400/409 |
| PUT `/api/jobs/{id}` | 200 + JobDto; sửa title/description/GPA/experience/headcount/closingDate; 400/404/409 |
| POST `/api/jobs/{id}/deactivate` | 200 + JobDto, gọi lặp idempotent; 404 khi không có |
| POST `/api/jobs/{id}/activate` | 200 + JobDto, gọi lặp idempotent; 404 khi không có |

**Không có DELETE Job**, không thêm skills/aliases payload hoặc routes A22. Request có trường không thuộc A21 như `skills`, `isActive` bị 400 để tránh âm thầm bỏ qua thao tác frontend kỳ vọng.

`JobUpsertRequest` gồm title, description, minGpa, minYearsExperience, headcount, closingDate. PUT cập nhật đầy đủ các field này, không phải PATCH; B21 cần gửi toàn bộ thông tin form. Không thay IsActive bằng PUT, không sửa/xóa Skills, Applications hoặc đánh giá liên quan.

`JobRules` thuần, nhận ngày Việt Nam tường minh để unit test deterministic:

- Title bắt buộc, chuẩn hóa mọi whitespace (space/tab/newline/NBSP), tối đa 150 UTF-16 ký tự sau chuẩn hóa; giữ nguyên case/dấu, không ToLower/đổi collation.
- Description tối đa 20.000 ký tự; lưu trim, trắng thành null.
- MinGpa null hoặc 0–4; experience 0–50; headcount null hoặc 1–1.000.
- ClosingDate null hoặc khi tạo không trước hôm nay ở Việt Nam; hôm nay hợp lệ. Khi sửa cho giữ/chọn ngày đã qua. IsExpired chỉ true khi closingDate < today, không đổi IsActive tự động.
- Tái sử dụng **VietnamTime hiện hữu**, cùng TimeProvider đã có trong DI. Không chép/sửa helper A19. Biên ngày là 17:00 UTC, không theo timezone OS.
- Lưu nhất quán precision SQL hiện hữu: GPA làm tròn 2 chữ số, experience 1 chữ số, AwayFromZero. Không thay decimal mapping; ví dụ 3,456→3,46 và 1,24→1,2. Frontend nên dùng step tương ứng.

400 trả ProblemDetails tiếng Việt `title="Dữ liệu chưa hợp lệ"`, detail và `errors` theo field camelCase. Có kiểm null/empty/malformed body, sai kiểu, số vượt miền/nonfinite, DateOnly sai ngày/format, trường lạ. Unsupported Content-Type trả 415; body >256 KiB trả 413. Không echo body hoặc thông báo JSON/SQL gốc. CancellationToken truyền tới JSON/EF.

Tên trùng kiểm trước bằng SQL theo collation thật, sau đó unique index A20 là chốt race. Catch đúng lỗi 2601/2627 của `UQ_Jobs_Title`, trả 409 tiếng Việt với `errors.title`; không trả stack trace SQL. Log Job chỉ ghi ID, không ghi title/description/candidate PII/keys.

### JobDto và statistics

Giữ đủ field cũ: jobId/title/description/minGpa/minYearsExperience/isActive/skills. Thêm headcount, closingDate, isExpired, createdAt UTC có Z, mustHaveCount; phía HR thêm hasEvaluations, applicationCount, stageCounts.

- MustHaveCount đếm đúng skill MustHave; Skills/Aliases giữ thứ tự cũ.
- HasEvaluations true khi có evaluation record của Application thuộc Job, không gọi AI.
- ApplicationCount là số hồ sơ; StageCounts luôn đủ 6 khóa New/Reviewing/Shortlisted/Interview/Rejected/OnHold trong response HR.
- Stage counts GROUP BY một lần cho cả tập Jobs; Skills/Aliases lấy split query; không query theo từng Job. Load một Job cũng hoàn thiện statistics và IsExpired, tránh lỗi mẫu ZIP thay list item nhưng trả object cũ.

### Bảo mật và GET tương thích

Writes chỉ map khi **Development + `CvUpload:Endpoint:Enabled=true`**, cùng boundary HR/CV hiện tại. Không tạo auth/role/cookie giả. Ngoài Development, guard CV hiện hữu từ chối bật quyền HR; gate tắt thì write routes không tồn tại. POST/PUT tại path đã có GET trả 405; activate/deactivate không có route trả 404. Đây là fail-closed, không phải 401/403 do M6 chưa có.

GET/health cũ vẫn truy cập được khi gate tắt. Để không mở public thống kê tuyển dụng nội bộ, **hasEvaluations/applicationCount/stageCounts là nullable và bỏ khỏi JSON ngoài gate HR**, không trả số 0/false giả. HR Development enabled nhận đủ số liệu và 6 stage keys. Các thông tin Job/kỹ năng đã public, cùng headcount/closingDate/isExpired/createdAt/mustHaveCount, vẫn có trên GET. B20/B21 cần xử lý statistics vắng mặt như “cần quyền truy cập nội bộ”, không diễn giải thành không có hồ sơ.

Mọi response Jobs đã map có `Cache-Control: no-store`, gồm 400/404/409. Không thay middleware/privacy/rate limit của module khác. M6 thực tế vẫn cần trước khi mở writes/statistics ngoài Development.

## 3. Migration và SQL

Migration mới: **`20261117045748_JobHeadcountClosingDate`**, liền sau A20 **`20261117042418_JobTitleUniqueIndex`** đã xác minh bằng source/EF list.

| Chiều | Operations |
| --- | --- |
| Up | Add ClosingDate `date NULL`; Add Headcount `int NULL`; Add `CK_Jobs_Headcount` (`Headcount IS NULL OR Headcount BETWEEN 1 AND 1000`) |
| Down | Drop check; Drop đúng hai cột mới |

Không đổi A20 index `UQ_Jobs_Title`, PK/FK, seed, named defaults hoặc cột khác. Snapshot chỉ thêm hai properties và một check. Designer EF chuẩn. EF scaffolder ban đầu thêm 5 UpdateData đặt NULL cho hai cột mới của seed; đã bỏ khỏi **migration mới** vì mọi row vốn nhận NULL khi add nullable column. Unit test yêu cầu Up/Down đúng 3 operations, không có seed DML. `has-pending-model-changes` xác nhận không drift.

`database/a21_job_headcount_closing_date.sql` xuất đúng delta A20→A21, không xuất lại lịch sử. Bọc XACT_ABORT + TRY/CATCH rollback/THROW để tránh ghi history sai khi DDL lỗi. Riêng ADD CHECK dùng `EXEC(N'ALTER TABLE ... ADD CONSTRAINT ...')` để bind sau khi ADD COLUMN hoàn tất trong cùng batch. Body này được so với EF GenerateScript cộng wrapper/deferred binding trong integration test.

Một lần SQL integration trung gian phát hiện lỗi mới **207 Invalid column Headcount** do check bind sớm trong batch script có TRY/CATCH; đường EF migration đã pass. Đã sửa deferred binding ở script A21 và chạy lại cả focused/full. Không chỉnh A20 hoặc database thật để chữa lỗi này. Evidence lỗi giữ tại `artifacts/a21-focused-sql-binding.log`.

### SQL Server cô lập, Up/Down và dữ liệu

Tests dùng SQL Server `.\BB`, catalog mới `SmartCv_A21_Test_<32 hex GUID>`. Guard kiểm prefix/no attached files, DB_ID chưa tồn tại và DB_NAME trước thao tác. Chỉ catalog mới được đặt MULTI_USER (tránh kế thừa SINGLE_USER từ model của máy); không thay model hoặc catalog cũ.

Tất cả fixture áp dụng A20 trước và xác nhận `UQ_Jobs_Title` có is_unique=1/is_unique_constraint=0. Up và Down dùng **catalog độc lập**, không rollback schema ứng dụng thật. Snapshot đầy đủ dữ liệu tổng hợp + fingerprint trước/sau; không backup/đọc/lưu CV thật.

| Bảng | Trước Up | Sau Up / Down trên copy |
| --- | ---: | ---: |
| Jobs | 5 | 5 |
| Candidates | 0 | 0 |
| JobSkills / JobSkillAliases | 29 / 52 | 29 / 52 |
| Cvs / Applications / ApplicationEvents | 2 / 2 / 2 | 2 / 2 / 2 |
| PromptVersions / EvaluationSets | 1 / 1 | 1 / 1 |
| HrLabels / EvaluationRuns | 2 / 1 | 2 / 1 |
| Evaluations / EvaluationSkills | 2 / 2 | 2 / 2 |

So sánh toàn bộ giá trị cột cũ và FK/default/check/trigger, không chỉ row count. Hai cột A21 và check mới được kiểm riêng. Sau Up, 5 seed Jobs có Headcount/ClosingDate null; direct SQL headcount 0/-1/1001 bị CHECK chặn bằng 547. Date/int đúng SQL type, nullable; A20 unique index vẫn nguyên. History thêm đúng một A21 entry. Down bỏ hai cột mới và entry A21, giữ nguyên năm migration nền; giá trị hai cột mới bị loại theo định nghĩa rollback, dữ liệu cũ không đổi.

Catalog Up của full cuối: `SmartCv_A21_Test_40fa71df03214996aa1e5dcd4130d0c3`. Legacy data SHA256 trước/sau `354F2D194D0129DFE3300395D0760E0748A015BD9BF97871EB363131875F4709`; schema FK/default/check/trigger cũ `B27F503EBB2FEF7C661B505F0DABFA175ED74E76EE6E62B5EEF264DC6685CCAC`.

### HTTP thật, race, SQL đối chiếu và performance

- Kestrel thử trên loopback cổng động, gỡ toàn bộ BackgroundService, dùng SQL catalog riêng; không đụng server demo/port đang chạy. Tạo 201, Location đúng, sửa/đổi tên 200, duplicate 409 theo collation, repeated toggle 200, thiếu Job 404, DELETE 405.
- Ngừng tuyển ẩn Job khỏi GET mặc định, GET all/detail vẫn có. A8 assign CV khi inactive trả 400; activate rồi assign trả 200 và tạo đúng một hồ sơ. Quá hạn chỉ là cờ hiển thị: Job expired nhưng active vẫn dùng theo hợp đồng A8 hiện hữu.
- Sửa tiêu chí Job đã có evaluation giữ Skills/Aliases, CV, Applications, events và **hai điểm FinalScore=77** đã lưu. Không gọi Gemini/chấm lại. Job mới chưa có skill A22 không được tuyên bố đã kiểm thử chấm AI.
- Barrier giữ hai request cùng qua precheck trước SaveChanges. Race create cho `[201,409]`; race rename hai Job cho `[200,409]`; mỗi race có đúng một lỗi SQL unique thật. Không dựa vào kiểm tra trước để giả lập 409.
- Statistics trên **55 Jobs / 300 Applications** khớp SQL, đủ 6 stage counts; A39 anonymized CV không lộ dữ liệu. Chỉ **4 SELECT cho request** (không N+1), focused khoảng **398 ms**, full cuối **1.141 ms** trên máy này, không phải SLA Production.
- Public GET trên Production không có ba trường statistics nội bộ. Response không có candidateName/extractedText hoặc marker CV tổng hợp. Cancellation trước gửi và trong khi EF đọc đều được kiểm; interceptor xác nhận token bị hủy.
- Evidence: `artifacts/a21-sql/*-up-result.json`, `*-race-result.json`, `*-http-result.json`, `*-stats-result.json`, snapshots và rollback SQL. Catalogs/snapshots được giữ nguyên để chủ dự án quyết định dọn sau.

## 4. Build, tests và regression

Môi trường .NET SDK **10.0.403**, runtime **10.0.14**, EF Core **10.0.15**; SQL Server **16.0.1000.6 Developer Edition (64-bit)** (`artifacts/a21-sql-server-version.txt`, SELECT metadata từ master). Global dotnet-ef **10.0.14** cảnh báo patch thấp hơn runtime nhưng generation/model checks đều pass. Không cập nhật global tool hoặc package/config để che warning. Build thường không có warning.

| Kiểm tra | Passed | Failed | Skipped | Tổng / bằng chứng |
| --- | ---: | ---: | ---: | --- |
| Restore | — | 0 | — | Thành công |
| Baseline build | — | 0 | — | 0 warnings, 0 errors |
| Baseline full | 1.968 | 0 | 78 | 2.046; 1 phút 17 giây, trước sửa source |
| Focused HTTP/unit ban đầu | 66 | 0 | 0 | Validation/CRUD/gate/metadata và regression liên quan |
| Focused cuối | **75** | **0** | **0** | 52 test A21 mới + 23 regression Job/A20; 12 giây |
| Final build | — | 0 | — | **0 warnings, 0 errors** |
| Final full | **2.020** | **0** | **78** | **2.098**, 1 phút 17 giây |

52 test A21 gồm 24 JobRules, 21 HTTP, 2 metadata/migration và **5 SQL integration**. Baseline/final đều bật SQL A19/A20; final bật thêm A21. Toàn bộ **44 A19**, **12 A20** và 52 A21 pass. 78 skipped có sẵn (73 SQL opt-in khác, 5 demo cần tài liệu ngoài repository); không bật fixture cũ có dọn database, không xem skipped là passed.

Lệnh chính từ repository (build/test output riêng, không kill API/Web):

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a21-build -v minimal
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/a21-build -v minimal
dotnet build src/SmartCv.Api/SmartCv.Api.csproj --configuration A21 -v minimal
dotnet ef migrations list --no-connect --no-build --configuration A21 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api
dotnet ef migrations add JobHeadcountClosingDate --no-build --configuration A21 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output-dir Data/Migrations
dotnet ef migrations script 20261117042418_JobTitleUniqueIndex 20261117045748_JobHeadcountClosingDate --no-build --configuration A21 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output database/a21_job_headcount_closing_date.sql
dotnet ef migrations has-pending-model-changes --no-build --configuration A21 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api
$env:SMARTCV_A19_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A19_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A20_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A20_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A21_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A21_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-restore --artifacts-path artifacts/a21-build --filter 'FullyQualifiedName~JobCrud|FullyQualifiedName~JobRulesTests|FullyQualifiedName~JobTitleIndex|FullyQualifiedName~JobSeedDataTests|FullyQualifiedName~JobEndpointsTests' --logger 'trx;LogFileName=a21-focused.trx' --results-directory artifacts/a21-focused-results
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a21-build --logger 'trx;LogFileName=a21-full.trx' --results-directory artifacts/a21-full-results
```

EF CLI dùng configuration A21 và connection design-time `SmartCv_A21_DesignTime_NotUsed`, Production, feature gate/scoring/retention tắt, key giả. Không kết nối SmartCvDb khi scaffold/script. SQL xuất ra được bổ sung guard/deferred binding như mục 3; không chạy lệnh migrations add lần hai khi tái kiểm.

Logs/TRX: `artifacts/a21-restore.log`, `a21-baseline-build.log`, `a21-baseline-tests.log`, `a21-final-build.log`, `a21-focused-tests.log`, `a21-full-tests.log`, thư mục `a21-baseline-results/`, `a21-focused-results/`, `a21-full-results/`. Tool evidence: `a21-migrations-before.txt`, `a21-migration-add.log`, `a21-pending-model.log`, `a21-migration-script.log`. Phân loại lỗi: không có lỗi nền ở các test đã chạy; lỗi script 207 là lỗi mới đã sửa và regression xác nhận.

## 5. Manifest và toàn bộ file M/A/D

**9 M / 11 file mới chưa stage / 0 D = 20 file.**

| Trạng thái | File | Nội dung |
| --- | --- | --- |
| M | `src/SmartCv.Api/Endpoints/JobEndpoints.cs` | CRUD/toggle, parsing/validation, SQL conflict, gated statistics và GET tương thích |
| M | `src/SmartCv.Core/Contracts/JobDto.cs` | Metadata Job mới và statistics nullable theo gate |
| M | `src/SmartCv.Core/Entities/Job.cs` | Headcount/ClosingDate nullable |
| M | `src/SmartCv.Infrastructure/Data/Configurations/JobConfiguration.cs` | Hai mapping và check mới, bảo toàn A20/seed/defaults |
| M | `src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs` | Model mới đúng phạm vi A21 |
| M | `tests/SmartCv.Tests/JobEndpointsTests.cs` | Assertion route GET phân biệt HTTP verb sau khi thêm POST/PUT cùng path |
| M | `tests/SmartCv.Tests/JobSeedDataTests.cs` | Danh sách migration thêm A21, giữ no-pending |
| M | `tests/SmartCv.Tests/Jobs/JobTitleIndexTests.cs` | Compatibility đã được duyệt cho check/migration sequence mới |
| M | `tests/SmartCv.Tests/Jobs/JobTitleIndexSqlServerTests.cs` | Compatibility đã được duyệt: chỉ đọc/attach JobId/Title khi test schema A20 |
| A | `src/SmartCv.Core/Contracts/JobRequests.cs` | Upsert request chỉ A21, không A22 |
| A | `src/SmartCv.Core/Jobs/JobRules.cs` | Validation thuần tiếng Việt/normalization/expiry |
| A | `src/SmartCv.Infrastructure/Data/Migrations/20261117045748_JobHeadcountClosingDate.cs` | Up/Down đúng hai cột và check |
| A | `src/SmartCv.Infrastructure/Data/Migrations/20261117045748_JobHeadcountClosingDate.Designer.cs` | Target model EF |
| A | `database/a21_job_headcount_closing_date.sql` | Delta A20→A21 an toàn transaction/binding |
| A | `tests/SmartCv.Tests/Jobs/JobRulesTests.cs` | 24 cases validation/ngày/biên |
| A | `tests/SmartCv.Tests/Jobs/JobCrudEndpointTests.cs` | 21 HTTP cases, gate/DTO/errors và clock fixture |
| A | `tests/SmartCv.Tests/Jobs/JobCrudMigrationTests.cs` | 2 metadata/migration invariants |
| A | `tests/SmartCv.Tests/Jobs/JobCrudSqlServerTests.cs` | 5 SQL tests: migration, rollback copy, lifecycle, race, statistics |
| A | `tests/SmartCv.Tests/Jobs/JobCrudSqlFixture.cs` | Catalog GUID guard, EF/HTTP server và snapshots tổng hợp |
| A | `docs/A21-implementation-report.md` | Báo cáo/handoff này |

`artifacts/a21-commit-files.txt` UTF-8 không BOM, mỗi dòng relative path đúng status, gồm đủ 20 file trên và báo cáo, không chứa artifacts/ZIP/log/snapshot/secret. `artifacts/a21-base-sha.txt` chứa SHA nền đầy đủ. Rule `artifacts/` sẵn có bảo vệ toàn bộ evidence; không sửa .gitignore.

## 6. Handoff B20/B21 và giới hạn cần biết

Đã đọc token hiện tại trong app.css: Inter; Deep Teal `--sc-brand-900` #0a2e2d, `--sc-brand-700` #0f766e; `--sc-grad-ai` #14b8a6→#06b6d4→#6366f1 dành cho nội dung AI. Không sửa Web/Razor/CSS/JS/Figma.

- B20: filter Đang tuyển/Ngừng tuyển/Tất cả, active trước; status dùng chữ và icon bên màu. IsExpired độc lập IsActive: hiển thị quá hạn kèm ngày, không suy rằng đã ngừng tuyển. Dòng gồm headcount, closingDate, tổng hồ sơ và sáu stage counts. Null headcount/closingDate là chưa khai báo/không có hạn.
- B21: form title/description/GPA/experience/headcount/closingDate, spacing 8px, lỗi gắn đúng `errors[field]` bằng aria-describedby; summary lỗi có focus, giữ dữ liệu khi 400/409. Date input gửi yyyy-MM-dd không đổi sang UTC. Render lại title đã chuẩn hóa và numeric precision từ response. Không gửi skills/isActive trong A21 request.
- Dialog ngừng tuyển nói rõ: ẩn khỏi Upload, từ chối gán hồ sơ mới, lịch sử giữ nguyên. Mở lại Job quá hạn nên nhắc sửa hạn. Không có nút xóa Job; chống submit lặp trong lúc đang lưu, thông báo thành công qua aria-live.
- Desktop 1440/1024 có bảng và form chia cột hợp lý; tablet 768 tự xuống hàng; mobile 390/360 xếp dọc, action dễ chạm, không ép toàn trang tràn ngang. Loading skeleton ổn định, empty có tạo Job, error có retry. Keyboard/focus rõ, contrast theo WCAG, không chỉ dựa vào màu. Glow dùng tiết chế đúng ý nghĩa AI, không trang trí form CRUD như kết quả Gemini.
- Ngoài HR gate, statistics vắng mặt: hiển thị yêu cầu quyền truy cập, không dùng 0/false thay thế. Trong HR response đủ 6 khóa. B38 cũ chỉ dùng field hiện hữu nên không bị yêu cầu thay code.
- **Kết quả scoring cũ:** source hiện tại không có criteria snapshot/version hoặc cờ stale theo GPA/experience/description của Job; Run snapshot lưu cấu hình chấm, không đóng băng toàn bộ Job profile. A21 không tự chấm lại hay đánh dấu giả kết quả là mới. Khi `hasEvaluations=true`, B21 phải cảnh báo rõ trước khi sửa tiêu chí rằng kết quả cũ dùng tiêu chí trước đó; muốn versioning/stale/chấm lại cần nhiệm vụ riêng. Test chứng minh điểm cũ giữ nguyên, không chứng minh chúng còn phản ánh tiêu chí mới.
- PUT Job hiện dùng last-write-wins; không bổ sung concurrency version cho Job hoặc khóa sửa tiêu chí trong lúc run đang xử lý. Race unique title đã được xử lý riêng; không thay worker/scoring ngoài phạm vi.
- Migration phải được chủ dự án review/áp dụng theo quy trình riêng trước khi chạy code A21 với database thật. Task này không sửa/kiểm readiness schema thật, không auto migrate hoặc làm giả migration history. GET mới sẽ cần hai cột A21; health cũ không thay thành bộ kiểm tra schema A21.
- Down loại hai cột mới và dữ liệu của chúng; rollback schema không thay backup. Test giữ dữ liệu cũ trên copy độc lập; không rollback A20/A35/A38 trên database ứng dụng.
- Không gọi Gemini thật, không bật worker ứng dụng, không ghi/xóa/ẩn danh CV thật, không drop hoặc dọn catalog có sẵn. Test fake/loopback worker lifecycle của bộ regression cũ được giữ nguyên. Chưa kiểm chấm AI Job mới vì A22 nằm ngoài scope.
- Không có blocker chức năng A21 đã biết trong phạm vi đã chạy. 78 skipped chưa được nghiệm thu lại; cảnh báo patch EF tooling đã ghi rõ. Không có quyết định/approval nào còn chờ: thay đổi test A20 đã được người dùng duyệt.

## 7. Chốt trước Git

`git diff --check` và kiểm whitespace/EOF cả file mới sạch; index rỗng (`git diff --cached --name-only`). Manifest khớp chính xác porcelain status, không file untracked ngoài manifest. Nhánh `feature/a21-job-crud-api`, HEAD vẫn `14dad000c4fcc258163f5da8135756df3a29cca8`, **9 M / 11 mới / 0 D**.

Evidence cuối: `artifacts/a21-git-verification.json`, `artifacts/a21-test-summary.json`, `artifacts/a21-final-test-catalogs.txt`. Chỉ bàn giao để người dùng review; không stage/commit/push/merge/PR. Dừng tại đây.
