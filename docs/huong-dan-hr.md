# Ghi chú HR và hồ sơ đã ẩn danh

- Chỉ dùng chức năng ẩn danh với quyền được giao trong môi trường đã được vận hành phê duyệt. Đây là thao tác xóa nội dung/file CV; không thể hoàn tác bằng đổi giai đoạn.
- A39 giữ điểm, giai đoạn, lịch sử quyết định và nhãn đánh giá để bảo toàn nghiệp vụ. Ghi chú trong `ApplicationEvents.Note`, `HrLabels.Note` và thông tin người ghi lịch sử vẫn được giữ.
- Không nhập tên, email, số điện thoại, địa chỉ, đường dẫn CV hoặc đoạn trích CV vào ghi chú HR. Viết nhận xét nghiệp vụ ngắn gọn, gắn với mã hồ sơ.
- Nếu ghi chú cũ chứa thông tin cá nhân, báo quản trị viên để xử lý theo quy trình riêng. Không tự UPDATE/DELETE lịch sử hoặc tắt trigger bảo vệ lịch sử.
- Khi API báo lỗi phục hồi, không gửi lại hàng loạt. Quản trị viên cần kiểm tra CvId, trạng thái SQL và bản phục hồi riêng trước khi thử lại đúng hồ sơ.
- Chế độ Ẩn danh trên giao diện B36 là chế độ xem giảm thiên kiến. A39 là xử lý xóa nội dung/file ở Backend; hai chức năng có tác động khác nhau.
- Tiến trình tự động A39 mặc định tắt. Chỉ bật sau nghiệm thu, kiểm tra storage/backup và phê duyệt của người vận hành.