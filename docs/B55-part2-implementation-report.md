# B55 Part 2 — Báo cáo triển khai / nghiệm thu

- Nhánh: `feature/b55-account-and-external-login-part2`
- Phụ thuộc: B55 Part1 + A58 đã merge; A59 GitHub chỉ bật khi API thực sự enabled.
- Phạm vi: `/auth/callback`, Google social login, login methods, link/unlink, error mapping.
- Không sửa API A57/A58, database, OAuth secret.

## Đã có mã nguồn

1. Social buttons theo `registration-settings.providers` (không hardcode provider enabled).
2. Google URL trên API origin 7269; GitHub chỉ khi A59 public enabled.
3. Callback JS `readAndClearFragment` xóa hash trước POST exchange; dùng lại `AuthSession.SignInAsync`.
4. Link-ticket, get logins và unlink có Bearer circuit; `last_login_method` có hướng dẫn.
5. AuthText ánh xạ mã lỗi, cancelled dạng thông báo nhẹ; account linked banner.
6. Test URL redirects chống open redirect và HTTP exchange JSON.

## Kiểm thử — CHƯA CHẠY TRÊN MÁY CỦA NGƯỜI DÙNG

- [ ] `dotnet build` PASS
- [ ] `dotnet test --filter FullyQualifiedName~B55` PASS
- [ ] Google login thật mới/lần 2 → dashboard, JWT hợp lệ
- [ ] Trùng email báo `link_required`, không gộp
- [ ] Link/unlink/409 last method
- [ ] Sau callback xóa fragment; refresh không exchange lại
- [ ] Provider false → không có nút
- [ ] 390×844, keyboard/a11y, regression login/logout/B29/B54
- [ ] A59 GitHub: được nghiệm thu riêng, hiện chưa xác minh.

## Known dependencies

- A58 đã được kiểm thử focused 30/31 ban đầu, user đang khắc phục test 1; phải có PASS 31/31 và Google OAuth thật.
- Nút GitHub chờ A59 backend thực tế. Không quảng cáo đã hoàn thành.
