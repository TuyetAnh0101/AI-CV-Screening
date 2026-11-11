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
| `erdplus.png` | Sơ đồ quan hệ (ERD). |

Bảng và dữ liệu nền được tạo bằng **EF Core Migration** (`dotnet ef database update`), không chạy thủ công `SQL.sql`. Hướng dẫn tạo database, nạp dữ liệu demo và kiểm tra xem [README chính](../README.md#database).

Chi tiết nguồn sai lệch demo, cách tái tạo bằng thuật toán thật và giới hạn so sánh:
[Tính nhất quán sau A18](../docs/scoring-data-consistency.md).
