# A58 — Google Login / Link / Unlink — Báo cáo để tự nghiệm thu

- Nhánh bắt buộc: `feature/a58-google-login`.
- Baseline: `main` đã merge A57.
- Phạm vi: API Google OIDC / transaction / audit / email. **Không có UI B55, LinkedIn A59 hoặc Google access token storage.**
- DB: dùng schema A51 đã có `UserExternalLogins` và `UserTokens`; không cần migration.
- Source đã chuẩn bị: code 5 file mới trong `src` + 5 test files + báo cáo; sửa 9 file hiện có qua script.
- **Trạng thái kiểm thử:** CHƯA CHẠY `dotnet build` / SQL thật / Google thật. Điền kết quả thực tế trước khi commit.

## Kết quả cần điền

| Hạng mục | PASS/FAIL/SKIP | Ghi chú ngắn, không secret/email/token |
|---|---|---|
| Dotnet 10 restore/build | Chưa chạy | |
| A58DecisionTests bảng 8.4 | Chưa chạy | |
| A58ExchangeTests code 60s, one-time | Chưa chạy | |
| A58LinkTests và EndpointTests | Chưa chạy | |
| Regression A27/A29/A56/A57 | Chưa chạy | |
| OAuth Google thật lần đầu | Chưa chạy | |
| Google login lần 2 cùng user | Chưa chạy | |
| Trùng email => link_required | Chưa chạy | |
| Admin/khóa/thiếu email_verified | Chưa chạy | |
| Link bằng ticket SignedIn, hủy khi có password | Chưa chạy | |
| Không thể hủy phương thức cuối | Chưa chạy | |
| API JWT cũ vẫn hoạt động sau unlink | Chưa chạy | |
| 2 complete đồng thời cùng sub trên SQL test catalog | Chưa chạy | Bắt buộc SQL opt-in riêng |
| Return URL injection/correlation/callback cancellation | Chưa chạy | |
| Audit failurecode và email notification outbox | Chưa chạy | |
| Production redirect URI và secret guard | Chưa chạy | |

## Chú ý bắt buộc

1. Chưa thể gọi đây là bản build PASS cho tới khi chạy trên máy đã cài .NET SDK 10 và SQL Server.
2. Test OIDC thực tế cần OAuth Client do người dùng tạo; Google Client Secret không có trong ZIP hay báo cáo.
3. `#code` trong fragment, `state`/`nonce`/PKCE do middleware quản lý. Không lưu Google token.
4. Không tự động liên kết theo email; `UserExternalLogins` dùng `(Provider, sub)` làm định danh.
5. Đừng commit file Development chứa Client Secret, user secrets, JWT hay email thật.
6. Test SQL concurrency cần catalog thử nghiệm riêng, không xóa hoặc reset `SmartCvDb`.
7. Chức năng hiển thị Google ở Login và `/auth/callback` nằm ở B55; hiện A58 chỉ cung cấp backend.
