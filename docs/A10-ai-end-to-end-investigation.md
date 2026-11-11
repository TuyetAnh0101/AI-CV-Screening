# Điều tra Gemini end-to-end — 11/11/2026

Trạng thái: **ĐÃ KHẮC PHỤC THÀNH CÔNG — đã xác minh một CV tổng hợp qua Gemini thật, SQL Server, API và HTML Blazor thực tế.**

Nhánh: `fix/A10-ai-end-to-end-analysis`, bắt đầu từ `aad69f2` của `main`. Không commit, push, tạo PR hoặc merge. Các bản ghi thử được giữ trong `SmartCvDb` để người dùng kiểm tra. Các tiến trình API/Web thử trên cổng 15156/15157 đã dừng sau kiểm chứng.

## A. Tại sao trước đây chưa có điểm Gemini?

**Nguyên nhân được chứng minh trong dữ liệu gần nhất là lỗi gọi model snapshot cũ, sau đó hệ thống fallback KeywordOnly; Completed không có nghĩa AI thành công.**

- Trước kiểm thử, Evaluation 65 / Run 23 và Evaluation 64 / Run 22 có `AiScore=NULL`, `ScoreSource=KeywordOnly`, snapshot `gemini-3.8-flash`, lỗi không phản hồi sau 90 giây và hết 3 lần thử.
- Evaluation 56 / Run 17 và Evaluation 54 / Run 16 lưu lỗi HTTP 503, high demand của cùng model. Đây là lỗi nhà cung cấp tại thời điểm đó, không phải bằng chứng EF làm mất điểm.
- Tổng lịch sử của model `gemini-3.8-flash`: 16 evaluations, không evaluation nào có AiScore; 10 KeywordOnly, không có raw response. Không sửa hoặc chấm lại các lượt này.
- Đổi cấu hình hiện tại không đổi model của lượt cũ. Phải tạo lượt mới. Cấu hình repository hiện tại đã dùng `gemini-3.1-flash-lite` từ bản sửa trước; phiên này không đổi model.
- Không tìm thấy evaluation lịch sử có lỗi JSON/GPA/kỹ năng trong ErrorMessage. Không có bằng chứng để gán các lỗi 503/timeout đó cho parser.

**Lỗi nội bộ bổ sung đã tái hiện và sửa:** parser ghép cả text của phần `thought:true` với câu trả lời JSON. HTTP 200 chứa thought + answer hợp lệ bị từ chối; ngược lại thought-only trông giống JSON hợp lệ bị nhận nhầm. Trước sửa: hai test FAIL; sau sửa: hai test PASS. Chưa chứng minh lỗi này đã xảy ra trong các lượt lịch sử của người dùng. `Part.thought` là metadata của phần suy nghĩ trong [GenerateContent API](https://aistudio.google.com/api-reference/generate-content#Resource:Part).

**Lỗi hiển thị:** tổng kết tiến độ gọi các hồ sơ KeywordOnly là thành công; trang chi tiết Pending không có thao tác tải lại cùng Id. Điều này có thể làm người dùng hiểu nhầm Completed hoặc tiếp tục nhìn dữ liệu cũ.

Không tìm thấy lỗi làm mất AiScore khi evaluator trả thành công: `RunScoringProcessor.cs:221` gọi evaluator bằng snapshot; `ScoreCombiner.cs:61` ghép điểm; `RunScoringProcessor.cs:313` gán điểm/raw/reasoning; `SaveChanges` tại dòng 280 lưu kết quả và kỹ năng. Persistence SQL thật và API đã được đối chiếu thành công.

## B. Bảng phân tích lỗi

| Vị trí | Lỗi hoặc phát hiện | Bằng chứng | Đã sửa? |
|---|---|---|---|
| `EvaluationRuns.ModelName`, `Evaluations.ErrorMessage` | Lượt cũ dùng 3.8, 503/timeout rồi KeywordOnly | Run 16, 17, 22, 23; không có AiScore | Giữ lịch sử; tạo Run 24 bằng model hiện tại thành công |
| `src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs:194` | Ghép thought vào JSON answer | Hai regression FAIL trước sửa | Có, chỉ đọc final answer parts |
| Cùng file, dòng 162–166 | Schema chưa diễn đạt các giới hạn validation sẵn có | Validator yêu cầu score 0–100, GPA 0–4, years 0–60 | Có, thêm minimum/maximum; không đổi giới hạn |
| `src/SmartCv.Core/Ai/AiPromptBuilder.cs:41,58` | Chưa yêu cầu rõ kinh nghiệm dự án và bằng chứng trong nhận xét | Đối chiếu notebook | Có, bổ sung user prompt; không đổi SystemPrompt đã lưu |
| `src/SmartCv.Web/Helpers/RunFlow.cs:67,84` | Completed KeywordOnly vẫn có tổng kết thành công | Regression toàn bộ/một phần fallback | Có, đếm riêng AI/KeywordOnly và cảnh báo |
| `src/SmartCv.Web/Components/Upload/RunProgressPanel.razor:102,245` | Nhãn và tổng kết dễ nhầm AI thành công | Render test | Có |
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor:43` | Detail cùng Id không có cách làm mới | Pending → refresh → Completed | Có, nút Làm mới kết quả |
| `src/SmartCv.Web/Helpers/Display.cs:53` | Thông báo an toàn bỏ sót HTTP 404 | UI test với lỗi model | Có |
| `appsettings.json`, `launchSettings.json` | Worker mặc định tắt nhưng hai launch profile đã bật sẵn | Test binding cấu hình + kiểm tra file | Không cần sửa cấu hình |

Giới hạn số trong schema được xác minh từ [Google Schema API](https://aistudio.google.com/api-reference/generate-content#Resource:Schema). Phân biệt JSON/schema với lỗi dịch vụ theo [structured output](https://ai.google.dev/gemini-api/docs/structured-output) và [API errors](https://ai.google.dev/gemini-api/docs/api-errors).

## C. Cấu trúc, cấu hình và luồng đã rà soát

Repository gồm `SmartCv.Api` (endpoints/workers/configuration), `SmartCv.Core` (entities/contracts/scoring/prompt), `SmartCv.Infrastructure` (Gemini/EF/storage/extraction), `SmartCv.Web` (Blazor/API clients) và `SmartCv.Tests`.

Phạm vi kiểm tra gồm Program/DI/options; appsettings theo môi trường, launch profiles, User Secrets, biến môi trường; Gemini evaluator/rate limiter/fake/contracts/prompt/preview/health; keyword/combiner/run processor/worker/run/evaluation endpoints; DbContext/entities/configurations/migrations; upload/extraction/application endpoints và frontend upload/job/progress/results/detail; các nhóm test tương ứng. Không có AGENTS.md trong repository hoặc các thư mục cha đã kiểm tra.

Đã đọc notebook tại `C:\TTCN\project\Smart_CV_Analyzer_v2_clean_fixed.ipynb`, gồm AIResponse, AIJobResult, PROMPT_TEMPLATE, build_ai_prompt, analyze_cv_ai, build_results và is_quota_error. Nội dung được dùng làm dữ liệu tham chiếu. Giữ hợp đồng một job/một evaluation của C#, không sao chép schema nhiều job của Python.

| Cấu hình | Kết quả kiểm chứng |
|---|---|
| Model có hiệu lực | `Scoring:ModelName=gemini-3.1-flash-lite` |
| `Gemini:Model` | Không có property binding; đã bỏ trong bản sửa trước |
| Key | User Secrets .NET, mục `Gemini:ApiKey`, có giá trị và gọi thật được; không xuất giá trị |
| Connection | User Secrets `ConnectionStrings:SmartCvDb`, kết nối SQL Server thành công |
| Biến môi trường | Không phát hiện override model/key/worker ở Process/User/Machine lúc audit |
| Launch profiles | `http` và `https`: Development, `Scoring__WorkerEnabled=true` |
| Chạy không dùng launch profile | Cấu hình nền `WorkerEnabled=false`; cần chủ động bật bằng cấu hình tiến trình |
| UseFake | false trong phiên live; Preview xác nhận `AiSimulated=false` |
| Active Prompt | Đúng một bản, PromptVersionId 2 |
| Snapshot Run 24 | Model 3.1-flash-lite, temperature 0.2, KeywordWeight 0.30, AiWeight 0.70, PromptVersionId 2 |
| Health/Preview/worker | Cùng nguồn model Scoring:ModelName; worker dùng snapshot lượt |
| Database | `SmartCvDb`, MULTI_USER; trước test không có lượt Queued/Running |

Không dùng `C:\TTCN\API.env`, không đưa key vào source/log/báo cáo, không giả định `.env` Python tự cấp cấu hình cho .NET. Không cần thay đổi User Secrets.

| Bước | Trạng thái | Bằng chứng thực tế |
|---|---|---|
| Upload | PASS | POST upload HTTP 200, một PDF tổng hợp Accepted; file tồn tại |
| Extraction | PASS | Completed, PdfText, 654 ký tự; có bằng chứng Entity Framework Core |
| Application | PASS | POST gán Job HTTP 200, ApplicationId 42, JobId 1 |
| Job criteria | PASS | Backend Developer (.NET), 3 must-have, 3 nice-to-have, GPA 2.5, kinh nghiệm 0.5 năm |
| Run | PASS | POST /api/runs HTTP 201, Run 24 Queued, snapshot đúng |
| Worker | PASS | Worker thật, MULTI_USER, quan sát Queued → Running → Completed, 1/1 processed |
| Gemini | PASS | Metadata model HTTP 200; một request tối thiểu .NET HTTP 200; Preview/worker mỗi lần HTTP 200 |
| Parsing | PASS | JSON có score/matched/missing/years/gpa/reasoning; parse/validate thành công |
| Scoring | PASS | AiScore 100; Combined; công thức 0.3 × 100 + 0.7 × 100 = 100 |
| Database | PASS | Evaluation 66, raw JSON hợp lệ, nhận xét và 6 EvaluationSkills có evidence |
| API | PASS | Detail/raw/điểm/nhận xét khớp SQL đọc bằng DbContext mới |
| UI | PASS | HTML trang Results/Detail thật HTTP 200, thấy model/điểm/CV; Detail có đúng reasoning |

PDF tổng hợp có C#, ASP.NET Core, SQL Server, EF Core, Git, Docker; dự án Web API và inventory; GPA 3.5/4; mốc kinh nghiệm 01/2024–10/2026. Không dùng dữ liệu cá nhân của ứng viên thật. OCR không tham gia lượt này vì PDF có chữ; nhóm test OCR có sẵn vẫn chạy trong full suite.

## D. Các file đã chỉnh sửa

| File | Thay đổi và ảnh hưởng |
|---|---|
| `src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs` | Lọc thought, đồng bộ schema với validation; bảo vệ parsing HTTP 200 |
| `src/SmartCv.Core/Ai/AiPromptBuilder.cs` | Yêu cầu nhận xét có bằng chứng và kinh nghiệm thực hành; không đổi years/formula |
| `src/SmartCv.Web/Helpers/RunFlow.cs` | Đếm AI thành công và KeywordOnly; thêm outcome cảnh báo |
| `src/SmartCv.Web/Helpers/Display.cs` | Hiển thị HTTP 404 an toàn |
| `src/SmartCv.Web/Components/Upload/RunProgressPanel.razor` | Tổng kết, thống kê nguồn điểm rõ ràng |
| `src/SmartCv.Web/Components/Upload/RunProgressPanel.razor.css` | Màu cảnh báo và sáu ô thống kê |
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor` | Tải lại dữ liệu cùng EvaluationId bằng nút rõ ràng |
| `tests/SmartCv.Tests/GeminiEvaluationServiceTests.cs` | Thought+answer, thought-only, multipart, schema bounds |
| `tests/SmartCv.Tests/GeminiModelConfigurationTests.cs` | Binding launch env vào cấu hình nền; kiểm tra worker, model, fake, 30/70 |
| `tests/SmartCv.Tests/GeminiScoringFlowTests.cs` | HTTP 200 invalid → persisted KeywordOnly; multipart valid → persisted Combined; API khớp |
| `tests/SmartCv.Tests/RunProgressUiTests.cs` | Tổng kết toàn bộ/một phần fallback và cách đếm AI |
| `tests/SmartCv.Tests/EvaluationDetailUiTests.cs` | Refresh Pending cùng Id và lỗi 404 an toàn |
| `docs/A10-ai-end-to-end-investigation.md` | Báo cáo và bằng chứng kiểm chứng |

Không đổi thuật toán keyword, công thức, threshold, database schema/migration, model, cơ chế versioning SystemPrompt hoặc chức năng upload/OCR. Không có thay đổi cấu hình ứng dụng trong diff cuối cùng.

## E. Kiểm thử

| Kiểm tra | Kết quả |
|---|---|
| `dotnet build` | PASS, 0 warnings, 0 errors |
| Full `dotnet test --no-build --no-restore --logger "trx;LogFileName=verified-sql.trx"` | 855 PASS, 0 FAIL, 0 SKIP |
| SQL Server integration | 28 tests SQL Server thật trong database tạm được bảo vệ bằng tiền tố riêng; không đổi SmartCvDb |
| GeminiEvaluationServiceTests | 61 PASS, mock HTTP |
| GeminiScoringFlowTests | 5 PASS, evaluator thật với mock HTTP → worker → persistence/API |
| Progress/Detail UI tests | 53 PASS, gồm thao tác refresh |
| Live Gemini | Ba lần generateContent tổng cộng: tối thiểu, Preview, worker; đều HTTP 200; không fake |
| UI live | GET Results/Detail HTTP 200, HTML render dữ liệu thật qua API |
| `git diff --check` | PASS |

Các test có sẵn và mới bao phủ binding, model snapshot, active prompt, HTTP/schema/parsing, 404 không retry, 429/503 retry có giới hạn, timeout/network, malformed JSON, blocked response, fallback, CV chưa trích xuất, chưa có Active Prompt, processor/combiner, persistence và Results API. Lỗi được mock, không phá key/model/prompt dùng chung.

Baseline trước sửa dưới sandbox: lần đầu 99 FAIL do logger Windows EventLog bị Access denied; cùng mã nguồn chạy với `Logging__EventLog__LogLevel__Default=None` đạt 817 PASS, 28 SKIP (SQL chưa bật). Đây là hạn chế môi trường kiểm thử, không phải 99 lỗi chức năng. Lần cuối chạy bằng tài khoản có quyền Windows và bật SQL tests bằng biến môi trường dành riêng đạt toàn bộ 855/855. Không bỏ assertion, tắt test hoặc bỏ qua test để đạt kết quả.

TRX cuối ở `tests/SmartCv.Tests/TestResults/verified-sql.trx` (artifact local được gitignore). Không phải mọi test trong full suite đều gọi Gemini thật; bằng chứng live được tách riêng ở phần F.

## F. Bằng chứng AI thật

| Trường | Giá trị |
|---|---|
| Database | SmartCvDb |
| CV | `aff831a6-55bf-453f-8dea-1267a8d443df` — scapp-a10-synthetic-backend.pdf |
| Application / Job | 42 / 1 |
| RunId / EvaluationId | **24 / 66** |
| PromptVersionId | 2, giữ nguyên bản đang Active |
| Model | gemini-3.1-flash-lite |
| HTTP generateContent worker | 200, response headers sau khoảng 1419 ms |
| HTTP generateContent Preview | 200, response headers sau khoảng 1932 ms |
| AiScore / KeywordScore / FinalScore | 100.00 / 100.00 / 100.00 |
| ScoreSource | Combined |
| Raw | JSON hợp lệ, score khớp AiScore trong DB |
| Skills | 6 dòng, mã đúng Job, có evidence trích từ CV |
| API equivalence | AiScore, FinalScore, raw, reasoning khớp DB |

Nội dung `AiRawResponse` đã đọc lại từ SQL của Evaluation 66:

```json
{
  "score": 100,
  "matchedSkills": ["aspnetcore", "csharp", "sqlserver", "docker", "efcore", "git"],
  "missingSkills": [],
  "yearsExperience": 2.75,
  "gpa": 3.5,
  "reasoning": "Ứng viên đáp ứng đầy đủ các kỹ năng bắt buộc và ưu tiên với 2 năm 9 tháng kinh nghiệm thực tế. CV thể hiện rõ năng lực qua các dự án xây dựng Web API, sử dụng thành thạo C#, ASP.NET Core, SQL Server và EF Core. GPA 3.5/4.0 vượt yêu cầu tối thiểu của vị trí."
}
```

Điểm 100 là kết quả Gemini trả cho mẫu tổng hợp đầy đủ tiêu chí, không phải số điền sẵn. Regression riêng sử dụng AiScore 82 và keyword khác AI để kiểm tra công thức 30/70 có tác động thực sự.

Sau khi chạy lại API/Web bằng profile thông thường và cùng SmartCvDb, có thể kiểm tra:

- API: `/api/runs/24`, `/api/runs/24/ranking?jobId=1`, `/api/evaluations/66`.
- Web: `/results?runId=24&jobId=1`, `/results/evaluations/66`.

Phiên thử dùng API `http://localhost:15156`, Web `http://localhost:15157` với ApiBaseUrl trỏ API thử. HTML đã xác nhận có tên CV tổng hợp, model, nhãn AI và điểm 100; trang chi tiết có chính reasoning phía trên và nút Làm mới kết quả. Chưa chạy trình duyệt tương tác bằng Playwright; component tests đã kiểm tra thao tác refresh, render và fallback.

## G. Git và bảo vệ dữ liệu

- Repository chính: `C:\TTCN\project\AI-CV-Screening`; không sửa các worktree khác.
- Working tree sạch trước khi tạo nhánh; đã fetch origin. `main` và `origin/main` cùng `aad69f2`.
- `fix/A10-gemini-model` có patch tương đương đã squash vào `main` qua #32 (`git cherry` đánh dấu `-`); A11 database access đã merge qua #28.
- Nhánh hiện tại: `fix/A10-ai-end-to-end-analysis`.
- `git status`: 12 tracked files modified, báo cáo này là một untracked file; không staged.
- `git diff --stat`: 12 files changed, 318 insertions(+), 16 deletions(-). File báo cáo mới chưa tracked nên không nằm trong diff stat đó.
- Chỉ dùng `safe.directory` cho từng lệnh Git; không sửa global Git config.
- Không commit/push/PR/merge/stash/reset hoặc xóa file người dùng.

## H. Vấn đề còn lại và giới hạn

- Lượt cũ lỗi vẫn giữ nguyên đúng lịch sử; việc đổi model không tự chấm lại chúng. Không dùng Run cũ để kết luận model hiện tại hỏng.
- Không khẳng định lỗi HTTP 503/timeout của nhà cung cấp sẽ không tái diễn. Retry/fallback hiện có vẫn hoạt động có giới hạn và UI nay phân biệt nguồn điểm.
- Không có bằng chứng HTTP 200 hợp lệ đã bị EF mất AiScore trong dữ liệu cũ. Trường hợp HTTP 200 malformed JSON → KeywordOnly và trường hợp thought → parse lỗi đã được kiểm thử có kiểm soát, tách khỏi lịch sử thực tế.
- Base WorkerEnabled=false là chủ đích; launch profiles bật true. Khi chạy không dùng profile, cần chủ động đặt `Scoring__WorkerEnabled=true`. Không có yêu cầu thay đổi key/model hiện tại.
- Lỗi Data Protection khi chạy Web dưới sandbox được xử lý bằng việc chạy phiên kiểm chứng dưới tài khoản sở hữu key Windows; không xóa key hoặc sửa ứng dụng để né lỗi môi trường.
- Bằng chứng hoàn thành áp dụng cho mẫu tổng hợp vừa thử, không thay thế đánh giá chất lượng tuyển dụng trên tập CV thực tế.
- Không có quyết định hoặc phê duyệt nào đang chờ để hoàn tất phạm vi sửa này. Người dùng tự review và commit/push sau báo cáo.
