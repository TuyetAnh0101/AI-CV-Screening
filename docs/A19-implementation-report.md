# A19 — Báo cáo triển khai trước khi commit

Ngày thực hiện: 17/11/2026 (Việt Nam). Phạm vi duy nhất: A19 API tổng quan. Báo cáo này và manifest dành cho chủ dự án kiểm tra; chưa stage, commit, push, merge hoặc tạo PR.

## 1. Nền Git và tài liệu đã đọc

- Repository: `C:\TTCN\project\AI-CV-Screening`.
- Remote: `origin`, `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Working tree ban đầu sạch, nhánh ban đầu `feature/b38-upload-practical`, HEAD `ae5ba9b2bc21efa57c0a9589e17eb4bae21cac03`.
- Đã `git fetch origin --prune`; `origin/main` cập nhật từ `94265e2` lên **`6a9889659a28c4e61e2a866b56e6c3ca8cc2f9fb`**, commit `feat(B38): implement practical CV upload experience (#64)`.
- Không có nhánh A19 local/remote, không có merge/rebase/cherry-pick dở dang, không có A19 hoặc VietnamTime trong nền. Đã tạo `feature/a19-dashboard-api` bằng `git switch --no-track -c feature/a19-dashboard-api origin/main`. Không có upstream trỏ nhầm main; HEAD vẫn tại commit nền trên.
- A35 có `ApplicationStageEndpoints`, constants, `ApplicationEvents` và các kiểm thử. A40 có `ReviewFlags`, `ReviewInput`, `ReviewFlagOptions`, `EvaluationReviewReader` và tích hợp Ranking/Detail/Compare. Đã đọc trước khi tích hợp. Không ghi đè thay đổi của đồng đội.
- Git trong sandbox gặp ownership/permission; sử dụng `git -c safe.directory=...` theo từng lệnh và quyền thực thi được công cụ cho phép cho fetch/tạo nhánh. Không sửa global Git config.

Nguồn đã đọc đầy đủ:

1. `C:\TTCN\A19_Codex_Prompt_20261117.md`; SHA256 `E60A76C947F35807E2885FE4B9A44E0B35316FE30140EA56CE2515A9ABE4B31D`.
2. `C:\TTCN\A19_B26_SmartCv.zip`; SHA256 `3E0C98598DA639AC0B4B27D6CC400C794E5A772B8154437741954A198FAC1327`.
3. Trong ZIP: toàn bộ `HUONG_DAN_A19_A20_A21_A22_A25_B19_B20_B21_B26_B22.md`, 6 file `A19/src` (contracts, funnel, screening time, period, VietnamTime, endpoint), 2 file `A19/tests`, `database/check_a19_dashboard.sql`, `snippets/Program.A19-A25.cs.txt`.

Đã kiểm kê ZIP: **không có PNG/JPG/JPEG/SVG/WebP/mockup/screenshot**. Không sử dụng hoặc tuyên bố có ảnh tham chiếu. Mã được tích hợp theo source hiện tại; không chép đè nguyên file mẫu. Các lệnh drop database/Git publish trong guide cũ không được thực hiện.

## 2. API, DTO và định nghĩa số liệu

`GET /api/dashboard/summary?jobId=&from=&to=` trả `DashboardSummaryDto`, JSON camelCase, dùng `RunSummaryDto` hiện hữu cho recent runs. Không sửa contract hoặc hành vi endpoint cũ.

| Yêu cầu | Kết quả triển khai |
| --- | --- |
| Bộ lọc | Job tùy chọn; `from`/`to` là ngày Việt Nam `yyyy-MM-dd`, bao gồm hai đầu; đổi UTC+7 cố định thành `[FromUtc, ToUtc)`, không phụ thuộc timezone OS. Bỏ trống là không giới hạn. |
| `totalCvs`, `failedCvs` | Không chọn Job: đếm CV theo `UploadedAt`, gồm CV chưa gắn Job. Có Job: tập CV distinct qua Applications trong scope `CreatedAt`; failed theo đúng tập đó. |
| `totalApplications` | Số hồ sơ CV × Job theo Job và `Applications.CreatedAt`. Một CV ứng tuyển hai Job được tính thành hai hồ sơ. |
| `activeJobs` | Job hiện đang tuyển; bỏ qua ngày, có áp dụng Job đã chọn. Job tồn tại nhưng ngừng tuyển cho giá trị 0. |
| `runningRuns` | Toàn bộ runs Queued/Running hiện tại, bỏ qua cả ngày và Job. |
| `flaggedUnresolved` | Chỉ New/Reviewing trong scope. Mỗi hồ sơ chọn Screening mới nhất theo `Evaluation.CreatedAt DESC, EvaluationId DESC`, xét mọi trạng thái trước rồi mới áp dụng A40. Pending/Failed mới thay thế Completed cũ; PromptTest không thay thế Screening. |
| `flaggedJobId` | Job có nhiều hồ sơ cần xem kỹ nhất; bằng nhau chọn JobId nhỏ hơn; không có cờ trả null. |
| `funnel` | Luôn 4 bước New/Reviewing/Shortlisted/Interview với tên tiếng Việt chuẩn. Current là trạng thái hiện tại. Reached xét current + cả FromStage/ToStage của toàn bộ lịch sử đúng hồ sơ; đi tắt và sau đó Rejected/OnHold vẫn giữ reached; không đếm trùng. |
| Conversion | Reached bước hiện tại / reached bước trước × 100, 1 số thập phân, AwayFromZero; bước đầu null, mẫu số 0 trả 0. |
| `rejectedCount`, `onHoldCount` | Trạng thái hiện tại của hồ sơ trong scope, không cộng số lần chuyển trạng thái. |
| `screeningTime` | Từ Application.CreatedAt tới HR event đầu tiên, không dùng ngày upload CV. Không có event thì loại khỏi mẫu. Count, averageHours, medianHours; làm tròn 1 chữ số. Timestamp ngược thứ tự clamp về 0, vẫn giữ mẫu. |
| `rejectReasons` | Đủ 6 mã/tên A35. `isProfessional` tái sử dụng quy tắc A36 `LabelSuggestionRules`. Chỉ hồ sơ hiện Rejected; phần trăm chia **tổng Rejected**, gồm dữ liệu legacy thiếu lý do; không tự gán lý do. Giảm theo count, hòa theo thứ tự constants. |
| `recentRuns` | Tối đa 5, CreatedAt DESC rồi RunId DESC. Job lọc qua Evaluation/Application; không lọc ngày. Jobs của mỗi run được lấy theo lô, không N+1; timestamp trả UTC có Z. |
| Trống | `isEmpty = TotalCvs == 0 && TotalApplications == 0`. Funnel 4 bước, 6 lý do, screening count=0/average=null/median=null. Catalog hoàn toàn trống có tất cả counts=0 và recentRuns=[]. Scope không có hồ sơ vẫn có thể có activeJobs/runningRuns/recentRuns theo định nghĩa trên. |

`FunnelStepDto`: stage, stageName, current, reached, conversionPercent. `ScreeningTimeDto`: count, averageHours, medianHours. `ReasonCountDto`: reason, reasonName, isProfessional, count, percent. `JobId`, `From`, `To` trong response phản ánh bộ lọc đã chuẩn hóa.

### HTTP và bảo mật

- 400 ProblemDetails tiếng Việt cho Job âm/0/sai định dạng/vượt Int32, ngày sai định dạng/không tồn tại/from>to, mốc UTC không biểu diễn được và query parameter lặp lại.
- Ngày nhỏ nhất dùng làm from và `9999-12-31` dùng làm to trả 400, không overflow thành 500. Ngày nhuận, đầu/cuối năm, mốc 16:59:59.9999999/17:00 UTC và bộ lọc một đầu đều được kiểm tra.
- 404 ProblemDetails tiếng Việt khi Job hợp lệ nhưng không tồn tại.
- Cùng chốt A35/A40: **chỉ map tại Development và `CvUpload:Endpoint:Enabled=true`**. Development tắt gate, Staging/Production tắt gate đều 404. Bật gate ngoài Development bị startup guard CV hiện hữu từ chối. Chưa có M6 nên không tạo role/auth giả. Để phục vụ Production cần M6 thật sau này; hiện tại fail-closed đúng yêu cầu.
- Mọi response của route đã map, kể cả 400/404, có `Cache-Control: no-store`. DTO không có tên/email ứng viên, tên/path file, nội dung CV, reasoning, raw AI, system prompt hay khóa bí mật.
- API chỉ SELECT; không SaveChanges, read-repair, migrate, queue run hay gọi Gemini. Không thêm Dashboard vào middleware khóa PII: response chỉ chứa số liệu tổng hợp, không trả danh tính/nội dung CV. Cờ có thể cập nhật khi A39 ẩn danh hóa, nhưng không làm lộ dữ liệu bị xóa.

### Khác với mẫu ZIP

1. Thêm chốt truy cập và no-store; mẫu mở public.
2. Parse bộ lọc chủ động, lỗi tiếng Việt nhất quán; kiểm tra repeated parameters và overflow UTC.
3. A40 hiện tại dùng `HasSavedMissingMustHave` và audit marker cap đã lưu trong Reasoning. Không dùng chữ ký ReviewInput cũ hoặc suy cap theo Scoring hiện tại. Test đặt cap cấu hình hiện tại thành 10 nhưng vẫn nhận đúng cap lịch sử 69.
4. Chọn latest Screening mọi status; mẫu lọc Completed trước sẽ hồi sinh cờ cũ.
5. Phễu xét cả hai đầu sự kiện, bao gồm lịch sử nhập từ một bước xa; mẫu chỉ ToStage.
6. Mẫu tính reject denominator bằng số có lý do; bản này chia tổng Rejected đúng prompt. Legacy thiếu lý do có thể làm tổng phần trăm dưới 100%; do làm tròn tổng cũng có thể lệch 0,1%.
7. Aggregation tại SQL; không tải mọi event/evaluation/timing vào RAM. Median SQL dùng `PERCENTILE_CONT`, `DATEDIFF_BIG(MICROSECOND)` tránh overflow 68 năm của DATEDIFF(SECOND).

## 3. Kết quả build, unit, integration, regression

Môi trường: Windows x64, .NET SDK **10.0.403**, runtime **10.0.14**, MSBuild 18.9.15; SQL Server instance `.\BB`, version **16.0.1000.6**. Build dùng artifacts riêng, không khóa/kill API hoặc Web của người dùng.

Các lệnh chính (chạy từ repository):

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a19-build -v minimal
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/a19-build -v minimal
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a19-build --logger 'trx;LogFileName=a19-baseline.trx' --results-directory artifacts/a19-baseline-results
$env:SMARTCV_A19_TEST_SQL_CONNECTION = 'Server=.\BB;Database=SmartCv_A19_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-restore --artifacts-path artifacts/a19-build --filter 'FullyQualifiedName~SmartCv.Tests.Dashboard' --logger 'trx;LogFileName=a19-focused.trx' --results-directory artifacts/a19-focused-results
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a19-build --logger 'trx;LogFileName=a19-full.trx' --results-directory artifacts/a19-full-results
```

Baseline được chạy trước khi sửa source với các SQL opt-in chưa bật. Chỉ bật biến A19 cho focused/full cuối; không bật các fixture cũ có dọn database.

| Kiểm tra | Passed | Failed | Skipped | Tổng / kết quả |
| --- | ---: | ---: | ---: | --- |
| Restore | — | — | — | Thành công sau khi chạy ngoài sandbox; sandbox ban đầu lỗi TLS/credentials NuGet |
| Baseline build | — | 0 | — | 0 errors, 1 warning NU1900 có sẵn ở SmartCv.Web |
| Baseline full regression | 1.912 | 0 | 78 | 1.990; TRX `artifacts/a19-baseline-results/a19-baseline.trx` |
| Focused A19 | 44 | 0 | 0 | 36 unit/HTTP + 8 SQL integration; 35 giây; TRX `artifacts/a19-focused-results/a19-focused.trx` |
| Final build | — | 0 | — | 0 errors, 1 warning NU1900; `artifacts/a19-final-build.log` |
| Final full regression | 1.956 | 0 | 78 | 2.034; 1 phút 44 giây; TRX `artifacts/a19-full-results/a19-full.trx`; toàn bộ 44 A19 pass, gồm 8 SQL tests |

Warning NU1900: không tải được vulnerability feed do TLS, xảy ra từ baseline; không phải lỗi compile hay test. Không đổi package/config để che warning. 78 skipped của nền gồm **73 SQL opt-in và 5 test cần dữ liệu demo ngoài repository**; không tính skipped thành passed.

Các lần chạy trung gian đã phát hiện và sửa fixture A19: thiếu using hosting; EnsureCreated tự thêm 5 Job seed A3 khiến fixture chưa trống; catalog mới kế thừa SINGLE_USER từ `model` gây 4060/timeout khi nhiều kết nối. Không sửa source/test cũ để làm xanh. Fixture cuối chỉ tạo tên GUID mới có guard, xóa riêng Job/skill/alias seed A3 trước khi nạp dữ liệu tổng hợp (lúc chưa có CV), rồi đặt **catalog mới đó** thành MULTI_USER. Không đổi model, SmartCvDb, không xóa CV hoặc catalog. Log chẩn đoán giữ trong artifacts/a19-*.

Một lần full regression trung gian có 1.955 passed / 1 failed / 78 skipped: timeout ở script đối chiếu lớn, sau khi API đã trả thành công. DMV xác nhận `RESOURCE_SEMAPHORE` khi SQL sort dữ liệu `e.*` có cột nvarchar(max); STATISTICS TIME ghi CPU khoảng 31 ms nhưng elapsed khoảng 55 giây vì chờ bộ nhớ. Đã sửa script để materialize key/timestamp của latest riêng, không sort AI content, và không lặp mở rộng CTE. Không giữ timeout chẩn đoán 120 giây: test cuối dùng timeout SqlCommand mặc định, focused 44/44 đã chạy lại thành công. Log cũ ở `artifacts/a19-full-before-sql-optimization.log`, chẩn đoán ở `artifacts/a19-audit-diagnostic.log`.

### SQL đối chiếu và smoke

8 test SQL sử dụng schema EF thật với constraints/indexes trên catalog mới `SmartCv_A19_Test_<guid>`, giữ lại sau test. `EnsureCreated` chỉ chạy trong fixture trên catalog xác minh chưa tồn tại, không chạy trong API và không tạo migration. Guard từ chối connection trỏ SmartCvDb hoặc AttachDBFilename.

- `check_a19_dashboard.sql` là bản đối chiếu độc lập, chỉ đọc bảng ứng dụng; dùng/tự dọn `#Apps`, `#Furthest`, `#LatestScreening`, `#FlaggedJobs` trong phiên. 6 result sets: cards, funnel/conversion, timing, 6 reasons, 5 runs và A40 saved-evidence flags/top Job. SQL nhận cùng bộ lọc và ngưỡng ReviewFlags từ API trong test.
- Khớp từng trường API với SQL khi không lọc, lọc Job active/inactive/không hồ sơ, lọc `2026-11-01`–`2026-11-17`, ngày Việt Nam đơn lẻ, một đầu from/to, khoảng không có dữ liệu, catalog trống, legacy thiếu reason, cap lịch sử và ngưỡng A40 tùy chỉnh.
- Fixture cohort: 15 CV, 13 Applications, 7 Rejected, 1 OnHold, 2 active Jobs, 1 queued run; reached `[13,5,4,3]`, current `[2,1,0,2]`, conversion `[null,38.5,80,75]`; timing 4 mẫu, average=2, median=2 giờ. Một CV ứng tuyển hai Job và CV chưa gắn Job đều được xử lý đúng.
- Ngày Việt Nam 02/11: 12 hồ sơ; 12 CV upload. Job chính cùng ngày: 10 CV, gồm CV cũ upload từ tháng trước; 1 failed CV. Mốc UTC cuối 17:00 bị loại khỏi khoảng.
- History có đi thẳng New→Interview, Interview→New→Interview→Rejected, FromStage=Shortlisted→OnHold, current Interview không event và hồ sơ không quyết định; không đếm trùng hay mất reached.
- Timing riêng kiểm tra odd `[2,4,48]` cho average=18, median=4; even có thêm mẫu âm clamp 0 cho average=13,5, median=3; khoảng >68 năm không overflow.
- A40: Pending/Failed/clean mới che cờ cũ; PromptTest không che Screening; ID hòa timestamp; Job tie-break; SQL LEN bảo toàn 500 trailing spaces; CV A39 AnonymizedAt/text=null; so cờ cap với **Ranking HTTP hiện hữu** và cap cấu hình mới.
- Test legacy mô phỏng dữ liệu cũ bằng nới constraint duy nhất trong catalog thử; API không sửa dữ liệu. Hai Rejected, một có Duplicate, một null ⇒ Duplicate 50%, không tự gán reason.
- Smoke Kestrel chạy server thử, tắt toàn bộ BackgroundService trong fixture, có empty trước seed, default, Job, ngày 01–17/11, no matches, 400 invalid và 404 missing Job. Evidence `artifacts/a19-sql/*-kestrel-smoke.json` ghi địa chỉ, server type và status. Server thử được dispose sau test.

### Privacy, hiệu năng, cancellation

- Kiểm tra response không có candidateName/email/fileName/storedPath/extractedText/reasoning/aiRawResponse/systemPrompt hoặc marker dữ liệu nhạy cảm tổng hợp. Giữ timestamp UTC và RunSummaryDto cũ.
- Query recorder xác nhận các truy vấn A19 chỉ SELECT, không lấy AiRawResponse. CV text chỉ tính độ dài bằng SQL sentinel; không lấy nội dung CV. Reasoning cần cho audit marker A40 được đọc trong lô tối đa 256; không trả ra DTO/log.
- Phễu là số truy vấn cố định bằng EXISTS; timing trả một row aggregate; recent run Jobs lấy chung một query. Cờ lọc scope/latest trên SQL và keyset batch 256, RAM giữ từng batch + counts theo Job, không giữ toàn bộ lịch sử.
- Dữ liệu lớn: **2.048 hồ sơ, 4.096 sự kiện, 2.048 evaluations**, 21 SELECT, không N+1 theo hồ sơ. Focused đo được **2.219 ms**, full regression cuối **2.786 ms** cho HTTP response trên máy này; đây là số đo cục bộ, không phải SLA Production. Evidence JSON ghi riêng từng lần chạy.
- Hủy request trước khi gửi và trong lúc truy vấn đang chờ đều truyền CancellationToken tới EF; interceptor xác nhận token SQL đã bị hủy. SQL script cũng khớp trên tập dữ liệu lớn.

## 4. Danh sách chính xác file M/A/D

**M: 1; A: 12; D: 0; tổng 13 file.** Chữ A dưới đây là file mới chưa stage (Git hiển thị `??`).

| Trạng thái | File | Nội dung |
| --- | --- | --- |
| M | `src/SmartCv.Api/Program.cs` | Thêm đúng một dòng MapDashboardEndpoints, giữ đăng ký A39/A41/A42 |
| A | `src/SmartCv.Api/Endpoints/DashboardEndpoints.cs` | Route/gate/validation, aggregate, latest flags, timing SQL, recent runs |
| A | `src/SmartCv.Core/Common/VietnamTime.cs` | Quy đổi ngày UTC+7 độc lập OS, có thể dùng chung sau này |
| A | `src/SmartCv.Core/Contracts/DashboardContracts.cs` | 4 DTO cho B19, tái sử dụng RunSummaryDto |
| A | `src/SmartCv.Core/Dashboard/DashboardPeriod.cs` | Parse/validate ngày và giới hạn UTC |
| A | `src/SmartCv.Core/Dashboard/FunnelCalculator.cs` | Schema cố định, display và conversion từ SQL counts |
| A | `src/SmartCv.Core/Dashboard/RejectReasonCalculator.cs` | 6 lý do, tỷ lệ đúng denominator, professional từ A36 |
| A | `src/SmartCv.Core/Dashboard/ScreeningTimeCalculator.cs` | Tính/chuẩn hóa average/median, rounding và empty |
| A | `tests/SmartCv.Tests/Dashboard/DashboardCalculatorTests.cs` | Unit ngày/funnel/timing/reasons/isEmpty |
| A | `tests/SmartCv.Tests/Dashboard/DashboardEndpointTests.cs` | HTTP 400 tiếng Việt/no-store, access gate/startup guard |
| A | `tests/SmartCv.Tests/Dashboard/DashboardSqlServerTests.cs` | 8 integration tests, retained fixture, SQL/Ranking/Kestrel/privacy/performance/cancellation |
| A | `database/check_a19_dashboard.sql` | SQL đối chiếu độc lập, không ghi bảng ứng dụng |
| A | `docs/A19-implementation-report.md` | Báo cáo và handoff B19 này |

Manifest local UTF-8 không BOM: `artifacts/a19-commit-files.txt`. SHA nền: `artifacts/a19-base-sha.txt`. Cả hai và toàn bộ artifacts được `.gitignore:66:artifacts/` bảo vệ. Không thay `.gitignore` vì rule đã có. Không đưa ZIP/log/TRX/catalog/secret vào manifest. `RejectReasonCalculator` là file thêm ngoài danh sách mẫu, chỉ phục vụ A19 và unit test tỷ lệ/sắp xếp.

## 5. Handoff UX/UI cho B19 — không triển khai B19

Đã đọc token hiện tại ở `src/SmartCv.Web/wwwroot/app.css`: Inter; Deep Teal `--sc-brand-900` #0a2e2d, `--sc-brand-800` #0e4a47, `--sc-brand-700` #0f766e; gradient `--sc-grad-ai` #14b8a6→#06b6d4→#6366f1 **chỉ dành cho nội dung AI theo quy ước hiện có**. Đây là đề xuất dùng token hiện hữu, không phải mockup từ ZIP.

- Desktop 1440/1024: bộ lọc Job/ngày ở đầu, KPI đọc nhanh (Tổng CV kèm hồ sơ; CV lỗi; Job active; run đang chấm; cần xem kỹ), phễu có current/reached riêng, lý do loại dùng thanh ngang, median nổi bật và average phụ, bảng 5 run cuối.
- Tablet 768: KPI chia 2–3 cột, các khối biểu đồ tự xuống hàng. Mobile 390/360: bộ lọc/KPI và khối thống kê xếp dọc, run trình bày thành hàng/card ngắn; không ép biểu đồ/bảng tràn ngang toàn trang. Không sửa Web/Razor/CSS trong A19.
- Bộ lọc ghi URL; ngày hiển thị theo Việt Nam, preset 7/30 ngày/Tất cả. Nhãn/tooltip nói rõ KPI theo upload/applications và activeJobs/runningRuns/recentRuns không cùng scope ngày. Job inactive vẫn chọn để xem lịch sử. Chỉ áp dụng khi dữ liệu hợp lệ; hủy request cũ khi bộ lọc đổi.
- Funnel dùng chữ/số cạnh màu: “Đang ở” và “Đã từng tới”, conversion ở giữa; Rejected/OnHold tách khỏi 4 bước chính. HR quyết định, AI không tự chuyển giai đoạn.
- Lý do loại luôn hiển thị đủ 6; tooltip giải thích tổng phần trăm có thể dưới 100% vì legacy thiếu lý do hoặc làm tròn. `isProfessional` dùng cho nhãn/chú thích, không chỉ đổi màu.
- Tooltip median: “Một nửa hồ sơ có quyết định đầu tiên trong thời gian này”; average: “Có thể tăng mạnh bởi hồ sơ chờ lâu”. Null hiển thị “Chưa có quyết định”, không hiển thị 0 giờ. Đơn vị giờ API, UI có thể đổi phút/ngày với locale vi-VN.
- Skeleton loading giữ kích thước, `aria-busy`; lỗi dùng title/detail tiếng Việt và nút thử lại; empty không lọc dẫn tới upload, empty do filter có nút bỏ lọc. Chỉ CV upload mà chưa gắn Job vẫn không phải isEmpty.
- Keyboard/focus rõ, label gắn input, thông báo cập nhật `aria-live=polite`, biểu đồ có mô tả số bằng chữ, không dùng màu làm tín hiệu duy nhất. Dùng Deep Teal cho cấu trúc/nút chính, glow tiết chế và chỉ đúng ý nghĩa AI; tôn trọng reduced motion.
- `flaggedJobId` chỉ gợi ý Job có nhiều hồ sơ cần xem kỹ nhất. Dashboard cộng latest **từng Application**, có thể nằm trên nhiều run; Results hiện tại chọn theo **một run**. B19 không được khẳng định số thẻ luôn bằng ô flagged của một run; cần chú thích và chọn run phù hợp, không hứa link/filter chưa triển khai ở B19/B26.

## 6. Giới hạn và trạng thái bàn giao

- Không thực hiện A20/A21/A22/A25 hoặc B19–B26; không sửa Web, Job CRUD, migration, connection strings, appsettings, secrets, worker/retention options của ứng dụng.
- Không sửa SmartCvDb, không migrate database thật, không xóa CV, không drop database/catalog; chỉ schema/dữ liệu tổng hợp trên catalog mới có guard. Các catalog test và evidence còn nguyên cho chủ dự án kiểm tra.
- Không tự bật worker ứng dụng, không gọi Gemini thật. Full regression nền có các test vòng đời worker với fake/loopback theo thiết kế sẵn; fixture A19 gỡ toàn bộ BackgroundService.
- API dùng nhiều SELECT read-committed, không cung cấp snapshot transaction cho toàn dashboard khi HR/worker đồng thời thay đổi dữ liệu; số liệu có thể chuyển động giữa các truy vấn. Test hiệu năng là cục bộ 2.048 hồ sơ; chưa phải load test Production hoặc đánh giá execution plan ở quy mô doanh nghiệp.
- SQL đối chiếu A40 là bản audit tham khảo theo ngưỡng truyền vào; runtime luôn lấy ReviewFlags C# làm nguồn chuẩn. Không suy cap từ cấu hình hiện tại.
- 78 skipped có sẵn chưa được nghiệm thu lại (SQL opt-in/demo ZIP ngoài repo); không xem là passed. NU1900 vẫn cần môi trường có vulnerability feed TLS hoạt động để kiểm tra advisory mới.
- Full regression cuối 0 failed; không còn blocker chức năng A19 đã biết trong phạm vi đã chạy. Các giới hạn nghiệm thu là 78 skipped có sẵn, vulnerability feed TLS và chưa load test Production như nêu trên; không khẳng định đã nghiệm thu các phần đó.
- Kiểm tra cuối: `git diff --check` sạch; đã kiểm tra whitespace và EOF cả 12 file mới; `git diff --cached --name-status` rỗng. Manifest khớp chính xác 13 đường dẫn trong porcelain status, UTF-8 không BOM; SHA nền khớp HEAD và origin/main. Evidence `artifacts/a19-git-verification.json`, kết quả TRX tổng hợp `artifacts/a19-test-summary.json`.
- Trạng thái bàn giao: `feature/a19-dashboard-api`, **1 M / 12 file mới chưa stage / 0 D**, HEAD không đổi. Đã dừng trước mọi thao tác stage/commit/push/merge/PR; chủ dự án kiểm tra báo cáo và manifest trước khi thực hiện Git.
