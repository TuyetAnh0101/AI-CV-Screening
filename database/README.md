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
