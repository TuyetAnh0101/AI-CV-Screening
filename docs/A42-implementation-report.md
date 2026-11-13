# A42 – Hỗ trợ trang Tải CV (backend/API) – Báo cáo triển khai

> Trạng thái: **READY FOR USER REVIEW**. Code nằm trên nhánh `feature/a42-upload-support-api`, **chưa** `git add`, **chưa** commit,
> **chưa** push, **chưa** tạo PR. Không có migration mới, không đổi schema, không sửa `Program.cs`, `appsettings*.json` hay `SmartCv.Web`,
> không ghi vào `SmartCvDb`, không gọi Gemini.

## 1. Phạm vi Git

| Mục | Giá trị |
|---|---|
| Repo / remote | `C:\TTCN\project\AI-CV-Screening`, `origin = https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Nhánh | `feature/a42-upload-support-api`, tạo một lần bằng `git switch -c feature/a42-upload-support-api origin/main` (Git tự đặt upstream là `origin/main`) |
| SHA nền | `7cd42ab68c348b4ba9cebbb4bbcc65880edd1eb6` (`feat(A41) … (#61)`), ghi ở `artifacts/a42-base-sha.txt`; `HEAD` vẫn bằng SHA này |
| Trạng thái ban đầu | Đang ở `feature/a41-prompt-tools-api` (đã squash-merge thành #61, nội dung giống hệt `main`); không thay đổi local/staged/untracked, không unmerged, không merge/rebase/cherry-pick dở dang |
| Nhánh A42 trước đó | Không có ở local và remote |
| Phụ thuộc | A38 (#59), A39 (#60), A41 (#61) đã có trên `main`. B38 chưa có nhánh/PR |
| Ngoài phạm vi | `stash@{0}` cũ của `fix/A10-test-lifecycle` có sẵn từ trước, không đụng tới |

## 2. ZIP và mã nguồn thực tế

ZIP `A36_A42_B38_B42_SmartCv.zip` (mục A42) chỉ dùng làm tham khảo nghiệp vụ; **không chép đè file nào**.

| Thành phần ZIP | Xử lý |
|---|---|
| `TextQuality`, `RunTimeEstimator`, `CvTextContracts` | Viết mới theo đúng ngưỡng của ZIP và bổ sung: chuẩn hóa Unicode FormC; so ngưỡng trên giá trị chưa làm tròn; `LetterRatio` cắt (không làm tròn lên) để 0,649 không hiển thị thành 0,65; bỏ mẫu bất thường (> 1 giờ/CV); chống tràn số; giới hạn 0–500 |
| `CvEndpoints.cs` của ZIP | Dùng `MapCvEndpoints(this IEndpointRouteBuilder)`, **không** có chốt Development/Enabled, rate limit, giới hạn multipart → không chép; chỉ chèn `GET /{id:guid}/text` và `textQuality` vào file của `main` |
| `CvUploadService.cs` của ZIP | Chỉ lấy ý tưởng truy vấn job đã ứng tuyển; giữ toàn bộ A5/A6/A7/A38 của `main` (validate, hash, cleanup, enqueue, source/consent) |
| `RunEndpoints.cs` của ZIP (462 dòng) | Không chép; chỉ chèn `MapGet("/estimate")` trước `/{id:int}` và một handler |
| `CvDetailDto`, `CvUploadResponse` của ZIP | Chỉ thêm trường mới; giữ nguyên mọi trường A38/A39 |
| Test ZIP (`namespace SmartCv.Tests.Extraction` / `.Scoring`) | Viết lại trong `namespace SmartCv.Tests` để tránh xung đột tên (CS0118 đã gặp ở A18) |

Phát hiện khi tích hợp: A39 đã để sẵn một route tạm `GET /api/cvs/{id:guid}/text` trong `RetentionEndpoints.cs`, kèm chú thích *"A42 is not present: expose only the tombstone guard"*. Khi có endpoint thật, hai route trùng làm request trả 500 (ambiguous match). Tôi đã gỡ route tạm (đúng như chú thích A39 dự kiến). Endpoint A42 vẫn trả **410 với cùng tiêu đề và nội dung** cho CV đã ẩn danh hóa; `A39SqlTests` vẫn pass.

## 3. Nghiệp vụ A42

### 3.1. Chất lượng chữ – `TextQuality.Assess(text, extractionMethod)`

| Điều kiện | Kết quả |
|---|---|
| `null` / rỗng / chỉ khoảng trắng | `Empty`, `maybeMisread = true`, cảnh báo "Không đọc được chữ nào từ CV." |
| `charCount < 500` | cảnh báo "Chỉ đọc được N ký tự (CV thường trên 500): có thể đọc thiếu." |
| `letterRatio < 0,65` (`char.IsLetter` / ký tự không phải khoảng trắng) | cảnh báo "Chỉ N% là chữ cái: có nhiều ký hiệu lạ…" |
| ≥ 20 từ và từ một chữ cái > 30% | cảnh báo "Nhiều chữ cái đứng rời rạc…" |
| Có cảnh báo và `extractionMethod` là `PdfOcr`/`ImageOcr` | thêm "CV được đọc bằng OCR từ ảnh: nên mở file gốc để kiểm tra trước khi chấm." |
| Có ít nhất một cảnh báo / không có | `Low` (`maybeMisread = true`) / `Good` |

- Chữ được chuẩn hóa FormC trước khi đếm, nên tiếng Việt dạng tổ hợp (NFD) cho kết quả giống dạng dựng sẵn.
- `letterRatio` theo thang **0–1**. Cảnh báo chỉ chứa con số, không chứa nội dung CV.
- Đây là **heuristic cảnh báo**, không kết luận CV sai.

### 3.2. `GET /api/cvs/{id}/text` (mới) và `GET /api/cvs/{id}` (thêm `textQuality`)

Mẫu thật qua Kestrel (CV tổng hợp, đã rút gọn `text`):

```json
{
  "cvId": "254217e9-542c-4fab-af58-c88878a7594e", "originalFileName": "CV_TongHop_A42.pdf",
  "extractionStatus": "Completed", "extractionMethod": "PdfText", "extractionError": null,
  "text": "Kinh nghiệm C# và ASP.NET Core, …",
  "quality": { "charCount": 863, "wordCount": 156, "letterRatio": 0.80, "level": "Good", "maybeMisread": false, "warnings": [] },
  "statusMessage": "Đã đọc xong."
}
```

| Tình huống | Kết quả |
|---|---|
| Completed | 200, `text` + `quality` (Good/Low/Empty) |
| Completed nhưng không có chữ | 200, `quality.level = Empty`, `statusMessage` gợi ý kiểm tra file gốc |
| Pending (kể cả còn chữ cũ khi đang đọc lại) | 200, `text = null`, `quality = null`, "CV đang chờ đọc chữ, hãy tải lại sau ít giây." |
| Failed | 200, `text = null`, `quality = null`, `extractionError`, gợi ý đọc lại |
| Không có CV | 404 "Không tìm thấy CV" |
| CV đã ẩn danh hóa | **410** "CV đã được ẩn danh hóa", trả về **trước** khi đọc nội dung, không lộ tên file hay chữ |
| `id` không phải GUID | 404 (ràng buộc route) |

- Không trả `StoredPath`. Không ghi log nội dung CV.
- Response có `Cache-Control: no-store` và đi qua khóa đọc A39, vì route nằm trong `/api/cvs`.
- `GET /api/cvs/{id}` thêm `textQuality`: có khi Completed và chưa ẩn danh; `null` khi Pending/Failed/đã ẩn danh. Mọi trường A38/A39 giữ nguyên (có test kiểm tra từng tên trường).

### 3.3. Thông tin CV trùng – `POST /api/cvs/upload`

`CvUploadItemResult` có thêm 4 trường (đều nullable hoặc mặc định rỗng, client cũ không bị ảnh hưởng): `existingCvId`, `existingFileName`, `existingUploadedAt` (UTC), `existingJobTitles`.

```json
{ "fileName": "cv-anh-ro-lan-2.png", "status": "Rejected", "cvId": null, "errorCode": "Duplicate",
  "errorMessage": "Trùng với CV đã tải trước đó: \"cv-anh-ro.png\" (ngày 13/11/2026), chưa ứng tuyển vị trí nào.",
  "existingCvId": "c1e91357-f879-497c-90b3-85f3cce08008", "existingFileName": "cv-anh-ro.png",
  "existingUploadedAt": "2026-11-13T06:39:13.5184653Z", "existingJobTitles": [] }
```

- **Trùng CV trong database:** `Rejected` / `Duplicate`, kèm mã CV cũ, tên file, ngày tải, các vị trí đã ứng tuyển (không trùng lặp, theo `JobId`).
  - `errorMessage` **giữ tiền tố A5** `Trùng với CV đã tải trước đó: "…" (ngày dd/MM/yyyy)` để client và test cũ không đổi, rồi nối thêm `, đã ứng tuyển: A, B.` hoặc `, chưa ứng tuyển vị trí nào.`. ZIP gợi ý câu "Đã có từ ngày…"; tôi chọn giữ tiền tố A5 để tương thích ngược.
  - Không tạo CV mới, không đổi `SourceChannel`/`ConsentConfirmedAt` của CV cũ, không xếp đọc chữ lại.
- **Trùng trong cùng lần tải:** giữ thông báo cũ "Trùng nội dung với file … trong cùng lần tải.", các trường `existing*` để null/rỗng.
- **Hai request tải cùng file đồng thời:** ràng buộc `UQ_Cvs_FileHash` vẫn quyết định bên thắng; bên thua được dọn file như trước, rồi đọc lại bản đã thắng để điền `existing*`. Nếu đọc lại lỗi thì vẫn báo Duplicate như cũ. Đã kiểm trên SQL Server thật.
- **CV cũ đã ẩn danh hóa:** A39 đổi `FileHash` nên thực tế không khớp. Nhánh phòng thủ vẫn được thêm: không trả mã, tên, ngày hay vị trí của hồ sơ cũ, chỉ báo "Trùng với một hồ sơ đã được ẩn danh hóa…".
- Một truy vấn cho mọi file đã qua kiểm tra (không N+1). `SuccessCount`, `FailedCount`, thứ tự `files[]`, validate, hash, source/consent bắt buộc (thiếu → 400, không lưu file nào) giữ nguyên.

### 3.4. `GET /api/runs/estimate?count=N`

```json
{ "count": 10, "secondsPerCv": 7, "estimatedSeconds": 70, "estimatedMinutes": 2, "basedOnRuns": 3, "isDefault": false,
  "minSecondsPerCv": 0.8, "aiSimulated": true, "text": "10 CV · khoảng 2 phút",
  "note": "Ước tính theo 3 lượt chấm gần nhất với AI giả lập. Thời gian thực tế phụ thuộc hàng đợi và số lần thử lại khi Gemini quá tải, không phải cam kết." }
```

**Cách lấy mẫu:**
- Tối đa 10 lượt **Screening**, `Completed`, mới nhất theo `CompletedAt`.
- Chỉ lấy lượt có `TotalItems > 0` và `CompletedAt > CreatedAt` (lọc ngay trong SQL, `AsNoTracking`, chỉ chọn 3 cột).
- PromptTest, Queued, Running, Failed không được tính.

**Cách tính:**
- `secondsPerCv` = tổng thời gian / tổng số CV (không lấy trung bình theo lượt), làm tròn 1 chữ số. Không thấp hơn sàn.
- Mẫu bất thường (> 1 giờ/CV) bị bỏ.
- `estimatedSeconds` và `estimatedMinutes` làm tròn lên. `count = 0` trả "0 CV".

**Sàn và giá trị mặc định:**
- Gemini thật: sàn = `Gemini:MinDelayBetweenCallsMs`.
- AI giả: sàn = `Gemini:FakeDelayMs`, vì AI giả không đi qua khoảng giãn 4,5 giây; phản hồi có `aiSimulated: true` và ghi chú "với AI giả lập".
- Chưa có lượt nào: sàn + 2 giây (Gemini thật 6,5 giây/CV, AI giả mặc định 2,8 giây/CV). `isDefault = true`, ghi chú "Ước tính ban đầu".

**Kiểm tra đầu vào:**
- `count` được tự đọc. Thiếu, chữ, số thập phân, số âm, vượt 500 hoặc tràn int → 400 "Số CV không hợp lệ", không có stack trace.
- `POST /api/runs` dùng chung hằng số 500 (`RunTimeEstimator.MaxCount`), hành vi không đổi.
- `/estimate` không che route `GET /api/runs/{id:int}` (có test).

### 3.5. Bảo vệ truy cập

- `/text` nằm trong `CvEndpoints`: chỉ map khi `CvUpload:Endpoint:Enabled = true`; bật ngoài Development thì ứng dụng dừng lúc khởi động như trước.
- `/estimate` nằm trong `RunEndpoints`: chỉ có ở Development + Enabled.
- Production/Staging hoặc `Enabled = false` → 404 (có test). Upload giữ nguyên rate limit `cv-upload` và giới hạn dung lượng; hai GET mới không thêm hay nới giới hạn nào.

## 4. Kiểm thử (số liệu thật)

| Lần chạy | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Baseline `7cd42ab` trước khi sửa (`artifacts/a42-baseline-test.log`) | 1761 | 0 | 73 | 1834 |
| **Cuối**, `dotnet test SmartCv.slnx --no-build --no-restore` (`artifacts/a42-full-test.log`) | **1832** | **0** | 76 | 1908 |
| Cuối, bật SQL opt-in giữ catalog + OCR thật + demo ZIP (`artifacts/a42-full-optin-test.log`) | 1876 | 2 (*) | 38 | 1916 |

- `dotnet restore SmartCv.slnx`: exit 0. `dotnet build SmartCv.slnx --no-restore`: **0 Warning(s), 0 Error(s)**.
- 74 test mới: `A42UploadSupportEndpointTests` 33, `RunTimeEstimatorTests` 20, `TextQualityTests` 18, `A42SqlTests` 2, `A42OcrDemoTests` 1. Ở lần chạy mặc định, 3 test opt-in bị skip (tăng skipped từ 73 lên 76); ở lần opt-in cả 3 pass.
- (*) 2 lỗi **có sẵn, không do A42**: `RuntimeScriptDeploymentTests.Sqlcmd_default_decoding…` (dòng 98) và `RuntimeSchemaSqlTests.Run25_schema_mismatch…` (dòng 108). Đã chạy lại đúng 2 test này trên mã nguồn base `7cd42ab` (xuất bằng `git archive` vào thư mục tạm, không tạo worktree): **fail y hệt, cùng dòng**. A42 không sửa migration hay script runtime.
- Skipped còn lại: các bộ SQL **tự DROP database** (A11, A14, A15, A16, A17, A35). Tôi cố ý không chạy chúng, theo yêu cầu không tự chạy thao tác phá hủy database.

| Nhóm | Nội dung đã kiểm |
|---|---|
| Unit `TextQuality` | CV rõ = Good; OCR mờ giả lập = Low với cả 4 cảnh báo; null/rỗng/khoảng trắng = Empty; 499/500; tỷ lệ chữ cái 0,649/0,65 (hiển thị 0,64, không làm tròn lên); 30%/31%; quy tắc chỉ áp dụng khi ≥ 20 từ; NFD = NFC; lời khuyên OCR chỉ cho `PdfOcr`/`ImageOcr`; cảnh báo không chứa email/SĐT |
| Unit `RunTimeEstimator` | mặc định 6,5 / 2,8 / 2,0; trung bình theo tổng CV; sàn 4,5; bỏ mẫu lỗi và bất thường; chỉ 10 mẫu; làm tròn; `count = 0`; 500 × 3.600 giây không tràn; −1/501 bị từ chối; định dạng "dưới 1 phút" / "khoảng 1 giờ 15 phút" |
| HTTP `/text` | 200 Completed (đủ tên trường, `no-store`, không `StoredPath`); Low + lời khuyên OCR; Empty; Pending/Failed không có chữ/chất lượng giả; 404; 410 không lộ nội dung; route không phải GUID |
| HTTP chi tiết CV | `textQuality` có; đủ 20 trường A38/A39; null khi Pending/Failed/ẩn danh |
| HTTP upload | trùng DB có đủ `existing*`, job không trùng lặp, nguồn/đồng ý cũ không đổi, không tạo CV; CV chưa ứng tuyển; lô lẫn hợp lệ/trùng trong lô/trùng DB/sai định dạng giữ thứ tự và số đếm; CV ẩn danh không lộ gì; thiếu đồng ý → 400, không lưu, không lộ thông tin trùng |
| HTTP estimate | mặc định; AI giả; 10 lượt Screening mới nhất (bỏ PromptTest/Running/Queued/Failed/0 CV/0 giây); 7 giá trị `count` sai → 400; 0 và 500; không che `/{id:int}`; đọc bằng `ReadFromJsonAsync<RunEstimateDto>` như client Blazor |
| Bảo vệ | Production, Staging, Enabled=false → 404; upload giữ rate limit và giới hạn dung lượng |
| **SQL Server thật** (LocalDB, catalog mới `SmartCv_A42_Test_<GUID>`, migration thật, giữ lại) | Hai upload cùng file **đồng thời** (barrier sau bước kiểm tra trùng): đúng 1 CV được lưu, bên thua báo Duplicate với `existingCvId` = bên thắng, file của bên thua đã được dọn. Truy vấn trùng + job, `/text` (khóa đọc A39, `no-store`), chi tiết CV và estimate đều chạy đúng trên SQL Server |
| **OCR thật** (Tesseract `eng+vie`, ảnh đọc thẳng từ ZIP, worker đọc chữ thật) | `cv-anh-ro.png`: ImageOcr, 724 ký tự, 136 từ, tỷ lệ chữ cái 0,85, **Good**. `cv-anh-mo.png`: ImageOcr, 182 ký tự, 36 từ, 0,68, **Low**, 3 cảnh báo. Tải lại ảnh rõ → Duplicate trỏ đúng CV cũ. Chỉ ghi con số vào `artifacts/a42-tests/ocr-evidence.txt`; số liệu có thể khác trên máy có Tesseract/tessdata khác |
| Kiểm tay qua Kestrel | Development + AI giả, catalog thử nghiệm A42, file tải lên vào thư mục tạm, xong thì dừng tiến trình. Kết quả: `/text` 200 + `no-store`, 404, estimate 200, bốn `count` sai → 400, `/api/runs/1` 200; tải 2 ảnh → OCR cho đúng số như trên; tải lại → Duplicate. CV cũ giữ nguồn `Email` dù lần tải lại gửi `Website`. Log API không chứa nội dung CV, email, SĐT hay API key |

Chưa chạy: Gemini thật (không cần cho A42); các bộ SQL tự DROP database (xem trên).

## 5. Tương thích

- **A38:** giữ nguyên bắt buộc nguồn + xác nhận đồng ý; test mới đều gửi hai trường này; không thêm mặc định ngầm trong Web/production.
- **A39:**
  - Gỡ route tạm `/text`; endpoint thật vẫn trả 410 cho CV đã ẩn danh. `GET /api/cvs/{id}` của CV ẩn danh không có `textQuality`.
  - Upload trùng với CV ẩn danh không lộ gì.
  - `A39SqlTests` 24/24 pass trên LocalDB.
- **A5/A6/A7:** tiền tố thông báo trùng, thông báo trùng trong cùng lần tải, cleanup, enqueue và số đếm đều không đổi. Test cũ `CvUploadServiceTests` (22) pass nguyên văn.
- **B38/Web:** không sửa `SmartCv.Web`. Các DTO chỉ thêm trường nên client cũ đọc bình thường.
- **Database:** không migration, không đổi schema, không chạy SQL lên `SmartCvDb`.

## 6. Bàn giao UX/UI cho B38 (đề xuất, chưa code Web)

Giữ Design System SCApp: Inter, Deep Teal `#0F766E` cho hành động chính, nền trung tính, tương phản ≥ 4.5:1, không gradient chói hay icon thừa.

1. **Luồng theo nghiệp vụ:** chọn nguồn CV → tick "Ứng viên đã đồng ý xử lý dữ liệu" (bắt buộc; nút Tải lên bị khóa đến khi tick) → kéo thả nhiều file → hiện kết quả từng file ngay (API trả về không chờ OCR) → theo dõi đọc chữ → xem chữ và chất lượng → gán vị trí → xem ước tính → phân tích AI. Không làm OCR hay Gemini đồng bộ trên trình duyệt.
2. **Danh sách file:** mỗi dòng có tên, dung lượng và huy hiệu trạng thái **gồm cả chữ và icon**, không chỉ màu:
   - Đã lưu
   - Đang đọc chữ (Pending, tự cập nhật bằng `GET /api/cvs/{id}`)
   - Đã đọc (Completed)
   - Đọc lỗi
   - Trùng
   - Bị từ chối (theo `errorCode`)

   Mỗi dòng bấm để mở drawer.
3. **Cảnh báo chất lượng chữ:**
   - `Low`: nền vàng nhạt, viền ấm, chữ tối, câu "CV ảnh có thể được đọc chưa chính xác; xem chữ đã đọc và file gốc trước khi chấm", liệt kê `warnings`.
   - `Good`: chỉ dấu kiểm nhỏ, không huy hiệu rực rỡ.
   - `Empty`: cảnh báo hành động rõ "Không đọc được chữ — mở file gốc hoặc đọc lại".
4. **Drawer "Xem chữ đã đọc"** (`GET /api/cvs/{id}/text`):
   - Hai cột "File gốc" (`/file`) | "Chữ trích xuất", phía trên là thống kê `charCount`, `wordCount`, `letterRatio` hiển thị dạng % và phương thức (PDF chữ / OCR).
   - Mobile chuyển sang tab; đóng bằng Esc; giữ focus trong drawer; có `aria-live` cho trạng thái.
   - Pending hiện skeleton và `statusMessage`; 410 hiện "Hồ sơ đã được ẩn danh hóa", không hiển thị gì thêm.
5. **Thẻ trùng file** (khi có `existingCvId`):
   - Nội dung: "Đã tồn tại từ dd/MM/yyyy · {existingFileName}", chip các vị trí đã ứng tuyển, nút phụ "Xem CV hiện có".
   - Màu trung tính/thông tin, không dùng màu lỗi hệ thống; không gợi ý tải lại.
   - Trùng trong cùng lần tải: chỉ hiện câu của API.
6. **Dòng ước tính:**
   - Chữ nhỏ, trung thực: "10 CV · khoảng 2 phút — ước tính theo 8 lượt gần nhất" (từ `text` và `basedOnRuns`).
   - `isDefault` → "Ước tính ban đầu"; `aiSimulated` → nhãn "AI giả lập"; tooltip lấy từ `note`.
   - Không đếm ngược hay cam kết, nhất là khi Gemini 503 hoặc đang có hàng đợi.
7. **Responsive và trạng thái:** 1440/1024/768/375 px (bảng → thẻ ở 375). Đủ các trạng thái loading/empty/error/413/429 bằng tiếng Việt; vùng chạm ≥ 44 px; điều khiển được bằng bàn phím; nhãn cho trình đọc màn hình.

## 7. Rủi ro còn lại và việc cần kiểm tay

- Ngưỡng chất lượng là heuristic. OCR cùng một ảnh có thể cho số khác trên máy khác; ảnh mờ có tỷ lệ chữ cái cao (0,68) vẫn bị gắn Low nhờ quy tắc độ dài và chữ rời rạc.
- Ước tính tính cả thời gian chờ trong hàng đợi của các lượt cũ; lịch sử chạy bằng AI giả sẽ làm ước tính thấp khi chuyển sang Gemini thật (sàn 4,5 giây hạn chế phần này).
- Ngày trong `errorMessage` dùng giờ máy chủ như A5. Giao diện nên tự định dạng từ `existingUploadedAt` (UTC).
- `existingCvId` và các vị trí đã ứng tuyển chỉ an toàn vì API vẫn chỉ có ở Development; đến M6 cần phân quyền.
- B38/QA cần kiểm tay: luồng tải lên thật với 20 CV, drawer trên mobile, đọc màn hình, 410 với CV ẩn danh, file trùng có ứng tuyển.
- Database thử nghiệm được **giữ lại**, không xóa: trong lần làm A42 có 45 catalog LocalDB mới (A36 1, A38 8, A39 24, A40 1, A41 1, A42 4, Runtime 6), danh sách ở `artifacts/a42-tests/retained-localdb-catalogs-this-task.txt`. Chỉ dọn khi bạn cho phép. Không đụng `SmartCvDb` hay instance `localhost\BB`.

## 8. Danh sách file (6 sửa, 9 mới = 15 file, khớp `artifacts/a42-commit-files.txt`)

| Trạng thái | File |
|---|---|
| M | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` |
| M | `src/SmartCv.Api/Endpoints/RetentionEndpoints.cs` (gỡ route tạm `/text` của A39) |
| M | `src/SmartCv.Api/Endpoints/RunEndpoints.cs` |
| M | `src/SmartCv.Core/Contracts/CvDetailDto.cs` |
| M | `src/SmartCv.Core/Contracts/CvUploadResponse.cs` |
| M | `src/SmartCv.Infrastructure/Uploads/CvUploadService.cs` |
| A | `docs/A42-implementation-report.md` |
| A | `src/SmartCv.Core/Contracts/CvTextContracts.cs` |
| A | `src/SmartCv.Core/Extraction/TextQuality.cs` |
| A | `src/SmartCv.Core/Scoring/RunTimeEstimator.cs` |
| A | `tests/SmartCv.Tests/A42OcrDemoTests.cs` |
| A | `tests/SmartCv.Tests/A42SqlTests.cs` |
| A | `tests/SmartCv.Tests/A42UploadSupportEndpointTests.cs` |
| A | `tests/SmartCv.Tests/Extraction/TextQualityTests.cs` |
| A | `tests/SmartCv.Tests/Scoring/RunTimeEstimatorTests.cs` |

Không có ảnh, CV thật, credential hay migration trong danh sách. Hai ảnh thử chỉ được đọc từ ZIP lúc chạy test.

## 9. Git cuối

```
$ git status --short --branch --untracked-files=all
## feature/a42-upload-support-api...origin/main
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Api/Endpoints/RetentionEndpoints.cs
 M src/SmartCv.Api/Endpoints/RunEndpoints.cs
 M src/SmartCv.Core/Contracts/CvDetailDto.cs
 M src/SmartCv.Core/Contracts/CvUploadResponse.cs
 M src/SmartCv.Infrastructure/Uploads/CvUploadService.cs
?? docs/A42-implementation-report.md
?? src/SmartCv.Core/Contracts/CvTextContracts.cs
?? src/SmartCv.Core/Extraction/TextQuality.cs
?? src/SmartCv.Core/Scoring/RunTimeEstimator.cs
?? tests/SmartCv.Tests/A42OcrDemoTests.cs
?? tests/SmartCv.Tests/A42SqlTests.cs
?? tests/SmartCv.Tests/A42UploadSupportEndpointTests.cs
?? tests/SmartCv.Tests/Extraction/TextQualityTests.cs
?? tests/SmartCv.Tests/Scoring/RunTimeEstimatorTests.cs

$ git diff --stat                  → 6 files changed, 190 insertions(+), 12 deletions(-)
$ git diff --check                 → không có lỗi (exit 0)
$ git diff --cached --name-status  → rỗng (chưa stage gì)
$ git rev-parse HEAD               → 7cd42ab68c348b4ba9cebbb4bbcc65880edd1eb6 (= SHA nền)
```

Các file mới (`??`) đã được rà riêng: không có khoảng trắng cuối dòng, không có dòng trống thừa ở cuối file, mỗi file kết thúc đúng một xuống dòng, UTF-8. File cũ giữ CRLF như trước, không đổi hàng loạt.

Tôi chưa commit, push, merge hay tạo PR.

## 10. Hướng dẫn kiểm thử nhanh (không secrets, không PII)

```powershell
# Chỉ trên database THỬ NGHIỆM, Development, AI giả; không dùng SmartCvDb.
$env:SMARTCV_A42_DEMO_ZIP = "C:\TTCN\A36_A42_B38_B42_SmartCv.zip"                       # OCR thật hai ảnh giả
$env:SMARTCV_A42_TEST_SQL_CONNECTION = "Server=(localdb)\MSSQLLocalDB;Database=SmartCv_A42_Test_x;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test SmartCv.slnx --filter "FullyQualifiedName~A42|FullyQualifiedName~TextQuality|FullyQualifiedName~RunTimeEstimator"

# Thử API (Send-Api/Get-Api của hướng dẫn tuần 2; ảnh lấy từ ZIP giải nén ra thư mục tạm)
curl.exe -s -F "files=@<thu-muc-tam>\cv-anh-ro.png" -F "files=@<thu-muc-tam>\cv-anh-mo.png" -F "sourceChannel=Email" -F "consentConfirmed=true" "$base/api/cvs/upload"
Get-Api "/api/cvs/<cvId>/text" | Select-Object extractionStatus, extractionMethod, statusMessage -ExpandProperty quality
Get-Api "/api/runs/estimate?count=10" | Format-List
Show-Error { Get-Api "/api/runs/estimate?count=-1" }      # 400 "Số CV không hợp lệ"
```

Kỳ vọng: ảnh rõ Good, ảnh mờ Low (cảnh báo vàng); tải lại ảnh rõ trả Duplicate kèm `existingCvId`; `count=-1` trả 400. Kết quả OCR có thể khác đôi chút trên máy có tessdata khác.
