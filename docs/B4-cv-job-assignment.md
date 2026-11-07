# B4 – Chọn công việc và chuẩn bị gán CV

## Nhánh nền

- Nhánh: `feature/B4-cv-job-assignment`.
- Đã fetch và kiểm tra working tree sạch trước khi chuyển nhánh.
- B3 đã được squash merge qua PR #16, commit `2fa2228` trên `origin/main`; cây nội dung trùng với B3 `cd39a47`.
- Cập nhật `main` bằng fast-forward, sau đó tạo B4 từ `main` tại `2fa2228`.
- Khi mở PR, chọn base `main`; chỉ các commit B4 là thay đổi mới cần review.

## Chức năng B4

- `/upload` dùng `JobApiClient.GetJobsAsync()` gọi `GET /api/jobs?activeOnly=true`.
- Chọn Job bằng `JobId`, không tự chọn vị trí đầu tiên. Chỉ hiển thị vị trí đang tuyển.
- Ngay dưới ô chọn: kỹ năng `MustHave`, GPA tối thiểu (hai chữ số thập phân), kinh nghiệm tối thiểu. Có nội dung thay thế khi không yêu cầu GPA hoặc kỹ năng.
- Có trạng thái đang tải, danh sách rỗng, lỗi và nút thử lại. Lỗi Job không chặn tải CV.
- Đổi hoặc bỏ chọn Job giữ nguyên danh sách CV, trạng thái và checkbox.
- Đăng ký typed HTTP client `ApplicationApiClient` với cùng `ApiBaseUrl` và quy ước xử lý lỗi của các client hiện có.
- `AssignSelectedAsync(job, items, ct)` gửi `POST /api/applications` theo hợp đồng A8, chỉ lấy dòng tích chọn, `CanAnalyze`, có `CvId` khác rỗng; loại mã trùng, giữ thứ tự.
- Chặn Job không hợp lệ/ngừng tuyển, không có CV hợp lệ, hoặc trên 100 CV trước khi gửi HTTP. Trả nguyên kết quả từng CV (`Created`, `AlreadyExists`, `CvNotFound`) và `ApplicationId` cho bước điều phối sau này.

`ApplicationApiClient` đã sẵn sàng cho bước điều phối tiếp theo. B4 không tự gán khi đổi Job, tích checkbox hoặc upload; chưa nối nút **Phân tích hồ sơ** với POST. Nút và thẻ phân tích giữ trạng thái B3, vì luồng đó thuộc B6. Không thêm AI, tiến độ, backend A-runs hoặc thông báo lỗi upload B5.

## Giao diện và ảnh tham chiếu

- Figma: https://www.figma.com/design/ghT1AR7GR0PLWsi6WqZemi/Project_GD?node-id=375-1982.
- Figma MCP không trả design context/screenshot vì tài khoản đã hết hạn mức Starter. Chưa xác minh trực tiếp node trong lần thực hiện B4.
- Đường dẫn được cung cấp `C:\TTCN\B3_B6_SmartCv(1).zip` không tồn tại. File có thật là `C:\TTCN\B3_B6_SmartCv.zip`.
- Chỉ giải nén và đọc hai ảnh `B3B6/anh-xem-truoc/the-tai-nhieu-ho-so.png` và `the-phan-tich-ho-so.png` trong thư mục riêng dưới `%TEMP%`, ngoài repository. Không lấy code hoặc ghi đè `Upload.razor` từ ZIP.
- Dùng lại `.job-hint`, `.skill-chip`, `.field-error` trong CSS B3. Không sửa CSS, layout, sidebar, topbar, banner, tabs, dropzone, bảng hoặc các nút hành động.

## Kiểm thử

```powershell
dotnet build SmartCv.slnx --no-restore -p:UseAppHost=false
dotnet test SmartCv.slnx --no-restore -p:UseAppHost=false --logger "console;verbosity=minimal"
```

- Toàn bộ suite: **160 passed, 0 failed, 0 skipped** (149 test hiện có + 11 test B4).
- Test UI dispatch sự kiện Blazor thật: đổi Job cập nhật kỹ năng/GPA/kinh nghiệm, bỏ chọn xóa yêu cầu, giữ nguyên CV và checkbox, loại Job đóng, danh sách rỗng, API mất kết nối và thử lại.
- Test client: payload A8 đúng Job và CV, lọc CV lỗi/đang tải/chưa chọn/mã rỗng, loại trùng, giới hạn 100, lỗi HTTP, hủy request và giữ nguyên kết quả thành công một phần.
- Test tích hợp: GET Job thật → upload A6 thật → gán A8 → gán lại cùng Job/CV nhận `AlreadyExists` với cùng `ApplicationId`, chỉ có một bản ghi.
- API chạy bằng TestServer với database EF InMemory riêng và lưu file vào thư mục tạm. Không POST vào SQL Server thật; không đổi connection string, `SmartCvDb_A5Test` hoặc `Uploads/CV-test`.
- Sandbox chặn Windows Event Log của TestServer; bộ test đã được chạy lại ngoài sandbox với quyền được phê duyệt.
- `UseAppHost=false` tránh ghi đè executable Web đang được tiến trình khác sử dụng; không dừng tiến trình của người dùng.

## Kiểm tra trình duyệt còn lại

Chưa kiểm thử trình duyệt thật hoặc đối chiếu screenshot của bản chạy. Cần kiểm tra desktop/mobile, kéo thả/chọn nhiều file, checkbox/chọn tất cả, menu ba chấm, mở CV gốc, xóa dòng, Hủy, chuyển tabs và đổi Job khi danh sách có CV. Các thành phần B3 này được giữ nguyên; test tự động không thay thế kiểm tra thao tác và hình ảnh trong trình duyệt.
