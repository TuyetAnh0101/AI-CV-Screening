# A20 — Job Title UNIQUE INDEX: báo cáo trước Git

Ngày: 17/11/2026 (Việt Nam). **READY FOR USER REVIEW** trong phạm vi A20 đã kiểm thử. Chưa stage, commit, push, merge hoặc tạo PR. Chưa triển khai migration lên database ứng dụng.

## 1. Repository, nhánh và nguồn tài liệu

- Đường dẫn làm việc: `C:\TTCN\project\AI-CV-Screening`. Không cần worktree mới vì working tree ban đầu hoàn toàn sạch.
- Ban đầu đang ở `feature/a19-dashboard-api`, HEAD `525d1b23157859264b2ea76818a2581b83009fbd`; đây là commit A19 đã có sẵn khi bắt đầu A20. Không có staged/untracked/conflict hoặc merge/rebase/cherry-pick dở dang.
- Remote `origin`: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Đã fetch/prune; A19 đã merge vào `origin/main` tại **`6725951a3d7915252872d682bbc3b5f00d856552`**, `feat(A19): implement dashboard summary and recruitment metrics (#65)`.
- Không có nhánh A20 local/remote trùng tên. Đã tạo đúng `feature/a20-job-title-index` từ origin/main bằng `git switch --no-track -c feature/a20-job-title-index origin/main`. HEAD và origin/main lúc tạo cùng SHA đầy đủ trên; không thay đổi commit nền hay nhánh A19.
- Git dùng `-c safe.directory=...` theo từng lệnh vì sandbox ownership; fetch/tạo nhánh được công cụ cho phép. Không sửa global Git config. Không stash/reset/clean/restore, không ghi đè thay đổi đồng đội.

Đã đọc prompt `C:\TTCN\A20_Codex_Prompt_20261117.md`, SHA256 `FE01D912DF2CC97F969D70C53F34D0514B2D1C4D585F9E44FB6BBCB0DC7AF570`.

Đã mở trực tiếp ZIP `C:\TTCN\A19_B26_SmartCv.zip`, SHA256 `3E0C98598DA639AC0B4B27D6CC400C794E5A772B8154437741954A198FAC1327`: guide tuần 3 (chuẩn bị, A20, phụ thuộc A20→A21→A22, hợp đồng A21 và lỗi thường gặp), mẫu A20 `JobConfiguration.cs`, `database/a20_kiem_tra_doi_ten_job.sql`. Guide cũng đã được đọc đầy đủ trong nhiệm vụ A19 trước đó. ZIP không có ảnh PNG/JPG/SVG/WebP/mockup. Không làm UI và không chép đè mẫu ZIP lên source thật.

Đã đối chiếu live configuration, Job/DbContext, toàn bộ migration Up/Down liên quan, snapshot, `JobSeedData`, các FK/configuration, `JobSeedDataTests`, fixture SQL hiện hữu và `database/SQL.sql`.

## 2. Thay đổi EF và migration

Chỉ thay một mapping trong `JobConfiguration.cs`:

```csharp
// Trước
b.HasAlternateKey(x => x.Title).HasName("UQ_Jobs_Title");
// Sau
b.HasIndex(x => x.Title).IsUnique().HasDatabaseName("UQ_Jobs_Title");
```

JobId vẫn là PK `PK_Jobs`; Title required/150; MinGpa và MinYearsExperience giữ precision/check; các named defaults `DF_Jobs_IsActive`, `DF_Jobs_MinYearsExperience`, `DF_Jobs_CreatedAt` và `HasData(JobSeedData.Jobs)` giữ nguyên. Không đổi seed, collation, cách chuẩn hóa Title, relationship hoặc entity/DTO/API.

Migration mới được tạo bằng EF CLI: **`20261117042418_JobTitleUniqueIndex`**. Migration liền trước được xác minh từ source và `dotnet ef migrations list --no-connect`: **`20261113014746_CvConsentRetention`**, không suy đoán từ ZIP.

| Chiều | Đúng hai operations |
| --- | --- |
| Up | DropUniqueConstraint `UQ_Jobs_Title` trên Jobs → CreateIndex cùng tên, cột Title, unique=true |
| Down | DropIndex `UQ_Jobs_Title` → AddUniqueConstraint cùng tên trên Title |

Không có InsertData/UpdateData/DeleteData, đổi default constraint, thêm/xóa cột/bảng, sửa FK hoặc thao tác dữ liệu nghiệp vụ trong migration A20. Không sửa migration cũ. Designer được EF sinh đúng chuẩn; snapshot chỉ đổi block HasAlternateKey sang HasIndex/IsUnique/HasDatabaseName (3 dòng thêm, 2 dòng bỏ).

Lịch sử sau A20:

1. `20261106132922_InitialCreate`
2. `20261106142155_SeedJobs`
3. `20261112045258_ApplicationStages`
4. `20261113014746_CvConsentRetention`
5. `20261117042418_JobTitleUniqueIndex`

`dotnet ef migrations has-pending-model-changes` và unit test đều xác nhận không có model drift. Đã cập nhật **đúng assertion danh sách migration** trong `JobSeedDataTests` để giữ bốn migration cũ theo thứ tự và thêm A20 thứ năm; không bỏ assertion hoặc tắt test.

### SQL delta có thể review

`database/a20_job_title_unique_index.sql` xuất từ đúng khoảng migration 4→5. Nội dung EF sinh chỉ có DROP constraint, CREATE unique index và INSERT một migration history entry trong transaction; không chứa toàn bộ lịch sử.

Đã bọc nguyên phần delta bằng `SET XACT_ABORT ON` và `TRY/CATCH` rollback rồi THROW. Điều này ngăn lỗi DDL dẫn đến ghi nhầm history khi người dùng chạy file SQL trực tiếp. Integration test so sánh chính xác body với `IMigrator.GenerateScript(4,5)` cộng wrapper bảo vệ, rồi thực thi trên DB thử. Rollback SQL tạo bằng EF cũng được kiểm thử với wrapper tương tự; lưu trong ignored artifacts.

`database/a20_kiem_tra_doi_ten_job.sql` được viết lại có guard chỉ cho `SmartCv_A20_Test_<32 hex GUID>`, yêu cầu ít nhất hai Jobs, từ chối transaction đang mở, xác minh index trước thử. Rename/duplicate nằm trong transaction và luôn rollback; lỗi không đúng 2601 sẽ THROW. Không thể vô tình chạy script này trên SmartCvDb.

## 3. Bằng chứng SQL Server và bảo toàn dữ liệu

Instance kiểm thử: `.\BB`. Các test chỉ tạo catalog mới hoàn toàn có GUID; kiểm tra DB_ID chưa tồn tại, validate prefix/no AttachDBFilename và xác nhận `DB_NAME()` trước thao tác. Không tái sử dụng hoặc xóa catalog cũ.

Máy này có `model` ở SINGLE_USER; fixture tạo và đặt riêng **catalog A20 mới** sang MULTI_USER trước khi dùng. Không đổi `model`, SmartCvDb, database của nhiệm vụ khác hoặc cấu hình server.

### Kiểm tra database ứng dụng chỉ đọc

Các câu SELECT metadata trên SmartCvDb trước và sau cho cùng kết quả:

- `UQ_Jobs_Title`: NONCLUSTERED, `is_unique=1`, **`is_unique_constraint=1`**.
- Chỉ `FK_Applications_Jobs` và `FK_JobSkills_Jobs` trực tiếp tới Jobs, đều tham chiếu **JobId**; không FK tới Title.
- `__EFMigrationsHistory` vẫn bốn migration cũ, cuối là CvConsentRetention; **không có A20**.

Evidence: `artifacts/a20-live-schema-readonly.txt`, `artifacts/a20-live-schema-after-readonly.txt`. Không đọc/lưu CV thật, không cập nhật migration/schema/data thật. Lịch sử hiện hữu khớp tiền nhiệm cần cho A20; không tự reconciliation.

### Kiểm thử schema chuẩn từ EF migrations

Mỗi test áp dụng bốn migration nền trên catalog mới, giữ 5 Job seed và nạp dữ liệu nghiệp vụ tổng hợp. Trước Up lưu snapshot JSON toàn bộ dữ liệu tổng hợp cùng row counts và SHA256; đây là snapshot dữ liệu test để đối chiếu, không phải backup database thật. Không ghi PII hay dữ liệu ứng viên thật.

| Bảng | Trước Up | Sau Up / rename rollback / Down / reapply |
| --- | ---: | ---: |
| Candidates | 0 | 0 |
| Jobs | 5 | 5 |
| JobSkills | 29 | 29 |
| JobSkillAliases | 52 | 52 |
| Cvs | 2 | 2 |
| Applications | 2 | 2 |
| ApplicationEvents | 2 | 2 |
| PromptVersions | 1 | 1 |
| EvaluationSets | 1 | 1 |
| HrLabels | 2 | 2 |
| EvaluationRuns | 1 | 1 |
| Evaluations | 2 | 2 |
| EvaluationSkills | 2 | 2 |

Fingerprint so sánh **toàn bộ giá trị**, không chỉ số lượng. Hash FK/default/check/trigger cũng khớp, gồm trigger bảo vệ lịch sử A35. Migration history được kiểm riêng vì Up/Down phải thêm/bỏ đúng entry A20.

Catalog EF Up/Down của full regression cuối: `SmartCv_A20_Test_31cd678bd2034a32a36c3d5ea52e9c06`.

- Data SHA256 trước/sau: `B7E8BC004AA3153657C35C1808657329D6DCC4A42CEA748CC91271EDAF3B3D93`.
- FK/default/check/trigger SHA256: `B27F503EBB2FEF7C661B505F0DABFA175ED74E76EE6E62B5EEF264DC6685CCAC`.
- Sau Up: index đúng tên, một key column Title, `is_unique=1`, **`is_unique_constraint=0`**.
- EF change tracking rename Unicode thành công, đọc lại đúng, đổi lại tên gốc thành công; transaction cuối rollback.
- Rename trùng bị DbUpdateException/SqlException **2601**, thông báo có `UQ_Jobs_Title`. Collation giữ `SQL_Latin1_General_CP1_CI_AS`; tên chỉ khác hoa/thường cũng bị chặn 2601, không thêm Trim/ToLower trong code ứng dụng.
- Down chỉ A20 về migration 4: UNIQUE CONSTRAINT khôi phục, `is_unique_constraint=1`, duplicate lỗi **2627**, row data/history nghiệp vụ không đổi.
- Reapply A20 và gọi lại migration đã applied: index đúng, history không trùng, dữ liệu không đổi.

Test SQL delta xuất ra cũng kiểm Up/Down và dữ liệu tương tự, độc lập với đường `MigrateAsync`.

### FK bất ngờ và schema từ SQL.sql

Test tạo thêm FK tới Title **chỉ trên một catalog thử mới**: EF migration và SQL delta có wrapper đều thất bại đúng lỗi SQL **3725/3727**, không tự bỏ FK, không thêm history A20, constraint và dữ liệu còn nguyên. Đây là kiểm thử fail-closed, không phải FK tồn tại trong model hoặc SmartCvDb đang sử dụng.

Đã dựng schema từ `database/SQL.sql` trên catalog thử mới bằng cách loại bỏ chính xác header CREATE DATABASE/USE SmartCvDb rồi chỉ chạy phần CREATE TABLE/index vào catalog đã guard. Schema đó có UNIQUE CONSTRAINT nhưng **không có `__EFMigrationsHistory`, không có A35 Stage hoặc A38 AnonymizedAt**. Hai Job tổng hợp còn nguyên. Không áp dụng migrations toàn bộ hoặc làm giả history cho schema legacy này; cần kế hoạch reconciliation riêng khi gặp ngoài thực tế.

### Smoke bằng sqlcmd

Đã chạy file verification thật bằng `sqlcmd -b -f 65001` trên `SmartCv_A20_Test_c6471ace25c6446e98b5e828de0b672c` (catalog từ focused tests):

```text
UQ_Jobs_Title  NONCLUSTERED  is_unique=1  is_unique_constraint=0
RenameRolledBack=1  DuplicateError=2601  JobsPreserved=5
```

Evidence: `artifacts/a20-sqlcmd-verification.txt`. Tất cả catalog mới, snapshot và evidence được giữ trong `artifacts/a20-sql/`; không có cleanup/drop database.

## 4. Build, unit, integration và full regression

Môi trường: .NET SDK **10.0.403**, EF Core runtime **10.0.15**, global EF CLI **10.0.14** cùng major 10. CLI cảnh báo patch cũ hơn runtime; không cập nhật global tool của người dùng. Generation/model-drift/schema tests đều đạt. Không đổi package versions, appsettings, connection strings hoặc secrets.

Baseline/focused/full dùng `--artifacts-path artifacts/a20-build` để không khóa binary demo. EF CLI dùng configuration riêng **A20** (`src/SmartCv.Api/bin/A20/...`), tránh output Debug đang chạy. CLI design-time nhận connection `SmartCv_A20_DesignTime_NotUsed`, Production, feature gate/scoring/retention tắt và key giả; migrations list dùng `--no-connect`. Không chạy `dotnet ef database update` nhắm database thật.

| Kiểm tra | Passed | Failed | Skipped | Kết quả |
| --- | ---: | ---: | ---: | --- |
| Restore | — | 0 | — | Thành công |
| Baseline build | — | 0 | — | 0 warnings, 0 errors |
| Baseline full | 1.956 | 0 | 78 | Tổng 2.034, 1 phút 14 giây; source origin/main trước thay đổi |
| Focused | 21 | 0 | 0 | 12 test A20 mới (8 metadata/unit/guard, 4 SQL) + 9 seed regression; 8 giây |
| Final build | — | 0 | — | **0 warnings, 0 errors** |
| Final full regression | **1.968** | **0** | **78** | Tổng **2.046**, 1 phút 27 giây |

Baseline và full cuối đều bật riêng SQL A19; full cuối bật thêm A20. Cả **44 A19 tests**, gồm 8 SQL tests, và 12 A20 tests đều pass. 78 skipped có sẵn gồm 73 SQL opt-in khác và 5 test cần demo ngoài repo; không bật fixture cũ có dọn database và không tính skipped là passed. Không có lỗi nền cần sửa source ngoài phạm vi.

Trong quá trình viết test đã sửa import metadata/analyzer và collation conflict ở UNION đọc system metadata trong fixture mới. Chỉ thêm `COLLATE DATABASE_DEFAULT` cho biểu thức so metadata; không đổi collation của database hoặc Jobs.Title. Log trung gian `artifacts/a20-focused-metadata-collation.log`; focused/full cuối đều xanh.

Lệnh chính:

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a20-build -v minimal
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/a20-build -v minimal
dotnet build src/SmartCv.Api/SmartCv.Api.csproj --configuration A20 -v minimal
dotnet ef migrations list --no-connect --no-build --configuration A20 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api
dotnet ef migrations add JobTitleUniqueIndex --no-build --configuration A20 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output-dir Data/Migrations
dotnet ef migrations script 20261113014746_CvConsentRetention 20261117042418_JobTitleUniqueIndex --no-build --configuration A20 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output database/a20_job_title_unique_index.sql
dotnet ef migrations has-pending-model-changes --no-build --configuration A20 --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api
$env:SMARTCV_A19_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A19_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A20_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A20_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-restore --artifacts-path artifacts/a20-build --filter 'FullyQualifiedName~SmartCv.Tests.Jobs.JobTitleIndex|FullyQualifiedName~SmartCv.Tests.JobSeedDataTests' --logger 'trx;LogFileName=a20-focused.trx' --results-directory artifacts/a20-focused-results
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a20-build --logger 'trx;LogFileName=a20-full.trx' --results-directory artifacts/a20-full-results
```

Lệnh xuất SQL tạo body nguyên bản; wrapper TRY/CATCH bổ sung như mục 2 và được test so với body EF. Baseline test dùng cùng lệnh full, logger/results tại `artifacts/a20-baseline-results/`, trước sửa code và chưa bật biến A20.

Log/TRX: `artifacts/a20-restore.log`, `a20-baseline-build.log`, `a20-baseline-tests.log`, `a20-final-build.log`, `a20-focused-tests.log`, `a20-full-tests.log`, ba thư mục `a20-*-results/`. Migration evidence: `a20-migrations-before.txt`, `a20-migration-add.log`, `a20-pending-model.log`, `a20-migration-script.log`, `a20-job-title-rollback.sql`. Không đưa artifacts vào Git.

## 5. Danh sách file và manifest

**3 M / 7 file mới chưa stage / 0 D = 10 file.**

| Trạng thái | File | Mục đích |
| --- | --- | --- |
| M | `src/SmartCv.Infrastructure/Data/Configurations/JobConfiguration.cs` | Thay đúng một dòng mapping Title |
| M | `src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs` | Snapshot chuyển key thành index, giữ toàn bộ mapping/seed khác |
| M | `tests/SmartCv.Tests/JobSeedDataTests.cs` | Mở rộng assertion lịch sử đúng một migration A20, giữ kiểm tra no-pending |
| A | `src/SmartCv.Infrastructure/Data/Migrations/20261117042418_JobTitleUniqueIndex.cs` | Hai operations Up và hai operations Down |
| A | `src/SmartCv.Infrastructure/Data/Migrations/20261117042418_JobTitleUniqueIndex.Designer.cs` | Target model/metadata EF sinh chuẩn |
| A | `database/a20_job_title_unique_index.sql` | Delta SQL 4→5 với transaction/error guard |
| A | `database/a20_kiem_tra_doi_ten_job.sql` | Verification chỉ cho catalog thử GUID, rename/duplicate luôn rollback |
| A | `tests/SmartCv.Tests/Jobs/JobTitleIndexTests.cs` | Metadata, change tracking, migration operations, seed/default/FK và SQL target guard |
| A | `tests/SmartCv.Tests/Jobs/JobTitleIndexSqlServerTests.cs` | 4 SQL integration tests, snapshot, script parity, reversal và failure safety |
| A | `docs/A20-implementation-report.md` | Báo cáo này |

`artifacts/a20-commit-files.txt`: manifest UTF-8 không BOM, mỗi dòng một relative path, khớp chính xác toàn bộ M/A nêu trên. `artifacts/a20-base-sha.txt`: SHA nền đầy đủ 40 ký tự. Cả hai được rule `artifacts/` hiện hữu trong `.gitignore` bảo vệ. Không sửa .gitignore, không đưa ZIP/log/TRX/SQL test snapshot/secret vào manifest.

## 6. Rủi ro, rollback và handoff

- Chưa deploy. Trước áp dụng lên môi trường thật cần chủ dự án review schema/history, xác minh không FK tham chiếu Title, backup thật và chọn thời điểm phù hợp vì thao tác index cần schema lock. Nhiệm vụ này không cấp quyền cập nhật SmartCvDb.
- Database dựng bằng SQL.sql hoặc đã chỉnh tay cần reconciliation riêng. Không chạy migrations từ 0 vào schema đã có, không thêm history giả và không dùng raw ALTER để bỏ qua mismatch. Nếu có FK Title thì dừng và thiết kế migration tương thích; test đã chứng minh fail-closed.
- Down A20 khôi phục constraint trên **tên đang có**, không tự phục hồi tên cũ hay ghi lại seed. Phục hồi dữ liệu ngoài hai DDL này cần backup theo quy trình riêng. Chỉ rollback A20, không rollback A35/A38 hoặc migration dữ liệu nền.
- Unique index giữ semantics collation SQL Server; A20 không thay trim/case/normalization. A21 sau này cần dùng JobId làm định danh ổn định và xử lý duplicate 2601; có thể cần nhận cả 2627 khi chẩn đoán môi trường chưa nâng cấp.
- Không triển khai PUT/CRUD A21, skills A22, A25, thay Dashboard A19 hoặc file Web/Blazor/CSS/JS. B20/B21 làm UI sau: hiển thị lỗi tên trùng tại ô Tên, giữ dữ liệu form khi lỗi, link hồ sơ theo JobId để đổi tên không làm hỏng liên kết. Không có hình tham chiếu từ ZIP.
- Không gọi Gemini thật, không bật worker ứng dụng. Full regression có các test fake/loopback/worker lifecycle hiện hữu; SQL A20 chỉ dùng DbContext/SqlClient trực tiếp. Không xóa CV, không drop database/LocalDB catalog hoặc dữ liệu của nhiệm vụ khác.
- 78 test skipped chưa được nghiệm thu lại trong lượt này; không khẳng định toàn bộ SQL opt-in cũ đã pass. EF CLI patch 10.0.14 thấp hơn runtime 10.0.15 là cảnh báo tooling đã ghi nhận, không gây migration drift hoặc lỗi test.

Git cuối: nhánh `feature/a20-job-title-index`, HEAD vẫn bằng SHA nền. `git diff --check` và kiểm tra whitespace file mới sạch; `git diff --cached --name-only` rỗng; manifest khớp status. Evidence cuối: `artifacts/a20-git-verification.json`, `artifacts/a20-test-summary.json`, `artifacts/a20-final-test-catalogs.txt`.

Đề xuất PR title để chủ dự án dùng sau khi duyệt: **`feat(A20): replace Job title alternate key with unique index`**. Không tạo PR hoặc thực hiện bất kỳ thao tác stage/commit/push/merge nào. Dừng bàn giao tại đây.
