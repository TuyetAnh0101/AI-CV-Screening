# B56 — Onboarding workspace, quota, Users UI

- Branch: `feature/b56-onboarding-quota`
- Commit: CHƯA COMMIT
- Source baseline: GitHub `main`, review ngày 23/11/2026
- Backend scope: không sửa API, database, migration
- Thực hiện: file mới đầy đủ + exact-anchor integration qua B56 Manual Kit

## Phạm vi giao diện

- `/dashboard`: onboarding Personal + quota meter, sample-job A54, ba bước theo dữ liệu thật, localStorage per workspace.
- `/upload`: quota 409/429 và no_active_prompt theo mã lỗi; không trộn với rate limiting upload.
- `/prompts`: prompt test & playground phân biệt workspace AI quota với preview global quota.
- `/users`: nguồn, xác minh email, workspace; lọc nguồn theo trang đang xem và giới hạn reset mật khẩu trên UI.
- `/account`: trạng thái Admin system workspace được giải thích rõ.
- `/samples/cv-mau.zip`: tạo từ test-files/cv-mau-a7/ (CV giả, kiểm tra lại trước khi đưa vào demo).

## Kiểm thử (điền kết quả thực tế)

| Hạng mục | Kết quả | Ghi chú |
|---|---|---|
| Build solution | CHƯA CHẠY | |
| B56OnboardingTests | CHƯA CHẠY | |
| B56QuotaMessageTests | CHƯA CHẠY | |
| Regression | CHƯA CHẠY | |
| Personal onboarding 3 bước | CHƯA TEST TAY | |
| Tạo Job mẫu /409 trùng | CHƯA TEST TAY | |
| 429 AI, 409 CV, 409 prompt | CHƯA TEST TAY | |
| Internal/Admin không hiện Personal | CHƯA TEST TAY | |
| Users columns + quyền HR | CHƯA TEST TAY | |
| Responsive 390x844 / keyboard | CHƯA TEST TAY | |

## Các tồn tại cần nhiệm vụ backend

- GET /api/users chưa hỗ trợ query `source` server-side: UI chỉ lọc nguồn trong trang, không được suy diễn tổng.
- A30 reset-password hiện không kiểm tra `SignupSource`; UI ẩn nút không thay cho quy tắc backend.
- Nguồn Synthetic cần quy trình đồng ý A38 phù hợp, không được làm giả consent.

> Chỉ cập nhật báo cáo PASS sau khi đã chạy trên máy thật; không auto-commit/push/merge.
