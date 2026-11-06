# Quy trình làm việc nhóm với GitHub

## 1. Nguyên tắc

1. Không ai sửa trực tiếp trên nhánh `main`. Nhánh `main` đã được bảo vệ.
2. Mỗi tính năng làm trên **một nhánh riêng** tạo từ `main`.
3. Làm xong thì tạo **Pull Request (PR)** vào `main`.
4. Thành viên còn lại **review** và bấm **Approve** thì mới được merge.
5. Merge xong thì xóa nhánh.

## 2. Quy ước đặt tên nhánh

| Loại | Mẫu | Ví dụ |
|---|---|---|
| Tính năng | `feature/<mã>-<tên>` | `feature/a04-cv-upload-api` |
| Sửa lỗi | `fix/<số-issue>-<tên>` | `fix/12-loi-file-trung` |
| Tài liệu | `docs/<tên>` | `docs/c5-bao-cao` |
| Kiểm thử | `test/<tên>` | `test/b17-kiem-thu-luong` |

## 3. Danh sách nhánh theo phân công

### Bạn A – Backend và AI

| Nhánh | Công việc |
|---|---|
| `feature/c2-database` | Đưa thiết kế CSDL C2 lên repo |
| `feature/a01-setup-solution` | A1. Tạo solution, kết nối SQL Server, User Secrets cho Gemini API key |
| `feature/a02-ef-migrations` | A2, A3. Tạo bảng bằng EF Core Migration, nhập sẵn 5 job |
| `feature/a04-cv-upload-api` | A4, A5, A6. API tải CV, kiểm tra định dạng, dung lượng, file trùng, lưu file |
| `feature/a07-text-extraction` | A7. Đọc chữ bằng PdfPig, OCR `eng+vie` |
| `feature/a08-applications-api` | A8. API gắn CV với job |
| `feature/a09-keyword-scoring` | A9. Chấm keyword bằng C#, xử lý thiếu GPA hoặc kinh nghiệm |
| `feature/a10-gemini-client` | A10. Gọi Gemini, JSON có cấu trúc, thử lại, xử lý vượt hạn mức |
| `feature/a11-background-scoring` | A11, A12. Chấm điểm dưới nền, lưu điểm kèm phiên bản prompt |
| `feature/a13-results-api` | A13. API tiến độ, bảng xếp hạng, chi tiết CV |
| `feature/a14-prompt-versions-api` | A14. API phiên bản và kích hoạt prompt |
| `feature/a15-evaluation-api` | A15, A16. Nhập nhãn HR, chạy đánh giá, tính tỷ lệ đúng |
| `feature/a17-prompt-compare-api` | A17. API so sánh hai phiên bản prompt |
| `test/a18-unit-tests` | A18. Unit test tính điểm và tỷ lệ đúng |

### Bạn B – Giao diện và kiểm thử

| Nhánh | Công việc |
|---|---|
| `docs/b01-figma-design` | B1. Luồng sử dụng và 3 trang chính trên Figma |
| `feature/b02-blazor-layout` | B2. Dự án Blazor, bố cục chung, menu, service gọi API |
| `feature/b03-upload-page` | B3 – B6. Trang tải CV, chọn job, báo lỗi, thanh tiến độ |
| `feature/b07-results-page` | B7, B8, B11. Bảng xếp hạng, màu mức đánh giá, biểu đồ |
| `feature/b09-cv-detail-page` | B9, B10. Trang chi tiết CV, hiển thị trạng thái và lỗi |
| `feature/b12-prompt-page` | B12 – B16. Trang System Prompt, so sánh tỷ lệ đúng |
| `test/b17-e2e-testing` | B17, B18. Kiểm thử toàn bộ luồng, ghi lỗi lên Issues |

### Cả nhóm

| Nhánh | Công việc |
|---|---|
| `docs/c4-evaluation-dataset` | C4. Mô tả bộ CV đánh giá và nhãn HR |
| `docs/c5-bao-cao` | C5. Báo cáo, tài liệu hướng dẫn, kịch bản demo |

## 4. Quy ước commit

Mẫu: `loai(pham-vi): mo ta ngan`

| Loại | Dùng khi |
|---|---|
| `feat` | Thêm tính năng |
| `fix` | Sửa lỗi |
| `docs` | Tài liệu |
| `test` | Kiểm thử |
| `refactor` | Sửa cấu trúc code, không đổi chức năng |
| `style` | Giao diện, định dạng |

Ví dụ: `feat(upload): kiem tra file trung bang SHA-256`

## 5. Tiêu chí review

Người review kiểm tra:

- [ ] Đúng yêu cầu của mã công việc
- [ ] Build và chạy được
- [ ] Tên biến, hàm, API rõ ràng, khớp danh sách API đã chốt ở C2
- [ ] Khớp thiết kế CSDL trong thư mục `database/`
- [ ] Có xử lý lỗi (file sai định dạng, Gemini lỗi hoặc vượt hạn mức…)
- [ ] Không lộ Gemini API key, mật khẩu, chuỗi kết nối
- [ ] Không đưa `bin/`, `obj/`, `Uploads/` lên repo

Kết quả review: **Approve** (đồng ý), **Request changes** (yêu cầu sửa) hoặc **Comment** (chỉ góp ý).

## 6. Phân công review

| Người tạo PR | Người review |
|---|---|
| Bạn A | Bạn B |
| Bạn B | Bạn A |
