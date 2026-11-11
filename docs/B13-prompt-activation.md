# B13 — Kích hoạt phiên bản System Prompt

## Phạm vi và Git

- Repository: `D:\Projects\AI-CV-Screening`.
- Nhánh: `feature/B13-prompt-activation`.
- Base/HEAD khi bàn giao: `606f0c42e2328717f25687040d9a70b8650c4708`.
- Base là `origin/main`, đã merge B12 và tinh chỉnh shell UI (#43). Working tree
  sạch trước khi tạo nhánh B13. Không lấy thay đổi chưa commit từ nhiệm vụ khác.
- Codex không commit, push, tạo PR, merge hoặc sửa backend/database migration.

## API và tính toàn vẹn

UI gọi API A14 thật qua `PromptApiClient`:

1. `POST /api/prompts/{id}/activate` — một yêu cầu kích hoạt.
2. `GET /api/prompts` — đồng bộ lại danh sách sau POST, kể cả POST thất bại.

Không có fake mode. Frontend không gửi từng request để tắt/bật nhiều phiên bản.
API sẵn có dùng transaction `Serializable`, tắt bản cũ trước khi bật bản được chọn,
và unique filtered index `UX_PromptVersions_OneActive` (`[IsActive] = 1`).
Không thay đổi cơ chế này trong B13.

Kích hoạt chỉ cập nhật `PromptVersions.IsActive`. Nội dung, ghi chú, các lượt chấm
và kết quả không bị cập nhật hoặc chạy lại AI. Lượt Screening mới lưu ID phiên bản
đang dùng; lượt cũ tiếp tục tham chiếu ID phiên bản đã chọn khi tạo lượt. Đây là
liên kết tới phiên bản bất biến qua API, không phải bản sao độc lập của text prompt.

## Giao diện và hành vi

- Phiên bản chưa active có nút **Kích hoạt** riêng, không lồng trong nút chọn bản.
- Phiên bản active có nhãn **Đang dùng**, dấu timeline và viền teal.
- Phiên bản đang xem có nhãn **Đang xem**, nền chọn và `aria-pressed`.
- Chip đầu trang lấy tên và tỷ lệ đúng từ phiên bản active do server trả về;
  thiếu tỷ lệ hiển thị `—`, không thay bằng 0%.
- Khi gửi POST và GET xác minh: nút tương ứng hiện **Đang kích hoạt...** với spinner,
  khóa các thao tác xung đột; guard tại component và trang chặn double-click.
- Chỉ thông báo **Đã kích hoạt...** khi POST thành công và GET xác nhận đúng một
  bản active là bản được chọn. Không cập nhật active theo suy đoán trước phản hồi.
- Khi POST lỗi hoặc mất phản hồi, vẫn GET để hiển thị trạng thái thực; không báo
  thành công giả. Người dùng có thể thử lại khi trạng thái đã đồng bộ.
- Nếu GET không xác minh được hoặc trả nhiều active: giữ lịch sử cuối cùng có
  cảnh báo, chip chuyển thành **chưa được đồng bộ**, khóa kích hoạt cho tới khi
  bấm **Đồng bộ trạng thái** thành công. Nút này chỉ GET, không lặp lại POST.
- Danh sách ban đầu không có active hoặc rỗng được xử lý bình thường; dữ liệu có
  nhiều active hiển thị lỗi thay vì dựng hai bản đang dùng.

## Bảo toàn B12

Đồng bộ danh sách giữ nguyên ID phiên bản đang xem, nội dung nháp, tên bản mới và
ghi chú đang soạn. Metadata nguồn được cập nhật để nhãn editor khớp trạng thái
active mới. Hoàn tác vẫn trở về đúng phiên bản đang xem. Luồng lưu mới, validation,
cảnh báo bỏ nháp và lịch sử B12 giữ nguyên; không sửa `PromptEditor` hoặc `PromptDraft`.

## File thuộc B13

| File | Thay đổi |
|---|---|
| `src/SmartCv.Web/Components/Prompts/PromptHistory.razor` | Nút kích hoạt, loading, guard và phân biệt xem/dùng |
| `src/SmartCv.Web/Components/Prompts/PromptHistory.razor.css` | Nút, spinner, viền active, focus/responsive |
| `src/SmartCv.Web/Components/Pages/Prompts.razor` | POST/GET, đồng bộ lỗi, chip, bảo toàn bản nháp |
| `src/SmartCv.Web/Components/Pages/Prompts.razor.css` | Cảnh báo kích hoạt và chip chưa đồng bộ |
| `src/SmartCv.Web/Services/PromptApiClient.cs` | Thêm lệnh gọi API kích hoạt có sẵn |
| `tests/SmartCv.Tests/PromptActivationUiTests.cs` | 15 trường hợp kiểm thử B13 mới |
| `tests/SmartCv.Tests/PromptVersioningUiTests.cs` | Một assertion cho phép nút B13 mới; giữ toàn bộ test B12 |
| `docs/B13-prompt-activation.md` | Tài liệu bàn giao này |

Không thay đổi Upload, Results, FitDistribution, Gemini/OCR, database migration,
Docker, shell UI hoặc các chức năng B14–B16.

## Kiểm thử thực tế ngày 11/11/2026

- `dotnet build`: thành công, **0 warning, 0 error**.
- Test tập trung B13 + B12 + snapshot lượt chấm: **32 pass, 0 fail**.
- Bộ đầy đủ `dotnet test --no-build`: **1.160 pass, 0 fail, 26 skipped**, tổng
  **1.186**; bao gồm 5 test SQL Server A14 thật. Các test SQL khác chưa có cấu hình
  riêng phù hợp vẫn skip và không được tính là đã kiểm chứng.
- 5 test SQL A14 cũng đã chạy riêng: tạo/đọc, tỷ lệ gần nhất, một active/index,
  rollback giữa transaction và kích hoạt đồng thời: **5 pass, 0 fail**.
- Dùng biến môi trường `Logging__EventLog__LogLevel__Default=None` cho tiến trình
  test để tránh lỗi quyền Event Log. Không thay đổi cấu hình logging của dự án.
- `SMARTCV_TEST_SQL_CONNECTION` trỏ tới tiền tố test A14; fixture tạo và tự dọn
  database `SmartCv_A14_Test_<GUID>`, không dùng database nhóm.
- Chrome headless với **Web + API + SQL Server thật** trên database demo riêng:
  kích hoạt v1 (70%), kích hoạt lại v2 (80%), reload vẫn đúng; một bản active;
  giữ nguyên bản nháp/nội dung/tên/ghi chú B12; chọn bản cũ, hoàn tác và lưu mới
  vẫn hoạt động; phiên bản chưa đánh giá hiển thị `—`.
- Đọc trực tiếp SQL sau thử trình duyệt xác nhận hai lượt đánh giá cũ giữ nguyên
  PromptVersionId, tỷ lệ, trạng thái và cấu hình. Nội dung/ghi chú/thời gian tạo
  của hai phiên bản gốc cũng không đổi.
- Database demo có tên GUID riêng `SmartCv_A14_Test_B13Demo_<GUID>`; đã xóa sau khi
  kiểm tra. Các tiến trình Web/API/Chrome của phiên thử đã dừng. Không kích hoạt
  prompt trong `SmartCvDb` của nhóm.
- Responsive đã kiểm tra ở 320, 390, 768, 1.024, 1.366, 1.440 và 1.920px,
  kể cả tên dài; không tràn ngang trang.
- Đã đọc ZIP và xem trực tiếp đủ bốn ảnh, trọng tâm ảnh `4-prompt-do-chinh-xac.png`.
  Giữ bố cục B12/shell hiện có; cải thiện phân biệt đang xem và đang dùng.
- Ảnh chụp database demo nằm ngoài repository: `%TEMP%/SCApp-B13-desktop.png`,
  `SCApp-B13-activated-v1.png`, `SCApp-B13-mobile.png`,
  `SCApp-B13-mobile-long-name.png`.
- `git diff --check`: đạt; không có conflict marker trong các file B13.

## Giới hạn

Các lỗi mạng/mất phản hồi, double-click khi POST/GET còn pending, phản hồi JSON
lỗi và cập nhật cạnh tranh được kiểm tra bằng HTTP handler cách ly trong component
tests. Không gây lỗi cưỡng bức trên database nhóm. B14–B16 vẫn thuộc nhiệm vụ riêng.
