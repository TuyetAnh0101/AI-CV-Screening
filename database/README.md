# Cơ sở dữ liệu (C2)

Thư mục chứa thiết kế CSDL đã chốt ở bước C2:

- `schema.sql` – script tạo các bảng `Jobs`, `Cvs`, `Applications`, `PromptVersions`, `EvaluationRuns`, `EvaluationResults`, `HrLabels`
- `seed.sql` – dữ liệu mẫu (5 job và tiêu chí từ `JOB_CRITERIA`)
- `ERD.png` – sơ đồ quan hệ

Khi code, bảng được tạo bằng EF Core Migration (A2) theo đúng thiết kế này.
