namespace SmartCv.Web.Services;

/// <summary>
/// B24: trạng thái dùng chung của trang Ứng viên trong MỘT phiên (scoped theo circuit, không lưu trình duyệt, không ghi database).
///   · Từ khóa tìm người chỉ giữ ở đây (không đưa tên / email / số lên địa chỉ trang).
///   · Địa chỉ gần nhất của mỗi chế độ để chuyển tab / quay lại từ trang chi tiết không mất bộ lọc.
///   · DataVersion tăng sau mỗi thao tác ghi thành công (gắn CV, tạo, sửa, gộp, xóa) → chế độ kia biết phải tải lại
///     khi được mở, thay vì tự đồng bộ bằng lệnh ghi thừa hoặc tải trùng mỗi lần chuyển tab.
/// </summary>
public sealed class CandidateHubState
{
    public string PeopleSearch { get; set; } = "";
    public string PeopleUrl { get; set; } = "/candidates";
    public string CvsUrl { get; set; } = "/candidates?view=cvs";
    public int DataVersion { get; private set; }
    public event Action? Changed;

    public void NotifyDataChanged() { DataVersion++; Changed?.Invoke(); }
}
