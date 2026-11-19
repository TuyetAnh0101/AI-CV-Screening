# B24 – Trang Ứng viên thống nhất (quản lý ứng viên + Hồ sơ CV)

Ngày thực hiện: 19/11/2026 · Nhánh `feature/b24-candidate-management-ui` · Chưa stage / commit / push / PR.

## 1. Tài liệu trong ZIP đã đọc

ZIP `C:\TTCN\A23_B25_SmartCv.zip` có 108 mục (thư mục + file). Đã kiểm kê toàn bộ và đọc:

| Nhóm | File đã đọc |
|---|---|
| Hướng dẫn | `HUONG_DAN_A23_A24_A43_B23_B24_B25.md`: bảng phân công, thứ tự phụ thuộc, "Những chỗ mình phải tự quyết", Chuẩn bị chung, A24, A43, Trước khi làm (phần B), B23, **B24**, B25, Lỗi thường gặp |
| B24 | `Services/CandidateApiClient.cs`, `Helpers/CandidateText.cs`, `Components/Pages/CandidateDetail.razor`, `Components/Cvs/MergeCandidateDialog.razor`, `Components/Cvs/EraseDataDialog.razor`, `Components/Pages/Cvs.razor` (so sánh với bản B23 trong ZIP), `tests/.../CandidateTextTests.cs` |
| Đối chiếu | B23 (`Cvs.razor`, `CandidateApiClient.cs`), A24/A43 (`CandidateContracts.cs` trên `main`), `snippets/Program.A23-A43.cs.txt` (theo danh sách) |

Không chép đè file nào trong ZIP: mã trên `main` đã khác gói (B23 trên `main` là `CvLibrary.razor` + `Components/CvLibrary/*`, không phải `Cvs.razor` + `Components/Cvs/*`).

## 2. Hình ảnh / sơ đồ trong ZIP

**ZIP không có ảnh, sơ đồ hay mockup nào** (0 file .png/.jpg/.gif/.svg/.webp/.pdf/.bmp). Bố cục được thiết kế từ nội dung hướng dẫn và ngôn ngữ thiết kế SCApp hiện có.

## 3. Git

| Mục | Giá trị |
|---|---|
| Remote | `origin https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Kiểm tra trước | working tree sạch, không có thao tác Git dở dang; nhánh UX v2 cũ trùng nội dung với `main` (đã squash #80) |
| Nền | `origin/main` = `3c5c459fed7fad2ef8631c135f0f5c503d259215` (đã gồm B23 #79, A24 #77, A43 #78, UX Workflow V2 #80) |
| Nhánh | `feature/b24-candidate-management-ui` (tạo bằng `git switch --no-track -c … origin/main`) |
| HEAD | `3c5c459…` — **không có commit mới** |

## 4. Hạn chế của B23 và của mẫu B24 trong ZIP

- B23 có trang **Kho CV** (mỗi dòng một file). Mẫu B24 thêm trang chi tiết `/candidates/{id}` nhưng **không có danh sách người**: muốn tìm một người phải đi qua Kho CV rồi bấm tên. Kết quả là hai khu vực gần giống nhau, và người dùng không có điểm vào theo con người.
- CV chưa gắn người chỉ lẫn trong Kho CV; API không lọc được, nên một màn hình theo người sẽ "làm mất" các CV này.
- Mẫu B24: sửa liên hệ ngay trong thẻ; gộp chỉ một bước (chọn người giữ xong là bấm Gộp ngay); trang ứng viên **luôn hiện liên hệ kể cả khi bật Ẩn danh**; nút quay lại cố định về `cvs`.
- Mẫu B24 hiện "Điểm gần nhất" cạnh tên vị trí nhưng không nhóm theo CV. Khi một người có nhiều CV, không thấy rõ CV nào nộp vị trí nào.

## 5. Vì sao chọn một trang "Ứng viên" thống nhất

HR làm việc theo **con người** (ai, liên hệ, đã nộp gì) và theo **file** (CV nào chưa đọc chữ, chưa gắn người, sắp hết hạn lưu). Hai cách nhìn này dùng chung bộ lọc và thao tác, nên tôi để chung một trang với hai chế độ. Mỗi chế độ vẫn gọi đúng API sở hữu dữ liệu của nó (A24 cho người, A23 cho CV); **không có API gộp chung** và không tạo bản sao dữ liệu ứng viên ở hai nơi.

## 6. Luồng trước / sau

| Việc | Trước (B23) | Sau (B24) |
|---|---|---|
| Tìm một người | Kho CV → tìm → bấm tên (chưa có trang người) | Ứng viên → tìm tên/email/số → Xem hồ sơ |
| CV chưa gắn người | Lướt Kho CV | Dải nhắc "N CV chưa gắn ứng viên" ở chế độ người → mở Hồ sơ CV đã lọc "Chưa gắn" (lọc ở máy chủ) |
| Xem mọi lần ứng tuyển của một người | Không có | `/candidates/{id}`: từng CV → các vị trí, giai đoạn, điểm của lần ứng tuyển đó, dòng thời gian |
| Thêm người | Chỉ qua "Gắn ứng viên" cho CV | Thêm trực tiếp (nút "Thêm ứng viên"), có kiểm tra trùng |
| Gộp người trùng | Không có | Thẻ "Có thể là cùng một người" → chọn người giữ → xem hậu quả → tick xác nhận → Gộp vĩnh viễn |
| Yêu cầu dữ liệu (A43) | Không có giao diện | Xuất JSON; Xóa 2 bước (kiểm tra → gõ XÓA); nhật ký vẫn xem được sau khi xóa |

## 7. Route

| Địa chỉ | Nội dung |
|---|---|
| `/candidates` (`?page=N`) | Chế độ **Ứng viên** (mặc định). Từ khóa **không** ghi lên địa chỉ (có thể là tên/email/số) |
| `/candidates?view=cvs&…` | Chế độ **Hồ sơ CV**: toàn bộ bộ lọc B23 + `candidate=none|attached` trên địa chỉ (F5, Back/Forward, gửi link) |
| `/candidates/{id}` (`?from=cvs`) | Chi tiết ứng viên; `from=cvs` để nút quay lại về đúng chế độ và bộ lọc trước đó |
| `/cvs?…` (cũ) | Chuyển hướng **replace** sang `/candidates?view=cvs&…`, giữ nguyên mọi bộ lọc |

Menu: mục **"Kho CV" được thay bằng "Ứng viên"**, không để hai mục ngang hàng.

## 8. Component và state

| Thành phần | Vai trò |
|---|---|
| `Pages/Candidates.razor` | Khung trang: tiêu đề, Ẩn danh, Tải CV lên, 2 tab (link theo URL, `aria-current`). Chế độ đã mở được **giữ trong bộ nhớ** (ẩn bằng `hidden`) nên chuyển tab không tải lại |
| `Components/Candidates/CandidatePeopleView.razor` | Danh sách người: tìm chờ 400 ms (`Debouncer`), phân trang máy chủ, tổng số người, dải "CV chưa gắn", Thêm ứng viên |
| `Pages/CvLibrary.razor` (B23) | Thành chế độ Hồ sơ CV (bỏ `@page`), thêm bộ lọc "Gắn ứng viên", link tên → hồ sơ người, báo tổng cho tab |
| `Pages/CvsRedirect.razor` | `/cvs` cũ → chế độ Hồ sơ CV |
| `Pages/CandidateDetail.razor` | Hồ sơ người: 2 cột (CV & hồ sơ ứng tuyển · Liên hệ / Trùng / Yêu cầu dữ liệu), 1 cột trên mobile |
| `CandidateEditorDialog`, `MergeCandidateDialog`, `EraseDataDialog`, `RequestHistory`, `ModalShell` | Hộp thoại dùng `<dialog>` gốc (bẫy focus, Escape, trả focus về nút mở) |
| `Services/CandidateHubState` (scoped, theo phiên) | Từ khóa tìm người, địa chỉ gần nhất của mỗi chế độ, `DataVersion` tăng sau mỗi thao tác ghi thành công |
| `Helpers/CandidateWorkspaces.cs` | `CandidateListWorkspace`, `CandidateDetailWorkspace`: số thế hệ + hủy request cũ + timeout 30 giây |
| `Helpers/CandidateListFilter.cs`, `CandidateText.cs` | Hàm thuần (URL, kiểm tra, chữ hiển thị, xem trước gộp/xóa) |

## 9. Thao tác → API

| Thao tác | API |
|---|---|
| Danh sách người, tìm | `GET /api/candidates?search=&page=&pageSize=` |
| Đếm CV chưa gắn (dải nhắc) | `GET /api/cvs?candidate=none&page=1&pageSize=1` (chỉ đọc tổng số) |
| Chế độ Hồ sơ CV | `GET /api/cvs?…` (B23, thêm `candidate`) |
| Xem file | `GET /api/cvs/{id}/file` |
| Gắn / đổi người cho CV (B23) | `GET /api/cvs/{id}/candidate-suggestion`, `PUT /api/cvs/{id}/candidate` (NeedsDecision giữ nguyên) |
| Chi tiết người | `GET /api/candidates/{id}` (404 → `GET /api/data-requests?candidateId=` để phân biệt đã xóa / không tồn tại) |
| Thêm người | `POST /api/candidates` (409 + `matches` → chỉ gửi `allowDuplicate: true` sau khi HR bấm "Vẫn tạo người mới") |
| Sửa liên hệ | `PUT /api/candidates/{id}` (409 → "Vẫn lưu thay đổi") |
| Gộp | `POST /api/candidates/{nguồn}/merge { targetCandidateId, actor }` |
| Xuất dữ liệu | `GET /api/candidates/{id}/data-export?handledBy=` (thẻ `<a download>`) |
| Xóa dữ liệu | `POST /api/candidates/{id}/erase { confirm: true, handledBy, note }` |

## 10. Chống xung đột / race condition

- Mỗi danh sách có số thế hệ + `CancellationToken`: phản hồi cũ về muộn bị bỏ, request cũ bị hủy khi đổi từ khóa / trang / chế độ / route (có test).
- Chuyển tab không tải trùng: chế độ đã mở giữ nguyên; chỉ tải lại khi địa chỉ của chế độ đó đổi hoặc `DataVersion` tăng (sau khi gắn CV, tạo, sửa, gộp, xóa).
- **Không tự gửi lại** POST/PUT/merge/erase: kết quả không xác định (mất kết nối, quá thời gian, 5xx) hoặc 409/404 → khóa nút gửi, báo "tải lại để kiểm tra". Trang chi tiết tự tải lại và giữ dữ liệu cũ trên màn hình tới khi có dữ liệu mới.
- Chi tiết kiểm tra `CandidateId` trả về đúng người đang mở.
- Lỗi 400/401/403/404/409/410/429/5xx/timeout có chữ cố định, **không hiện `title`/`detail` thô của máy chủ**. Lỗi 400 chỉ đọc `errors` theo trường; 409 chỉ đọc `matches`.

## 11. CV chưa gắn người

- Thay đổi **ngoài Web duy nhất** của B24, có giới hạn: `CvListQuery` thêm tham số `candidate` (`none` | `attached`, giá trị lạ hoặc lặp → 400) và `CvEndpoints` lọc ở máy chủ.
  - `none` = `CandidateId IS NULL` **và** chưa ẩn danh hóa, vì CV đã ẩn danh hóa không gắn lại được.
  - Lý do: lọc trên trang đã phân trang sẽ sai số lượng.
  - Không đổi schema, không có migration.
- Dải nhắc ở chế độ người luôn cho biết số CV chưa gắn trên toàn kho và có lối tắt sang Hồ sơ CV đã lọc.
- Không bao giờ tự tạo người từ tên file hay chữ OCR; gợi ý từ CV chỉ điền sẵn và HR phải xác nhận (B23 giữ nguyên).

## 12. Tạo / sửa / gộp / xóa

- **Tạo / sửa:** dùng đúng `CandidateRules.Validate` của Core (họ tên bắt buộc; có email hoặc số; chuẩn hóa như API). Lỗi hiện dưới từng ô và không gọi API khi chưa hợp lệ. Khi trùng, hiện danh sách người kèm tiêu chí trùng, với 3 lựa chọn: mở hồ sơ người đó / sửa lại / vẫn lưu. Không tự gộp theo tên.
- **Gộp (2 bước):**
  1. Chọn người giữ, hai hồ sơ hiển thị cạnh nhau.
  2. Xem hậu quả theo đúng contract A24: CV của người bị gộp cùng mọi hồ sơ ứng tuyển, điểm, lịch sử chuyển sang người được giữ; liên hệ chỉ bổ sung khi người giữ đang thiếu; người bị gộp bị xóa; **không phải** xóa dữ liệu A43.

  Sau đó nhập người thực hiện, tick "đây là cùng một người" rồi bấm **Gộp vĩnh viễn**. Nếu người đang xem bị gộp vào người kia, trang chuyển sang người được giữ (replace) và không hiện lại người đã bị xóa.
- **Xóa dữ liệu A43 (2 bước, đúng mẫu ZIP B24):** bước 1 xem trước bằng `CandidateErasePlan` của Core và nhập người xử lý + ghi chú (nhắc không ghi thông tin cá nhân); bước 2 gõ "XÓA" (nhận cả "xóa", "XOA", "Xoá") mới bật nút đỏ. Xóa xong trang hiện "đã được xóa" kèm kết quả và nhật ký; mở lại link cũ vẫn thấy nhật ký.
- **Phạm vi:** theo hướng dẫn ZIP, xuất / xóa dữ liệu thuộc B24 (B25 là trang Bộ đánh giá). Vì vậy tôi triển khai đủ, có xác nhận 2 bước, không có luồng xóa giả.

## 13. Quyền riêng tư B36 / A39 / A43

- Dùng chung `AnonymityState`, không tạo state ẩn danh thứ hai. Khi bật Ẩn danh:
  - Danh sách người: "Ứng viên #id", không tên, chữ cái đầu, email hay số; `aria-label` của link cũng dùng bí danh.
  - Chi tiết: che tên, liên hệ, tên file, ghi chú trong dòng thời gian và trong nhật ký. **Khóa** Sửa / Gộp / Xuất / Xóa (cần đối chiếu danh tính).
  - **Khác mẫu ZIP:** mẫu "luôn hiện liên hệ"; tôi theo yêu cầu "không để PII trong DOM khi phải che".
- CV đã ẩn danh hóa (A39): hiện "Hồ sơ đã ẩn danh", không có Xem file / Gắn người, không thuộc bộ lọc "Chưa gắn".
- Không ghi từ khóa tìm người lên địa chỉ trang hay bộ nhớ trình duyệt (chỉ giữ trong phiên server). Bộ lọc CV giữ trên địa chỉ như B23 đã thống nhất.
- Development gate và bộ lọc lỗi A24 (`CandidateApiFilter`) không đổi.

## 14. File thay đổi

| TT | File | Mục đích |
|---|---|---|
| M | `src/SmartCv.Core/Cvs/CvListQuery.cs` | Tham số `candidate` (none/attached) |
| M | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` | Lọc CV chưa gắn / đã gắn ở máy chủ |
| M | `src/SmartCv.Web/Components/Pages/CvLibrary.razor`, `.razor.css` | Thành chế độ Hồ sơ CV; bộ lọc Gắn ứng viên; link người |
| M | `src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.js` | Focus dự phòng sau khi đóng (tiêu đề mới) |
| M | `src/SmartCv.Web/Components/Layout/NavMenu.razor` | "Kho CV" → "Ứng viên" |
| M | `src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor` | Link gợi ý `/cvs` → `/candidates?view=cvs` |
| M | `src/SmartCv.Web/Components/_Imports.razor` | `SmartCv.Web.Components.Candidates` |
| M | `src/SmartCv.Web/Helpers/CvLibraryFilter.cs` | `Candidate`, địa chỉ chuẩn mới |
| M | `src/SmartCv.Web/Services/CandidateApiClient.cs` | Danh sách người, chi tiết, tạo, sửa, gộp, xóa, nhật ký, link xuất; đọc 409/400 an toàn |
| M | `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` | Đăng ký `CandidateHubState` |
| M | `src/SmartCv.Web/wwwroot/app.css` | Kiểu dùng chung trong hộp thoại |
| M | `tests/SmartCv.Tests/B23CvLibraryTests.cs` | 3 test cập nhật theo route/menu/link mới (mục 16) |
| A | `src/SmartCv.Web/Components/Pages/Candidates.razor`, `.razor.css` | Trang Ứng viên 2 chế độ |
| A | `src/SmartCv.Web/Components/Pages/CandidateDetail.razor`, `.razor.css` | Hồ sơ ứng viên |
| A | `src/SmartCv.Web/Components/Pages/CvsRedirect.razor` | `/cvs` cũ |
| A | `src/SmartCv.Web/Components/Candidates/*` | Chế độ người, hộp thoại tạo/sửa, gộp, xóa, nhật ký, khung hộp thoại |
| A | `src/SmartCv.Web/Helpers/CandidateListFilter.cs`, `CandidateText.cs`, `CandidateWorkspaces.cs` | Logic thuần + workspace |
| A | `src/SmartCv.Web/Services/CandidateHubState.cs` | State theo phiên |
| A | `src/SmartCv.Web/wwwroot/js/modal-dialog.js` | Mở / đóng `<dialog>` |
| A | `tests/SmartCv.Tests/B24CandidateManagementTests.cs` | Test B24 |
| A | `docs/B24-implementation-report.md` | Báo cáo này |

Không có file bị xóa (D). Không có migration.

## 15. Khác với ZIP

1. Một trang Ứng viên 2 chế độ thay vì giữ "Kho CV" + trang chi tiết riêng; `/cvs` chuyển hướng.
2. Thêm danh sách người, tạo ứng viên và dải "CV chưa gắn" + bộ lọc máy chủ `candidate` (yêu cầu bổ sung của người dùng).
3. Ẩn danh che cả trang chi tiết (mẫu: luôn hiện liên hệ).
4. Gộp 2 bước có xác nhận cuối + người thực hiện (mẫu: 1 bước).
5. Sửa / tạo bằng hộp thoại; hộp thoại dùng `<dialog>` gốc thay cho `div` + backdrop.
6. Nhóm hồ sơ ứng tuyển theo CV, nói rõ điểm thuộc từng lần ứng tuyển; dòng thời gian thu gọn được.
7. Link xuất dùng `HttpClient.BaseAddress` thay cho `IConfiguration["ApiBaseUrl"]`; thư mục `Components/Candidates` thay cho `Components/Cvs`; giữ `CvLibrary` của B23 trên `main` thay vì chép `Cvs.razor` của ZIP.

## 16. Build và test

| Hạng mục | Kết quả |
|---|---|
| Baseline (trước khi sửa) `dotnet build -c Release` | 0 cảnh báo, 0 lỗi |
| Baseline test đầy đủ | **2765 đạt · 0 lỗi · 122 bỏ qua** |
| Build sau khi sửa (toàn solution, Release) | 0 cảnh báo, 0 lỗi |
| Test B24 mới + B23 + A23 + CvList + RunScoringWorker | 182 test: 179 đạt · 0 lỗi · 3 bỏ qua (SQL opt-in); B23 + B24 sau cùng 80/80 đạt |

Test cũ phải cập nhật, mỗi test đều vẫn giữ phạm vi kiểm chứng:
- `B23…URLs_round_trip…`: URL chuẩn của chế độ CV là `/candidates?view=cvs`; thêm kiểm tra chuyển hướng `/cvs?stage=…&page=…` giữ bộ lọc.
- `B23…Menu_adds_library…`: menu có `candidates` thay `cvs`; thêm kiểm tra **không** còn "Kho CV".
- `B23…Page_reads_real_A23_pipeline…`: trước đây khẳng định chưa có link `/candidates/`; nay khẳng định có link `?from=cvs` cho người đã gắn và vẫn không lộ dữ liệu của CV đã ẩn danh.

Test mới (`B24CandidateManagementTests`):
- Bộ lọc `candidate` (Core + API thật trên InMemory, 25/3/2 CV: đúng trên toàn kho, không phải một trang).
- URL / chế độ / chuyển hướng; chữ hiển thị và che dữ liệu; xem trước gộp/xóa theo `CandidateErasePlan`.
- API client: 409 `matches`, 400 `errors`, không lộ chữ thô, mất kết nối → không xác định và không gửi lại; body merge/erase; link xuất.
- Race: phản hồi cũ về muộn bị bỏ.
- Chi tiết: đã xóa / không tồn tại / lỗi 503 / API trả nhầm người.
- Giao diện:
  - danh sách người và dải "CV chưa gắn";
  - Ẩn danh không lộ tên / email / số;
  - chuyển tab không tải trùng, tải lại sau thao tác ghi;
  - tìm kiếm debounce và không ghi lên địa chỉ;
  - `/cvs` replace;
  - chi tiết một người 2 CV, một CV 2 vị trí;
  - chi tiết khi Ẩn danh;
  - đã xóa / không tồn tại;
  - tạo kèm trùng (`allowDuplicate` chỉ sau khi chọn rõ);
  - không gửi lại khi kết quả không xác định;
  - gộp cần chọn + xác nhận, 409 không gửi lại;
  - xóa cần người xử lý + "XÓA".

## 17. Full regression

- **Lần 1** (chạy song song lúc build harness): 2793 đạt · 1 lỗi · 122 bỏ qua. Lỗi duy nhất là `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`, test nhạy thời gian đã từng lỗi ở baseline các nhiệm vụ trước; chạy riêng thì đạt.
- **Lần cuối** (sạch, không chạy song song): **2794 đạt · 0 lỗi · 122 bỏ qua** (tổng 2916). Sau đó thêm 7 ca test xác nhận "XÓA" theo mẫu ZIP; nhóm B23 + B24 chạy lại **80/80 đạt**, nên tổng hiện tại là **2801 đạt · 0 lỗi · 122 bỏ qua**.
- 122 bỏ qua là các test SQL Server / demo opt-in có sẵn (không đặt biến môi trường; không tạo database).

## 18. Kiểm tra giao diện thật

- **Môi trường:** API thật trên Kestrel + InMemory với dữ liệu tổng hợp (15 người, 1 cặp trùng email, 1 người 3 CV gồm 1 CV đã ẩn danh hóa, 5 CV chưa gắn); Web bản Release; Chrome cài sẵn qua Playwright .NET. Không dùng SQL Server, không dùng CV thật.
- **Ở 1440 / 1024 / 768 / 390 / 360 / 320 px** (cả 3 màn hình Ứng viên, Hồ sơ CV, Chi tiết): **không tràn ngang, 0 lỗi console**.
- **Đã kiểm tra:**
  - tìm kiếm không ghi lên địa chỉ;
  - hộp thoại tạo: lỗi dưới ô, danh sách trùng, đóng bằng Escape;
  - Ẩn danh không lộ tên / email;
  - lọc "Chưa gắn" ra 5 CV; đổi tab rồi Back khôi phục đúng `candidate=none`;
  - `/cvs?stage=Rejected` → `/candidates?view=cvs&stage=Rejected`;
  - hộp thoại gộp 2 bước; xóa bị khóa với "XO", mở với "xóa";
  - lỗi số điện thoại khi sửa;
  - `/candidates/99999` → "Không có ứng viên #99999".
- **Đánh giá thẩm mỹ:** bố cục cân đối, tiêu đề 26px, nội dung 13–14px, badge 11–12px. Trên tablet/mobile danh sách chuyển thành thẻ, chi tiết thành một cột, hộp thoại vừa màn hình.
- **Lỗi phát hiện từ ảnh và đã sửa:**
  - cột điểm trong chi tiết quá hẹp (chữ gãy 3 dòng) → rộng hơn, nhãn mức xuống dòng riêng;
  - thẻ người ở 320px bị chật → 2 cột ở ≤ 380px;
  - câu gợi ý tìm kiếm chứa số mẫu "0912…" làm sai kiểm tra rò rỉ → bỏ số mẫu.
- **Ảnh:** `artifacts/b24-screens/` (bị `.gitignore`, không đưa vào commit), kèm `report.txt`.

## 19. Giới hạn chưa kiểm chứng

- Chưa thử với SQL Server thật cho bộ lọc `candidate` (mới thử trên InMemory). Biểu thức chỉ là `CandidateId IS NULL AND AnonymizedAt IS NULL`; không có truy vấn thô.
- Chưa thử thật Xuất JSON và Xóa trên API (cần SQL vì A43 dùng `ExecuteUpdate`); giao diện và body request đã có test.
- Chưa thử trình đọc màn hình thật (NVDA / JAWS); chỉ kiểm tra thuộc tính ARIA, focus và Escape.
- Tên trùng không phân biệt dấu ("Nguyen" không ra "Nguyễn") là quy tắc A24, không đổi.

## 20. `git status --short --branch`

```
## feature/b24-candidate-management-ui
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Core/Cvs/CvListQuery.cs
 M src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.js
 M src/SmartCv.Web/Components/Layout/NavMenu.razor
 M src/SmartCv.Web/Components/Pages/CvLibrary.razor
 M src/SmartCv.Web/Components/Pages/CvLibrary.razor.css
 M src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor
 M src/SmartCv.Web/Components/_Imports.razor
 M src/SmartCv.Web/Helpers/CvLibraryFilter.cs
 M src/SmartCv.Web/Services/CandidateApiClient.cs
 M src/SmartCv.Web/Services/ServiceCollectionExtensions.cs
 M src/SmartCv.Web/wwwroot/app.css
 M tests/SmartCv.Tests/B23CvLibraryTests.cs
?? docs/B24-implementation-report.md
?? src/SmartCv.Web/Components/Candidates/
?? src/SmartCv.Web/Components/Pages/CandidateDetail.razor
?? src/SmartCv.Web/Components/Pages/CandidateDetail.razor.css
?? src/SmartCv.Web/Components/Pages/Candidates.razor
?? src/SmartCv.Web/Components/Pages/Candidates.razor.css
?? src/SmartCv.Web/Components/Pages/CvsRedirect.razor
?? src/SmartCv.Web/Helpers/CandidateListFilter.cs
?? src/SmartCv.Web/Helpers/CandidateText.cs
?? src/SmartCv.Web/Helpers/CandidateWorkspaces.cs
?? src/SmartCv.Web/Services/CandidateHubState.cs
?? src/SmartCv.Web/wwwroot/js/modal-dialog.js
?? tests/SmartCv.Tests/B24CandidateManagementTests.cs
```

## 21. `git diff --check`

Không có lỗi khoảng trắng (exit 0). Chỉ có cảnh báo "LF will be replaced by CRLF" ở 4 file đã sửa (`CvLibrary.razor.css`, `_Imports.razor`, `CandidateApiClient.cs`, `app.css`): `core.autocrlf=true` sẽ tự chuẩn hóa khi add.

## 22. `git diff --cached --name-status`

Trống: chưa stage file nào.

## 23. Manifest và SHA nền (cục bộ, không commit)

- `artifacts/b24-base-sha.txt`: `3c5c459fed7fad2ef8631c135f0f5c503d259215` (40 ký tự + xuống dòng).
- `artifacts/b24-commit-manifest.tsv`: 34 dòng (13 `M`, 21 `A`, 0 `D`), phân cách bằng TAB thật, gồm cả báo cáo này.

## 24. Kiểm tra hai chiều manifest ↔ Git status

So `git status --porcelain -uall` (bỏ `artifacts/`) với manifest:
- có trong manifest mà không có trong status: **0**;
- có trong status mà không có trong manifest: **0**;
- đường dẫn trùng: **0**;
- dòng thuộc `artifacts/` hoặc file bí mật / cấu hình môi trường: **0**.

Chưa `git add`, chưa commit, chưa push, chưa tạo PR, chưa merge. Chờ người dùng duyệt.
