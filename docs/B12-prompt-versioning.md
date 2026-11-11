# B12 — System Prompt Editor & Version History

## Phạm vi và dữ liệu

Trang `/prompts` dùng API A14 thật: `GET /api/prompts` và `POST /api/prompts`.
Không có fake mode, thao tác sửa/xóa phiên bản, kích hoạt prompt hoặc chạy đánh giá.
DTO dùng lại `PromptVersionDto` và `CreatePromptVersionRequest`; không thay đổi API,
database, `AiPromptBuilder`, các trang Results hoặc B13–B16.

Lần kiểm tra ngày 11/11/2026: API cục bộ trả một phiên bản
`v1-default-screening`, đang dùng, chưa có tỷ lệ đúng. UI hiển thị theo dữ liệu này,
không dựng thêm v1/v2 hoặc tỷ lệ mẫu từ ZIP.

## Editor và lịch sử

- Editor lưu bản nháp riêng; không sửa DTO nguồn. Mặc định chọn bản đang dùng,
  nếu chưa có bản đang dùng thì chọn bản mới nhất. Danh sách trống vẫn tạo được bản đầu tiên.
- Font monospace, `wrap="off"`, số dòng và textarea có cùng chiều cao dòng 22px,
  cùng cuộn dọc trong khung ngoài; nội dung dài cuộn ngang trong textarea.
- Đếm ký tự từ nội dung nguyên bản. Đếm dòng và so sánh chuẩn hóa CRLF/CR thành LF;
  không tự định dạng hoặc trim nội dung gửi đi. A14 hiện có trim đầu/cuối khi lưu.
- Đếm dòng thêm/xóa bằng LCS có thứ tự, biểu diễn bitset; nhận diện đổi thứ tự,
  dòng lặp, dòng trống, sửa dòng và hoàn tác. Trạng thái đã sửa dựa trên so sánh
  nội dung độc lập với con số diff.
- Hoàn tác về đúng phiên bản đang chọn, xóa tên/ghi chú nháp. Đổi phiên bản hoặc
  điều hướng nội bộ khi có bản nháp yêu cầu giữ/bỏ; đóng hoặc tải lại tab dùng
  xác nhận chuẩn của trình duyệt qua `NavigationLock`.
- Lịch sử hiển thị tên, giờ Việt Nam, ghi chú, bản đang dùng, nhãn Mới cho bản vừa
  được API tạo và tỷ lệ đúng gần nhất. Thiếu tỷ lệ hiển thị `—`, không phải 0%.

## Validation và lưu

- Tên bắt buộc, trim, tối đa 100 ký tự; không kết thúc bằng `-`, không trùng tên
  trong danh sách theo so sánh không phân biệt hoa thường.
- Nội dung không trắng, tối đa 20.000 ký tự, phải khác bản nguồn. Chặn nội dung
  trùng phiên bản cũ theo quy tắc gộp whitespace của A14; thay đổi chỉ khoảng trắng
  được nhận diện là đã sửa nhưng API vẫn không cho tạo bản tương đương.
- Ghi chú tùy chọn, tối đa 500 ký tự sau trim theo API.
- Khóa lưu ở cả component và trang khi request đang chạy. Thành công dùng DTO
  trả về để thêm lịch sử và chọn bản mới; không tự kích hoạt. Thất bại giữ toàn bộ
  bản nháp và hiển thị thông báo API để người dùng sửa hoặc thử lại.
- Tải lại trang đọc từ API; không có bộ nhớ fake thay thế lưu trữ của backend.

## Kiểm thử đã thực hiện

- `dotnet build`: thành công, 0 warning, 0 error.
- Bộ đầy đủ `dotnet test --no-build`: 1.140 pass, 0 fail, 31 skipped,
  tổng 1.171. Biến môi trường `Logging__EventLog__LogLevel__Default=None` chỉ dùng
  trong tiến trình kiểm thử để tránh lỗi quyền Windows Event Log của môi trường.
- Các test SQL Server tùy chọn bị skip vì chưa cấu hình database kiểm thử riêng;
  không coi các test đó là đã chạy.
- `PromptDraftTests`: dòng thêm/xóa/sửa/đổi thứ tự/lặp/trống, CRLF/LF, metadata,
  undo, tên và giới hạn A14, bảo toàn nguồn, nội dung trùng, prompt 10.000 dòng và
  đối chiếu diff ngẫu nhiên với LCS tham chiếu.
- `PromptVersioningUiTests`: mở trang, lịch sử và tỷ lệ, validation, lưu mới,
  giữ nguyên nguồn, tải lại, lỗi GET/POST và retry, double-click, chọn bản cũ,
  undo, xác nhận bỏ nháp khi đổi phiên bản/rời trang và tạo bản đầu tiên.
  HTTP handler mô phỏng chỉ tồn tại trong test; trang gọi qua `PromptApiClient`.
- Test A14 hiện có chạy cùng bộ đầy đủ và xác minh tạo/đọc dữ liệu, validation,
  bất biến phiên bản cũ trên database kiểm thử InMemory.
- Chrome headless dùng Web + API thật: 320, 390, 768, 1.024, 1.366, 1.440 và
  1.920px không tràn ngang trang. Validation tên `v3-`/tên trùng hoạt động;
  điều hướng giữ bản nháp và đưa focus tới thông báo xác nhận; hoàn tác đúng nguồn.
  Kiểm tra editor 140 dòng đến dòng cuối: textarea và gutter cùng
  chiều cao 3.112px, cuộn ngoài, textarea không cuộn dọc độc lập.
- Đã xem đủ bốn ảnh ZIP; giữ layout chung của repository. Ảnh chụp thực tế ở
  `%TEMP%/SCApp-B12-desktop.png`, `SCApp-B12-mobile.png`,
  `SCApp-B12-long-editor.png`, không đưa ảnh tạm vào source.

## Giới hạn xác minh

Phiên trình duyệt với SQL Server thật chỉ đọc danh sách và sửa/hoàn tác bản nháp;
không tạo dữ liệu thử trên database nhóm. Luồng POST và tải lại được xác minh
bằng component tests và API tests cách ly; chưa kiểm chứng lưu mới qua trình duyệt
rồi khởi động lại ứng dụng với SQL Server thật. B13–B16 vẫn thuộc nhiệm vụ riêng.

Base B12: `c69cccfc46cb72a792121300d2ca1cc78858a712` (`origin/main`, đã merge B11).
Nhánh: `feature/B12-prompt-versioning`. Codex không commit, push, tạo PR hoặc merge.
