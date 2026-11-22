# A57 — Báo cáo triển khai / checklist nghiệm thu

- Nhánh riêng: `feature/a57-password-reset-profile`
- Phạm vi: backend-only (5 endpoint); UI thuộc B54 / B55.
- Trạng thái: **MÃ NGUỒN ĐÃ SOẠN, CHƯA BUILD/TEST TRÊN MÁY NGƯỜI DÙNG**.
- Baseline: GitHub `main` SCApp ngày 22/11/2026, phải có A55 + A56.

## Hợp đồng API

| Endpoint | Quyền | Trả thành công | Nội dung lỗi |
| --- | --- | --- | --- |
| POST `/api/auth/forgot-password` | Anonymous, IP 10/giờ | 202 + thông báo chung | 429 / 415 / 400 JSON / 503 |
| POST `/api/auth/reset-password` | Anonymous, public verify limiter | 200 + `{message}` | `token_invalid`, `token_expired`, `validation_failed`, 429, 503 |
| GET `/api/auth/me/profile` | SignedIn | 200 `ProfileDto` | 401 / 503 |
| PUT `/api/auth/me/profile` | SignedIn | 200 `ProfileDto` | 400 `fullName`, 409 `account_conflict`, 401 |
| POST `/api/auth/me/password-setup` | SignedIn | 202 + thông báo chung | 409 `password_already_set`, 401 / 503 |

`POST /api/auth/change-password` A30 được bổ sung `409 no_password_set` cho user chưa có mật khẩu. **Không thay đổi** admin reset `/api/users/{id}/reset-password`.

## Bảo mật / tính đúng nghiệp vụ

1. Không tiết lộ tồn tại của email: forgot trả cùng mã 202 và cùng body cho email sai/chưa có/bị khóa/đang hoạt động trong điều kiện dịch vụ ổn định; không log email hoặc link.
2. Throttling: 10 yêu cầu mỗi giờ trên một IP với policy in-memory/mỗi node; 3 email `ResetPassword`/giờ/user đếm ở SQL EmailOutbox trong `AccountWriteCoordinator`. Các API instance cần edge/distributed limiter cho IP.
3. `UserTokenService`: token 32 bytes CSPRNG, SHA-256 trong DB, raw chỉ trong email outbox đã mã hóa bằng Data Protection; `PasswordReset` phân biệt `EmailVerification`.
4. `ResetAsync` cập nhật PasswordHash, SecurityStamp, MustChangePassword=false, EmailVerifiedAt nếu null, gửi email PasswordChanged và ghi audit trong cùng transaction; invalidate session cache sau commit.
5. Hai request đồng thời dùng token: `ConsumeAsync` cập nhật có điều kiện; đúng một request thành công. Nếu một thao tác thất bại trước commit, transaction rollback.
6. `GET/PUT profile`: identity lấy từ JWT đã xác minh, không nhận userId từ client. PUT dùng coordinator/conditional update. Không đổi email, quyền, workspace, hash hay SecurityStamp.
7. Hỗ trợ tài khoản OAuth-only: `hasPassword=false` → `/me/password-setup` tạo link cho email hiện tại. `change-password` → 409 `no_password_set`.
8. Mẫu email ResetPassword bổ sung lời giải thích cho người có tài khoản provider mà chưa có password.
9. Audit `auth.forgot_password` là sự kiện không định danh; `auth.reset_password` và `account.profile_updated` là sự kiện đã xác nhận người dùng. `SafeSummary` tĩnh trong catalog; không ghi email/mật khẩu/token.
10. Không tạo migrations, không thay schema.

## Danh sách file

Mới (8):
- `src/SmartCv.Core/Contracts/AccountSelfServiceContracts.cs`
- `src/SmartCv.Api/Auth/AccountSelfServiceService.cs`
- `src/SmartCv.Api/Auth/ForgotPasswordRateLimitPolicy.cs`
- `src/SmartCv.Api/Endpoints/AccountSelfServiceEndpoints.cs`
- `tests/SmartCv.Tests/Registration/A57ResetTests.cs`
- `tests/SmartCv.Tests/Registration/A57ProfileTests.cs`
- `tests/SmartCv.Tests/Registration/A57SqlIntegrationTests.cs`
- `docs/A57-implementation-report.md`

Sửa (5):
- `src/SmartCv.Api/Endpoints/AuthEndpoints.cs`
- `src/SmartCv.Api/Auth/AuthSetup.cs`
- `src/SmartCv.Api/Auth/UserAccountService.cs`
- `src/SmartCv.Api/Auditing/AuditCatalog.cs`
- `src/SmartCv.Infrastructure/Email/EmailTemplates.cs`

Không sửa `AuthPasswordEndpoints.cs` hay `AccountWriteCoordinator.cs` — code A57 tái sử dụng nguyên cơ chế hiện hữu.

## Kiểm thử bắt buộc

### Trên máy cá nhân (chạy mặc định)

```powershell
# Cần build xanh trên máy có .NET 10
dotnet build SmartCv.slnx --verbosity minimal
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --filter "FullyQualifiedName~A57" --verbosity normal
dotnet test SmartCv.slnx --verbosity minimal
```

Các test mặc định gồm: 202 chung khi email không đúng/chưa có, token sai, password rules, không tiết lộ hash, GET/PUT profile, tên rỗng/quá dài, xung đột If-Match, user A không sửa user B.

### Test end-to-end thật trên SQL Server tách biệt (opt-in)

`A57SqlIntegrationTests` **SKIP** cho đến khi chính người dùng chuẩn bị catalog mới, rỗng và xác nhận tên theo mẫu `SmartCv_A57_Test_<32hex>`. Không được dùng `SmartCvDb`, `master`, hoặc database đã có dữ liệu. Test không tự tạo/drop catalog; chỉ chạy migrations và thêm dữ liệu giả trên catalog được cho phép.

Trong PowerShell, trước khi chạy, đặt đúng connection string dành riêng cho test (không lưu vào source control):

```powershell
$env:SMARTCV_A57_SQL_APPROVED='1'
$env:SMARTCV_A57_TEST_SQL_CONNECTION='Server=YOUR_SERVER;Database=SmartCv_A57_Test_0123456789abcdef0123456789abcdef;Trusted_Connection=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --filter "FullyQualifiedName~A57SqlIntegrationTests" --verbosity normal
Remove-Item Env:SMARTCV_A57_SQL_APPROVED,Env:SMARTCV_A57_TEST_SQL_CONNECTION
```

**Đổi `YOUR_SERVER` và GUID thành catalog test bạn đã tự tạo và kiểm chứng rỗng.** Test xác nhận: forgot không tiết lộ, per-email 3/giờ, purpose riêng, token hết hạn, mật khẩu yếu, hai reset song song một thành công, JWT cũ mất hiệu lực, mật khẩu cũ thất bại, email `PasswordChanged` trong outbox, setup mật khẩu cho tài khoản OAuth-only.

## Test thủ công API

1. Khởi động API ở môi trường Development, `Email:Mode=File` và worker đã xử lý được outbox. Chuẩn bị một tài khoản email kiểm thử **riêng**, không dùng tài khoản quản trị thật.
2. `POST /api/auth/forgot-password` với email thật/không có/đã khóa: cùng 202 thông báo chung.
3. Mở `.eml` lấy `/reset-password?token=...`; không chụp màn hình/log token.
4. `POST /api/auth/reset-password` với `{ "token": "...", "newPassword": "Strong9NewPass" }`; mong đợi 200.
5. Gọi `/api/auth/me` bằng JWT cũ: 401. Đăng nhập mật khẩu cũ: 401; mật khẩu mới: 200. Mở lại token: 400 `token_invalid`.
6. `GET /api/auth/me/profile` trả workspace hiện hành và danh sách provider không có `providerKey`.
7. `PUT /api/auth/me/profile` với `{ "fullName": "Tên mới" }`; GET `/api/auth/me` trả tên mới; người dùng khác không bị ảnh hưởng.
8. Tài khoản chưa có password: `/api/auth/change-password` phải trả `409 no_password_set`; `/api/auth/me/password-setup` trả 202.
9. Kiểm tra audit và EmailOutbox không chứa token/mật khẩu trong các cột hoặc log.

## Chưa thực hiện trên môi trường soạn thảo

- Chưa chạy .NET build/test vì môi trường soạn thảo không có .NET SDK.
- Chưa xác thực SMTP thật, SQL Server thật hoặc chạy browser UI.
- Khi build/test chưa xanh thì **KHÔNG commit, không tự merge**.
