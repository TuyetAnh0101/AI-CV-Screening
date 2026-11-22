# B54 — Báo cáo triển khai giao diện quên/đặt lại mật khẩu

**Nhánh mục tiêu:** `feature/b54-password-reset-ui`  
**Phạm vi:** Frontend Blazor Interactive Server, API client, unit/HTTP contract tests, tài liệu.  
**Phụ thuộc:** B53 và A57 đã nằm trên `main` (không phải chỉ có branch chưa merge).  
**Tình trạng:** Bộ mã được chuẩn bị và kiểm tra tính đầy đủ bằng công cụ dry-run. Người triển khai phải chạy build, test .NET và E2E trên máy có SQL Server; chưa được ghi là PASS khi chưa có log thực tế.

## A. Nghiệp vụ đã triển khai

1. `/login`: thay nhãn “sắp hỗ trợ” thành liên kết thật “Quên mật khẩu?” và xử lý `/login?reset=1` bằng notice thành công.
2. `/forgot-password`: email định dạng hợp lệ → POST JSON không Bearer → HTTP 202 chỉ hiện thông báo chung; không biết tài khoản tồn tại/đã khóa. Khóa gửi lại 60 giây, tôn trọng 429 Retry-After nếu lâu hơn; tránh gửi trùng khi pending. Với lỗi mạng không tuyên bố email đã gửi.
3. `/reset-password?token=`: nhận token lần đầu, `history.replaceState` xóa query trước khi cho phép gửi; không đưa token vào URL API hoặc log Web. Token chỉ giữ trong component của circuit; huỷ khi rời trang.
4. Mật khẩu mới + xác nhận dùng `PasswordInput`, luật `PasswordRules` (8–128 ký tự, chữ và số, không có khoảng trắng đầu/cuối). Backend kiểm tra thêm mật khẩu không trùng email và khác mật khẩu trước đó.
5. HTTP 400 `token_expired` → liên kết hết hạn; `token_invalid` → đã dùng/sai; 400 `validation_failed`/`errors.newPassword` → lỗi tại ô mật khẩu; 429 → khóa form tạm thời; network/5xx/JSON lỗi → kết quả chưa rõ, không tự thử lại.
6. Thành công reset → `/login?reset=1` với `replace:true`; màn hình Login có thông báo xác nhận. JWT cũ do A57 thay SecurityStamp sẽ bị từ chối ở request API kế tiếp bởi A29/A30, không cần frontend thu hồi thủ công từ tab khác.
7. UI giữ phong cách Deep Teal + AI Glow, `AuthLayout` B53, CSS isolation, responsive mobile, `aria-*`, focus điều hướng, Enter gửi form.

## B. Các file mới (đầy đủ mã)

| File | Vai trò |
|---|---|
| `src/SmartCv.Web/Helpers/PasswordRecoveryFlow.cs` | Trạng thái và validation/test của 2 form, phân loại lỗi API |
| `src/SmartCv.Web/Components/Pages/ForgotPassword.razor` | Trang quên mật khẩu |
| `src/SmartCv.Web/Components/Pages/ForgotPassword.razor.css` | Giao diện quên mật khẩu |
| `src/SmartCv.Web/Components/Pages/ResetPassword.razor` | Trang đặt lại, scrub token |
| `src/SmartCv.Web/Components/Pages/ResetPassword.razor.css` | Giao diện đặt lại, component mật khẩu |
| `tests/SmartCv.Tests/B54PasswordResetUiTests.cs` | Unit tests cho form, contract HTTP, JWT cũ 401 trong tab |
| `docs/B54-implementation-report.md` | Báo cáo và checklist nghiệm thu |

## C. Các file chỉnh sửa bằng `apply_b54.py`

| File | Thay đổi |
|---|---|
| `src/SmartCv.Web/Services/AuthApiClient.cs` | Thêm `IAuthRecoveryApi` + `ForgotPasswordAsync`, `ResetPasswordAsync` dùng A57; không phá `IAuthApi` của B27/B29 |
| `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` | Đăng ký DI `IAuthRecoveryApi` trỏ về `AuthApiClient` đã có, không gắn session |
| `src/SmartCv.Web/Components/Pages/Login.razor` | Liên kết `/forgot-password`, xử lý `reset=1` |
| `src/SmartCv.Web/Components/Pages/Login.razor.css` | Style cho liên kết |

**Tổng:** 7 file mới + 4 file chỉnh sửa, không có migrations/backend/schema changes.

## D. Bảng kiểm chứng (điền kết quả từ máy triển khai)

| Test | Kỳ vọng | Kết quả |
|---|---|---|
| `dotnet build SmartCv.slnx` | 0 lỗi | CHƯA CHẠY tại môi trường soạn kit |
| `dotnet test ... --filter FullyQualifiedName~B54` | Xanh | CHƯA CHẠY tại môi trường soạn kit |
| Full regression | Không có lỗi mới | CHƯA CHẠY |
| Email tồn tại, active → forgot | 202, có outbox ResetPassword | CHƯA CHẠY |
| Email không tồn tại/khóa → forgot | 202, cùng một thông báo, không outbox | CHƯA CHẠY |
| 3 email/giờ/account, 10 req/giờ/IP | Không lộ thông tin, khóa theo giới hạn | CHƯA CHẠY |
| Link hiệu lực, reset hợp lệ | 200, `/login?reset=1` | CHƯA CHẠY |
| Link 30 phút, đã dùng, sai purpose | `token_expired` / `token_invalid` | CHƯA CHẠY |
| Mật khẩu yếu/trùng email/trùng mật khẩu cũ | 400, hiển thị cạnh field | CHƯA CHẠY |
| Hai reset đồng thời cùng token | Chỉ một request thành công | CHƯA CHẠY, thuộc A57 SQL |
| Tab cũ: request API sau reset | 401, login?expired=1 | CHƯA CHẠY |
| Mobile 375px, desktop, tab/Enter, focus | Không tràn, điều hướng được | CHƯA CHẠY |
| Referrer/URL/token logs | Token không tồn tại trong URL sau JS; API dùng JSON body | CẦN kiểm tra thủ công |

## E. Ghi chú bảo mật/thử nghiệm

- Không dùng tài khoản thật cho automated tests nếu không cần; không commit Gmail App Password, mật khẩu DB, JWT, liên kết reset.
- URL gửi từ email **ban đầu có** `token` (không tránh được), B54 xóa token khi trang tương tác. Ở reverse proxy/access log, cần cấu hình không ghi raw query của `/reset-password` và triển khai `Referrer-Policy: no-referrer` ở response HTTP nếu đưa SCApp lên Internet.
- Blazor Interactive Server phải kết nối JS; nếu `history.replaceState` thất bại thì form không cho reset, tránh truyền token vào API trước khi xóa URL.
- Khi mạng lỗi sau khi nhấn “Đặt mật khẩu mới”, token có thể đã được dùng; ứng dụng không tự gửi lại, hướng dẫn thử login bằng mật khẩu mới hoặc yêu cầu liên kết mới.
- Test `Existing_tab_reacts_to_revoked_jwt_with_401` chỉ chứng minh UI handling đã có từ B28; kiểm chứng SecurityStamp thay đổi thật phải chạy A57 SQL/E2E hoặc luồng thực tế.

## F. Definition of Done

- [ ] Có đúng 7 file mới và 4 file chỉnh sửa, đúng nhánh B54.
- [ ] Build và focused B54 tests PASS, full regression không lỗi mới.
- [ ] API A57 thật + Email Mode=File (hoặc SMTP thật) chạy flow đầy đủ.
- [ ] Thử lỗi token đã dùng/hết hạn, 429, mật khẩu không đạt, responsive và bàn phím.
- [ ] Cập nhật báo cáo với số lượng test PASS/FAIL/SKIP, ảnh hoặc mô tả UI được kiểm tra, mã lỗi còn tồn tại.
- [ ] Gửi báo cáo và `git diff --stat` trước khi người dùng tự commit/push.
