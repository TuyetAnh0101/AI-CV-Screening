# B38 – Trang Tải CV theo thực tế: báo cáo triển khai

> Trạng thái: **SẴN SÀNG ĐỂ BẠN REVIEW, CHƯA COMMIT**. Code nằm trên nhánh `feature/b38-upload-practical`, chưa `git add`,
> chưa commit, chưa push, chưa tạo PR. Không chạy migration hay ghi dữ liệu vào `SmartCvDb`.

## 1. Git và điều kiện bắt đầu

| Mục | Kết quả |
|---|---|
| Trạng thái đầu | Nhánh `fix/a42-upload-support-followup`, HEAD `27733fc`, working tree sạch (`git status --porcelain=v1 -uall` rỗng), index rỗng, không có file xung đột, không merge/rebase dở |
| `origin` | `https://github.com/TuyetAnh0101/AI-CV-Screening.git` (fetch + push) |
| `git fetch origin --prune` | Chỉ xóa ref remote `origin/fix/a42-upload-support-followup` (nhánh đã gộp). Không pull |
| Nhánh B38 trước khi tạo | Không có ở local, `origin` và `git ls-remote` |
| SHA nền | `94265e27585397683593d437527cde6b9dc0cb42` = `origin/main` "fix(A42): preserve ETA floor and protect duplicate upload metadata (#63)" |
| A38 / A42 đã vào main | A38 PR #59 (`e15ab5f`), A42 PR #62 (`823a595`) + follow-up #63 (`94265e2`). Đã kiểm mã thật, xem mục 2 |
| Tạo nhánh | `git switch --no-track -c feature/b38-upload-practical origin/main` (không đặt upstream để không lỡ đẩy nhầm lên `main`) |
| `artifacts/b38-base-sha.txt` | Một dòng UTF-8: `94265e27585397683593d437527cde6b9dc0cb42` (thư mục `artifacts/` được Git ignore) |
| Trạng thái cuối | HEAD vẫn `94265e2…`, khớp file SHA nền; 15 file sửa + 7 file mới, **chưa stage** (mục 10) |

## 2. Hợp đồng API đã đối chiếu trên `origin/main`

| Việc | Hợp đồng thực tế | Dùng ở B38 |
|---|---|---|
| A38 | `CvSourceChannels` (`Email`, `Website`, `JobBoard`, `Referral`, `Other`, `DisplayName`); `POST /api/cvs/upload` nhận form `sourceChannel`, `consentConfirmed`, thiếu/sai → **400 cả request, không lưu file**; `CvDetailDto.SourceChannel/SourceChannelName/ConsentConfirmedAt/AnonymizedAt` | Ô chọn nguồn lấy đúng 5 mã + nhãn từ Core; mọi nhóm gửi 2 trường; test API thật đọc lại nguồn + thời điểm đồng ý |
| A42 | `GET /api/cvs/{id}` có `TextQuality` (null khi Pending/Failed/ẩn danh); `GET /api/cvs/{id}/text` → `CvTextDto` (410 khi ẩn danh, Text/Quality null khi chưa xong); trùng DB → `Rejected` + `Duplicate` + `ExistingCvId/ExistingFileName/ExistingUploadedAt/ExistingJobTitles`, `CvId = null`; `GET /api/runs/estimate?count=0..500` → `RunEstimateDto` | Vòng hỏi trạng thái, hộp "Chữ đã đọc", CV trùng dùng `ExistingCvId`, dòng ước tính |

Phát hiện quan trọng khi đối chiếu: với A42, CV trùng có `CvId = null` và mã cũ nằm ở `ExistingCvId`. Code `main` (B5) chỉ đọc `CvId`
nên mọi CV trùng đều thành "Bị từ chối" và **không** phân tích được cho job mới. B38 sửa việc này (mục 4.3).

## 3. Giao diện trước / sau

### 3.1 Điểm yếu của bản trước (ảnh `screens/before/*`)

1. **Không tải được CV nào**: `main` không gửi nguồn + đồng ý nên API A38 trả 400 cho mọi lần tải; thông báo còn lộ tên trường kỹ thuật `(consentConfirmed = true)`.
2. Một lỗi hiện **ba lần** (khung đỏ đầu thẻ + thông báo vàng + dòng đỏ trong bảng), chồng chất ở đầu trang, xa nơi thao tác.
3. Điện thoại: bảng cuộn ngang trong khung, cột Trạng thái/Thao tác nằm ngoài màn hình, câu "Chưa có CV nào…" bị cắt.
4. Không có trạng thái đọc chữ, cảnh báo CV ảnh mờ, xem chữ đã đọc, thông tin CV trùng hay ước tính thời gian.
5. Chọn công việc nằm trên cùng, trước cả bước tải CV, không thể hiện quy trình thật.

### 3.2 Thiết kế mới (giữ Inter, Deep Teal `#0F766E`, token `--sc-*` có sẵn; không sửa App Shell / `app.css`)

- **Đầu trang** có thanh quy trình `Nguồn & đồng ý → Tải CV → Kiểm tra chữ → Chọn công việc → Phân tích`: bước xong có ✓, bước
  cần xem lại có "!" hổ phách, bước hiện tại viền teal (`aria-current="step"`). Điện thoại chỉ hiện chữ của bước hiện tại, các bước khác
  giữ số (trình đọc màn hình vẫn đọc đủ).
- **Khung "Nguồn hồ sơ và sự đồng ý"**: biểu tượng khiên, nhãn trạng thái "Còn thiếu N mục / ✓ Đã đủ điều kiện", ô chọn nguồn và ô xác
  nhận cùng một hàng trên desktop (một cột trên màn hình hẹp), dòng trấn an về quyền riêng tư bên dưới.
- **Ô kéo thả** cùng một khung cho mọi trạng thái (không giật bố cục): mở (đám mây teal, hover có AI Glow nhẹ), kéo vào ("Thả file để tải
  lên"), **khóa** (ổ khóa, nền sọc nhạt, "Chưa mở tải lên" + viên thuốc nói rõ thiếu gì), đang gửi (vòng chờ + "Đang gửi nhóm 1/2 · 20 CV").
  Trạng thái không chỉ dựa vào màu.
- **Danh sách CV** dạng bảng/thẻ: nhãn loại file (PDF/PNG/JPG), tên, dòng phụ, dòng "chữ đã đọc" có biểu tượng; dòng cần xem lại chỉ có
  **vạch trái + nền hổ phách rất nhạt**, không phủ cả bảng; CV trùng không dùng lại được có nhãn "Trùng hồ sơ" (không gọi chung là lỗi đỏ).
  Bảng chuyển sang thẻ theo **độ rộng thật của vùng danh sách** (container query 640px) nên máy tính bảng 768px còn thanh bên cũng
  không cuộn ngang.
- **Khu phân tích** cuối trang: chọn công việc + yêu cầu, lỗi phân tích hiện ngay cạnh nút, dòng ước tính `N CV · khoảng X phút` +
  nguồn số liệu + "Cho công việc: …", một nút chính "Phân tích hồ sơ (N)". ≤ 400px nút chính lên trên, rộng hết hàng.
- **Hộp "Chữ đã đọc"**: thẻ giữa màn hình (gần toàn màn hình trên điện thoại), cảnh báo hổ phách liệt kê lý do, 5 ô chỉ số, toàn văn
  monospace giữ xuống dòng, cuộn trong khung; chân hộp: Mở file gốc · Sao chép chữ · Đóng (44px).
- Vùng chạm chính ≥ 44px (đo bằng trình duyệt: tối thiểu 44 ở cả 4 kích thước); có `prefers-reduced-motion`.

## 4. Chức năng

### 4.1 Nguồn CV + xác nhận đồng ý (A38)
- 5 lựa chọn đúng mã hợp đồng, nhãn lấy từ `CvSourceChannels.DisplayName`; placeholder "Chọn nguồn CV…"; ô xác nhận **không tích sẵn**.
- Thiếu điều kiện → ô kéo thả **khóa thật**: `input` bị `disabled` (không mở hộp chọn file), `pointer-events: none`, ô chặn
  `dragover`/`drop` mặc định của trình duyệt (không chặn thì thả file vào ô đang khóa làm trình duyệt mở file và rời trang, mất danh sách).
  Trình duyệt thật: `defaultPrevented = true`, không thêm dòng, số CV trong DB không đổi.
- Nếu sự kiện chọn file vẫn tới (ngay trước khi khóa): trang kiểm tra lại, báo "Chưa mở tải lên", không tạo dòng, không gửi HTTP.
- Nguồn + đồng ý được **chốt một lần cho cả lần chọn file**; khi đang gửi, hai ô bị khóa; test chứng minh dù có sự kiện đổi nguồn lọt qua
  giữa chừng, nhóm 2 vẫn gửi đúng nguồn đã chốt.
- `CvApiClient` không bao giờ tự điền đồng ý: thiếu nguồn hợp lệ hoặc `consentConfirmed = false` → `ArgumentException`, không gửi request.
- 400 từ A38 → khung "Máy chủ chưa nhận lần tải này" + cách khắc phục, không lộ `consentConfirmed`/`sourceChannel`; dòng "Chưa lưu: …";
  **dừng các nhóm còn lại** (cũng với 401/403/413/429), dòng chưa gửi ghi "Chưa gửi…"; không tự thử lại; giữ nguyên lựa chọn nguồn/đồng ý.
- Lựa chọn nguồn + đồng ý **giữ nguyên** sau khi tải xong (theo bước thử 4 của hướng dẫn: kéo thêm file mà không tích lại).

### 4.2 Theo dõi đọc chữ và cảnh báo (A42)
- Sau khi nhóm có `CvId`, CV vào **một vòng hỏi chung cho cả trang** (`GET /api/cvs/{id}` mỗi 2 giây, tối đa 2 phút, mỗi lúc một request,
  timeout 15 giây/request). CTS **riêng** (`textCts`), không đụng `pollCts` của tiến độ phân tích; test chứng minh dừng theo dõi lượt
  (`?runId` → `/upload`) không dừng vòng đọc chữ.
- Trạng thái dòng: Đang đọc chữ… · ✓ Đọc tốt · N ký tự · **⚠ Có thể đọc sai · Xem chữ đã đọc** (nút hổ phách) · ⚠ Không có chữ ·
  ⚠ Không đọc được chữ · Xem chi tiết · "Chưa đọc xong sau 2 phút · Kiểm tra lại" · "Chưa kiểm tra được… · Thử lại" ·
  Hồ sơ đã được ẩn danh hóa · Không còn CV này trên máy chủ. Lỗi kỹ thuật (`ExtractionError`) không bao giờ hiện.
- Dưới bảng: dòng nhắc "N CV có thể bị đọc sai…", nút **tùy chọn** "Bỏ chọn N CV cần xem lại" (không ép bỏ CV). Khu phân tích nhắc khi CV
  đã chọn chưa đọc xong (worker A11 sẽ chờ, không lỗi).
- Dòng **không giữ chữ CV** (chỉ trạng thái, cách đọc, chỉ số); phản hồi của dòng đã xóa / đã Hủy / sau khi rời trang bị bỏ;
  vòng hỏi dừng khi xong, khi quá hạn, khi Hủy và khi rời trang.

### 4.3 Hộp "Chữ đã đọc"
- Chỉ gọi `GET /api/cvs/{id}/text` khi mở; mỗi lần mở là component mới (`@key`); chỉ một hộp mỗi lúc.
- Mở từ nút hổ phách, liên kết "Kiểm tra lại/Thử lại" hoặc menu ⋯ "Xem chữ đã đọc" (mọi dòng có `CvId`, kể cả CV trùng).
- Trạng thái: đang tải (skeleton) · Pending ("Tải lại") · Failed · Empty · Low (cảnh báo hổ phách + danh sách cảnh báo) · Good ·
  410 ("Hồ sơ đã được ẩn danh hóa", không thử lại, dòng trong bảng đổi theo) · 404 · mất kết nối / quá lâu / 5xx (có "Thử lại").
  Không hiện dữ liệu của lần tải trước trong lúc chờ.
- 5 chỉ số: **Số ký tự / Số từ / Tỷ lệ chữ cái / Phương pháp trích xuất / Đánh giá**. Tên hiển thị là tên file HR vừa chọn (không dùng
  tên file đã lưu của CV cũ).
- `<dialog>` gốc + `showModal()`: phần còn lại của trang trơ (`:modal`), Tab quay vòng trong hộp, Escape / bấm nền mờ để đóng, đóng xong
  trả focus về nút đã mở (dự phòng: nút ⋯ của dòng → tiêu đề danh sách). Kết quả /text cập nhật lại dòng (ví dụ dòng quá hạn → đọc xong).

### 4.4 CV trùng (A42 `existing*`)
- `UploadRules.ApplyDuplicateDetails` nâng dòng trùng có `ExistingCvId` thành **"Đã có sẵn"** với đúng mã CV cũ, vẫn tích chọn được:
  "Đã có từ ngày 17/11/2026; đã ứng tuyển: Backend Developer (.NET). Vẫn chọn được để phân tích." (ngày theo UTC+7, job không trùng lặp).
  Không upload lại, không tạo CV mới; logic gán Application (A8, idempotent) giữ nguyên — E2E: 1 CV, 2 hồ sơ ứng tuyển cho 2 job.
- Không có `ExistingCvId` (trùng trong cùng lần tải, CV cũ đã ẩn danh hóa, `Guid.Empty`) → giữ `Rejected` + mã `Duplicate`, nhãn
  "Trùng hồ sơ", không bao giờ tạo mã giả. Câu trùng do giao diện tự viết, không chứa tên file của hồ sơ cũ.

### 4.5 Ước tính thời gian (A42)
- Gọi `GET /api/runs/estimate?count=N` khi **số CV thật sự được chọn** đổi (sau mỗi nhóm tải lên, tích/bỏ, chọn tất cả, xóa dòng, Hủy);
  gộp thao tác liên tiếp 250 ms, hủy request cũ, phản hồi về muộn không ghi đè. N = 0 hoặc > 500: không gọi.
- Hiển thị đúng `text` của API ("2 CV · khoảng 2 phút") + nguồn ("Ước tính theo 1 lượt chấm gần nhất · AI giả lập"). Lỗi → chỉ "N CV" +
  "Chưa ước tính được thời gian, vẫn phân tích được", nút phân tích không bị khóa.
- Ước tính đã có cho đúng N được dùng lại (không gọi thêm); xóa khi vừa tạo lượt chấm mới (lịch sử chấm đổi).

### 4.6 Giữ nguyên B3–B6
Chọn job + yêu cầu (B4), giới hạn từ cấu hình (25 CV/lần chọn, 20 file/request, 2 MB/tệp), `UploadFlow.SplitIntoBatches`, `UploadValidator`,
lỗi từng file (B5), `BrowserFileContent` mở luồng trễ, `generation` + Hủy, gán CV → tạo lượt → `?runId` → `RunProgressPanel` + polling,
thử lại, 3 lỗi liên tiếp. Bổ sung: bấm **Hủy** khi đang tải thì các nhóm chưa gửi không gửi nữa.

## 5. Quyền riêng tư, bất đồng bộ, hiệu năng

- **Không log nội dung CV**: trang/hộp chỉ log mã lỗi chung; `ApiClientBase` chỉ log URL + mã HTTP. Đã quét log API/Web của bộ review:
  không có email/số điện thoại; chữ `ExtractedText` chỉ xuất hiện trong câu SQL của EF (tên cột).
- **B36 Ẩn danh** (đọc `AnonymityState`, chưa đọc được thì coi như đang bật): hộp "Chữ đã đọc" chỉ hiện chỉ số + cảnh báo, **không giữ và
  không render toàn văn** (không có trong HTML/ARIA/title), không có "Mở file gốc"/"Sao chép"; có công tắc B36 ngay trong hộp để HR tự tắt
  nếu cần xem (bật lại thì toàn văn bị bỏ ngay). Menu ⋯ không mở file gốc ("Tắt Ẩn danh để mở"). Câu trùng của API có thể chứa tên file hồ sơ
  khác → thay bằng câu chung. Phạm vi: tên file HR vừa chọn trên máy vẫn hiển thị (cần để làm việc ở bước nhập); B36 trước đây không áp dụng
  cho trang Upload và thẻ tiến độ `RunProgressPanel` (ngoài phạm vi B38) vẫn hiện tên như cũ.
- **A39**: 410 được xử lý ở hộp và ở vòng hỏi (`IsAnonymized`), không hiện dữ liệu cũ, không có đường tắt vượt API.
- **Bất đồng bộ**: mọi vòng nền chạy trên dispatcher của circuit; vòng đọc chữ định danh bằng chính CTS của nó (Hủy khi một request đang
  treo rồi tải tiếp vẫn mở vòng mới, không bao giờ có hai vòng song song — có test hồi quy); ước tính bỏ phản hồi cũ bằng CTS + so N;
  hộp thoại dùng CTS + số phiên bản; mọi thứ dừng khi rời trang (`DisposeAsync`).
- **Circuit**: giữ `BrowserFileContent` (test: mỗi lúc chỉ mở 1 file, không file nào mở trước khi gửi, 2/5/25 file); vòng hỏi tuần tự,
  render một lần mỗi lượt; trang vẫn xử lý thao tác khác khi nhóm 1 chưa xong (test). Không đọc file vào RAM.
- **JS mới**: `ExtractedTextDialog.razor.js` (module cùng component, tải bằng `import()` khi hộp mở, theo mẫu `StageDialog.razor.js` sẵn có)
  vì Blazor không tự gọi được `dialog.showModal()`; chỉ mở/đóng hộp, trả focus và sao chép khi người dùng bấm.

## 6. Ảnh kiểm thử giao diện (bằng chứng cục bộ, KHÔNG commit)

Thư mục `artifacts/b38-tests/screens/` (Git ignore). Dữ liệu: catalog LocalDB thử nghiệm, AI giả, 2 ảnh CV mẫu của ZIP, PDF tổng hợp do
công cụ kiểm thử tạo. Ô toàn văn trong ảnh chụp hộp thoại được **làm mờ** để không lưu chữ/họ tên/liên hệ mẫu.

| Trước (`before/`) | Sau (`after/`) |
|---|---|
| `before-empty-1440/768/390/320.png`, `before-upload-1440.png` (400 A38) | `after-locked-1440/768/390/320.png`, `after-ready-1440.png`, `after-ocr-1440.png`, `after-dialog-low-1440.png`, `after-dialog-good-1440.png`, `after-duplicate-1440.png`, `after-run-progress-1440.png`, `after-bulk-25-1440.png`, `after-list-1440/768/390/320.png`, `after-dialog-768/390/320.png`, `after-privacy-anonymous-1440.png`, `after-410-1440.png`, `after-offline-dialog-1440.png`, `after-offline-upload-1440.png` |

Số đo OCR thật (Tesseract `eng+vie` của API review): `cv-anh-ro.png` 724 ký tự · 136 từ · 85% · OCR từ ảnh · **Đọc tốt**;
`cv-anh-mo.png` 182 ký tự · 36 từ · 68% · OCR từ ảnh · **Có thể đọc sai** (`after/ocr-evidence.txt`, chỉ con số). Có thể khác trên máy có
tessdata khác.

## 7. Danh sách file (15 sửa + 7 mới, 0 xóa)

| Loại | File | Mục đích |
|---|---|---|
| M | `src/SmartCv.Web/Components/Pages/Upload.razor` | Bố cục mới, nguồn + đồng ý, vòng hỏi đọc chữ, hộp chữ, CV trùng, ước tính, dừng nhóm khi 400/Hủy |
| M | `src/SmartCv.Web/Components/Pages/Upload.razor.css` | Thiết kế trang, responsive |
| M | `src/SmartCv.Web/Components/Upload/CvDropzone.razor` | Khóa thật, chặn thả file khi khóa, `DisabledTitle/DisabledReason/Busy` |
| M | `src/SmartCv.Web/Components/Upload/CvDropzone.razor.css` | Trạng thái mở/kéo/khóa/đang gửi *(ngoài danh sách dự kiến, cùng component)* |
| M | `src/SmartCv.Web/Components/Upload/CvFileTable.razor` | Trạng thái chữ, nút cảnh báo, menu "Xem chữ đã đọc", trùng, Ẩn danh |
| M | `src/SmartCv.Web/Components/Upload/CvFileTable.razor.css` | Bảng/thẻ (container query), tô dòng cần xem lại |
| M | `src/SmartCv.Web/Models/UploadItem.cs` | `TextCheck`, trạng thái/chất lượng chữ, thông tin trùng |
| M | `src/SmartCv.Web/Services/CvApiClient.cs` | `UploadAsync(files, sourceChannel, consentConfirmed)`, `GetCvAsync`, `GetTextAsync` (giữ `BrowserFileContent`) |
| M | `src/SmartCv.Web/Services/RunApiClient.cs` | `EstimateAsync(count)` |
| A | `src/SmartCv.Web/Helpers/UploadRules.cs` | Quy tắc thuần B38 |
| A | `src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor` | Hộp "Chữ đã đọc" |
| A | `src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor.css` | Giao diện hộp |
| A | `src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor.js` | `showModal`, trả focus, sao chép *(ngoài danh sách dự kiến, xem mục 5)* |
| A | `tests/SmartCv.Tests/B38UploadRulesTests.cs` | 43 test: quy tắc, client, hợp đồng API thật trong bộ nhớ |
| A | `tests/SmartCv.Tests/B38UploadPageTests.cs` | 23 test: trang chạy bằng renderer Blazor thật + API giả |
| A | `docs/B38-implementation-report.md` | Báo cáo này |
| M | `tests/SmartCv.Tests/UploadValidationTests.cs` | Chọn nguồn + đồng ý trước khi thả file, API giả kiểm 2 trường form + trả trạng thái đọc chữ, đăng ký `AnonymityState`, đọc cây render trên dispatcher |
| M | `tests/SmartCv.Tests/JobSelectionUiTests.cs` | Tìm ô công việc theo `id="job"`; ô kéo thả mặc định khóa → chọn nguồn + đồng ý rồi mới kỳ vọng "Tải CV lên để phân tích"; đăng ký `AnonymityState` |
| M | `tests/SmartCv.Tests/RunProgressUiTests.cs` | Tìm ô công việc theo `id="job"` (ô nguồn CV đứng trước) |
| M | `tests/SmartCv.Tests/WebApiClientTests.cs` | Chữ ký `UploadAsync` mới; test với API thật bỏ handler "đồng ý giả" vì client B38 tự gửi |
| M | `tests/SmartCv.Tests/JobAssignmentTests.cs` | Như trên (B4) |
| M | `tests/SmartCv.Tests/A38ConsentEndpointTests.cs` | Test "Web trước B38 không gửi đồng ý → 400" không còn đúng với Web; đổi thành `Web_client_never_fabricates_consent_and_api_still_rejects_requests_without_it` (client không tự điền đồng ý + request kiểu cũ vẫn 400, không lưu file) |

Không sửa Backend/Core/Infrastructure/DB/EF/migration, không đụng A36/A39/A41/B39/B41/B42, System Prompt, `Program.cs`, `app.css`,
App Shell, `UploadFlow`, `UploadValidator`, `RunFlow`, `StatusText`, `BrowserFileContent`, `AnonymityState`, không thêm package.

## 8. Build và kiểm thử

Ba nhóm tách riêng: **(A) build/test tự động**, **(B) test logic/component với API giả**, **(C) E2E trình duyệt thật**.

### (A) Build và hồi quy
| Lệnh | Kết quả |
|---|---|
| `dotnet build SmartCv.slnx -c Release` (nền, trước khi sửa) | 0 warning, 0 error |
| `dotnet test -c Release` (nền) | **1846 passed / 0 failed / 78 skipped** (1924) |
| `dotnet build SmartCv.slnx -c Release` (cuối) | 0 warning, 0 error |
| `dotnet test -c Release` (cuối, full regression) | **1912 passed / 0 failed / 78 skipped** (1990) = nền + 66 test B38; 78 skip giống hệt nền (SQL/OCR opt-in) |
| Nhóm liên quan (B38, B3–B6, B34–B36, A38, A42, client Web) | 255 passed / 0 failed / 12 skipped, chạy 3 lần liên tiếp đều vậy |
| Test trang có vòng nền (B38 trang, B5, B6, B4) | 8 lần liên tiếp 77/77 (trước khi thêm 2 test cuối) |
| Opt-in dùng chính ZIP (`SMARTCV_A38_DEMO_ZIP`, `SMARTCV_A42_DEMO_ZIP`) | 21/21 passed, 0 skip (OCR thật 2 ảnh qua API trong bộ nhớ) |

Lý do dùng `-c Release`: bạn đang chạy API (`dotnet run`) và Web (`dotnet watch`) từ chính repo nên `bin/Debug` bị khóa; tôi không dừng
tiến trình của bạn. Sau khi tắt hai tiến trình đó, có thể chạy lại `dotnet build SmartCv.slnx` + `dotnet test SmartCv.slnx` bản Debug.

### (B) Test logic/component (API là `HttpMessageHandler` giả, nhận multipart/đường dẫn thật)
`B38UploadRulesTests` (43) + `B38UploadPageTests` (23): khóa tải lên (input disabled, thả file khi khóa không gửi gì), 5 nguồn, đồng ý không
tích sẵn, lô 2/5/25 file (20+5) cùng metadata, luồng mở trễ 1 file/lúc, metadata chốt khi đang gửi, trang vẫn phản hồi, 400 đồng ý/nguồn,
timeout, mất kết nối, Hủy giữa chừng, vòng hỏi Low/Good/Failed/quá hạn, bỏ phản hồi cũ (xóa dòng, Hủy khi request treo), dừng khi rời
trang, độc lập với polling tiến độ, CV trùng có/không `ExistingCvId`, phân tích CV trùng cho job mới, ước tính (bỏ phản hồi cũ, xuống cấp,
N = 0, dùng lại theo N), hộp chữ (chỉ tải khi mở, 5 chỉ số, 1 hộp/lúc, 410/404/503, offline → thử lại, Pending), Ẩn danh (không toàn văn,
không link file gốc, không tên file hồ sơ khác trong chữ/`title`/`aria-label`), hợp đồng API thật trong bộ nhớ (A38 lưu nguồn + đồng ý,
`/text`, trùng → `ExistingCvId`, estimate). Không dùng bUnit (repo chưa có; dùng renderer kiểm thử sẵn có của dự án).

### (C) E2E trình duyệt thật (Chrome headless qua Playwright .NET 1.55 trong scratchpad, không cài gì vào repo)
Bộ review riêng: API + Web build Release trên cổng 5299/5298, catalog LocalDB mới `SmartCv_B38_E2E_*` (migration thật + bản sao
`seed_demo.sql` đã đổi `USE` sang catalog thử nghiệm), `Gemini:UseFake=true`, worker chấm bật, file tải lên vào scratchpad.

| Kịch bản | Kết quả | Nội dung chính |
|---|---|---|
| main | 38/38 | khóa (input, thả file `defaultPrevented`, DB không đổi), mở khóa, tải 2 ảnh → DB có `Email` + thời điểm đồng ý, OCR thật Good/Low, tô dòng, ước tính 2 → 1 CV, hộp: 5 chỉ số, focus trap, `:modal`, Escape trả focus, menu ⋯, phân tích job thật đến khi xong, tải lại ảnh rõ → "Đã có sẵn… đã ứng tuyển: Backend Developer (.NET)", DB không thêm CV, phân tích CV trùng cho "Tester (QA)" (1 CV / 2 hồ sơ), 0 lỗi console |
| bulk | 6/6 | 26 file: file thứ 26 bị chặn, 25 CV lưu qua đúng 2 request 20 + 5 (log API), nguồn `JobBoard` cho cả 25, ước tính 25 CV, mọi dòng rời "đang đọc" |
| responsive | 19/19 | 1440/768/390/320: không tràn ngang (khóa + danh sách), vùng chạm chính ≥ 44px, hộp thoại nằm trong khung nhìn, 0 lỗi console |
| privacy | 8/8 | bật Ẩn danh bằng công tắc B36 ở trang Kết quả → Upload: không link file gốc, hộp không có toàn văn trong DOM; tắt trong hộp → tải toàn văn; 410 (mô phỏng A39 bằng `UPDATE Cvs SET AnonymizedAt` **chỉ trên catalog thử nghiệm**, vì API ẩn danh hóa cần đăng nhập HR) → hộp báo ẩn danh, không thử lại, dòng đổi trạng thái |
| offline | 4/4 | dừng **chỉ** API review (PID đối chiếu command line): hộp báo mất kết nối + Thử lại, ước tính xuống "1 CV", nút phân tích vẫn bật, tải file mới → banner kết nối, dòng không được coi là đã lưu |

Một lần chập chờn: lần chạy privacy đầu tiên trong loạt cuối, công tắc B36 trong hộp không thao tác được sau 30 giây (log
`e2e-privacy-run1-flaky-fail.log`); 4 lần chạy lại liền sau đều 8/8 và công tắc ở trạng thái bình thường. Công tắc này khóa khi chưa đọc xong
lựa chọn đã lưu (code B36, không đổi trong B38); nguyên nhân chưa xác định.

### Chưa kiểm / giới hạn
- Không gọi Gemini thật (không cần); chấm điểm dùng AI giả. Không chạy các bộ SQL tự DROP database (A11/A14–A17…) như baseline.
- File `test-files/cv-20-backend/01-tran-minh-khoa.pdf` mà hướng dẫn nhắc **không có trong ZIP**; tình huống trùng + "đã ứng tuyển" được
  kiểm bằng cách phân tích `cv-anh-ro.png` cho một job rồi tải lại.
- Không truy cập Figma (không cần; bám Design System trong repo).
- Chưa thử trên Safari/Firefox, trình đọc màn hình thật hay thiết bị cảm ứng thật (chỉ Chrome headless, giả lập kích thước).

## 9. Tác động tới chức năng cũ
- B3/B5: giới hạn, kiểm tra file, lỗi từng file, tổng kết, "Xóa N file lỗi" giữ nguyên; thêm: 400/429 dừng nhóm sau, Hủy dừng nhóm chưa gửi.
- B4: chọn job + yêu cầu giữ nguyên, ô chọn chuyển xuống khu phân tích (cùng `id="job"`).
- B6: luồng phân tích, `?runId`, polling, thử lại không đổi; 2 test cũ chỉ đổi cách tìm ô công việc.
- B34/B35/B36: không sửa; B36 giờ được tôn trọng thêm ở trang Upload (mục 5). A38/A42: chỉ là bên gọi; test A38 đổi tên như mục 7.
- CV trùng: trước B38 không phân tích được cho job mới (mục 2), nay được.

## 10. Trạng thái Git cuối (chưa stage)

```text
$ git status --short --branch --untracked-files=all
## feature/b38-upload-practical
 M src/SmartCv.Web/Components/Pages/Upload.razor
 M src/SmartCv.Web/Components/Pages/Upload.razor.css
 M src/SmartCv.Web/Components/Upload/CvDropzone.razor
 M src/SmartCv.Web/Components/Upload/CvDropzone.razor.css
 M src/SmartCv.Web/Components/Upload/CvFileTable.razor
 M src/SmartCv.Web/Components/Upload/CvFileTable.razor.css
 M src/SmartCv.Web/Models/UploadItem.cs
 M src/SmartCv.Web/Services/CvApiClient.cs
 M src/SmartCv.Web/Services/RunApiClient.cs
 M tests/SmartCv.Tests/A38ConsentEndpointTests.cs
 M tests/SmartCv.Tests/JobAssignmentTests.cs
 M tests/SmartCv.Tests/JobSelectionUiTests.cs
 M tests/SmartCv.Tests/RunProgressUiTests.cs
 M tests/SmartCv.Tests/UploadValidationTests.cs
 M tests/SmartCv.Tests/WebApiClientTests.cs
?? docs/B38-implementation-report.md
?? src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor
?? src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor.css
?? src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor.js
?? src/SmartCv.Web/Helpers/UploadRules.cs
?? tests/SmartCv.Tests/B38UploadPageTests.cs
?? tests/SmartCv.Tests/B38UploadRulesTests.cs

$ git diff --stat            → 15 files changed, 1561 insertions(+), 282 deletions(-)
$ git diff --check           → không có lỗi (exit 0)
$ git diff --cached --name-status → rỗng
$ git rev-parse HEAD         → 94265e27585397683593d437527cde6b9dc0cb42 (= artifacts/b38-base-sha.txt)
```

Git in cảnh báo "LF will be replaced by CRLF" vì máy đặt `core.autocrlf=true`; các file vẫn giữ kiểu xuống dòng như trước
(LF; riêng `RunApiClient.cs` trong working copy là CRLF từ lần checkout, diff chỉ +11 dòng), Git chuẩn hóa về LF khi add.

## 11. Rủi ro, điều cần bạn xác nhận

1. **Quyết định thiết kế cần bạn duyệt**: ô chọn công việc chuyển xuống khu phân tích; lựa chọn nguồn + đồng ý giữ nguyên sau mỗi lần tải
   (đổi thành "bỏ tích sau mỗi lần tải" chỉ là một dòng trong `OnFilesSelectedAsync`); CV trùng được tích sẵn như B3.
2. Vòng hỏi đọc chữ tuần tự: một request treo làm các dòng khác chờ tối đa 15 giây (đổi lấy việc không dồn request lên API).
3. `RunProgressPanel` (thẻ tiến độ) chưa theo chế độ Ẩn danh — ngoài phạm vi B38, đề xuất làm ở việc sau.
4. `A38TestData.ClientWithSyntheticConsent` không còn ai dùng và chú thích "until B38" đã cũ — để nguyên vì là file test của A38.
5. Gói B42 trong ZIP kỳ vọng câu trùng dạng "…, đã ứng tuyển: …" / "chưa ứng tuyển job nào"; B38 theo prompt hiện tại dùng "…; đã ứng tuyển: …"
   / "chưa ứng tuyển vị trí nào" (khớp câu của A42). B42 cần chỉnh theo khi làm.
6. Tiến trình API/Web Debug của bạn đã được **bạn** khởi động lại lúc 10:13 (PID 13676/24088); `dotnet watch` của Web hiện chạy code B38 trong
   working tree và nối API dùng `SmartCvDb` — nếu bạn tải CV trên bản dev đó, dữ liệu sẽ vào `SmartCvDb`.
7. Đã giữ lại để bạn quyết định dọn: 3 catalog LocalDB `SmartCv_B38_E2E_20261117092312`, `…100528`, `…101355`
   (`artifacts/b38-tests/retained-localdb-catalogs.txt`), file tải lên thử trong scratchpad của phiên. Không đụng `SmartCvDb`/`localhost\BB`.

**Kết luận: SẴN SÀNG COMMIT** sau khi bạn review (không còn blocker kỹ thuật; các mục trên là điểm cần bạn xác nhận).

## 12. Manifest commit

`artifacts/b38-commit-files.txt`: 22 đường dẫn (15 M + 7 A), lập từ `git status --porcelain=v1 -uall`; không có `artifacts/`, ảnh test,
ảnh chụp, log, zip, `bin/obj` hay secret.

## 13. Khoảng trắng của file mới
`git diff --check` không thấy file chưa track nên đã quét riêng cả 22 file: 0 dòng có khoảng trắng cuối, mỗi file kết thúc đúng một `\n`,
không dòng trống thừa cuối file, không BOM. Không stage để kiểm tra.

## 14. Phạm vi
Mọi thay đổi nằm trong `src/SmartCv.Web` (Upload), `tests/SmartCv.Tests` và `docs/`. Ngoài danh sách "phạm vi dự kiến" có:
`CvDropzone.razor.css`, `ExtractedTextDialog.razor.js` (cùng component trong phạm vi) và 6 file test cũ bị ảnh hưởng trực tiếp bởi chữ ký
`UploadAsync` / bố cục mới (mục 7). Không có thay đổi Backend.
