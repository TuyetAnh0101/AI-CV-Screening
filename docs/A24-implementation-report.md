# A24 – API ứng viên: báo cáo triển khai

Ngày thực hiện: 18/11/2026 (Asia/Saigon). Trạng thái: đã triển khai và hoàn tất kiểm thử trong môi trường sẵn có; sẵn sàng để người giao việc review. Không stage, commit, push hoặc tạo PR.

## 1. Căn cứ và tên nhiệm vụ

Tên chính xác trong guide: **A24 – API ứng viên**. Phạm vi: danh sách, tạo, sửa, chi tiết, gắn CV, phát hiện nộp lại và gộp ứng viên. Tiêu chí nghiệm thu: **HR thấy toàn bộ lịch sử ứng tuyển của một người ở một chỗ**.

ZIP đọc trực tiếp: C:/TTCN/A23_B25_SmartCv.zip (130.166 bytes).
SHA-256: 4AFD8D4D7DFD845FC11FBFCFB15305F7824AC129ABD1585317481CC57C8DD8FB.

Đã kiểm kê **128 mục, 56 file**, trong đó **10 file A24**. Inventory đầy đủ nằm ở artifacts/a24/zip-inventory.txt, được ignore.

## 2. Tài liệu và source ZIP đã đọc

- HUONG_DAN_A23_A24_A43_B23_B24_B25.md: hướng dẫn chung, thứ tự phụ thuộc, quyết định C7, toàn bộ mục A24 (quy tắc, hợp đồng API, file, các bước, sáu kịch bản nghiệm thu), các ghi chú liên quan và bảng lỗi thường gặp. Lệnh Git trong guide không được thực hiện khi trái yêu cầu người giao việc.
- snippets/Program.A23-A43.cs.txt: phần đăng ký route A24; phân biệt các dòng A43 ngoài phạm vi.
- A24/src/SmartCv.Core/Contracts/CandidateContracts.cs.
- A24/src/SmartCv.Core/Candidates/CandidateRules.cs.
- A24/src/SmartCv.Core/Candidates/CandidateInfoExtractor.cs.
- A24/src/SmartCv.Core/Candidates/DuplicateMatcher.cs.
- A24/src/SmartCv.Infrastructure/Candidates/CandidateQueries.cs.
- A24/src/SmartCv.Api/Endpoints/CandidateEndpoints.cs.
- A24/src/SmartCv.Api/Endpoints/CvEndpoints.cs.
- A24/tests/SmartCv.Tests/Candidates/CandidateRulesTests.cs.
- A24/tests/SmartCv.Tests/Candidates/CandidateInfoExtractorTests.cs.
- A24/tests/SmartCv.Tests/Candidates/DuplicateMatcherTests.cs.

Đã đối chiếu source thật: Program, CvEndpoints A23, Candidate/Cv/Application/ApplicationEvent, cấu hình EF, DbContext, migrations/snapshot, privacy middleware/scope/anonymizer, cơ chế HR gate, TestSupport và các fixture/test A21/A23/A25/B26 liên quan. Không chép đè CvEndpoints từ ZIP. Chỉ khởi tạo các file mẫu chưa tồn tại sau khi kiểm tra, rồi điều chỉnh cho source hiện tại.

## 3. Ảnh và dữ liệu kiểm thử

**ZIP không có PNG, JPG/JPEG, SVG, PDF, GIF hoặc WebP.** Vì vậy không có ảnh giao diện, sơ đồ ảnh hoặc ảnh dữ liệu đầu vào để mở xem. Không tuyên bố đã xem ảnh.

Các SQL trong database/ thuộc A23/A43; không chạy chúng. ZIP A24 chứa test logic với dữ liệu tổng hợp, không chứa file CV thật. Test mới dùng tên/email/số điện thoại giả, EF InMemory hoặc SQL opt-in; không dùng hồ sơ cá nhân.

## 4. Git và SHA nền

| Mục | Giá trị |
|---|---|
| Repository duy nhất | C:/TTCN/project/AI-CV-Screening |
| Nhánh ban đầu | feature/a23-implementation |
| HEAD ban đầu | 65128c2c5777f07623508cc5e33a7a2b1dc7488d |
| Trạng thái ban đầu | Sạch; staged/unstaged/untracked/unmerged đều trống |
| Thao tác đang dở | Không có merge/rebase/cherry-pick/revert/sequencer/bisect |
| Nhánh A24 trước khi làm | Không có local hoặc remote |
| Remote | origin — https://github.com/TuyetAnh0101/AI-CV-Screening.git |
| Fetch | Thành công: git fetch origin --prune |
| Nhánh triển khai | feature/a24-implementation |
| Upstream nhánh A24 | Không đặt; tạo bằng --no-track từ origin/main |
| SHA nền 40 ký tự | 3d7db8df8b3c03aae9c66682288aeb8d0f6064d2 |
| HEAD khi bàn giao | 3d7db8df8b3c03aae9c66682288aeb8d0f6064d2 |

origin/main sau fetch là commit A23 #76. Đã xác minh nền có B26 #75 tại ca8e5e8fbe3d320470d8033a6bb962d1706562e9 và các trang Runs/RunDetail. Không dùng SHA B26 cũ để tạo nhánh.

Git trong sandbox ban đầu báo khác chủ sở hữu; dùng safe.directory ở từng lệnh, không thay cấu hình global. Fetch/switch được chạy qua cơ chế escalation vì .git chỉ được đọc trong sandbox. Không có thay đổi chưa commit của A23/B26 cần di chuyển. Không tạo worktree/clone/thư mục dự án khác.

## 5. Đối chiếu yêu cầu với triển khai

| Yêu cầu A24 | Kết quả và kiểm chứng |
|---|---|
| Họ tên bắt buộc, tối đa 150, gộp khoảng trắng | CandidateRules; test null/rỗng/khoảng trắng/150/151 |
| Email hợp lệ, chữ thường, tối đa 254 | CandidateRules; test hợp lệ/sai/254/255, email legacy viết hoa |
| Điện thoại 10 số bắt đầu 0; bỏ dấu phân cách, đổi +84/84 | CandidateRules; test các dạng mẫu, số bàn 11 số, thiếu/dư số, ký tự lạ và chữ số Unicode |
| Ít nhất email hoặc điện thoại | Lỗi errors.contact; không ghi dữ liệu lỗi |
| Nhận ra nộp lại theo email hoặc số; trùng cả hai đứng đầu | DuplicateMatcher + CandidateQueries; không so tên, không tự gộp |
| Danh sách/tìm/phân trang/số CV và hồ sơ | GET candidates; thứ tự ổn định, UTC, null LastUploadedAt, trang vượt cuối, kiểm tra query lặp/sai/tràn số |
| Tạo trùng trả 409 + matches; allowDuplicate cho HR quyết định | POST candidates; test xung đột không ghi và xác nhận tạo mới |
| Sửa liên hệ trùng người khác trả 409 | PUT candidate; giữ nguyên khi lỗi, cho override rõ ràng, đổi tên với liên hệ không đổi |
| Chi tiết có liên hệ, toàn bộ CV và hồ sơ ở các job | GET candidate; test 2 CV, 2 job, điểm Screening gần nhất, không lấy điểm PromptTest |
| Timeline cũ → mới, có actor/lý do/note | Dùng ApplicationEventDto hiện có, sắp CreatedAt rồi EventId; test actor/note/thứ tự/UTC |
| Gợi ý từ CV chỉ điền sẵn | Tái sử dụng GuessCandidateName; test tên/email/điện thoại và text null; GET không SaveChanges |
| Gắn trùng trả 200 NeedsDecision + matches | Không tạo hoặc đổi liên kết trước quyết định HR; test xác nhận chọn người có sẵn |
| confirmNew tạo người khác khi HR xác nhận | Test override; không tự gộp |
| CV đã ẩn danh không gắn được | PUT trả 409; GET suggestion trả 410; không phục hồi danh tính |
| Đổi ứng viên, người cũ hết CV thì xóa | Cùng transaction với liên kết mới; test giữ khi còn CV, xóa khi hết CV và gắn lại cùng người |
| Gộp chuyển mọi CV, bổ sung liên hệ thiếu, xóa source | Giữ contact target, giữ nguyên application/events/scores; source sau gộp trả 404 |
| Không tạo migration | Entity/schema/configuration/snapshot/migrations không đổi; Candidates đã tồn tại từ A2 |
| Quyền truy cập, bảo mật | Giữ Development + CvUpload:Endpoint:Enabled; route vắng khi gate tắt; no-store; A39 privacy lease; lỗi không lộ dữ liệu |
| Transaction/concurrency/cancellation, không retry write | CandidateWriteScope: SQL transaction Serializable + sp_getapplock transaction-owned; token truyền xuyên suốt, không retry |
| Không N+1 | Danh sách dùng projection/count; chi tiết dùng số query cố định, timeline qua quan hệ, không query theo từng CV/job |
| Bảo toàn tính năng hiện có | Chỉ mở rộng tìm số điện thoại đúng yêu cầu A24 trong GET cvs; full regression được ghi ở mục 10 |

Phụ thuộc: C7 theo quyết định trong guide (HR nhập tay, hệ thống gợi ý), Candidate schema A2, A23 cho kho CV, A35 cho timeline, A39 cho bảo vệ dữ liệu. Các phần này có trên nền đã fetch.

Ngoài phạm vi: giao diện B23/B24/B25, A43 export/erase/data requests, A25 run history, thay thuật toán scoring/Gemini/OCR/worker hoặc chỉnh dữ liệu lịch sử.

## 6. Endpoint và status

| Method | Endpoint | Kết quả |
|---|---|---|
| GET | /api/candidates?search=&page=&pageSize= | 200 CandidatePageDto; 400 query sai |
| POST | /api/candidates | 201 CandidateDto + Location; 400 validation; 409 matches |
| GET | /api/candidates/{id} | 200 CandidateDetailDto; 404 |
| PUT | /api/candidates/{id} | 200 CandidateDto; 400/404/409 |
| PUT | /api/cvs/{id}/candidate | 200 Attached hoặc NeedsDecision; 400/404/409 |
| GET | /api/cvs/{id}/candidate-suggestion | 200 CandidateSuggestionDto; 404/410 |
| POST | /api/candidates/{id}/merge | 200 MergeCandidateResultDto; 400/404 |
| GET (mở rộng) | /api/cvs?search=... | Giữ hợp đồng A23, thêm khớp số điện thoại đã chuẩn hóa |

Mặc định page=1, pageSize=20; pageSize 1–100. Tìm ứng viên nhận một ký tự như mẫu, chặn từ khóa quá 254 ký tự. Điện thoại tìm theo số đầy đủ sau chuẩn hóa như guide.

JSON lỗi/null/không phải object/thuộc tính lặp/body quá 16 KiB trả 400; content type không phải JSON trả 415. Các lỗi dữ liệu đồng thời/FK có thể trả 409; timeout/khóa bận trả 503; lỗi không dự kiến trả 500 an toàn. Write timeout yêu cầu đọc lại trạng thái, không tự retry.

401/403 không áp dụng khi chưa có xác thực M6: giữ gate hiện có và trả 404 khi route tắt. Không thêm rate limiter ngoài yêu cầu; 429 của upload/AI hiện có được giữ nguyên, không tạo status 429 giả cho API mới.

## 7. DTO, helper, database và component

Thêm CandidateRequest, CandidateDto, CandidateListItemDto, CandidatePageDto, DuplicateMatchDto, CandidateDetailDto, CandidateCvDto, CandidateApplicationDto, AttachCandidateRequest, AttachCandidateResponse, CandidateSuggestionDto, MergeCandidateRequest, MergeCandidateResultDto và hằng AttachOutcomes.

Tái sử dụng ApplicationEventDto, CvListQuery (parser phân trang và TotalPages), CvSourceChannels, RetentionRules, TimeProvider, SmartCvDbContext và CvPrivacyScope. Thêm CandidateRules/Input, CandidateInfoExtractor/Suggestion, DuplicateMatcher/ExistingCandidate/DuplicateMatch, CandidateQueries, CandidateWriteScope, CandidateApiFilter và CandidateRequestReader.

Database: không tạo bảng/cột/index/migration; không migrate/drop/truncate database thật, không xóa file CV. Gắn/gộp chỉ thay quan hệ Candidate theo nghiệp vụ. Không sửa điểm, run, application stage hoặc event lịch sử.

Component/UI: không thay đổi trang, component, CSS hoặc JavaScript nào.

## 8. Khác biệt có chủ đích so với ZIP

1. Giữ CvEndpoints A23 mới nhất; chỉ thêm route, partial handler và tìm số điện thoại. Giữ gate, rate limit, xử lý ẩn danh và tối ưu query hiện có.
2. API ứng viên dùng cùng gate Development/HR thay vì mở công khai như file mẫu. Thêm /api/candidates vào privacy middleware và no-store cả khi chưa lấy được privacy lease.
3. Gợi ý tên dùng PersonalDataMasker.GuessCandidateName thực tế; hàm GuessNameFromFirstLine trong mẫu không tồn tại trên nền này. Không sửa masker/scoring.
4. Điện thoại chỉ nhận ASCII 0–9 để khớp CHECK SQL; không dùng \\d vốn nhận cả chữ số Unicode.
5. Duplicate query xử lý email legacy viết hoa, loại chính ứng viên trước, trả đủ matches; bỏ Take(20) trước khi xếp hạng để không mất người trùng cả email và số.
6. Gửi lặp yêu cầu tạo người cho CV đang gắn cũng so với người hiện tại, trả NeedsDecision; confirmNew vẫn tạo mới theo quyết định HR.
7. Kiểm tra hai chế độ gắn CV loại trừ nhau; null/JSON lỗi/body lớn trả ProblemDetails an toàn. Kiểm tra pagination lặp và overflow bằng helper hiện có.
8. Thêm transaction và khóa ghi SQL xuyên tiến trình; bảo vệ duplicate-check, attach/orphan cleanup và merge. Không có retry write. InMemory chỉ mô phỏng serialization, không được coi là bằng chứng rollback SQL.
9. Sử dụng TimeProvider và UTC nhất quán; timeline query theo quan hệ thay vì IN danh sách ID có thể quá lớn. File name của CV ẩn danh legacy được che.
10. Actor của merge được nhận và giới hạn 100 ký tự, không ghi vào log dữ liệu cá nhân, không thêm bảng audit ngoài guide. Timeline HR hiện có vẫn trả actor của quyết định lịch sử.
11. Giữ 33 case unit test mẫu, thêm test HTTP/biên/lỗi/concurrency/cancellation và 4 SQL opt-in. Không sửa hoặc skip test cũ.

## 9. Lỗi đã xử lý trong quá trình làm

- Baseline build mặc định không copy được DLL do tiến trình khác đang giữ file: dùng output cách ly, không dừng tiến trình người dùng.
- Sandbox không đọc/ghi Windows Event Log khi chạy test: dừng bản chạy lỗi môi trường, chạy lại bằng cơ chế escalation; không sửa test cũ để né lỗi.
- Mẫu gọi helper đoán tên không có trên source thật; đã đổi sang helper sẵn có.
- Thiếu gate, transaction và xử lý lỗi an toàn trong mẫu; đã bổ sung trong phạm vi A24.
- Build trung gian phát hiện gọi Gone() không tồn tại ở CvEndpoints hiện tại; đã trả ProblemDetails 410 tương thích.
- Focused lần đầu: 85 passed, 2 failed, 0 skipped. Hai lỗi thuộc fixture mới (email biên 255 thay vì 254, CV không gắn vẫn chứa số điện thoại trong text); đã sửa dữ liệu/đối tượng kiểm tra đúng nghiệp vụ. Không thay logic tìm nội dung để ép test xanh.

- Full regression lần 1 xuất hiện lỗi ở test cũ RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop: expected 2–5 lần poll sau Task.Delay(3500), actual 8; thời gian test thực tế khoảng 7 giây. Worker và test không có trong diff. Baseline trước sửa đã pass; test chạy riêng sau đó pass. Đây là dấu hiệu test phụ thuộc lập lịch/thời gian thực, không được báo thành lỗi baseline đã tái hiện. Giữ nguyên lần fail trong báo cáo; không sửa/skip test để ép xanh.

## 10. Build và kiểm thử

SDK: .NET 10.0.403. Solution: SmartCv.slnx. Output cách ly: artifacts/a24/build.

| Bước | Kết quả |
|---|---|
| Baseline restore mặc định | Thành công; các project up-to-date |
| Baseline build mặc định | Không thành công: 30 warnings MSB3026, 6 lỗi MSB3027/MSB3021 do DLL bị khóa; không phải lỗi compile source nền |
| Restore cách ly trong sandbox | Không thành công NU1301 TLS/NuGet; không dùng làm kết quả cuối |
| Restore cách ly qua escalation | Thành công; 1 warning NU1900 của Web về nguồn vulnerability NuGet |
| Baseline build cách ly | Thành công, 0 errors, 1 warning NU1900 |
| Baseline tests trong sandbox | Bị dừng sau lỗi quyền Event Log; không có summary hoàn tất để báo passed/failed/skipped |
| Baseline tests xác minh | **2549 passed, 0 failed, 110 skipped, total 2659** |
| Focused cuối | **93 passed, 0 failed, 4 skipped, total 97** |
| Final build | **Thành công, 0 errors, 1 warning NU1900** |
| Full regression lần 1 | **2641 passed, 1 failed, 114 skipped, total 2756** |
| Chạy riêng test worker bị lỗi | **1 passed, 0 failed, 0 skipped** |
| Full regression chạy lại, cùng cấu hình | **2642 passed, 0 failed, 114 skipped, total 2756** |

Lệnh xác minh (đã thực hiện, output được lưu vào artifacts/a24):

~~~powershell
dotnet restore SmartCv.slnx
dotnet build SmartCv.slnx --no-restore
dotnet restore SmartCv.slnx -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a24/build
dotnet build SmartCv.slnx --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a24/build --verbosity minimal
dotnet test SmartCv.slnx --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a24/build --logger 'trx;LogFileName=baseline-host.trx' --results-directory artifacts/a24/results
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a24/build --filter 'FullyQualifiedName~SmartCv.Tests.Candidates' --logger 'trx;LogFileName=focused-final.trx' --results-directory artifacts/a24/results
dotnet test SmartCv.slnx --no-build --no-restore -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/a24/build --logger 'trx;LogFileName=regression.trx' --results-directory artifacts/a24/results
~~~

Test HTTP sử dụng WebApplicationFactory, EF InMemory riêng từng database, background workers tắt và dữ liệu tổng hợp. Kiểm tra 400/404/409/410 thực qua HTTP; 500/503/concurrency exception và token cancellation qua filter/scope riêng. Concurrency HTTP giả lập bao gồm tạo trùng và gắn cùng một CV. Unit test xác nhận mỗi lỗi chỉ thực thi một lần, không retry.

Đối chiếu tên từng test trong baseline-host.trx và regression-recheck.trx: không mất test baseline nào (0 missing); thêm đúng 97 case A24 (93 pass, 4 SQL opt-in skip). Full suite chạy lại thành công với cùng cấu hình, không sửa source/test giữa hai lần regression.

Bằng chứng test: baseline-host.trx, focused-final.trx, regression.trx, worker-recheck.trx và regression-recheck.trx trong artifacts/a24/results; log tương ứng ở artifacts/a24. Bản chạy lại full suite dùng cùng command và cấu hình, chỉ đổi tên log/TRX.

## 11. Phần chưa kiểm chứng và giới hạn

- **4 test SQL A24 chưa chạy** vì SMARTCV_A24_TEST_SQL_CONNECTION không được cấu hình. Không tự bật hoặc dùng SQL Server thật để thay thế.
- Các test opt-in tạo database GUID mới với prefix SmartCv_A24_Test_, chỉ migrate catalog mới đã kiểm tra chưa tồn tại, giữ lại để review, không xóa catalog. Chúng kiểm tra hai API host tạo đồng thời, rollback sau SaveChanges, merge với query quan hệ thật và cancellation khi chờ khóa.
- Chưa có bằng chứng runtime SQL Server cho sp_getapplock/Serializable/rollback/collation/query plan, tính tương tác với A39 ở nhiều tiến trình hoặc commit timeout không xác định kết quả. Mã xử lý không retry; không suy diễn test InMemory thành kiểm thử SQL.
- 110 test opt-in/thiếu tài nguyên có sẵn trên baseline vẫn giữ điều kiện skip cũ. Không thay điều kiện để ép regression xanh.
- C7 dùng quyết định đã ghi trong guide: HR nhập tay, gợi ý từ CV, HR xác nhận. Không tự gọi AI/OCR để điền hoặc lưu.
- Không kiểm browser/Chromium/Playwright hoặc responsive: A24 chỉ backend và không có UI trong ZIP.
- Warning NU1900 còn tồn tại vì không truy cập được nguồn audit NuGet; không tắt audit hoặc thay package để che warning.
- Khóa ghi chung A24 ưu tiên tính toàn vẹn; các write A24 được tuần tự hóa trong một database. Chưa benchmark throughput SQL thật.

## 12. Xác nhận phạm vi và bàn giao

Không triển khai thay A23/A25/B25. Mở rộng tìm số điện thoại của GET cvs là yêu cầu A24 trong guide. Không tạo giao diện B23/B24, không làm A43, không thay scoring/Gemini/OCR/worker hoặc sửa dữ liệu lịch sử. A21/A22, A25, B19/B20/B21/B26, Results, Upload và System Prompt giữ nguyên source ngoài những điểm tích hợp A24 đã liệt kê.

Không chạy git add/commit/push, merge/rebase/reset/stash/clean/restore, cherry-pick, PR, worktree hoặc clone. HEAD vẫn bằng SHA nền. Artifacts/log/TRX không đưa vào manifest.

## 13. Manifest A/M/D và kiểm tra Git cuối

Quy ước: A = file mới đang untracked (??), M = file sửa unstaged; không có file đã stage. Tổng: **17 A, 3 M, 0 D = 20 file**.

| Trạng thái | File relative |
|---|---|
| A | docs/A24-implementation-report.md |
| A | src/SmartCv.Api/Endpoints/CandidateApiFilter.cs |
| A | src/SmartCv.Api/Endpoints/CandidateEndpoints.cs |
| A | src/SmartCv.Api/Endpoints/CandidateRequestReader.cs |
| A | src/SmartCv.Api/Endpoints/CvCandidateEndpoints.cs |
| A | src/SmartCv.Core/Candidates/CandidateInfoExtractor.cs |
| A | src/SmartCv.Core/Candidates/CandidateRules.cs |
| A | src/SmartCv.Core/Candidates/DuplicateMatcher.cs |
| A | src/SmartCv.Core/Contracts/CandidateContracts.cs |
| A | src/SmartCv.Infrastructure/Candidates/CandidateQueries.cs |
| A | src/SmartCv.Infrastructure/Candidates/CandidateWriteScope.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateEndpointTests.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateInfoExtractorTests.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateRulesTests.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateSafetyTests.cs |
| A | tests/SmartCv.Tests/Candidates/CandidateSqlServerTests.cs |
| A | tests/SmartCv.Tests/Candidates/DuplicateMatcherTests.cs |
| M | src/SmartCv.Api/Endpoints/CvEndpoints.cs |
| M | src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs |
| M | src/SmartCv.Api/Program.cs |

Metadata artifacts/a24-base-sha.txt chứa đúng SHA nền 40 ký tự. artifacts/a24-commit-files.txt chứa đúng 20 đường dẫn relative dùng /, mỗi dòng một file, không trùng; đã đối chiếu hai chiều với git status --porcelain=v1 --untracked-files=all, không thừa/thiếu.

Cả hai metadata đều được .gitignore dòng 66 (artifacts/) bảo vệ và không được Git track. Manifest không chứa artifacts/logs/TRX/bin/obj/screenshots/secrets/connection strings hoặc CV thật.

Kết quả git status --short --branch:

~~~text
## feature/a24-implementation
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
 M src/SmartCv.Api/Program.cs
?? docs/A24-implementation-report.md
?? src/SmartCv.Api/Endpoints/CandidateApiFilter.cs
?? src/SmartCv.Api/Endpoints/CandidateEndpoints.cs
?? src/SmartCv.Api/Endpoints/CandidateRequestReader.cs
?? src/SmartCv.Api/Endpoints/CvCandidateEndpoints.cs
?? src/SmartCv.Core/Candidates/
?? src/SmartCv.Core/Contracts/CandidateContracts.cs
?? src/SmartCv.Infrastructure/Candidates/
?? tests/SmartCv.Tests/Candidates/
~~~

Kết quả mở rộng từng file để đối chiếu manifest:

~~~text
## feature/a24-implementation
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
 M src/SmartCv.Api/Program.cs
?? docs/A24-implementation-report.md
?? src/SmartCv.Api/Endpoints/CandidateApiFilter.cs
?? src/SmartCv.Api/Endpoints/CandidateEndpoints.cs
?? src/SmartCv.Api/Endpoints/CandidateRequestReader.cs
?? src/SmartCv.Api/Endpoints/CvCandidateEndpoints.cs
?? src/SmartCv.Core/Candidates/CandidateInfoExtractor.cs
?? src/SmartCv.Core/Candidates/CandidateRules.cs
?? src/SmartCv.Core/Candidates/DuplicateMatcher.cs
?? src/SmartCv.Core/Contracts/CandidateContracts.cs
?? src/SmartCv.Infrastructure/Candidates/CandidateQueries.cs
?? src/SmartCv.Infrastructure/Candidates/CandidateWriteScope.cs
?? tests/SmartCv.Tests/Candidates/CandidateEndpointTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateInfoExtractorTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateRulesTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateSafetyTests.cs
?? tests/SmartCv.Tests/Candidates/CandidateSqlServerTests.cs
?? tests/SmartCv.Tests/Candidates/DuplicateMatcherTests.cs
~~~

git diff --check: exit 0, output trống. Đã kiểm tra thêm từng file untracked bằng git diff --no-index --check với /dev/null; không có chẩn đoán whitespace. Lệnh no-index có thể trả 1 vì có nội dung mới, không được nhầm với lỗi whitespace.

git diff --cached --name-status: exit 0, output trống. git ls-files -u: trống. Không có staged hoặc unmerged.

Danh sách manifest chỉ gồm các file A24 trong bảng trên, bao gồm chính báo cáo này. File source database/migrations, Web, scoring, OCR, Gemini, workers và test cũ không có trong diff.

Chưa có commit mới. Dừng sau khi bàn giao báo cáo để người giao việc đọc và tự commit/push.
