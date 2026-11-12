# Báo cáo B36 – Cần xem kỹ và chế độ Ẩn danh

Ngày: 12/11/2026. Phạm vi: Ranking, Evaluation Detail và các thành phần Web liên quan.

## 1. Nhánh, commit nền và kiểm tra đầu vào

- Nhánh duy nhất: `feature/B36-review-flags-anonymity`.
- Commit nền/HEAD: `885c910ffcc3a6c18dd635df22247a32a4ede8fd` – `feat(A40): add review flags and candidate comparison API (#56)`.
- Ban đầu ở nhánh A40 đã được người dùng commit, working tree sạch: không modified/staged/untracked/conflict.
- Origin đúng `TuyetAnh0101/AI-CV-Screening`; đã fetch `origin --prune`, không pull/merge.
- Đã xác minh origin/main chứa A37 (#54), A40 (#56), B34 (#50), B35 (#55), A35 và hardening (#49/#53). Nhánh B36 chưa có ở local/remote, được tạo trực tiếp từ origin/main.
- Solution `SmartCv.slnx`, SDK `10.0.403`, output riêng `artifacts/b36-build`. Restore một lần để tạo assets cho output mới, không nâng package.

## 2. Tài liệu và yêu cầu ZIP

Đã đọc mục B36, mẫu AnonymityState, AnonymousToggle Razor/CSS, phần B36 trong RankingTable/EvaluationDetail và CSS, snippets DI/app.css cùng API contracts mới nhất. Các PDF demo là dữ liệu tuyển dụng, không phải mockup UI. Bộ kiểm thử đọc PDF #17/#18 từ ZIP; browser dùng văn bản từ hai PDF này trong fixture synthetic.

Không ghi đè file bằng mẫu. Các khác biệt so với ZIP:

- Mẫu dùng masker cho nội dung tự do. B36 mới **không hiển thị** nhận xét/bằng chứng/ghi chú cũ khi ẩn danh vì masker theo tên/email/điện thoại không bảo đảm loại hết địa chỉ hoặc định danh khác. JSON cũ hoàn toàn không được đưa vào DOM khi ẩn danh.
- Bí danh có thêm mã hồ sơ ổn định để phân biệt đồng hạng: `Ứng viên #1 · HS2`, `Ứng viên #1 · HS3`. Detail và Ranking dùng chung quy tắc.
- Lọc cờ độc lập với giai đoạn để có thể kết hợp cả hai; số N lấy từ `RankingResponse.FlaggedCount`.
- Xử lý storage lỗi/không giải mã được theo hướng tiếp tục che, có retry và cảnh báo nếu lưu không thành công. Một lần đọc chậm không được ghi đè lựa chọn người dùng vừa đổi.
- Bổ sung kiểm tra thứ tự khi bằng điểm: ẩn danh không dùng tên/file thật làm tiêu chí phụ.
- Popover có nút truy cập bằng chuột, bàn phím và touch, đóng bằng Escape; không dùng tooltip cố định 300px dễ bị cắt ở mobile.
- Giữ toàn bộ xử lý A35/B34/B35 mới nhất thay vì chép các trang mẫu cũ. Không thực hiện các lệnh stage/commit/merge/push trong hướng dẫn ZIP.

## 3. Đánh giá UI trước/sau

Trước: thanh công cụ gồm tìm kiếm, nhiều nhóm lọc và sắp xếp trong một hàng flex; khi xuống màn hình nhỏ việc phân nhóm khó theo dõi. Chưa có cờ hoặc công tắc riêng tư. Tên còn xuất hiện ở KPI điểm cao nhất, aria-label, dialog HR; trang chi tiết hiển thị trực tiếp nội dung AI, JSON và bằng chứng cũ.

Sau:

- Ranking tách hàng tìm kiếm/sắp xếp, hàng mức phù hợp và hàng giai đoạn/cờ. Nút Cần xem kỹ dùng amber, tách biệt với màu giai đoạn.
- Công tắc rõ ràng, có chú thích giảm thiên kiến và trạng thái chờ đọc storage. Dùng Inter, Deep Teal và token hiện có; không sửa App Shell hoặc CSS toàn cục.
- Desktop giữ bảng đầy đủ và nhóm công cụ thông thoáng. Tablet giữ cơ chế cuộn bảng trong vùng riêng, không đẩy tràn tài liệu.
- Mobile 390px giữ dạng thẻ một cột sẵn có, bộ lọc xuống hàng, nút/điều khiển dễ chạm. Popover nằm trong luồng bố cục, không vượt viewport.
- Detail đặt toggle cùng thanh điều hướng; khối “Cần xem kỹ trước khi quyết định” ngay dưới header. Bố cục cột đánh giá/HR của B35, lịch sử và metadata được giữ nguyên.
- Loading/empty/error/success/disabled/focus được giữ. Không có hồ sơ khớp cờ dùng empty state hiện có, Xóa bộ lọc cũng tắt lọc cờ.

## 4. Cờ Cần xem kỹ

- Chỉ đọc ReviewFlags do A40 trả về; không gọi thuật toán xét cờ ở Web.
- Biểu tượng ⚑ amber cho hồ sơ có ít nhất một cờ; không hiển thị nếu danh sách rỗng.
- Hover/focus/click/tap mở danh sách toàn bộ Message. Nút có aria-expanded/aria-describedby; hỗ trợ Escape, blur và click/tap mở/đóng.
- Lọc “Cần xem kỹ N” kết hợp mức phù hợp, giai đoạn, tìm kiếm và sắp xếp. N là số hồ sơ có cờ của Job do API trả về, không phải tổng số cờ và không tự tính lại theo bộ lọc cục bộ. Số dòng đang hiển thị phản ánh bộ lọc đang kết hợp.
- Detail liệt kê Message của API trong khối cảnh báo gọn; không thay điểm hoặc giai đoạn.

## 5. Cơ chế Ẩn danh và bề mặt được bảo vệ

`AnonymityState` scoped theo circuit, dùng ProtectedLocalStorage với khóa `scapp.anonymous`. AnonymousToggle chỉ đọc storage sau interactive render. Trước khi đọc xong, Enabled luôn true; prerender đã che, không đợi JavaScript rồi mới đổi tên.

| Bề mặt | Khi Ẩn danh |
|---|---|
| Tên trong bảng/header/điểm cao nhất | Bí danh có hạng và mã hồ sơ |
| Chữ viết tắt | Số hạng hoặc mã hồ sơ, không lấy chữ tên/file |
| Email, tên file, tooltip file | Ẩn liên hệ, dùng “CV đã ẩn tên” |
| PageTitle/tab trình duyệt | Bí danh; khi chưa có dữ liệu dùng tiêu đề trung tính |
| aria-label checkbox/select, tiêu đề dialog HR | Bí danh, không chèn tên/file thật |
| Nhận xét AI, bằng chứng kỹ năng cũ | Thông báo nội dung đang ẩn; không đưa chuỗi gốc vào HTML |
| JSON gốc | Không render pre hoặc nội dung JSON |
| Ghi chú/actor trong timeline | Thông báo ghi chú bị ẩn và “HR đã ghi nhận”; dữ liệu gốc không đổi |
| Lỗi xử lý và nhãn mức không tin cậy | Che nội dung tự do/nhãn lạ, không đưa vào tooltip |
| Lỗi quyết định HR | Thông báo tiếng Việt theo mã HTTP; 409 vẫn yêu cầu kiểm tra dữ liệu mới, không echo error text nhạy cảm |
| CV gốc | Nút disabled, không render href; CheckCvAsync có guard trước và sau await |
| Tìm kiếm | Chỉ so với bí danh; tên/file thật không được dùng |
| Sắp xếp | Ẩn NameAsc, không dùng tên/file thật làm tie-break khi ẩn danh |

Bật/tắt không thay DTO gốc hoặc gửi thao tác ghi. Khi đổi chế độ, xóa từ khóa cũ và lựa chọn hàng để không giữ tên thật trong ô tìm hoặc áp dụng nhầm lựa chọn qua bộ lọc. Tắt ẩn danh khôi phục nội dung gốc từ dữ liệu API.

Lựa chọn giữ khi điều hướng Ranking/Detail, F5 và mở lại browser cùng profile/origin. Missing preference được coi là tắt sau khi đọc thành công; trước đó vẫn che. Lỗi đọc/khóa mã hóa đổi tiếp tục che, không tự xóa giá trị rồi hiện tên. Lỗi lưu có cảnh báo rõ: phiên hiện tại vẫn dùng lựa chọn vừa chọn, nhưng tải lại có thể dùng lựa chọn cũ chưa ghi đè được.

Ẩn danh là chức năng trình bày để giảm thiên kiến, không thay kiểm soát quyền Backend. Danh tính HR đang nhập cho quyết định vẫn là thông tin của người thao tác; nội dung do người dùng tự nhập trong form không phải dữ liệu ứng viên được tự động chèn. Lịch sử đã lưu được che như bảng trên.

## 6. Bằng chứng browser E2E

Chạy Chromium headless với API/Web riêng, SQL Server LocalDB catalog mới `SmartCv_B36_Test_<GUID>`, kiểm tra trên 1440/768/390px. API cấu hình Gemini fake/loopback, Scoring:WorkerEnabled=false. Toàn bộ CV fixture có ExtractionStatus=Completed; không đưa vào hàng đợi xử lý mới.

Fixture có ba ứng viên synthetic, hai hồ sơ đồng hạng, legacy nhận xét/JSON/bằng chứng/ghi chú chứa dấu nhận diện thử nghiệm. Văn bản CV lấy từ PDF demo #17/#18; điểm được đặt có chủ đích để kiểm tra UI/A40, **không phải kết quả Gemini thật**.

Kết quả cuối: **PASS**, PageError=0:

- API thật trả FlaggedCount=2; một hồ sơ có 3 cờ, hồ sơ khác có 1 cờ, hồ sơ còn lại không có cờ.
- HTML prerender của cả Ranking/Detail không có tên/email/file/địa chỉ thử nghiệm.
- DOM (gồm attribute/tooltip/aria) và title khi ẩn danh không có các dấu nhận diện thử nghiệm.
- Bí danh hai hồ sơ đồng hạng khác nhau; tìm tên thật không ra, `#1` ra hai hồ sơ đồng hạng.
- Kết hợp cờ và dự đoán Đạt lọc đúng số hàng; trường hợp kết hợp stage/search/sort được kiểm tra thêm trong unit test.
- Tab/focus mở lý do, Escape đóng; click/tap mở/đóng, popover 390px có bounding box trong viewport.
- Ranking → Detail → Ranking giữ chế độ; Detail không có JSON/CV href, nút CV gốc disabled.
- F5 vẫn ẩn; đóng context/browser rồi mở lại profile vẫn ẩn. Tắt chế độ khôi phục tên và JSON ở Detail.
- Không có document overflow ngang ở cả ba viewport. Đã xem ảnh desktop/mobile sau khi chụp.
- Snapshot trước/sau toàn luồng: điểm, trạng thái, StageVersion và số event không đổi; không phát sinh quyết định HR giả.

Browser đã tìm ra hai lỗi của popover trong lúc triển khai: native details đóng không hiện nội dung chỉ bằng CSS focus; mouseleave có thể đóng ngay sau tap. Đã chuyển sang nút có state và tách trạng thái ghim khi click/tap, rồi chạy lại thành công. Không bỏ assertion focus/touch để lấy xanh.

Ảnh và bằng chứng chỉ ở artifacts, bị ignore:

- [Ranking trước](../artifacts/b36-tests/ranking-before.png), [Detail trước](../artifacts/b36-tests/detail-before.png).
- [Ranking desktop](../artifacts/b36-tests/ranking-1440-after.png), [tablet](../artifacts/b36-tests/ranking-768-after.png), [mobile](../artifacts/b36-tests/ranking-390-after.png).
- [Detail desktop](../artifacts/b36-tests/detail-1440-after.png), [tablet](../artifacts/b36-tests/detail-768-after.png), [mobile](../artifacts/b36-tests/detail-390-after.png).
- `artifacts/b36-tests/browser-evidence.json`, `artifacts/b36-browser.log`, `artifacts/b36-tests/retained-databases.txt`.

Ảnh trước dùng binary Web trước B36 còn ở artifacts/a40-build, với cùng API/fixture demo; ảnh sau dùng build B36. Không phải ảnh production. Database thử của các lượt debug/E2E được giữ lại, không drop/reset. Không áp dụng migration hoặc ghi vào database ứng dụng; chỉ migrate schema sẵn có trong catalog GUID mới để chuẩn bị fixture.

## 7. Danh sách chính xác file sửa/mới

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Web/Services/AnonymityState.cs` | Mới | State fail-closed, ProtectedLocalStorage, bí danh/tìm/sort/nội dung an toàn |
| `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` | Sửa | Đăng ký state và ProtectedLocalStorage scoped |
| `src/SmartCv.Web/Components/Shared/AnonymousToggle.razor` | Mới | Switch, loading, lưu lựa chọn, warning/retry |
| `src/SmartCv.Web/Components/Shared/AnonymousToggle.razor.css` | Mới | Switch Deep Teal, touch/focus |
| `src/SmartCv.Web/Components/Shared/ReviewFlagsBadge.razor` | Mới | Cờ và popover truy cập bằng pointer/keyboard/touch |
| `src/SmartCv.Web/Components/Shared/ReviewFlagsBadge.razor.css` | Mới | Amber, ngắt dòng và giới hạn chiều rộng |
| `src/SmartCv.Web/Components/Pages/Results.razor` | Sửa | Che KPI, phản ứng khi đổi chế độ, truyền FlaggedCount |
| `src/SmartCv.Web/Components/Results/RankingTable.razor` | Sửa | Toggle, cờ, filter, tên/file/aria an toàn |
| `src/SmartCv.Web/Components/Results/RankingTable.razor.cs` | Sửa | Kết hợp filter, tên dialog, search/sort ẩn danh, lỗi HR an toàn |
| `src/SmartCv.Web/Components/Results/RankingTable.razor.css` | Sửa | Nhóm công cụ, màu filter cờ và responsive |
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor` | Sửa | Toggle/cờ, title/contact/free text/JSON/CV guard |
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css` | Sửa | Toolbar và khối cảnh báo gọn |
| `src/SmartCv.Web/Components/Results/HrDecisionPanel.razor` | Sửa | Truyền chế độ riêng tư xuống timeline; giữ nguyên luồng ghi |
| `src/SmartCv.Web/Components/Results/StageTimeline.razor` | Sửa | Che note/actor cũ khi ẩn danh |
| `tests/SmartCv.Tests/B36PrivacyTests.cs` | Mới | 16 case storage/race/search/filter/SSR/privacy/error/badge |
| `tests/SmartCv.Tests/B34StageUiTests.cs` | Sửa | Fixture chủ động opt-out; thay assert cấm tính năng chưa tồn tại bằng kiểm tra toggle hiện diện nhưng dữ liệu ở normal view |
| `tests/SmartCv.Tests/EvaluationDetailUiTests.cs` | Sửa | Đăng ký state đã đọc và opt-out để giữ mục đích test UI cũ |
| `tests/SmartCv.Tests/FitLevelUiTests.cs` | Sửa | Cùng fixture normal view cho test nhãn/điểm |
| `tests/SmartCv.Tests/ResultsUiTests.cs` | Sửa | Cùng fixture normal view cho test Ranking cũ |
| `tests/SmartCv.Tests/RunProgressUiTests.cs` | Sửa | Cùng fixture normal view cho test polling/progress |
| `docs/B36-implementation-report.md` | Mới | Báo cáo này |

Tổng: **14 file sửa, 7 file mới**, đều thuộc B36 hoặc hồi quy trực tiếp. Đã giải thích thay đổi fixture trước khi sửa; giữ các assert nghiệp vụ stage/bulk/count/history và không xóa test cũ. Assert B34 trước đây cấm chữ “Ẩn danh” không còn đúng yêu cầu mới; thay bằng kiểm tra có công tắc và không dùng bí danh trong fixture đã chủ động tắt.

`artifacts/b36-commit-files.txt` chứa đúng 21 đường dẫn trên, mỗi dòng một file. Manifest/helper/profile/log/ảnh/TRX chỉ dùng cục bộ, không đưa vào commit. Không thay Core/API A35/A37/A40, database/migration, ConnectionStrings, secrets, Gemini hoặc App Shell.

## 8. Build/tests và hồi quy

| Nhóm | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Baseline full trước B36 | 1567 | 0 | 43 |
| Focused B36 cuối | 16 | 0 | 0 |
| Full regression cuối | 1583 | 0 | 43 |
| Browser/API/SQL E2E cuối | 1 | 0 | 0 |

Baseline tổng 1610, full cuối tổng 1626, tăng đúng 16 case. Các nhóm focused/browser không cộng vào số full suite. Build cuối **0 warning, 0 error**. Một lượt UI focused rộng trước khi bổ sung 5 case lỗi HR và 1 case focus đã đạt 195 pass, 0 fail, 0 skipped.

43 skipped: 42 SQL opt-in cũ và 1 SQL opt-in A40 không bật trong nhiệm vụ B36. Test PDF A40/B34 được bật bằng biến ZIP, không bị skip. Browser dùng SQL Server thật qua fixture riêng; không coi các case SQL skipped là đã pass.

Các test B36 gồm: mã hóa/lưu/khôi phục preference; trạng thái chưa đọc; lỗi storage/ciphertext; retry; ghi lựa chọn lỗi; đọc chậm không ghi đè toggle; bí danh đồng hạng; tìm kiếm tên/file bị chặn; tie-sort không dùng định danh; filter cờ kết hợp; SSR không chứa identity/note/JSON; nút cờ 0/1/nhiều lý do; giữ lý do khi vẫn còn keyboard focus dù pointer rời đi; 400/404/409/500/offline vẫn rõ trạng thái mà không echo nội dung nhạy cảm.

Các suite A35/A37/A40/B34/B35 vẫn có trong full regression. Giữ StageVersion, lịch sử, đồng bộ sau lưu và không retry thao tác ghi. Thay đổi B36 không gửi quyết định hoặc tái tính ReviewFlags. Các SQL concurrency/trigger opt-in cũ chưa được bật lại; không tuyên bố đã kiểm chứng lại toàn bộ DB.

Lệnh kiểm chứng chính:

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/b36-build --no-restore
$env:SMARTCV_B34_DEMO_ZIP='C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip'
$env:SMARTCV_A40_DEMO_ZIP=$env:SMARTCV_B34_DEMO_ZIP
dotnet test SmartCv.slnx --artifacts-path artifacts/b36-build --no-restore --no-build --filter 'FullyQualifiedName~B36PrivacyTests' --logger 'trx;LogFileName=b36-unit.trx' --results-directory artifacts/b36-tests
dotnet test SmartCv.slnx --artifacts-path artifacts/b36-build --no-restore --no-build --logger 'trx;LogFileName=full.trx' --results-directory artifacts/b36-tests
```

Evidence: `artifacts/b36-build.log`, `artifacts/b36-baseline-test.log`, `artifacts/b36-focused.log`, `artifacts/b36-unit.log`, `artifacts/b36-full.log`, các TRX trong `artifacts/b36-tests`. Browser helper nằm `artifacts/b36-browser`, chạy với output `artifacts/b36-helper`; chỉ dừng các process do helper tự tạo, không kill API/Web người dùng.

## 9. Giới hạn và rủi ro

- Ẩn danh áp dụng Ranking/Detail và thành phần trực tiếp, đúng hướng dẫn B36; chưa mở rộng Upload/System Prompt hoặc trang Compare B37 chưa triển khai.
- Không phải phân quyền hay ẩn dữ liệu ở API/database. Nội dung gốc vẫn được lưu để khôi phục khi tắt chế độ. API có quyền truy cập vẫn trả dữ liệu gốc.
- Không cố đoán toàn bộ PII trong free text; che hẳn các vùng đó. Message cờ dùng trực tiếp hợp đồng A40 vốn không chứa PII. Metadata Job/Prompt và tên HR đang thao tác không phải bí danh ứng viên.
- Khi local storage không ghi được, không thể bảo đảm lưu qua restart; có warning rõ và fail-closed nếu lần đọc tiếp không thành công. Cần giữ key ring Data Protection phù hợp khi triển khai nhiều instance để đọc được preference cũ; mất key dẫn tới tiếp tục che, không tự hiện tên.
- Chưa thử Safari/Firefox, thiết bị vật lý hoặc screen reader thật; đã thử Chromium có touch emulation, keyboard và attribute semantics.
- Browser không tải file CV gốc ở normal view vì fixture chỉ phục vụ kiểm tra metadata/privacy. Guard ẩn danh được kiểm tra; các unit CV/error/late response cũ vẫn chạy.
- Không gọi Gemini thật hoặc kiểm thử lại xử lý hàng loạt AI/OCR. Các vấn đề runtime/loading được ghi nhận ở nhiệm vụ trước không được coi là đã sửa bởi B36.

## 10. Git cuối và kết luận

Working tree gồm đúng 14 file modified và 7 file untracked ở mục 7; không staged/conflict. Manifest được sinh từ `git diff --name-only` và `git ls-files --others --exclude-standard` sau khi có báo cáo.

```text
## feature/B36-review-flags-anonymity...origin/main
 M src/SmartCv.Web/Components/Pages/EvaluationDetail.razor
 M src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css
 M src/SmartCv.Web/Components/Pages/Results.razor
 M src/SmartCv.Web/Components/Results/HrDecisionPanel.razor
 M src/SmartCv.Web/Components/Results/RankingTable.razor
 M src/SmartCv.Web/Components/Results/RankingTable.razor.cs
 M src/SmartCv.Web/Components/Results/RankingTable.razor.css
 M src/SmartCv.Web/Components/Results/StageTimeline.razor
 M src/SmartCv.Web/Services/ServiceCollectionExtensions.cs
 M tests/SmartCv.Tests/B34StageUiTests.cs
 M tests/SmartCv.Tests/EvaluationDetailUiTests.cs
 M tests/SmartCv.Tests/FitLevelUiTests.cs
 M tests/SmartCv.Tests/ResultsUiTests.cs
 M tests/SmartCv.Tests/RunProgressUiTests.cs
?? docs/B36-implementation-report.md
?? src/SmartCv.Web/Components/Shared/AnonymousToggle.razor
?? src/SmartCv.Web/Components/Shared/AnonymousToggle.razor.css
?? src/SmartCv.Web/Components/Shared/ReviewFlagsBadge.razor
?? src/SmartCv.Web/Components/Shared/ReviewFlagsBadge.razor.css
?? src/SmartCv.Web/Services/AnonymityState.cs
?? tests/SmartCv.Tests/B36PrivacyTests.cs
```

`git diff --stat`: 14 tracked files changed, 101 insertions(+), 41 deletions(-). Không gồm 7 file mới chưa theo dõi; danh sách đầy đủ đã có trong manifest và bảng mục 7.

```text
 .../Components/Pages/EvaluationDetail.razor        | 40 +++++++++++++++-------
 .../Components/Pages/EvaluationDetail.razor.css    |  4 +++
 src/SmartCv.Web/Components/Pages/Results.razor     |  8 +++--
 .../Components/Results/HrDecisionPanel.razor       |  3 +-
 .../Components/Results/RankingTable.razor          | 25 ++++++++------
 .../Components/Results/RankingTable.razor.cs       | 29 +++++++++++++---
 .../Components/Results/RankingTable.razor.css      | 11 +++++-
 .../Components/Results/StageTimeline.razor         |  6 ++--
 .../Services/ServiceCollectionExtensions.cs        |  2 ++
 tests/SmartCv.Tests/B34StageUiTests.cs             |  7 ++--
 tests/SmartCv.Tests/EvaluationDetailUiTests.cs     |  2 +-
 tests/SmartCv.Tests/FitLevelUiTests.cs             |  1 +
 tests/SmartCv.Tests/ResultsUiTests.cs              |  2 +-
 tests/SmartCv.Tests/RunProgressUiTests.cs          |  2 +-
 14 files changed, 101 insertions(+), 41 deletions(-)
```

`git diff --check`: không có lỗi whitespace; Git có cảnh báo LF→CRLF theo cấu hình Windows ở một số file, không đổi cấu hình Git. `git diff --cached --name-status`: rỗng. HEAD vẫn bằng commit nền. Không stage, commit, push, merge, rebase hoặc tạo PR; không xóa code cũ hoặc dữ liệu ứng viên thật.

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT** trong phạm vi B36, với các giới hạn trên. Dừng tại đây; người dùng quyết định các thao tác Git tiếp theo.
