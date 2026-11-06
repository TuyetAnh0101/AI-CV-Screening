# Cơ sở dữ liệu SmartCvDb

| File | Nội dung |
|---|---|
| `SQL.sql` | Thiết kế CSDL gốc (C2): 12 bảng cùng PK, FK, UNIQUE, CHECK, DEFAULT và index. **Chỉ dùng để đối chiếu**, không chạy vào `SmartCvDb`. |
| `migration_InitialCreate.sql` | Script SQL sinh từ EF Core Migration `20261106132922_InitialCreate`, khớp với `SQL.sql`. |
| `seed.sql` | Dữ liệu mẫu cho database mới: 12 bảng, 368 dòng, trọng số chấm 30% Keyword + 70% AI. |
| `update_seed_weights_30_70.sql` | Chỉ dùng cho database đã seed bằng bản cũ 40/60; chạy một lần, chạy lại không thay đổi gì thêm. |
| `erdplus.png` | Sơ đồ quan hệ (ERD). |

Bảng được tạo bằng **EF Core Migration** (A2), không chạy thủ công `SQL.sql`. Hướng dẫn tạo database, nạp dữ liệu mẫu và kiểm tra xem [README chính](../README.md#database).
