# Cơ sở dữ liệu SmartCvDb

| File | Nội dung |
|---|---|
| `SQL.sql` | Thiết kế CSDL gốc (C2): 12 bảng cùng PK, FK, UNIQUE, CHECK, DEFAULT và index. **Chỉ dùng để đối chiếu**, không chạy vào `SmartCvDb`. |
| `migration_InitialCreate.sql` | Script SQL sinh từ EF Core Migration `20261106132922_InitialCreate` (schema), khớp với `SQL.sql`. |
| `migration_SeedJobs.sql` | Script SQL sinh từ EF Core Migration `20261106142155_SeedJobs` (dữ liệu nền 5 job / 29 kỹ năng / 52 từ khóa, có kiểm tra an toàn). |
| `seed_demo.sql` | Dữ liệu **demo** (282 dòng: CV mẫu, lượt chấm 30/70, nhãn HR, prompt). Chạy tay, tùy chọn, **sau** `dotnet ef database update`. |
| `check_jobs.sql` | Kiểm tra dữ liệu nền sau khi chạy migration. |
| `update_seed_weights_30_70.sql` | Chỉ dùng cho database đã seed bằng bản cũ 40/60; chạy một lần, chạy lại không thay đổi gì thêm. |
| `erdplus.png` | Sơ đồ quan hệ (ERD). |

Bảng và dữ liệu nền được tạo bằng **EF Core Migration** (`dotnet ef database update`), không chạy thủ công `SQL.sql`. Hướng dẫn tạo database, nạp dữ liệu demo và kiểm tra xem [README chính](../README.md#database).
