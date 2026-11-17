# A42 follow-up — Kiểm chứng bản đã merge và sửa lỗi có test tái hiện

Ngày kiểm tra: 17/11/2026 (Asia/Saigon). Trạng thái: **chờ người dùng duyệt thay đổi local**.

## 1. Kết luận và nguồn đối chiếu

A42 đã có trong `origin/main`, được merge qua [PR #62](https://github.com/TuyetAnh0101/AI-CV-Screening/pull/62).
Không triển khai lại tính năng và không tạo lại nhánh A42 cũ. Audit phát hiện hai lỗi có test đỏ: ETA làm tròn xuống dưới sàn cấu hình và response upload trùng chưa được privacy middleware bảo vệ.

- Đã đọc prompt `C:\TTCN\A42_Codex_Verify_No_Duplicate_Prompt.md`, hướng dẫn trong ZIP `C:\TTCN\A36_A42_B38_B42_SmartCv.zip`, toàn bộ 10 file mẫu thuộc `A42/` (gồm hai file unit test), và đối chiếu với source/test hiện tại.
- Hai PNG trong ZIP là CV giả để thử OCR. Đã xem trực tiếp bản rõ và bản mờ; chỉ giải nén vào thư mục tạm ngoài repo để xem. Test OCR đọc bytes trực tiếp từ ZIP, không đưa ảnh vào source.
- ZIP là tài liệu đối chiếu. Không chép đè template cũ lên source đã có A38/A39/A41 và các bảo vệ bổ sung.

| Bằng chứng Git | Kết quả |
|---|---|
| Remote | `https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| SHA nền đầy đủ | `823a595f081bb8f60d23eb448a7c5dc7421a5c74` |
| PR #62 | GitHub xác nhận `merged=true`, base `main`, merge SHA khớp SHA nền |
| Sau `git fetch origin --prune` | `origin/main` vẫn tại SHA nền; `merge-base --is-ancestor` trả 0 |
| Checkout ban đầu | `feature/a42-upload-support-api`, HEAD `bebbc3e1f66d5801c8afbc97873891f732ab0c97`; sạch, không staged/untracked/conflict hay thao tác Git dở dang |
| Cây source ban đầu và main | Cùng tree SHA `fe6e7894c90a4675e64aff3b3131ea830e998459`; `git diff HEAD origin/main` rỗng |
| Nhánh sửa | `fix/a42-upload-support-followup`, chỉ tạo sau test xUnit tạm đã tái hiện lỗi và kiểm tra nhánh chưa có cả local/remote |
| Cách tạo | `git switch --no-track -c fix/a42-upload-support-followup origin/main`; HEAD giữ nguyên SHA nền |

## 2. Đối chiếu yêu cầu A42

| Yêu cầu | Đánh giá trên main / kết quả sau follow-up |
|---|---|
| TextQuality: Empty, dưới 500 ký tự, tỷ lệ chữ cái < 0,65, từ một chữ > 30% khi ít nhất 20 từ | Đạt. Test ranh giới 499/500, 0,649/0,65, 30%/31%, số từ tối thiểu; không thay đổi thuật toán |
| Unicode tiếng Việt và tỷ lệ không làm tròn lên | Đạt. Chuẩn hóa FormC; tỷ lệ trả về cắt còn hai chữ số; test NFC/NFD và 0,649 → 0,64 |
| Warnings tiếng Việt, Low OCR có lời khuyên xem file gốc | Đạt qua unit, HTTP và OCR thật. Empty có cảnh báo không đọc được chữ; status message của API khuyên kiểm tra file gốc |
| `GET /api/cvs/{id}/text` | Đạt: đủ text/method/quality; Pending/Failed không giả text/quality; 404/410; không trả StoredPath; `no-store` |
| `GET /api/cvs/{id}` thêm textQuality | Đạt: chỉ khi Completed và chưa ẩn danh; giữ trường DTO A38/A39, không trả PII của tombstone |
| Upload giữ source/consent, file count/size, signature, SHA256, thứ tự kết quả | Đạt; test A5/A6/A7/A38 và integration hiện có giữ nguyên |
| Trùng CV cũ có ExistingCvId/FileName/UploadedAt/JobTitles | Đạt: UTC, job không lặp, không tạo CV/application giả, không sửa nguồn/consent cũ |
| Trùng cùng request / CV ẩn danh không lộ metadata cũ | Đạt; test hỗn hợp nhiều loại file và tombstone phòng thủ |
| Hai upload cùng hash | Đạt trên SQL Server thật: barrier buộc cả hai qua pre-check; một Accepted, một Duplicate có metadata bên thắng, chỉ một CV và một file được giữ |
| Cache/privacy của metadata cũ trong upload | **Chưa đạt trên main; đã sửa.** Upload được đưa vào shared privacy lease A39 và `no-store`; ba regression test chứng minh trước/sau |
| Route `/api/runs/estimate` và count sai | Đạt: route tĩnh, không che `/{id:int}`; thiếu/rỗng/chữ/thập phân/âm/>500/tràn int → 400 tiếng Việt; 0/500 hợp lệ |
| Tối đa 10 Screening Completed, trung bình có trọng số, bỏ mẫu không hợp lệ | Đạt. SQL chỉ lấy cột cần, AsNoTracking; không dùng PromptTest/Queued/Running/Failed; hàm thuần bỏ mẫu bất thường >1 giờ/CV |
| Sàn MinDelayBetweenCallsMs / FakeDelayMs | **Chưa đạt khi cấu hình không tròn 100 ms; đã sửa.** Giữ một chữ số thập phân nhưng không thấp hơn sàn mili giây |
| Mặc định 6,5 giây/CV khi sàn 4,5; DTO ETA và ghi chú | Đạt; giữ nguyên mặc định, tổng thời gian làm tròn lên và ghi rõ không cam kết |
| Development/CvUpload gate, rate limit, multipart | Đạt; không đổi đăng ký route, policy hay giới hạn; Production/Staging/Enabled=false vẫn được test |
| Route tạm A39 trùng `/text` | Đã được xử lý trong PR #62. Chỉ còn route thật trong CvEndpoints; không sửa lại |
| Bảo toàn A38/A39/A41, AI privacy, stage/history, Readiness, ranking, PromptTest, scoring | Kiểm bằng full regression và các fixture SQL giữ catalog; hai lỗi Runtime SQL nền được báo riêng, không nới assertion |
| B38 và API client cũ | Không sửa Web/DTO/route. Chỉ bổ sung bảo vệ response upload và sửa giá trị ETA; B38 cần xử lý 503 khi privacy lease đang bận |

Source chính đã đối chiếu: `TextQuality.cs`, `RunTimeEstimator.cs`, `CvTextContracts.cs`, `CvDetailDto.cs`, `CvUploadResponse.cs`, `CvUploadService.cs`, `CvEndpoints.cs`, `RunEndpoints.cs` và test tương ứng.

## 3. Lỗi, bằng chứng và cách sửa tối thiểu

### F1 — ETA thấp hơn sàn do thứ tự làm tròn

`RunTimeEstimator.Estimate` trước đây clamp bằng sàn rồi `Math.Round(..., 1)`. Với lịch sử 10 CV/1 giây và `MinDelayBetweenCallsMs=4510`, kết quả là `secondsPerCv=4.5`, thấp hơn `minSecondsPerCv=4.51`. Với 500 CV, tổng 2250 giây thấp hơn sàn 2255 giây. AI giả với delay 801 ms cũng gặp lỗi tương tự.

- Trước khi tạo nhánh: project xUnit tạm trong `artifacts/a42-verify/repro/`, tham chiếu DLL Core build từ source main: **3 passed, 4 failed**.
- Sau khi tạo nhánh, thêm 9 ca unit và 4 ca HTTP, giữ implementation chưa sửa: **5 passed, 8 failed** (`regression-red.trx`).
- Sửa đúng một biểu thức: lấy max giữa trung bình đã làm tròn và sàn được làm tròn lên đến 0,1 giây. Ví dụ 4,51 → tối thiểu 4,6; sàn mặc định 4,5 giữ nguyên.
- Test kiểm sàn, độ chính xác một chữ số, tổng thời gian, delay thật/giả và cấu hình gần ngưỡng trên.

### F2 — Metadata CV trùng trong upload nằm ngoài privacy lease

A39 loại `/api/cvs/upload` khỏi `CvPrivacyReadMiddleware`, trong khi A42 đã bổ sung tên file cũ, mã CV, ngày tải và vị trí ứng tuyển vào response upload. Vì vậy response không có `no-store`, có thể đọc metadata dù đang có exclusive privacy lease, và không chặn erasure đi trước response đang được chuẩn bị.

Ba test trước khi sửa middleware đều thất bại (`privacy-red.trx`):

1. Upload trùng qua TestServer: response có ExistingCvId nhưng `Cache-Control` là null.
2. SQL: đang giữ exclusive lease, upload trùng vẫn trả 200 và metadata; kỳ vọng 503 với lỗi an toàn.
3. SQL: pause service sau khi đọc được metadata; kết nối khác vẫn lấy được exclusive lease; kỳ vọng `CvPrivacyBusyException`.

Sửa bằng cách bỏ đúng điều kiện loại trừ upload khỏi middleware A39. Không tạo cơ chế khóa khác. Shared lease có sẵn được giữ xuyên suốt `await next(http)`, gồm handler và serialization. Khi khóa độc quyền đang chạy, upload fail trước handler với 503 an toàn; hết khóa có thể thử lại. Upload đồng thời vẫn dùng shared lease và cơ chế unique SHA256 hiện tại.

Không thêm log PII, tên file, chữ CV, email/điện thoại hay API key. Response upload không được cache sau sửa. Các nhánh lỗi storage kế thừa vẫn có log đường dẫn nội bộ theo GUID; follow-up không thay đổi chính sách log nền này, không tuyên bố đã audit/redact mọi exception của toàn hệ thống.

## 4. Môi trường, build và kết quả kiểm thử

- .NET SDK **10.0.403**, MSBuild **18.9.15**, runtime **10.0.14**, Windows x64.
- Output riêng: `--artifacts-path artifacts/a42-verify/build`; không thay binary của tiến trình demo.
- Restore lần đầu trong sandbox bị lỗi TLS/cache NuGet. Chạy lại bằng cache NuGet của máy và quyền được cấp đã thành công; không sửa package/config để né lỗi.
- `dotnet restore SmartCv.slnx`: thành công. `dotnet build SmartCv.slnx --no-restore`: **0 warnings, 0 errors**, gồm lần build cuối.
- Full test thường: xóa opt-in SQL khỏi riêng environment của tiến trình test; bật hai biến demo ZIP A38/A42 cho fixture giả. TestServer/EF InMemory/thư mục tạm, không gọi Gemini thật.
- SQL chỉ bật fixture đã đọc, tạo **catalog mới có GUID và giữ lại** trên `(localdb)\MSSQLLocalDB`. Không dùng `SmartCvDb` hoặc instance `localhost\BB`; không bật bộ test tự DROP database.

| Lần chạy | Passed | Failed | Skipped | Total | Bằng chứng trong `artifacts/a42-verify/` |
|---|---:|---:|---:|---:|---|
| Full baseline, source giống origin/main, OCR A42 bật | 1833 | 0 | 75 | 1908 | `results/baseline.trx`, `tests-baseline.log` |
| Repro tạm trước tạo nhánh | 3 | 4 | 0 | 7 | `results/floor-repro-before.trx` |
| Regression ETA đỏ trước sửa Core | 5 | 8 | 0 | 13 | `results/regression-red.trx` |
| SQL baseline: A42 + hai Runtime, trước sửa production | 2 | 2 | 0 | 4 | `results/sql-baseline.trx` |
| Focused sau sửa ETA, gồm OCR | 85 | 0 | 0 | 85 | `results/focused-green.trx` |
| Regression privacy đỏ trước sửa middleware | 0 | 3 | 0 | 3 | `results/privacy-red.trx` |
| Toàn bộ A42 cuối, gồm SQL/OCR | 90 | 0 | 0 | 90 | `results/a42-final-focused.trx` |
| Full regression cuối, không bật SQL | 1848 | 0 | 76 | 1924 | `results/full-final.trx`, `full-final.log` |
| SQL regression cuối, chỉ fixture giữ catalog | 42 | 2 | 0 | 44 | `results/sql-final.trx`, `sql-final.log` |

76 skipped của full test thường không được tính là pass: 38 là các bộ SQL có cleanup DROP (A11/A14/A15/A16/A17/A35); còn lại là các fixture SQL opt-in và dữ liệu demo khác chưa bật trong lượt này. Kết quả SQL được liệt kê riêng. Không nới assertion hay thay fixture Runtime để làm xanh.

Phân nhóm lượt SQL cuối: A38 9/9 (gồm một test model và tám ca SQL), A39 24/24, A41 1/1, A42 4/4, A36 1/1, A40 1/1, FullAudit SQL 2/2. Hai Runtime 0/2, fail đúng lỗi nền bên dưới. Các lượt chạy có test trùng nhau; không cộng các hàng thành một tổng test duy nhất.

### OCR thật trên đúng hai ảnh ZIP

| Input | Method / status | Ký tự | Từ | LetterRatio | Level | MaybeMisread | Warnings |
|---|---|---:|---:|---:|---|---|---:|
| `cv-anh-ro.png` | ImageOcr / Completed | 724 | 136 | 0,85 | Good | false | 0 |
| `cv-anh-mo.png` | ImageOcr / Completed | 182 | 36 | 0,68 | Low | true | 3 |

Tesseract `eng+vie`, dữ liệu ngôn ngữ đang có sẵn trên máy. Ảnh mờ bị cảnh báo đọc thiếu, chữ rời rạc và khuyên mở file gốc kiểm tra. Tỷ lệ 0,68 không tự động có nghĩa Good: đây là ba quy tắc độc lập. Test còn tải lại ảnh rõ và xác nhận Duplicate trỏ đúng CV đã lưu.

Chỉ số tổng hợp được append vào `artifacts/a42-tests/ocr-evidence.txt`; không ghi chữ OCR vào báo cáo/log. Số OCR có thể khác trên môi trường khác.

### Hai lỗi Runtime SQL nền

- `RuntimeScriptDeploymentTests.Sqlcmd_default_decoding_or_quoted_identifier_off_stop_before_changes_and_correct_run_matches_migration`: assertion `DatabaseSchemaDiagnostics.InspectAsync(db).IsReady`, dòng 98.
- `RuntimeSchemaSqlTests.Run25_schema_mismatch_reproduces_207_and_reviewed_upgrade_preserves_data_and_HR_decisions`: cùng assertion IsReady, dòng 108.

Cả hai đã được chạy trên DLL build khi production source vẫn giống main (trước sửa Core/middleware), fail cùng assertion. Sau sửa chạy lại cùng fixture: vẫn fail tại dòng 98 và 108; SQL A42 vẫn pass. Không sửa migration, SQL deployment script hay test Runtime trong A42 follow-up. Dấu hiệu là Readiness kiểm schema hiện hành rộng hơn mốc nâng cấp stage của fixture; báo riêng để xử lý lỗi nền, không coi là hai lỗi mới do follow-up.

## 5. Bàn giao UX/UI B38 — đề xuất, chưa sửa giao diện

### Bố cục và ngôn ngữ thiết kế

Dùng token hiện hữu trong `src/SmartCv.Web/wwwroot/app.css`: Deep Teal `--sc-brand-700` (#0f766e), nền `--sc-surface-50`, thẻ `--sc-surface-0`, viền `--sc-line-200`, shadow hiện có; Inter cục bộ có dấu tiếng Việt. Card radius 16 px, section 20 px, khoảng cách theo hệ 8 px. AI Glow chỉ nhấn nhẹ cho hành động/kết quả AI; trạng thái upload/OCR dùng màu ngữ nghĩa và nhãn chữ.

Desktop: tiêu đề và mô tả ngắn; thẻ nguồn/đồng ý; dropzone; bảng file; thanh hành động kèm ETA. Tablet giảm cột phụ. Mobile 360–390 px chuyển bảng thành thẻ và action bar gọn, không che nội dung; kiểm thêm 768/1024/1440 px. Mỗi trạng thái có icon + chữ; không dựa riêng vào màu.

### Luồng và hợp đồng API

1. Chọn một trong **Email, Website, JobBoard, Referral, Other**; checkbox xác nhận ứng viên đồng ý xử lý dữ liệu, mặc định chưa chọn. Khóa cả dropzone, input file và nút tải đến khi hợp lệ. Hiện lý do khóa, không dùng một nút disabled thiếu giải thích. Một nguồn/xác nhận áp dụng cho cả batch.
2. Upload multipart có `files`, `sourceChannel`, `consentConfirmed=true`. Kết quả giữ thứ tự file. `Accepted` nghĩa đã lưu; `Rejected` phân biệt validation/lưu lỗi với `errorCode=Duplicate`. Không hiển thị file trùng là đã tải mới thành công.
3. Trùng DB có ExistingCvId: nhãn **Đã có sẵn**, ngày từ ExistingUploadedAt UTC được định dạng theo locale người dùng, tên file cũ, chip ExistingJobTitles và nút **Xem CV hiện có**. Không có job: ghi chưa ứng tuyển vị trí nào. Trùng trong batch hoặc tombstone không có metadata: chỉ hiển thị thông báo API; không dựng liên kết giả.
4. Theo dõi Pending bằng `GET /api/cvs/{id}`, dừng polling khi Completed/Failed hoặc rời trang. Khi Pending/Failed, textQuality có thể null; không gán thành Empty. Loading skeleton và thông điệp trạng thái phải khác kết quả OCR thật.
5. Good: nhãn nhẹ **Đã đọc**, không cảnh báo vàng. Low/Empty: cảnh báo vàng dịu, tiêu đề và hướng dẫn hành động; luôn có **Xem chữ đã đọc**, **Xem file gốc** nếu còn file, và đọc lại khi phù hợp. Cảnh báo là heuristic, không kết luận ứng viên có chất lượng thấp.
6. Drawer/dialog **Xem chữ đã đọc** gọi `/api/cvs/{id}/text`; desktop có file gốc và text hai vùng, mobile đổi tab/chiếm toàn màn hình. Giữ xuống dòng, cho wrap từ dài; không chèn text OCR dưới dạng HTML. Hiển thị 5 chỉ số: ký tự, từ, tỷ lệ chữ cái (ratio ×100), mức chất lượng, khả năng đọc sai; phương thức extraction là metadata bổ sung. Liệt kê nguyên warnings tiếng Việt.
7. ETA gọi `/api/runs/estimate?count=N` khi số hồ sơ phân tích đổi, debounce và hủy request cũ. Dùng `text` và `note`; ví dụ **10 CV · khoảng 2 phút**. `isDefault` ghi ước tính ban đầu, `aiSimulated` ghi AI giả lập. Không đếm ngược hoặc hứa giờ hoàn tất.

### Trạng thái và khả năng truy cập

- Thiếu nguồn/đồng ý: chỉ rõ lỗi cạnh trường, giữ lựa chọn file để người dùng sửa.
- 400/413: giải thích số file/dung lượng; 429: tôn trọng Retry-After; 503 privacy đang bận: thông báo chờ rồi thử lại có giới hạn, không báo thành công giả.
- 404: CV không còn tìm thấy; 410: đóng/clear dữ liệu nhạy cảm cũ đang giữ và hiện hồ sơ đã ẩn danh, không tiếp tục gọi text/file/extract. Offline/timeout có nút thử lại, giữ trạng thái rõ ràng.
- Không lưu text CV/metadata cũ trong localStorage hoặc cache dùng chung. Tôn trọng `Cache-Control: no-store` của response nhạy cảm.
- Focus trap trong dialog, Esc để đóng, trả focus về nút mở; label/aria-describedby cho nguồn và checkbox, aria-live lịch sự cho trạng thái; bàn phím dùng được toàn luồng. Vùng chạm tối thiểu 44 px, tương phản chữ thường tối thiểu 4,5:1, hỗ trợ reduced motion.
- B38/B42 kiểm tay desktop/mobile, keyboard/screen reader, duplicate có/không job, Pending/Failed/Good/Low/Empty, 404/410/413/429/503 và offline. A42 không dựng mockup hay chỉnh Razor/CSS.

## 6. Danh sách thay đổi để duyệt

| File | Thay đổi |
|---|---|
| `src/SmartCv.Core/Scoring/RunTimeEstimator.cs` | Một biểu thức tính perCv và chú thích để giữ sàn sau làm tròn |
| `src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs` | Bỏ loại trừ upload, dùng lại lease/no-store A39 |
| `tests/SmartCv.Tests/Scoring/RunTimeEstimatorTests.cs` | 9 ca unit về sàn mili giây |
| `tests/SmartCv.Tests/A42UploadSupportEndpointTests.cs` | 4 ca HTTP ETA + 1 ca no-store upload trùng |
| `tests/SmartCv.Tests/A42SqlTests.cs` | 2 ca privacy lease thật với barrier và helper fixture |
| `docs/A42-followup-implementation-report.md` | Báo cáo audit/sửa lỗi và handoff B38 này |

Không thay DTO, endpoint mapping, upload service, Web, Program, appsettings, secret, migration, SQL deployment script hoặc cấu hình worker demo.

- Manifest: `artifacts/a42-followup-commit-files.txt`, chỉ gồm chính xác sáu file trên; artifacts bị ignore và không thuộc manifest commit.
- SHA nền: `artifacts/a42-followup-base-sha.txt`.
- Build, TRX và test tạm ở `artifacts/a42-verify/`, không đưa vào commit.
- Không staged file; không add/commit/push/merge/reset/stash/rebase/clean/cherry-pick, không tạo PR. Chỉ fetch và tạo nhánh sửa đã được yêu cầu.

## 7. Rủi ro và giới hạn nghiệm thu

OCR là heuristic; không đánh giá năng lực ứng viên bằng cảnh báo chất lượng text. ETA vẫn phụ thuộc hàng đợi/retry và dữ liệu lịch sử; thay đổi này chỉ bảo đảm không thấp hơn sàn, có thể cao hơn dưới 0,1 giây/CV để giữ định dạng một chữ số.

Upload nay giữ shared lease trong xử lý và trả response: nhiều upload vẫn được phép đồng thời; erasure có thể phải chờ hoặc báo bận trong cửa sổ này. Khi erasure đang giữ khóa, upload có thể nhận 503 theo cơ chế fail-closed A39. B38 phải thể hiện trạng thái này và cho thử lại.

Không gọi Gemini thật, không sửa dữ liệu demo; toàn bộ SQL test chỉ dùng catalog mới riêng được giữ lại. Danh sách catalog mới của lượt này được tổng hợp trong `artifacts/a42-verify/retained-catalogs-this-audit.txt`; không xóa catalog cũ hoặc mới.

Lượt audit tạo thêm **53 catalog mới**, tất cả được giữ lại: A36 1, A38 8, A39 24, A40 1, A41 1, A42 12, Runtime 6 (gồm các lượt test đỏ/trước/sau). Snapshot metadata trước/sau có 517/570 catalog test, **không có catalog cũ nào biến mất**. Không dọn 45/113 catalog của các nhiệm vụ trước hay bất kỳ dữ liệu CV/database sẵn có.

Trạng thái cuối: nhánh `fix/a42-upload-support-followup`, HEAD và origin/main vẫn là SHA nền; **5 tracked modified + 1 untracked report**, không staged. Manifest khớp chính xác sáu đường dẫn của `git status --porcelain=v1 --untracked-files=all`. `git diff --check` đạt (exit 0); cảnh báo LF/CRLF không phải lỗi whitespace. Các đường dẫn bị giới hạn phạm vi ở trên được kiểm bằng `git diff origin/main` và không thay đổi.

Follow-up sẵn sàng để duyệt local. Không tuyên bố toàn bộ SQL suite xanh vì còn hai lỗi Runtime nền. Không add/commit/push hay tạo PR; dừng để người dùng duyệt.
