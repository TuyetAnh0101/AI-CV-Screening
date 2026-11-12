# Báo cáo A36 – Đề xuất nhãn HR từ quyết định

Ngày thực hiện: 13/11/2026. Phạm vi: Core, API chỉ đọc và kiểm thử A36. Không làm UI B41 hoặc các nhiệm vụ khác.

## 1. Git đầu vào, nhánh và commit nền

- Repository: `TuyetAnh0101/AI-CV-Screening`, tại `C:\TTCN\project\AI-CV-Screening`.
- Nhánh làm việc: `feature/a36-label-suggestions`.
- Commit nền/HEAD: `3391796dead1f593c12607a6718dc022a9136eaa`, `feat(B36): add review flags UI and anonymous candidate review (#57)`.
- Lượt yêu cầu trước đã dừng vì báo cáo B36 còn modified. Khi tiếp tục lượt này, working tree sạch, không staged/untracked/conflict; nhánh A36 đã được chuẩn bị sẵn.
- Đã fetch `origin --prune`. Reflog xác nhận nhánh được tạo từ origin/main; HEAD và origin/main lệch `0/0`, không có commit riêng; chưa có remote branch A36. Giữ nguyên nhánh đúng đã chuẩn bị, không tạo lại hoặc tạo nhánh phụ.
- Main chứa A35 (#49/#53), A15/A16 và các bản sửa, B35 (#55), A37/A40/B36 (#54/#56/#57). Không pull/merge/rebase/reset/stash/clean.
- Solution thực tế `SmartCv.slnx`; SDK kiểm tra thực tế `10.0.403`, phù hợp global.json. Restore cho output mới `artifacts/a36-build`, không đổi package/configuration.

## 2. Các chức năng hoàn thành

| Yêu cầu | Triển khai |
|---|---|
| Đề xuất Đạt | Shortlisted, Interview |
| Đề xuất Không đạt | Rejected + MissingSkills/NotEnoughExperience/OtherRequirement |
| Không đề xuất | CandidateWithdrew/PositionFilled/Duplicate; New/Reviewing/OnHold |
| New/Same/Conflict | So sánh SuggestedIsPass với bool? nhãn hiện có trong đúng bộ |
| Reason | Lý do giai đoạn/lý do loại; Conflict thêm nhãn hiện tại khác đề xuất thế nào |
| API chỉ đọc | Hai SELECT, AsNoTracking, không SaveChanges hoặc gọi logic lưu nhãn |
| Job filter/404/empty | Giữ thông lệ API bộ đánh giá hiện tại; bộ không có trả 404, không có đề xuất trả 200 với Items=[] |
| Không dùng kết quả AI | Thuật toán không nhận điểm, không đọc Evaluations/PredictedPass để xét đề xuất |

Không tạo/cập nhật/xóa HrLabel. Đề xuất chỉ để HR duyệt, không phải nhãn đã được ghi nhận.

## 3. Đối chiếu ZIP với mã nguồn hiện tại

Đã đọc toàn bộ mục A36 trong `HUONG_DAN_A36_A38_A39_A41_A42_B38_B39_B41_B42.md` và source/contracts/unit tests dưới `A36/`. Đã khảo sát EvaluationSetEndpoints, HrLabel/EvaluationSet/Application, constants và các test A15/A16 hiện có trước khi sửa.

Các điểm khác với mẫu:

- Không ghi đè EvaluationSetEndpoints dài bằng bản ZIP. File hiện tại chỉ đổi thành partial và thêm MapGet, tổng diff 3 dòng thêm/1 dòng bỏ. Handler mới nằm trong file riêng, dùng lại NotFoundSet/Utc hiện có.
- Giữ nguyên toàn bộ SaveCoreAsync, import CSV, xóa nhãn, EvaluationSetWriteScope, khóa/transaction và tính lại accuracy A16. Không chép code A15 cũ từ mẫu.
- Mẫu gọi RejectReasons.IsProfessional nhưng constants hiện tại không có hàm này. Rules mới dùng pattern của đúng ba mã chuyên môn; không sửa constants dùng chung.
- Mẫu Reason chỉ giải thích giai đoạn. Response Conflict mới còn nêu rõ đề xuất Đạt/Không đạt khác nhãn hiện tại, hướng dẫn HR kiểm tra trước khi lưu; Explain vẫn giữ đúng kết quả mà test ZIP yêu cầu.
- Dùng LEFT JOIN theo EvaluationSetId và ApplicationId để lấy một lần metadata/nhãn, tránh N+1. Không đọc nội dung CV.
- Giữ nguyên 15 case và assert từ ZIP. Chỉ đổi namespace test từ `SmartCv.Tests.Evaluation` sang `SmartCv.Tests.A36` vì namespace mẫu che khuất entity Evaluation trong các test cũ, gây CS0118. Đã so sánh nội dung tự động: ngoài namespace và khác biệt xuống dòng, body test giống ZIP.
- Không thực hiện hướng dẫn ghi đè file, Git stage/commit/push hoặc các lệnh PATCH/PUT/DELETE thử nghiệm trên SmartCvDb trong tài liệu.

## 4. Nghiệp vụ và ý nghĩa API

```http
GET /api/evaluation-sets/{id}/label-suggestions?jobId={optional}
```

Response: EvaluationSetId, JobId, NewCount, SameCount, ConflictCount, SkippedCount và Items. Mỗi item có ApplicationId, CandidateName/FileName, JobId/JobTitle, Stage/RejectReason/StageUpdatedAt UTC, SuggestedIsPass, CurrentLabel, CurrentNote, CurrentLabeledBy, Reason và Status.

| Status | Ý nghĩa |
|---|---|
| New | Chưa có HrLabel trong bộ này; CurrentLabel/CurrentNote/CurrentLabeledBy là null |
| Same | Nhãn bool hiện tại trùng đề xuất; không yêu cầu ghi lại hoặc thay người/ghi chú/thời gian |
| Conflict | Nhãn bool hiện tại trái với đề xuất; giữ nguyên nhãn hiện tại và cung cấp lý do cụ thể |

Note/actor không tham gia xác định Same/Conflict; trạng thái đối chiếu chỉ dựa trên nhãn Đạt/Không đạt. Nhãn của bộ khác tuyệt đối không được lấy làm CurrentLabel.

Theo ZIP, pool gồm các Application đã rời New và CV có ExtractionStatus khác Failed; có thể gồm hồ sơ chưa từng được gán nhãn trong bộ. EvaluationSet xác định **nhãn đang so sánh**, không giới hạn pool vào các hồ sơ đã được gán nhãn trước đó.

- New và CV Failed nằm ngoài pool, không làm tăng SkippedCount.
- Reviewing, OnHold và ba lý do loại phi chuyên môn trong pool làm tăng SkippedCount, không xuất hiện trong Items.
- Không tự xóa nhãn cũ của hồ sơ bị bỏ qua.
- JobId lọc trước khi đếm; mã Job không có hồ sơ trả danh sách/count rỗng, theo cách lọc của Candidates API hiện tại.
- Conflict trước, New tiếp theo, Same cuối; trong mỗi nhóm theo JobTitle (ordinal) rồi ApplicationId để thứ tự ổn định.

Ví dụ Conflict: `Loại: Thiếu kỹ năng bắt buộc. Đề xuất Không đạt, khác nhãn hiện tại Đạt. HR cần kiểm tra trước khi lưu.`

Bộ không tồn tại trả 404 ProblemDetails qua helper cũ. Bộ tồn tại không có ứng viên/đề xuất trả 200 hợp lệ. Endpoint ở cùng group/chốt Development + CvUpload:Endpoint:Enabled hiện có; không thay cơ chế truy cập hoặc API lưu nhãn.

## 5. Danh sách chính xác file mới/sửa

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Core/Evaluation/LabelSuggestionRules.cs` | Mới | Enum trạng thái và hàm thuần Suggest/Explain/Compare |
| `src/SmartCv.Core/Contracts/LabelSuggestionContracts.cs` | Mới | DTO/response rõ ràng cho B41 dùng sau này |
| `src/SmartCv.Api/Endpoints/EvaluationSetSuggestionsEndpoints.cs` | Mới | Handler chỉ đọc, LEFT JOIN, counts/sort/Conflict reason |
| `src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs` | Sửa | Partial class và MapGet trong group hiện có |
| `tests/SmartCv.Tests/Evaluation/LabelSuggestionRulesTests.cs` | Mới | Đủ 15 case ZIP, chỉ điều chỉnh namespace cho tương thích |
| `tests/SmartCv.Tests/LabelSuggestionEndpointTests.cs` | Mới | HTTP integration, SQL cô lập, query guard và snapshot không ghi |
| `docs/A36-implementation-report.md` | Mới | Báo cáo này |

Tổng **1 file sửa, 6 file mới**. Không sửa test cũ, UI, migration, entity/configuration, ConnectionStrings, secrets, cấu hình production hoặc Gemini. Không làm A38/A39/A41/A42/B38/B39/B41/B42.

## 6. Build, unit, integration và regression

| Nhóm | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Baseline full trước A36 | 1580 | 0 | 46 |
| Unit rules từ ZIP | 15 | 0 | 0 |
| API integration InMemory | 2 | 0 | 0 |
| API integration SQL Server A36 | 1 | 0 | 0 |
| Focused A36 (gồm ba nhóm trên) | 18 | 0 | 0 |
| Full regression cuối | 1598 | 0 | 46 |

Build baseline và cuối thành công. Build cuối **0 warning, 0 error**. Full tổng 1644, tăng đúng 18 test so với baseline 1626. Các hàng focused/unit/integration chồng lấp full, không cộng thành tổng khác. Không tính skipped là pass.

API tests kiểm tra:

- New=3, Same=2, Conflict=2, Skipped=5 trên fixture 14 hồ sơ; đúng thứ tự Conflict/New/Same.
- Job chính có New=3, Same=2, Conflict=1, Skipped=5; Job thứ hai chỉ có một Conflict; Job không có dữ liệu trả rỗng.
- Cả hai hướng Conflict (đề xuất Đạt/nhãn Không đạt và ngược lại) có Reason cụ thể.
- CandidateWithdrew/PositionFilled/Duplicate không được đề xuất; Reviewing/OnHold bị bỏ qua; New/CV Failed bị loại khỏi pool.
- Không lấy nhãn/ghi chú/người gán từ bộ khác. Nhãn false vẫn là Same/Conflict đúng, không bị nhầm với thiếu nhãn.
- CurrentNote Unicode, CurrentLabeledBy và StageUpdatedAt UTC được giữ đúng; CandidateName null xử lý được.
- Bộ không tồn tại 404; bộ trống trả 200 với counts=0, Items=[]; chốt endpoint tắt vẫn 404.
- Fixture có AI/PredictedPass trái với quyết định HR: Interview vẫn đề xuất Đạt dù điểm 10/predicted=false; Rejected chuyên môn vẫn đề xuất Không đạt dù điểm 99/predicted=true.
- GET lặp lại không thay đổi nhãn, accuracy, điểm, stage/version hoặc lịch sử.

46 skipped gồm **43 SQL opt-in cũ chưa bật** và **3 case PDF demo A37/A40/B34**. ZIP cũ `C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip` không còn tồn tại ở môi trường hiện tại; ZIP A36 mới không có bộ `cv-20-backend` để thay thế. Không tắt test để lấy xanh; các Fact tự skip theo điều kiện có sẵn. SQL A36 mới đã được bật và chạy thực tế riêng.

Các suite A15/A16/A35/B35 vẫn nằm trong full regression và không có failure. Không sửa assert/fixture của các suite cũ. Các test SQL transaction/concurrency cũ chưa bật không được coi là đã kiểm chứng lại.

## 7. Bằng chứng GET không ghi dữ liệu

SQL test dùng SQL Server LocalDB với catalog mới `SmartCv_A36_Test_<GUID>`. Guard kiểm tra prefix và cấm AttachDBFilename, sau đó luôn thay catalog bằng GUID mới trước khi migrate. Không migrate vào catalog được truyền từ cấu hình hay database ứng dụng.

Chỉ áp dụng migrations **đã có** vào database test mới để dựng fixture; không tạo/sửa migration. Catalog thử được giữ lại, không drop/reset. Tên các lượt chạy được ghi trong `artifacts/a36-tests/retained-sql-databases.txt` và TRX.

Bằng chứng tự động:

1. DbCommandInterceptor trong giai đoạn GET chỉ chấp nhận SQL bắt đầu bằng SELECT; Reader/Scalar/NonQuery đều qua guard.
2. Guard từ chối query chứa cột ExtractedText. Response cũng không chứa marker nội dung CV synthetic hoặc PredictedPass.
3. Request toàn bộ pool và request chỉ một Job đều **2 SELECT**, không tăng theo số ứng viên/nhãn/Job, không N+1.
4. Snapshot trước/sau GET lặp lại so sánh tất cả nhãn (bool/note/actor/time), giai đoạn/lý do/thời điểm/version, accuracy/status Run, điểm/predicted của Evaluation và số event: không đổi.
5. Factory tắt Worker và thay IAiEvaluator bằng evaluator ném lỗi nếu bị gọi; các request vẫn pass.

Trích bằng chứng focused TRX:

```text
A36 GET : 2 SELECTs; no CV text or writes.
A36 GET ?jobId=7: 2 SELECTs; no CV text or writes.
Repeated GETs preserved labels (value/note/actor/time), A16 accuracy, scores, stage/version and history.
```

JobId trong trích xuất thuộc fixture, không phải Job ứng dụng người dùng. Không ghi CV text, API key hoặc dữ liệu ứng viên thật vào log/báo cáo. Handler không có logging nội dung và chỉ project metadata cần cho đề xuất.

## 8. Lệnh và artifacts kiểm chứng

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/a36-build --no-restore
$env:SMARTCV_A36_TEST_SQL_CONNECTION='Server=(localdb)\MSSQLLocalDB;Database=SmartCv_A36_Test_Template;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'
dotnet test SmartCv.slnx --artifacts-path artifacts/a36-build --no-restore --filter 'FullyQualifiedName~LabelSuggestion' --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/a36-tests
dotnet test SmartCv.slnx --artifacts-path artifacts/a36-build --no-restore --no-build --logger 'trx;LogFileName=full.trx' --results-directory artifacts/a36-tests
```

Output riêng tránh DLL của API/Web người dùng. Không kill tiến trình ứng dụng. Biến SQL trên chỉ phục vụ test tạo catalog GUID mới; không sửa ConnectionStrings/User Secrets.

- `artifacts/a36-baseline-build.log`, `artifacts/a36-baseline-test.log`.
- `artifacts/a36-build.log`, `artifacts/a36-focused.log`, `artifacts/a36-full.log`.
- `artifacts/a36-tests/baseline.trx`, `focused.trx`, `full.trx` và danh sách database test được giữ lại.

Các artifacts bị Git ignore, không thuộc file cần commit.

## 9. Giới hạn và công việc chưa kiểm chứng

- Không thao tác SmartCvDb ứng dụng, không chạy PATCH/PUT/DELETE theo kịch bản ZIP trên database thật. Không dùng CV thật, không bật Worker hoặc gọi Gemini thật.
- Không kiểm thử browser vì không thay UI; chưa triển khai B41 duyệt/lưu đề xuất. A15 hiện có vẫn là con đường lưu sau khi HR chủ động xác nhận.
- 46 skipped được tách rõ ở trên; chưa chạy lại các case SQL A15/A16/A35 opt-in trên catalog riêng của từng suite.
- GET là snapshot đề xuất tại thời điểm đọc, không khóa quyết định/nhãn đến lúc người dùng xác nhận. B41 nên làm mới trước khi duyệt; transaction ghi nhãn A15/A16 giữ nguyên.
- Theo contract mẫu, kết quả chưa phân trang; đã kiểm chứng query không N+1 nhưng chưa benchmark trên pool rất lớn/nhiều request đồng thời.
- Không xác nhận schema/readiness của database ứng dụng; A36 không cần migration mới nhưng sử dụng các trường A35 đã có trong schema nền.
- Các lỗi compile lúc tích hợp test (namespace Evaluation của ZIP và import IWebHostBuilder test) đã được sửa trong file test mới; không có lỗi build/test còn tồn tại ở các kiểm thử đã chạy.

## 10. Git cuối

```text
## feature/a36-label-suggestions...origin/main
 M src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs
?? docs/A36-implementation-report.md
?? src/SmartCv.Api/Endpoints/EvaluationSetSuggestionsEndpoints.cs
?? src/SmartCv.Core/Contracts/LabelSuggestionContracts.cs
?? src/SmartCv.Core/Evaluation/LabelSuggestionRules.cs
?? tests/SmartCv.Tests/Evaluation/LabelSuggestionRulesTests.cs
?? tests/SmartCv.Tests/LabelSuggestionEndpointTests.cs
```

`git diff --stat` (chỉ tracked, không gồm 6 file mới):

```text
 src/SmartCv.Api/Endpoints/EvaluationSetEndpoints.cs | 4 +++-
 1 file changed, 3 insertions(+), 1 deletion(-)
```

`git diff --check`: không có lỗi whitespace. `git diff --cached --name-status`: rỗng, không staged. HEAD vẫn là commit nền. Danh sách file mới/sửa đầy đủ ở mục 5, không có file ngoài A36, không mất code A15/A16/A35/B35 hoặc tính năng cũ.

## 11. Kết luận

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT** trong phạm vi A36, với các giới hạn kiểm chứng đã nêu. Không tự stage, commit, push, merge hoặc tạo PR. Dừng sau báo cáo để người dùng quyết định Git.
