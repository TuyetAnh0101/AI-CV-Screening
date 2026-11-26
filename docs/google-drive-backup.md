# Kết nối Google Drive cho sao lưu SCApp

Ứng dụng chỉ xin quyền `https://www.googleapis.com/auth/drive.file`: **chỉ thấy và sửa tệp do chính nó tạo**, không đọc được tệp khác trong Drive. Không gửi Client Secret, refresh token, mật khẩu Google hay khóa mã hóa cho ai (kể cả qua chat).

## 1. Dự án Google Cloud

SCApp đã dùng một OAuth Client cho "Đăng nhập bằng Google" (`Authentication:Google:ClientId/ClientSecret` trong user secrets của API). Nên dùng **cùng dự án** đó.

1. Mở <https://console.cloud.google.com/> bằng tài khoản quản lý dự án; chọn dự án ở thanh trên cùng.
2. **APIs & Services → Library**, tìm **Google Drive API** → **Enable**.

## 2. Màn hình đồng ý (Google Auth Platform)

1. **Google Auth Platform → Branding**: tên ứng dụng, email hỗ trợ, email nhà phát triển.
2. **Audience**: *External*. **Publishing status phải là “In production”**: nếu để *Testing*, refresh token hết hạn sau 7 ngày và sao lưu tự động sẽ dừng. Quyền `drive.file` thuộc nhóm không nhạy cảm nên công bố không yêu cầu Google xét duyệt.
3. **Data Access → Add or remove scopes**: thêm `.../auth/drive.file`.

## 3. OAuth Client

1. **Google Auth Platform → Clients**: mở client loại **Web application** đang dùng cho đăng nhập (hoặc **Create client** → Web application).
2. **Authorized redirect URIs → Add URI**, đúng từng ký tự:

   ```text
   https://localhost:7269/api/admin/backups/drive/callback
   ```

   Nếu API chạy ở địa chỉ khác, dùng `<địa chỉ API>/api/admin/backups/drive/callback`. Giữ nguyên URI `signin-google` đã có. Bấm **Save**.

## 4. Cấu hình trên máy chủ

Dùng chung client đăng nhập: **không cần thêm gì** (ứng dụng tự dùng `Authentication:Google:*`). Muốn client riêng cho sao lưu, bạn tự nhập trong terminal (không dán vào chat):

```powershell
dotnet user-secrets set "Backup:GoogleDrive:ClientId" "<client id>" --project src/SmartCv.Api
dotnet user-secrets set "Backup:GoogleDrive:ClientSecret" "<client secret>" --project src/SmartCv.Api
```

Tùy chọn: `Backup:GoogleDrive:RedirectUri` (khi API sau proxy), `Backup:GoogleDrive:FolderName` (mặc định `SCApp Backups`). Cần `App:WebBaseUrl` để quay lại trang web sau khi đồng ý (đã cấu hình). Khởi động lại API.

## 5. Kết nối và kiểm tra

1. Đăng nhập SCApp bằng tài khoản **Admin** → **Sao lưu hệ thống** → tab **Google Drive** → **Kết nối Google Drive**.
2. Chọn tài khoản Google sẽ chứa bản sao lưu (nên là tài khoản riêng của tổ chức) và đồng ý.
3. Trang quay lại với “Đã kết nối”. Bấm **Kiểm tra kết nối** → “Hoạt động”.
4. Trong Google Drive xuất hiện thư mục **SCApp Backups**. Không đổi tên/xóa tệp trong đó bằng tay.

Thu hồi: **Ngắt kết nối** trên web, hoặc <https://myaccount.google.com/permissions>. Tệp đã tải lên vẫn còn trên Drive.

## 6. Token hết hạn hoặc mất quyền truy cập

Dấu hiệu: trang Sao lưu báo **chặn** "Quyền truy cập Google Drive đã hết hiệu lực…", công việc lỗi `A62_DRIVE_AUTH`, bước **Google Drive** trong chuỗi trạng thái chuyển đỏ. Worker kiểm tra lại mỗi 6 giờ (mỗi 15 phút khi đang lỗi), và ngay sau một công việc lỗi Drive.

Nguyên nhân thường gặp: ứng dụng còn ở **Testing** (token sống 7 ngày); bạn thu hồi quyền trong tài khoản Google; đổi mật khẩu/chính sách bảo mật tài khoản; token không được dùng trong 6 tháng; Client Secret bị đổi.

Cách xử lý:

1. **Google Auth Platform → Audience → Publish app** (chuyển sang *In production*). Chỉ với quyền `drive.file` thì không cần xét duyệt. Token cấp lúc còn Testing **vẫn** hết hạn sau 7 ngày, nên phải làm bước 2.
2. Trên web: tab Google Drive → **Kết nối lại**, rồi **Kiểm tra kết nối**.
3. Các transaction log đang chờ sẽ được tải ở lần chạy kế tiếp; nếu lịch đã lỡ, bấm **Sao lưu ngay**.

Không mở rộng sang quyền `https://www.googleapis.com/auth/drive`; không cần thiết.

## 7. Đổi tài khoản Google hoặc OAuth Client

Quyền `drive.file` gắn với **client** và **tài khoản**: đổi một trong hai thì ứng dụng không còn thấy các bản cũ (Kiểm tra toàn vẹn sẽ báo thiếu tệp). Giữ nguyên cả hai nếu còn muốn khôi phục bản cũ; ghi lại tên dự án Google Cloud và Client ID ở nơi dùng cho khôi phục sự cố.
