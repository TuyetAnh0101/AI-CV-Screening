# Báo cáo A40 – Review Flags và API so sánh ứng viên

Ngày thực hiện: 12/11/2026. Phạm vi: Core, API đọc kết quả và kiểm thử A40. Không triển khai UI B36/B37.

## 1. Nhánh, commit nền và môi trường

- Repository: `TuyetAnh0101/AI-CV-Screening`, `C:\TTCN\project\AI-CV-Screening`.
- Nhánh duy nhất được tạo: `feature/a40-review-flags-compare`, trực tiếp từ `origin/main` sau fetch.
- Commit nền/HEAD: `75d2ac2234d307e6a13cce270a73ffc0e10d7c2c` – `feat(B35): implement HR decisions and decision history (#55)`.
- Trước khi tạo nhánh: working tree sạch, không modified/staged/untracked/conflict. Origin đúng repository; nhánh A40 chưa có ở local/remote.
- Đã xác minh A35 (#49, hardening #53), A37 (#54), B34 (#50), B35 (#55) có trong `origin/main`. Không merge hoặc đưa nhánh chưa được duyệt vào nền.
- Windows, .NET SDK `10.0.403`, solution `SmartCv.slnx`; theo cấu hình SDK hiện có.
- Restore một lần cho output mới `artifacts/a40-build`; không đổi package hoặc SDK. Build/test dùng output này, không dừng tiến trình API/Web người dùng.

## 2. Yêu cầu đã thực hiện

| Yêu cầu | Kết quả |
|---|---|
| Bốn cờ, chỉ Completed, cho phép nhiều cờ | Hàm thuần `ReviewFlags.Evaluate`, thứ tự mã ổn định |
| Ngưỡng cấu hình và Options validation | Bind mục ReviewFlags, ValidateOnStart; sai cấu hình dừng startup |
| Ranking/Detail/Compare nhất quán | Cùng bộ đọc và thuật toán cờ; Compare dùng trực tiếp dữ liệu Ranking |
| FlaggedCount đếm hồ sơ | Đếm item có ít nhất một cờ; không cộng số cờ |
| Compare 2–3 ID, giữ thứ tự | Validation nghiêm ngặt và dựng Candidates theo thứ tự request |
| Cùng Run/Job, lỗi 400/404 | ProblemDetails với thông báo tiếng Việt |
| Giữ dữ liệu/điểm/quyết định HR | Chỉ đọc, không SaveChanges, không gọi AI hoặc cập nhật Stage |
| Không N+1, không gửi CV text | Query theo tập, projection độ dài trên SQL; Compare 2/3 ứng viên đều 9 SELECT |
| Tương thích A35/A37/B34/B35 | Chỉ thêm fields, giữ StageVersion/StageCounts/Skills; full regression pass |

## 3. ZIP và mã nguồn hiện tại

Đã đọc toàn bộ mục A40 trong `A35_B36/HUONG_DAN_A35_A37_A40_B34_B36.md`, `ReviewFlags.cs`, `ReviewFlagsTests.cs`, phần A40 của ResultContracts/RunEndpoints/EvaluationEndpoints và snippets Program/appsettings. Dùng mẫu làm tham khảo, không ghi đè nguyên file.

Các khác biệt được xử lý:

- Source main đã có StageVersion, kiểm soát quyền truy cập Development, readiness/runtime hardening, xử lý GPA/JSON, B35 và A37. Tất cả giữ nguyên.
- Mẫu chỉ suy luận cap từ điểm cuối và cấu hình cap hiện tại. Triển khai mới yêu cầu thêm dấu vết cap thực sự và kỹ năng thiếu đã lưu, dùng trọng số lưu trong Run; không gắn cờ chỉ vì FinalScore=69.
- Mẫu bỏ empty ID khi tách chuỗi; triển khai mới từ chối `1,,2`, `1,2,` và giữ nguyên thứ tự hợp lệ.
- Mẫu Compare có thể xếp hạng các bản ghi chưa Completed còn lưu điểm. Triển khai mới tái sử dụng Ranking: Pending/Failed không có hạng, dù dữ liệu cũ còn điểm.
- Bổ sung Options validation; mẫu Configure đơn thuần chưa kiểm tra ngưỡng.
- SQL Server LEN bỏ khoảng trắng cuối. Bộ đọc mới dùng sentinel để độ dài tính cả khoảng trắng, đã kiểm thử 499 ký tự cộng một dấu cách thành 500.
- Không làm theo các lệnh pull/stage/commit/push trong tài liệu ZIP vì yêu cầu trực tiếp của người dùng cấm những thao tác đó.

PDF trong ZIP là dữ liệu demo, không phải UI mockup. Kiểm thử đã đọc CV #17 và #18 bằng PdfPig: lần lượt **596** và **168** ký tự theo cách trích xuất của test. #18 có ShortText, #17 không có cờ này. Không suy ra điểm Gemini thật từ các PDF.

## 4. Thuật toán cờ

`ReviewInput` nhận dữ liệu kết quả đã lưu: status, điểm, nguồn điểm, độ dài, trọng số Run, sự tồn tại của kỹ năng bắt buộc chưa khớp và Reasoning. Không cần tên, email hoặc CV text để xét cờ. Message không sao chép Reasoning, tên kỹ năng hoặc nội dung CV.

1. Nếu Status khác Completed: trả danh sách rỗng, kể cả bản ghi Pending/Failed còn lưu điểm cũ.
2. KeywordOnly: có KeywordScore và nguồn KeywordOnly hoặc không có AiScore. Không xét ScoreGap trong nhánh chỉ dùng keyword.
3. ScoreGap: có cả hai điểm và chênh tuyệt đối >= ScoreGap (mặc định 30).
4. ShortText: biết độ dài và độ dài < MinTextLength (mặc định 500). Độ dài null là chưa biết, không tự coi bằng 0; chuỗi rỗng có độ dài 0.
5. CappedHighAi: nguồn Combined, AI >= CappedHighAiScore (80), có EvaluationSkill bắt buộc được lưu là chưa khớp, trọng số Run hợp lệ, điểm tổng có trọng số sau làm tròn vượt FinalScore, và Reasoning chứa đúng dấu vết `ScoreCombiner` ghi khi áp dụng cap ở mức điểm cuối đó.

Hàm chỉ tạo mã/thông báo, không tính lại hoặc ghi đè bất kỳ điểm đã lưu nào. Phép nhân trọng số ở điều kiện cap chỉ kiểm chứng nguyên nhân, không gọi AI và không dùng để thay điểm trả về.

Dấu vết cap được đối chiếu với output thực của `ScoreCombiner` trong unit tests, gồm cap lịch sử 65,5 và trọng số 0,4/0,6. Không dùng cap hiện tại để diễn giải lại một Run cũ. Khi thiếu dấu vết hoặc thiếu kết quả kỹ năng đã lưu, không khẳng định có CappedHighAi; xem giới hạn ở mục 10.

Options hợp lệ: `0 < ScoreGap <= 100`, `MinTextLength > 0`, `0 <= CappedHighAiScore <= 100`. Giá trị mặc định trong API appsettings là 30/500/80. Không thay bất kỳ cấu hình Gemini, Worker, ConnectionStrings hoặc secrets nào.

## 5. Contracts và API

Bổ sung tương thích vào contracts hiện có:

- ReviewFlagDto: Code, Message.
- RankingItemDto.ReviewFlags, RankingResponse.FlaggedCount.
- EvaluationDetailDto.ReviewFlags, ExtractedTextLength.
- CandidateCompareResponse, CompareSkillDto, CandidateCompareItemDto.

Các collection mặc định rỗng; không bỏ/đổi tên trường cũ. Stage, RejectReason, StageUpdatedAt, StageVersion, StageCounts, Skills, GPA, điểm và metadata cũ được giữ nguyên.

API mới:

```http
GET /api/runs/{id}/compare?evaluationIds=21,22,23
```

Response gồm Run/Prompt, ngưỡng đạt, Job ID/title/min GPA/min kinh nghiệm, danh sách kỹ năng của Job (MustHave trước), Candidates theo đúng thứ tự ID. Mỗi ứng viên có ApplicationId, EvaluationId, CvId, tên/file, status/error, điểm/hạng/mức/nguồn điểm/dự đoán, kinh nghiệm, giai đoạn và version HR, nhận xét, cờ, số kỹ năng bắt buộc khớp và dictionary kỹ năng đạt kèm bằng chứng.

Compare tái sử dụng Ranking để điểm/hạng không bị tính theo thuật toán khác. Hạng cạnh tranh (1,1,3...), RankTotal chỉ đếm Completed có FinalScore; Pending/Failed có Rank null. Không phân hạng lại riêng 2–3 hồ sơ được chọn.

API mới dùng cùng nhóm `/api/runs` và cùng chốt truy cập với Runs hiện có. Endpoint không được map khi chức năng CV bị tắt; cấu hình bật API không xác thực ở Production tiếp tục bị guard hiện có từ chối startup. Không vô hiệu hóa authentication/validation/rate limits.

| Trường hợp | HTTP |
|---|---:|
| Hợp lệ, 2 hoặc 3 ID | 200 |
| Thiếu ID, sai số lượng, trùng, <=0, không phải int, overflow, phần tử rỗng | 400 |
| Các Evaluation khác Run đang yêu cầu | 400 |
| Các Evaluation khác Job | 400 |
| Run không tồn tại | 404 |
| Một hoặc nhiều Evaluation không tồn tại | 404 |

400/404 của Compare trả `application/problem+json`, có status/title/detail rõ ràng. Validation cú pháp thực hiện trước truy vấn; khi cú pháp hợp lệ thì kiểm tra Run, missing Evaluation, khác Run rồi khác Job.

## 6. Database và hiệu năng

Không thay entities/configurations/DbContext, không tạo hoặc sửa migration/SQL script. A40 chỉ đọc schema hiện có.

Bộ đọc cờ dùng một query scalar theo tập Evaluation. SQL tính độ dài, không tải ExtractedText về API; Reasoning chỉ dùng nội bộ để kiểm tra dấu vết cap. Message/response cờ không chứa nội dung đó. Compare trả nhận xét và bằng chứng theo hợp đồng, không có CV text hay AiRawResponse.

Kiểm thử SQL tạo catalog mới có prefix `SmartCv_A40_Test_` và GUID, migrate bằng migrations sẵn có chỉ trong catalog mới. Kiểm tra prefix và từ chối AttachDBFilename. Không dùng catalog từ cấu hình làm đích migrate; luôn thay bằng tên GUID mới. Database thử được **giữ lại**, không drop/reset.

Danh sách catalog các lượt kiểm thử cuối được ghi tại `artifacts/a40-tests/retained-sql-databases.txt`; tên catalog của case SQL cũng nằm trong TRX. Các database thử tạo trong lượt debug trước đó cũng được giữ lại; không tự dọn dữ liệu SQL.

SQL integration kiểm chứng LEN/sentinel, flags, hạng kể cả Pending/Failed còn điểm, thứ tự 2/3 ID, metadata, skills/evidence, state/version HR. Snapshot điểm/status/giai đoạn/version/thời điểm và số event không đổi trước/sau chuỗi GET. DbCommandInterceptor ghi nhận **9 query cho 2 ID và 9 query cho 3 ID**, không N+1.

Không chạy migration hoặc SQL thay đổi dữ liệu trên database ứng dụng; không xác nhận tình trạng triển khai schema database ứng dụng trong nhiệm vụ này. Không bật Worker chấm điểm; AI trong API test được thay bằng evaluator ném lỗi nếu bị gọi.

## 7. Danh sách chính xác file thay đổi

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Core/Scoring/ReviewFlags.cs` | Mới | Options/ngưỡng, mã cờ, dữ liệu đầu vào và thuật toán thuần |
| `src/SmartCv.Core/Contracts/ResultContracts.cs` | Sửa | Trường cờ, độ dài, số đếm và DTO Compare; giữ trường cũ |
| `src/SmartCv.Api/Endpoints/EvaluationReviewReader.cs` | Mới | Projection SQL dùng chung, không materialize CV text |
| `src/SmartCv.Api/Endpoints/RunCompareEndpoints.cs` | Mới | Partial RunEndpoints: validation, đọc Compare, ProblemDetails |
| `src/SmartCv.Api/Endpoints/RunEndpoints.cs` | Sửa | Map Compare, đọc cờ Ranking, FlaggedCount; đổi class thành partial |
| `src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs` | Sửa | Thêm flags và độ dài vào Detail qua bộ đọc chung |
| `src/SmartCv.Api/Program.cs` | Sửa | Bind/ValidateOnStart ReviewFlagOptions |
| `src/SmartCv.Api/appsettings.json` | Sửa | Chỉ thêm mục ReviewFlags mặc định; thêm newline cuối file |
| `tests/SmartCv.Tests/Scoring/ReviewFlagsTests.cs` | Mới | Boundary, cap thật/giả, privacy của message và PDF 17/18 |
| `tests/SmartCv.Tests/A40EndpointTests.cs` | Mới | API/Options/access boundary/read-only và SQL Server integration |
| `docs/A40-implementation-report.md` | Mới | Báo cáo này |

Tổng: **5 file sửa, 6 file mới**. Không có source UI, source Worker, Gemini/privacy hoặc migration trong danh sách. Các file hạ tầng chung Program/contracts chỉ thay tại điểm cần thiết để tích hợp A40.

## 8. Kiểm thử và số lượng

Baseline trước sửa: build thành công, 0 warning/error; full suite **1527 Passed, 0 Failed, 42 Skipped**, tổng 1569.

Build cuối: **0 warning, 0 error**.

| Nhóm | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Thuật toán cờ unit tests | 18 | 0 | 0 |
| PDF demo #17/#18 | 1 | 0 | 0 |
| HTTP integration/Options/access boundary, InMemory | 21 | 0 | 0 |
| SQL Server integration A40 | 1 | 0 | 0 |
| Focused A40 (gồm bốn nhóm trên) | 41 | 0 | 0 |
| Full regression (gồm cả A40) | 1568 | 0 | 42 |

Các hàng focused/full chồng lấp các nhóm nhỏ, không cộng thành tổng khác. Full tổng 1610, tăng đúng 41 test so với baseline; không bỏ hoặc sửa test cũ.

Các boundary đã thử: gap 29,99/30/-30; AI 79,99/80; text null/0/499/500; nhiều cờ; thiếu AiScore; Pending/Failed; FinalScore thấp không có cap thật; weighted score sau làm tròn bằng cap; cap lịch sử khác mặc định; cấu hình ngưỡng sai; số đếm ứng viên có nhiều cờ; cùng Evaluation có cùng flags ở ba API; thứ tự hai/ba ID; tie ranks; tất cả nhóm 400/404; access guard; dữ liệu HR không bị ghi.

Hai lỗi ở lượt test ban đầu thuộc fixture: thiếu FileSizeBytes vi phạm CHECK SQL, và test dự kiến Production trả 404 trong khi guard hiện có chủ động từ chối startup khi bật API chưa xác thực. Đã sửa seed và assert đúng hành vi bảo mật hiện có; không sửa production để vượt guard hoặc constraint. Sau đó focused và full đều pass.

42 skipped là các bộ SQL opt-in cũ chưa bật connection tương ứng: A35=7, A15=7, A16=10, FullAudit=2, Prompt=5, A11=6, RuntimeScriptDeployment=1, A17=3, RuntimeSchema=1. Không coi chúng là passed. Case SQL mới của A40 đã được bật và pass riêng.

Lệnh tái kiểm tra (SQL chỉ vào database thử nghiệm được tạo mới bởi test):

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/a40-build --no-restore
$env:SMARTCV_A40_DEMO_ZIP='C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip'
$env:SMARTCV_B34_DEMO_ZIP=$env:SMARTCV_A40_DEMO_ZIP
$env:SMARTCV_A40_TEST_SQL_CONNECTION='Server=(localdb)\MSSQLLocalDB;Database=SmartCv_A40_Test_Template;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'
dotnet test SmartCv.slnx --artifacts-path artifacts/a40-build --no-restore --no-build --filter 'FullyQualifiedName~ReviewFlagsTests|FullyQualifiedName~A40EndpointTests' --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/a40-tests
dotnet test SmartCv.slnx --artifacts-path artifacts/a40-build --no-restore --no-build --logger 'trx;LogFileName=full.trx' --results-directory artifacts/a40-tests
```

Không bật các biến SQL opt-in cũ để tránh chạy thêm các fixture ngoài A40. Output/restore/baseline/build/full logs và TRX nằm `artifacts/a40-*`, bị Git ignore, không đưa lên GitHub.

## 9. Hồi quy A35/A37/B34/B35

- Full suite giữ nguyên các test cũ, có các test stage/concurrency, privacy/masking, ranking UI và B35 HR timeline. Không có failure cuối.
- SQL/API test A40 xác minh Stage, RejectReason, StageVersion, StageUpdatedAt và StageCounts giữ nguyên, Rank/Skills/điểm khớp Detail/Ranking/Compare.
- Không sửa code quyết định HR, timeline UI, privacy masker, Gemini, OCR hoặc tiến trình xử lý.
- Các test SQL opt-in cũ không được chạy lại; không tuyên bố đã kiểm chứng mọi tình huống database/concurrency của toàn hệ thống.

## 10. Giới hạn và rủi ro

- CappedHighAi cần dấu vết do ScoreCombiner ghi và kỹ năng thiếu đã lưu. Dữ liệu legacy thiếu những bằng chứng này sẽ không được gắn cờ cap chỉ bằng suy đoán từ điểm. Không backfill hoặc thay dữ liệu cũ.
- Hệ thống chưa lưu một trường cap-applied có cấu trúc. Nếu sau này đổi format Reasoning của ScoreCombiner hoặc cách lưu kỹ năng, cần cập nhật nhận diện cùng test cap thực; không thay schema chỉ vì A40.
- Dữ liệu Job/JobSkills hiện có được dùng như các API hiện tại; chưa có snapshot lịch sử kỹ năng Job. Không giải quyết phiên bản hóa Job trong A40.
- Compare tái sử dụng toàn bộ Ranking của Job để bảo đảm hạng thống nhất. Số query cố định nhưng lượng hàng phụ thuộc số hồ sơ của Run/Job; chưa có kiểm thử tải lớn hoặc benchmark nhiều request đồng thời.
- Các GET không tạo transaction snapshot dài giữa các truy vấn. Nếu HR khác thay đổi trong lúc đọc, client vẫn cần dùng StageVersion ở thao tác ghi như A35; API Compare không ghi nên không ghi đè quyết định.
- Không chạy trên database ứng dụng, không gọi Gemini thật, không bật Worker chấm điểm. Không khẳng định điểm AI thật của CV demo #17/#18.
- Không làm UI B36/B37 hoặc kiểm thử browser mới vì không thay UI; hồi quy UI hiện có chạy qua full suite. Không khẳng định đã sửa các lỗi loading không ổn định được ghi nhận ở nhiệm vụ trước.

## 11. Git cuối

```text
## feature/a40-review-flags-compare...origin/main
 M src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs
 M src/SmartCv.Api/Endpoints/RunEndpoints.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Contracts/ResultContracts.cs
?? docs/A40-implementation-report.md
?? src/SmartCv.Api/Endpoints/EvaluationReviewReader.cs
?? src/SmartCv.Api/Endpoints/RunCompareEndpoints.cs
?? src/SmartCv.Core/Scoring/ReviewFlags.cs
?? tests/SmartCv.Tests/A40EndpointTests.cs
?? tests/SmartCv.Tests/Scoring/ReviewFlagsTests.cs
```

`git diff --stat` chỉ gồm tracked; sáu file mới chưa theo dõi được liệt kê riêng ở trên:

```text
 src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs |  7 ++-
 src/SmartCv.Api/Endpoints/RunEndpoints.cs        |  9 +++-
 src/SmartCv.Api/Program.cs                       |  6 +++
 src/SmartCv.Api/appsettings.json                 |  7 ++-
 src/SmartCv.Core/Contracts/ResultContracts.cs    | 59 ++++++++++++++++++++++++
 5 files changed, 84 insertions(+), 4 deletions(-)
```

`git diff --check`: không lỗi whitespace. `git diff --cached --name-status`: rỗng, không staged. HEAD không đổi so với commit nền. Không xóa code cũ, không có file ngoài A40; artifacts/logs/database thử nghiệm không thuộc thay đổi Git.

## 12. Kết luận

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT** trong phạm vi A40, với các giới hạn kiểm chứng nêu trên. Không tự stage, commit, push, tạo PR hoặc merge. Dừng sau báo cáo để người dùng duyệt và quyết định Git.
