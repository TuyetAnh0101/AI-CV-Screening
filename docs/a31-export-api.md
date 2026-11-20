# A31: hợp đồng xuất bảng xếp hạng cho B45

HR và Admin dùng JWT hiện hành; tài khoản bắt buộc đổi mật khẩu bị chặn. Endpoint mới nằm trong boundary và privacy lease hiện có. Không thay đổi giao diện B45.

| Method / đường dẫn | Quyền | Kết quả |
|---|---|---|
| GET `/api/exports/settings` | HR, Admin | `formats`, `pdfEnabled`, `maxRows`, bộ lọc, mặc định, `canExport`, `canViewLogs` |
| GET `/api/runs/{id}/ranking/export/preview` | HR, Admin | Số hồ sơ tổng/đang lọc/Qua vòng CV; `canExport`, `blockCode`, `blockReason` |
| GET `/api/runs/{id}/ranking/export` | HR, Admin | Excel thật + Content-Disposition; không cache |
| GET `/api/exports` | Admin | Nhật ký phân trang, liên kết `auditEventId` với B43 |

Danh sách lượt chấm, chi tiết, tiến độ và ranking tiếp tục dùng `/api/runs`, `/api/runs/{id}`, `/api/runs/{id}/ranking?jobId=...`. Không tạo API Dashboard mới: số lần tải file không phải chỉ số hiệu quả tuyển dụng; Dashboard hiện có đã thống kê hồ sơ Qua vòng CV và Cần xem kỹ.

Tham số preview và tải file giống nhau:

- `jobId`: mã dương; bỏ qua chọn vị trí đầu tiên của lượt chấm, giống ranking hiện có.
- `format=xlsx`: mặc định; `pdf` trả 400 `export_pdf_disabled`. PDF là tùy chọn trong ZIP, chưa cài renderer/giấy phép; không có công tắc cấu hình giả.
- `stage=Shortlisted`: mặc định. Nhận `all`, `New`, `Reviewing`, `Shortlisted`, `Interview`, `Rejected`, `OnHold`. `flagged` là alias cho `stage=all&needsReview=true`.
- `fit=all`: nhận `pass`, `notpass`, `unscored`, `excellent`, `good`, `potential`, `poor`; `issue` là alias cho `unscored`.
- `needsReview`, `manualFlags`: boolean độc lập, mặc định false. Dùng định nghĩa SCApp hiện có, bao gồm cờ HR.
- `search`: tối đa 100 ký tự, tìm không dấu và khớp mọi từ. Khi ẩn danh chỉ tìm bí danh, không dò tên thật.
- `anonymous=true`: mặc định phù hợp chế độ bảo vệ của SCApp. B45 phải gửi lựa chọn hiện hành. Bí danh giữ cả mã hồ sơ để phân biệt hạng bằng nhau.

Chỉ xuất lượt `Completed`, không còn hồ sơ `Pending`. Giữ thứ tự ranking theo điểm cuối giảm dần và hạng ban đầu; hồ sơ lỗi ở cuối, không biến điểm thiếu thành 0. Cột nguồn điểm phân biệt “Chỉ từ khóa” và “Keyword + AI”. B45 ghi rõ thứ tự xuất nếu người dùng đang sắp xếp bảng theo cột khác.

Excel có tiêu đề, prompt/ngưỡng đã lưu, người xuất từ JWT, giờ Việt Nam, lời nhắc HR quyết định, bảng lọc, điểm dạng số, kỹ năng bắt buộc thiếu. Không xuất nội dung CV, email, ghi chú, reasoning, evidence hoặc lỗi thô. Không lưu từ khóa search vào nhật ký; chỉ lưu `searchApplied`.

Luồng B45: tải settings và ranking; gọi preview khi phạm vi thay đổi; hiển thị trạng thái loading và khóa nút khi đang tải/`canExport=false`. Khi rỗng dùng `blockReason`. Tải file qua FileDownloadService có token của B43. Preview chỉ là ảnh chụp thời điểm đọc; download luôn đọc và kiểm tra lại, không dùng số liệu preview để quyết định quyền. Không tự gửi lại download sau lỗi mạng vì mỗi lần cấp file hợp lệ là một sự kiện riêng.

Lỗi ProblemDetails có `code`, thông báo tiếng Việt; lỗi validation thêm `errors` theo trường:

- 400: tham số rỗng/sai/lặp/không hỗ trợ, PDF chưa bật, vượt `Export:MaxRows` (mặc định 5000; cho phép 1–5000).
- 401/403: middleware JWT/phân quyền hiện có; HR đọc nhật ký xuất trả 403.
- 404 `export_not_found`: không có lượt chấm hoặc vị trí không thuộc lượt chấm.
- 409 `export_run_not_completed` / `export_empty`: chưa thể xuất hoặc không có hồ sơ theo bộ lọc.
- 500 `export_failed`: lỗi tạo file không dự kiến; không trả lỗi nội bộ.
- 503 `export_unavailable`: kết nối/schema/ghi nhật ký lỗi; không cấp file chưa ghi được lịch sử.

Nhật ký: `runId`, `jobId`, `actorUserId`, `anonymous`, `from`, `to`, `page=1`, `pageSize=25` (tối đa 100). Ngày `yyyy-MM-dd` theo Việt Nam, mặc định 7 ngày gần nhất, tối đa 90 ngày; mới nhất trước. DTO có `items`, `totalCount`, `totalPages`. Đây là lịch sử **chuẩn bị và cấp quyền tải**, không khẳng định client đã nhận đủ file.

Migration `20261120114152_AddRankingExportLogs` chỉ thêm ExportLogs, FK không cascade tới run/job/AuditEvents, index và trigger chống sửa/xóa. Actor và thời điểm lấy từ AuditEvents, không tạo hệ thống audit thứ hai. RepeatableRead giữ nhất quán scores/stages/flags/skills trong mỗi lần xuất; lưu metadata + B43 cùng transaction.

Áp dụng thủ công sau khi kiểm tra SQL: `database/a31_add_ranking_export_logs.sql`; thay placeholder database trước khi chạy. Cần B43 `20261120043759_AddAuditEvents` đã áp dụng. Không tự apply vào SmartCvDb. SQL Server integration chỉ chạy khi có catalog cô lập rỗng và opt-in rõ ràng.
