# B3 – Giao diện tải lên nhiều CV

Nhánh: `feature/B3-cv-upload-ui` (tạo từ `origin/main` sau khi B2 #10 và A4–A8 #11–#15 đã merge).
Thiết kế: Figma **Project_GD**, node `375:1982` ("Gán CV vào Job"). Đã đối chiếu bằng ảnh chụp node qua Figma MCP.

## Mục tiêu

Trang `/upload`: kéo thả hoặc chọn nhiều CV, **tải lên ngay** qua API thật, quản lý danh sách CV (ô tích, menu ⋯, Hủy).
Bố cục giữ đúng Figma: sidebar SCApp → topbar → banner → 2 thẻ → Chọn công việc → ô kéo thả → Danh sách CV → Hủy / Phân tích hồ sơ.

## Thành phần

| File | Vai trò |
|---|---|
| `Components/Pages/Upload.razor` (+ `.css`) | Trang, luồng tải lên, chia nhóm request, chọn/bỏ chọn, Hủy |
| `Components/Upload/CvDropzone.razor` (+ `.css`) | Ô kéo thả: `<InputFile multiple>` trong suốt phủ kín ô, không dùng JavaScript |
| `Components/Upload/CvFileTable.razor` (+ `.css`) | Bảng 5 cột, ô "chọn tất cả", menu ⋯ (Xem file gốc, Xóa khỏi danh sách); 2 dòng cuối mở menu lên trên |
| `Components/Shared/StatusBadge.razor` (+ `.css`) | Nhãn trạng thái (Component/Badge) |
| `Models/UploadItem.cs` | Một dòng: trạng thái, CvId, đã chọn |
| `Helpers/StatusText.cs` | Chữ + màu trạng thái, định dạng dung lượng |
| `Helpers/UploadFlow.cs` | Chia request theo giới hạn API; đổi kết quả API thành trạng thái từng dòng |
| `Services/CvApiClient.cs` | Giới hạn dung lượng đọc từ cấu hình `CvUpload` (trước đây viết cứng) |
| `Components/Layout/NavMenu.razor`, `NavMenu.razor.css`, `MainLayout.razor.css`, `wwwroot/app.css`, `App.razor` | Tông xanh ngọc, font Inter, logo SCApp theo Figma. Giữ nguyên cấu trúc `MainLayout.razor` của B2 |
| `wwwroot/images/icons/*.svg` | `upload-cloud.svg`, `dots.svg`, `arrow-down.svg` |

## API sử dụng

- `POST /api/cvs/upload` (multipart, phần `files`): chỉ `Accepted` **và** có `CvId` mới hiện "Hoàn thành".
  `Rejected`, `Received` (chưa lưu), lỗi kết nối / HTTP đều hiện "Không thành công", không có CvId, không chọn được.
- `GET /api/cvs/{cvId}/file` (A6): "Xem file gốc" mở tab mới bằng CvId thật.
- Không gọi API xóa. "Xóa khỏi danh sách" và "Hủy" chỉ thao tác trên giao diện.

## Giới hạn tải lên thực tế

| | Figma | API (A4) | Web (B3) |
|---|---|---|---|
| CV mỗi lần chọn | 25 | – | **25** (`CvUpload:MaxFilesPerSelection`) |
| File mỗi request | – | **20** (`MaxFilesPerRequest`) | 20, tự chia: 25 file → 20 + 5 |
| Dung lượng / file | 2 MB | **2 MB** | 2 MB |
| Tổng / request | – | 2 MB × 20 + 1 MB = 41 MiB | chia nhóm không vượt mức này |
| Tần suất | – | 30 request/60 s mỗi IP, 4 đồng thời | gửi tuần tự từng nhóm |

Từ bản sửa `fix/B6-upload-run-start` (2026-11-10), dung lượng là 2 MB như Figma (trước đó giữ 10 MB).
`MaxFileSizeMB` phải giống nhau ở **cả** `src/SmartCv.Api/appsettings.json` và `src/SmartCv.Web/appsettings.json`.
Test `Web_upload_limits_match_api_config` báo lỗi nếu hai bên lệch nhau. Giá trị mặc định trong code
(`CvUploadOptions`, 10 MB) chỉ dùng khi thiếu cấu hình.

## Vòng đời IBrowserFile

File chọn trong Blazor Server chỉ đọc được khi `InputFile` chưa bị chọn lại. Vì vậy trang tải lên **ngay** khi chọn và
**khóa ô kéo thả** (`Disabled`) cho tới khi mọi nhóm xong. Lần chọn sau chỉ thêm dòng mới, không mất dòng cũ.
`GetMultipleFiles(1000)` để không bị lỗi mặc định 10 file; file thứ 26 trở đi hiện "Lỗi file", không gửi.
Bấm Hủy khi đang tải: danh sách xóa ngay, request đang chạy vẫn hoàn tất trên máy chủ (không hủy giữa chừng).

**Chỉ mở một file mỗi lúc** (`BrowserFileContent`). Trong Blazor Server, mỗi `OpenReadStream` mở ngay một luồng dữ liệu
trình duyệt → máy chủ trên cùng kết nối SignalR. Bản cũ mở stream của cả nhóm (tới 20 file) trước khi gửi; multipart chỉ đọc
từng file nên các stream còn lại dồn ứ, kết nối bị nghẽn, sau 1 phút Blazor ném `TimeoutException: Did not receive any data
in the allotted time` cho circuit → giao diện đứng ở "Đang tải lên" rồi hiện "Tải lại trang" (đo được: 25 file × 1,9 MB).
`BrowserFileContent` chỉ mở stream khi HttpClient gửi tới phần đó và đóng ngay khi gửi xong, không đọc cả file vào bộ nhớ
(cùng 25 file: xong trong khoảng 7,5 giây). Test `CvApiClient_opens_browser_files_one_at_a_time_and_only_while_sending` giữ điều này.

## Chạy

```powershell
dotnet run --project src/SmartCv.Api --launch-profile https
dotnet watch --project src/SmartCv.Web
```
Mở `/upload`. CV mẫu: `C:\TTCN\CV`.

## Kết quả kiểm thử (2026-11-07)

- `dotnet build`: 0 lỗi, 0 cảnh báo. `dotnet test`: 149/149 đạt (thêm `UploadUiTests`: chia 25 → 20 + 5, chia theo tổng dung lượng,
  Accepted/Rejected/Received, lỗi kết nối không thành "Hoàn thành", định dạng dung lượng, cấu hình Web khớp API).
- API thật (DB test `SmartCvDb_A5Test`, không dùng `SmartCvDb`): 2 CV mẫu → `Accepted` + CvId; `GET /api/cvs/{id}/file` → 200 PDF; 21 file/request → 400.
- Ảnh chụp trình duyệt (Edge headless) trang trống ở 1440 px: đúng thứ tự bố cục Figma.

| ID | Kết quả |
|---|---|
| T01 | PASS (ảnh chụp 1440 px) |
| T02, T03, T04, T06, T07, T08, T10, T11 | CHƯA KIỂM CHỨNG trên trình duyệt (máy không có Node.js để điều khiển trình duyệt) |
| T05 | PASS ở mức API + test tích hợp; chưa thao tác trên giao diện |
| T09 | PASS ở mức API (`/file` trả đúng PDF); chưa bấm menu trên giao diện |
| T12 | PASS ở mức unit test chia nhóm + API từ chối 21 file; chưa thao tác 25 file trên giao diện |
| T13 | PASS ở mức unit test (`Connection_error_never_marks_files_completed`) |
| T14 | CHƯA KIỂM CHỨNG đầy đủ: ảnh 390 px của Edge headless bị tràn ngang, có thể do cửa sổ headless có bề rộng tối thiểu ~500 px |
| T15 | PASS về thứ tự / vị trí khu vực so với ảnh Figma |

Kiểm tra thủ công: làm lần lượt T02–T14 trong yêu cầu B3 với CV trong `C:\TTCN\CV`.

## Chưa làm (thuộc nhiệm vụ khác)

- **B4**: ô "Chọn công việc" mới giữ vị trí, đang khóa; chưa gọi `GET /api/jobs`, `POST /api/applications`.
- **B5**: thông báo lỗi chi tiết theo `ErrorCode`, xử lý trùng ("Đã có sẵn" cần API trả CvId cũ – hiện A6 không trả).
- **B6**: nút "Phân tích hồ sơ" đang khóa; thẻ "Phân tích hồ sơ" chỉ có dòng giữ chỗ. Chưa `POST /api/runs`, chưa polling.
- Topbar (tìm kiếm, chuông, tài khoản) và các mục menu chưa có trang: ngoài phạm vi (M1, M6).
