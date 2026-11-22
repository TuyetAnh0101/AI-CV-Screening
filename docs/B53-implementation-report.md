# B53 — Báo cáo triển khai giao diện đăng ký / xác minh email (mẫu nghiệm thu)

**Nhánh**: `feature/b53-register-verify-ui`
**Đối chiếu**: `main` có A56; .NET 10 / Blazor Interactive Server / API HTTP / SQL Server / A55 Outbox

> **Trạng thái**: Mã nguồn được chuẩn bị và kiểm tra tĩnh/điểm chèn trong bộ kit. Chưa khẳng định dotnet build, SQL integration hoặc browser E2E PASS trên máy người dùng. Điền kết quả thật sau khi chạy.

## 1. Phạm vi hoàn thành trong mã

- `/register`: kiểm tra feature flag và password rules từ GET `/api/auth/registration-settings`, có form họ tên/email/password/confirm/acceptTerms, validation chung `RegistrationRules`, aria-invalid/aria-describedby/role=alert, submit một lần, 400 map trường, 404 ẩn form, 429 đếm ngược, 202 sang sent.
- `/verify-email/sent`: email chỉ hiển thị dạng che; email đầy đủ giữ tại scoped circuit service; nếu refresh/mở tab khác cần nhập lại email để gửi lại; 60 giây cooldown, lỗi 429 và 404.
- `/verify-email`: lấy token từ query, xóa khỏi URL bằng `history.replaceState` trước POST, xử lý token_expired/token_invalid/429/503, gọi `AuthSession.SignInAsync` sau 200 và điều hướng `/dashboard?welcome=1`.
- `/login`: phân biệt 403 `email_not_verified` sau khi nhập đúng mật khẩu; nút gửi lại gọi API thật, không làm thay đổi 401 sai mật khẩu, không có đường dẫn quên mật khẩu giả khi B54 chưa tồn tại.
- UI Deep Teal + AI Glow, token CSS sẵn, nút 44-47px, responsive 390x844, giảm chuyển động nếu người dùng bật reduced motion.
- Test helper và HttpClient B53 có trong `tests/SmartCv.Tests/B53RegisterUiTests.cs`.

## 2. Danh mục file

**Sửa có patch kiểm tra anchor:**

- `src/SmartCv.Web/Services/AuthApiClient.cs`
- `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs`
- `src/SmartCv.Web/Services/PublicSettingsClient.cs`
- `src/SmartCv.Web/Helpers/AuthText.cs`
- `src/SmartCv.Web/Helpers/LoginForm.cs`
- `src/SmartCv.Web/Components/Pages/Login.razor`
- `src/SmartCv.Web/Components/Pages/Login.razor.css`
- `src/SmartCv.Web/Components/Layout/AuthLayout.razor`

**Thêm mới — nội dung nằm nguyên vẹn trong bộ kit:**

- `src/SmartCv.Web/Services/RegistrationFlowState.cs`
- `src/SmartCv.Web/Helpers/RegisterForm.cs`
- `src/SmartCv.Web/Helpers/VerificationFlow.cs`
- `src/SmartCv.Web/Components/Pages/Register.razor` + `.razor.css`
- `src/SmartCv.Web/Components/Pages/VerifyEmailSent.razor` + `.razor.css`
- `src/SmartCv.Web/Components/Pages/VerifyEmail.razor` + `.razor.css`
- `tests/SmartCv.Tests/B53RegisterUiTests.cs`
- `docs/B53-implementation-report.md`

## 3. Kiểm thử (cần ghi log thật)

| Nhóm | Kịch bản | Kết quả | Ghi chú/chứng cứ |
|---|---|---|---|
| Build | `dotnet build SmartCv.slnx` | CHƯA CHẠY | |
| Unit B53 | `dotnet test ... --filter FullyQualifiedName~B53` | CHƯA CHẠY | |
| Regression | `dotnet test SmartCv.slnx` | CHƯA CHẠY | |
| Happy path | Register → .eml → Verify → JWT → Dashboard | CHƯA CHẠY | |
| Validation | Họ tên/email/password/confirm/terms | CHƯA CHẠY | |
| API 400 | `errors.email` hiển thị ô email | CHƯA CHẠY | |
| API 429 | Đếm ngược Retry-After | CHƯA CHẠY | |
| API 404 | registration_disabled | CHƯA CHẠY | |
| Token expired | Nhập email và resend | CHƯA CHẠY | |
| Token invalid | Thông báo + login | CHƯA CHẠY | |
| Workspace 503 | Thử lại bằng token trong bộ nhớ | CHƯA CHẠY | |
| Login 403 | email_not_verified → resend | CHƯA CHẠY | |
| A11y | Chỉ phím, role alert, focus lỗi | CHƯA CHẠY | |
| Responsive | 390x844, tablet, desktop | CHƯA CHẠY | |
| Isolation | JWT `smartcv_ws`, không thấy dữ liệu Internal | CHƯA CHẠY | |

## 4. Điều kiện merge

- B51 và A56 đã merge `main`.
- Mọi build/test đạt, test browser riêng qua API thật ở `Email:Mode=File`.
- Team duyệt **nội dung Terms/Privacy còn là dự thảo** trước khi bật public register Production.
- Không commit thông tin cá nhân, API keys, App Password, token, `.eml`, file CV thật.
- Không tự merge hoặc push.

## 5. Giới hạn thiết kế có chủ đích

- B54 quên mật khẩu, B55 Google, B56 onboarding không nằm trong B53, do vậy không tạo các liên kết rỗng.
- Circuit refresh/tab mới mất địa chỉ email gốc (không lưu dữ liệu cá nhân vào query); người dùng nhập lại để gửi email.
- `history.replaceState` xóa token khỏi thanh địa chỉ sau khi Blazor interactive; **không thể bảo đảm token chưa từng đi vào HTTP request ban đầu hoặc access logs của Web/reverse proxy**. Bật kiểm soát log URL truy cập trang xác minh trên Production.
- Token chỉ được gửi đến POST API trong JSON body; không ghi nó vào log frontend.
