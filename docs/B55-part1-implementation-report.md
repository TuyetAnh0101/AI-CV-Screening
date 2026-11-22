# B55 Part 1 — Báo cáo triển khai / nghiệm thu

- Nhánh: `feature/b55-account-and-external-login-part1`
- Phụ thuộc: A57, A54, B54 đã merge
- Phạm vi: `/account`, ProfileCard, SecurityCard, WorkspaceCard, UserMenu, API clients
- Không thay đổi API / migration / OAuth

## Đã có mã nguồn

1. Trang Account chính sách SignedIn, có 3 thẻ tương thích MainLayout.
2. `GET/PUT /api/auth/me/profile`, cập nhật `AuthSession.RefreshMeAsync` và `UserMenu`.
3. Có mật khẩu: `/change-password?returnUrl=/account`; chưa có: `POST /api/auth/me/password-setup`.
4. `GET /api/workspace/` hiển thị quota và xử lý 403.
5. UI Deep Teal + AI Glow tinh gọn, 390px responsive, focus visible, thông báo lỗi có ngữ nghĩa.
6. Test HTTP contract và validate tên.

## Kiểm thử — CHƯA CHẠY TRÊN MÁY CỦA NGƯỜI DÙNG

- [ ] `dotnet build` PASS
- [ ] `dotnet test --filter FullyQualifiedName~B55` PASS
- [ ] HR sửa tên, UserMenu đổi tên ngay
- [ ] Dữ liệu lỗi/thiếu giữ nguyên tên nhập
- [ ] Email setup được EmailWorker gửi thật
- [ ] Admin system scope xử lý workspace 403
- [ ] 390×844, bàn phím, regression B29

## Known dependencies

- EmailWorker bị SQL 650 trong log A58 trước đó: cần khắc phục riêng nếu tái diễn.
- Không đánh dấu PASS trước khi thực sự kiểm chứng.
