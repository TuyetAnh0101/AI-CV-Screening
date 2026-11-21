# Cơ sở dữ liệu SmartCvDb

| File | Nội dung |
|---|---|
| `SQL.sql` | Thiết kế CSDL gốc (C2): 12 bảng cùng PK, FK, UNIQUE, CHECK, DEFAULT và index. **Chỉ dùng để đối chiếu**, không chạy vào `SmartCvDb`. |
| `migration_InitialCreate.sql` | Script SQL sinh từ EF Core Migration `20261106132922_InitialCreate` (schema), khớp với `SQL.sql`. |
| `migration_SeedJobs.sql` | Script SQL sinh từ EF Core Migration `20261106142155_SeedJobs` (dữ liệu nền 5 job / 29 kỹ năng / 52 từ khóa, có kiểm tra an toàn). |
| `seed_demo.sql` | Dữ liệu **demo lịch sử trước cap69** (282 dòng: CV mẫu, snapshot 30/70, nhãn HR, prompt). 70%/80% không phải kết quả kỳ vọng khi chấm mới. Chạy tay, tùy chọn, **sau** migration trên database trống. |
| `check_jobs.sql` | Kiểm tra dữ liệu nền sau khi chạy migration. |
| `update_seed_weights_30_70.sql` | **Đã ngừng cập nhật; chỉ SELECT.** Giữ đường dẫn cũ để lệnh cũ không sửa lịch sử 40/60. |
| `check_scoring_consistency.sql` | Audit chỉ đọc: trọng số snapshot, công thức/dự đoán, accuracy, và preview cap trên điểm đóng băng; không sửa dữ liệu. |
| `a35_application_stages.sql` | Script SQL sinh từ EF Core Migration `20261112045258_ApplicationStages` (A35). Chỉ để đối chiếu. |
| `check_a35.sql` | Kiểm tra chỉ đọc giai đoạn hồ sơ và lịch sử quyết định **sau** khi đã có A35. |
| `check_runtime_schema.sql` | Kiểm tra chỉ đọc trước/sau triển khai A35: lịch sử migration, 4 cột, bảng lịch sử, CHECK, index, FK, trigger (kể cả `MessageText`), Run 25. |
| `runtime_apply_application_stages.sql` | Áp dụng A35 có kiểm soát (transaction, khóa, kiểm tra tên database, không tự sửa schema dở dang). **Chỉ chạy sau khi được duyệt**, theo các bước bên dưới. File lưu UTF-8 có BOM; đừng lưu lại bằng trình soạn thảo bỏ BOM. |
| `a51_add_workspaces.sql` | Script **idempotent** sinh từ EF Core Migration `20261121040902_AddWorkspaces` (A51: workspace, backfill dữ liệu cũ vào workspace nội bộ số 1). UTF-8 có BOM. Áp dụng lên `SmartCvDb` **chỉ do người dùng thực hiện** sau backup, theo mục *A51* bên dưới. |
| `check_a51_workspaces.sql` | Kiểm tra **chỉ đọc** sau A51 (PASS/FAIL từng mục, báo lỗi 52051 nếu có FAIL). |
| `erdplus.png` | Sơ đồ quan hệ (ERD). |

Bảng và dữ liệu nền được tạo bằng **EF Core Migration** (`dotnet ef database update`), không chạy thủ công `SQL.sql`. Hướng dẫn tạo database, nạp dữ liệu demo và kiểm tra xem [README chính](../README.md#database).

## Chạy script bằng `sqlcmd`

Luôn thêm `-I` (bật `QUOTED_IDENTIFIER` giống EF Core) và `-f 65001` (đọc file UTF-8). Thiếu hai tham số này,
`sqlcmd` đọc file không BOM theo bảng mã ANSI (làm hỏng chữ tiếng Việt) và tạo đối tượng với `QUOTED_IDENTIFIER OFF`.
`runtime_apply_application_stages.sql` tự dừng trước khi thay đổi gì nếu gặp một trong hai trường hợp đó.

Cú pháp dấu nháy khác nhau giữa hai shell: CMD **không** hiểu dấu nháy đơn (`'localhost\BB'` bị coi là tên máy chủ có dấu nháy).

```cmd
:: CMD
sqlcmd -S "localhost\BB" -d "SmartCvDb" -E -b -I -f 65001 -i "database\check_runtime_schema.sql"
```

```powershell
# PowerShell
sqlcmd -S 'localhost\BB' -d 'SmartCvDb' -E -b -I -f 65001 -i 'database/check_runtime_schema.sql'
```

Trước khi áp dụng A35 lên database có dữ liệu: backup `COPY_ONLY, CHECKSUM` ra file mới, `RESTORE VERIFYONLY`,
rồi tạo bản sao script với `@ExpectedDatabase` là đúng tên database đã duyệt. Quy trình đầy đủ và lệnh khôi phục:
[báo cáo A35 runtime](../docs/A35-runtime-database-fix-report.md#m-những-bước-cần-phê-duyệt).

Chi tiết nguồn sai lệch demo, cách tái tạo bằng thuật toán thật và giới hạn so sánh:
[Tính nhất quán sau A18](../docs/scoring-data-consistency.md).

## A51 - Workspace (bảng, quan hệ, chuyển đổi dữ liệu)

Migration `AddWorkspaces` chỉ tạo **nền tảng dữ liệu**: chưa có API, JWT, query filter hay giao diện workspace (A52-A54, B56).
`Users.Role` (`Admin`/`HR`, quyền hệ thống) tách biệt với `WorkspaceMembers.Role` (`Owner`/`Member`, quyền trong workspace).

Hiệu ứng trên dữ liệu có sẵn (không xóa, không reset):

- Tạo `Workspaces` (workspace 1 = `Internal`, "SCApp nội bộ"), `WorkspaceMembers`, `UserTokens`, `UserExternalLogins`.
- Mọi dòng của `Jobs`, `Cvs`, `Candidates`, `Applications`, `PromptVersions`, `EvaluationSets`, `EvaluationRuns`, `ExportLogs` nhận `WorkspaceId = 1`.
  `AuditEvents.WorkspaceId` nullable; lịch sử cũ giữ `NULL` (bảng chỉ thêm, có trigger chặn UPDATE).
- Mọi tài khoản hiện có là thành viên workspace 1; Admin có `UserId` nhỏ nhất là `Owner` và là `OwnerUserId` của workspace 1, còn lại `Member`.
  Các cột `Email`, `PasswordHash`, `Role`, `IsActive`, `SecurityStamp` **không bị sửa**. Chỉ cột mới được điền: `EmailVerifiedAt = CreatedAt`, `SignupSource = 'Admin'`.
- Thay ràng buộc duy nhất toàn cục bằng ràng buộc theo workspace: `UQ_Jobs_Title` -> `UQ_Jobs_Workspace_Title (WorkspaceId, Title)`,
  `UQ_Cvs_FileHash` -> `IX_Cvs_Workspace_FileHash (WorkspaceId, FileHash)` (UNIQUE), `UQ_PromptVersions_VersionName` / `UQ_EvaluationSets_Name` theo workspace,
  `UX_PromptVersions_OneActive` -> `UX_PromptVersions_Workspace_OneActive` (một prompt active mỗi workspace).
- Khóa ngoại ghép chặn liên kết chéo workspace: `Applications -> Jobs/Cvs`, `EvaluationRuns -> PromptVersions/EvaluationSets`, `EvaluationSets -> Jobs`, `Cvs -> Candidates`.
  Liên kết **gián tiếp** (`Evaluations`, `EvaluationSetMembers`, `HrLabels`, `ExportLogs` -> Run/Job) chưa được database chặn; A52/A53 phải chặn ở tầng ứng dụng.
  `check_a51_workspaces.sql` đếm các liên kết chéo này.
- Hạn chế: `FK_EvaluationSets_Jobs` không còn `ON DELETE SET NULL` (không áp dụng được cho khóa ghép có `WorkspaceId NOT NULL`); hiện không có chức năng xóa Job.
- `seed_demo.sql`, `a23_tao_du_lieu_hieu_nang.sql` và `test_a5_trung_database.sql` viết cho schema **trước A51** (không có `WorkspaceId`, cột này không có DEFAULT trong database): chạy chúng **trước** khi áp dụng A51.

Quy trình áp dụng lên `SmartCvDb` (người dùng tự chạy, sau khi được duyệt; tuyệt đối không chạy tự động khi API khởi động):

```powershell
# 1. Backup COPY_ONLY, CHECKSUM ra file mới, rồi RESTORE VERIFYONLY (xem báo cáo A35 runtime ở trên).
# 2. Áp dụng script idempotent (một transaction; chạy lại lần hai không đổi gì):
sqlcmd -S 'localhost\BB' -d 'SmartCvDb' -E -C -b -I -f 65001 -i 'database/a51_add_workspaces.sql'
# 3. Kiểm tra chỉ đọc:
sqlcmd -S 'localhost\BB' -d 'SmartCvDb' -E -C -b -I -f 65001 -i 'database/check_a51_workspaces.sql'
sqlcmd -S 'localhost\BB' -d 'SmartCvDb' -E -C -b -I -f 65001 -i 'database/check_runtime_schema.sql'
```

Không dùng `Down()` trên dữ liệu thật; nếu cần quay lại, khôi phục từ backup. Script chỉ áp dụng được lên database đang ở migration
`20261120114152_AddRankingExportLogs` (trước A51). Sinh lại script: `dotnet ef migrations script 20261120114152_AddRankingExportLogs 20261121040902_AddWorkspaces --idempotent --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --output database/a51_add_workspaces.sql`.
