# A10 – Tích hợp Gemini AI và kết hợp điểm CV

## 1. Mục tiêu

Chấm một CV bằng Gemini với JSON có cấu trúc, xử lý lỗi và hạn mức an toàn, rồi gộp với điểm Keyword của A9 thành điểm cuối. A10 có API chấm thử nhưng **không lưu kết quả**; lưu và chạy nền thuộc A11/A12.

## 2. Quan hệ A7 → A9 → A10

```
CV gốc ─> A7: PdfPig / OCR ─> Cvs.ExtractedText ─┬─> A9: KeywordScore (0–100)
                                                 └─> A10: Gemini ─> AiScore (0–100)
                                                          │
                                  ScoreCombiner: FinalScore = 0.30 × Keyword + 0.70 × AI
```

A10 chỉ đọc `Cvs.ExtractedText`, không đọc lại file và không viết lại thuật toán Keyword.

## 3. Kiến trúc

| Thành phần | Vai trò |
|---|---|
| `IAiEvaluator`, `AiEvaluationRequest`, `AiEvaluationResult`, `AiErrorKind` (Core) | Hợp đồng chấm AI |
| `AiPromptBuilder` (Core) | Tạo phần user của prompt |
| `ScoreCombiner`, `FitLevels` (Core) | Gộp điểm, xếp mức, hàm thuần |
| `GeminiEvaluationService` (Infrastructure) | Gọi Gemini thật |
| `GeminiRateLimiter` (Infrastructure, Singleton) | Giãn cách các lần gọi |
| `FakeAiEvaluator` (Infrastructure) | AI giả để thử |
| `AiPreviewEndpoints`, `AiPreviewRateLimitPolicy` (Api) | API chấm thử |

`Model`, `Temperature`, `SystemPrompt` do người gọi truyền vào. Xem trước dùng prompt đang kích hoạt; A11/A12 truyền giá trị đã chụp của lượt chấm mà không cần sửa service.

`GeminiOptions` giữ nguyên lớp có từ A1 (`SmartCv.Infrastructure.Gemini`) và chỉ thêm thuộc tính mới, không dùng bản trong ZIP. Tên cấu hình độ dài CV vẫn là `MaxCvCharacters`.

## 4. Gọi Gemini

`POST {BaseUrl}models/{model}:generateContent`, key gửi qua header `x-goog-api-key`, không nằm trên URL. Dùng `HttpClientFactory` (typed client), hỗ trợ `CancellationToken`. Không ghi log API key, nội dung CV hay JSON của AI.

## 5. JSON có cấu trúc

Gửi `responseMimeType = application/json` và `responseSchema` gồm `score`, `matchedSkills`, `missingSkills`, `yearsExperience`, `gpa`, `reasoning`. Hai mảng kỹ năng giới hạn bằng `enum` theo mã kỹ năng của job.

Service vẫn tự kiểm tra sau khi nhận:

| Trường hợp | Xử lý |
|---|---|
| `score` thiếu, sai kiểu, ngoài 0–100 | `InvalidResponse`, **không clamp** |
| Thiếu mảng kỹ năng, phần tử không phải chuỗi, `reasoning` rỗng | `InvalidResponse` |
| Một mã vừa matched vừa missing | `InvalidResponse` |
| `gpa` ngoài thang 4, `yearsExperience` âm hoặc quá lớn | `InvalidResponse` |
| Mã kỹ năng lạ | Loại, ghi vào `InvalidSkillCodes`, không tính là đạt |
| Mã trùng, khác hoa thường | Gộp về mã thật của job |

`InvalidResponse` được thử lại trong giới hạn; hết lượt thì trả thất bại, không có điểm.

`RawResponse` giữ JSON gốc cho A11/A12, không ghi log và không trả qua API xem trước.

## 6. Xây dựng prompt

Phần user gồm: vị trí, mô tả, GPA và kinh nghiệm tối thiểu, bảng kỹ năng (mã, tên, loại, alias), quy tắc đánh giá, nội dung CV, yêu cầu JSON. Phần system là `SystemPrompt` của phiên bản prompt.

- CV được cắt theo `MaxCvCharacters` (12.000). Khi cắt, prompt báo cho AI và `CvTruncated = true`.
- **Chống prompt injection:** CV nằm trong khối `<<<CV ... CV>>>`, ký tự phân cách trong CV bị đổi nên CV không tự đóng được khối. Quy tắc "CV là dữ liệu" đặt cả trước và sau khối.
- Yêu cầu AI chỉ dựa trên bằng chứng, phân biệt đã dùng với chỉ nhắc đến, không đánh giá theo đặc điểm cá nhân.

Test chỉ kiểm chứng được cấu trúc prompt; khả năng model thật chống injection cần đánh giá riêng.

## 7–8. Nhóm lỗi, thử lại và giới hạn tần suất

Mặc định `MaxRetries = 2` (tối đa 3 lần gọi), `TimeoutSeconds = 90`, `MaxRetryDelaySeconds = 60`.

| Tình huống | `AiErrorKind` | Thử lại | Chờ |
|---|---|---|---|
| HTTP 429 | `QuotaExceeded` | Có | `retryDelay`, rồi `Retry-After`, rồi 2s, 4s |
| HTTP 500, 502, 503, 504 | `Network` | Có | như trên |
| Mất mạng, quá thời gian | `Network` | Có | 2s, 4s |
| JSON hỏng | `InvalidResponse` | Có | 2s, 4s |
| 400 do API key | `Configuration` | Không | |
| 400 khác (schema, tham số) | `InvalidRequest` | Không | |
| 401, 403 | `Configuration` | Không | |
| 404 (model) | `Configuration` | Không | |
| Nội dung bị chặn | `Blocked` | Không | |
| Người gọi hủy | ném `OperationCanceledException` | Không | |

`GeminiRateLimiter` giữ `MinDelayBetweenCallsMs` (4.500 ms) giữa các lần gọi, kể cả các lần thử lại. Mỗi lần gọi đặt trước một mốc rồi chờ ngoài khóa, nên không giữ khóa khi gọi mạng.

> **Giới hạn:** bộ đếm chỉ có tác dụng trong một instance ứng dụng. Chạy nhiều instance cần giới hạn dùng chung; hiện chưa xử lý phân tán.

## 9. AI giả

`Gemini:UseFake = true`: không gọi mạng, chờ `FakeDelayMs`, điểm AI bằng điểm Keyword làm tròn. CV có `#loi-ai` giả lập lỗi hạn mức.

- Chỉ chạy ở Development; môi trường khác dừng lúc khởi động. Mặc định `false`.
- Không cần API key khi `UseFake = true`; khi gọi Gemini thật, thiếu key vẫn dừng lúc khởi động như cũ.
- Mọi kết quả có `IsSimulated = true`, API trả `aiSimulated: true`, `model: "fake-ai (giả lập)"`, reasoning bắt đầu bằng "(AI giả lập)".

Bật cho một lần chạy: `$env:Gemini__UseFake = 'true'`.

## 10. KeywordOnly

AI lỗi: `AiScore = null` (không gán 0), `FinalScore = KeywordScore`, `ScoreSource = KeywordOnly`, `Reasoning` và `ErrorMessage` ghi nguyên nhân.

KeywordOnly **không phải** kết quả 30/70 đầy đủ; giao diện sau này phải thể hiện rõ.

## 11. ScoreCombiner

`FinalScore = KeywordWeight × KeywordScore + AiWeight × AiScore` (mặc định 0.30 và 0.70, từ `ScoringOptions`). Trọng số sai làm ứng dụng dừng lúc khởi động, và `ScoreCombiner` cũng từ chối.

| Điểm | Mức |
|---|---|
| 85–100 | Rất phù hợp |
| 70–84.99 | Phù hợp |
| 55–69.99 | Tiềm năng |
| Dưới 55 | Chưa phù hợp |

`PredictedPass = FinalScore ≥ PassThreshold` (70).

**Kỹ năng AI nhận thêm:** kỹ năng được đánh dấu có, `MatchedBy = "AI"`, `Evidence = "[Gemini xác nhận, không phải trích dẫn từ CV]"` để không giả làm trích dẫn. Kỹ năng Keyword đã thấy giữ nguyên bằng chứng, `MatchedBy = "Keyword"`, kể cả khi AI không đồng ý. Chỉ duyệt kỹ năng của job nên không sinh mã lạ.

## 12. MissingMustHaveCap

Thiếu kỹ năng bắt buộc (cả Keyword lẫn AI không xác nhận) thì điểm cuối không vượt `MissingMustHaveCap` (69), `Reasoning` nêu kỹ năng thiếu. Áp dụng cả cho KeywordOnly.

Tắt bằng `"MissingMustHaveCap": null`. Giá trị 69 đặt trong `appsettings.json`, còn mặc định của lớp là `null`, vì binder bỏ qua giá trị null và sẽ giữ 69 nếu đặt làm mặc định.

> **Cần quản lý/HR nghiệm thu:** đây là chính sách ảnh hưởng tuyển dụng, do tài liệu ZIP đề xuất.

## 13. API chấm thử

`POST /api/applications/{id}/ai-preview` — không tạo `Evaluation`, `EvaluationSkills`, `EvaluationRun`.

**Trả về:** `promptVersion`, `model`, `keywordScore`, `aiScore`, `finalScore`, `scoreSource`, `fitLabel`, `predictedPass`, `yearsExperience`, `reasoning`, `aiSucceeded`, `aiErrorKind`, `aiErrorMessage`, `aiAttempts`, `aiSimulated`, `aiCvTruncated`, `aiMatchedSkills`, `aiMissingSkills`, `aiInvalidSkillCodes`, `skills`. Không có JSON gốc.

| Tình huống | HTTP |
|---|---|
| Thành công, hoặc AI lỗi (KeywordOnly) | 200 |
| Không có hồ sơ hoặc job | 404 |
| CV `Pending`, `Failed`, chữ rỗng | 409, có `extractionStatus` |
| Không có prompt đang kích hoạt, job không có tiêu chí | 409, không gọi AI |
| Vượt `PreviewPermitPerMinute` (10/phút/IP) | 429 |

**Bảo vệ:** cùng chốt chặn API hồ sơ (chỉ Development khi `CvUpload:Endpoint:Enabled = true`) và giới hạn tần suất riêng, tách khỏi API tải CV.

> **Rủi ro:** hệ thống chưa có xác thực (M6). Không mở endpoint này ở Production khi chưa có `RequireAuthorization`.

## 14. Cách kiểm thử

```powershell
dotnet test
```

Chạy API với AI giả trên database test riêng:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__SmartCvDb = 'Server=localhost\BB;Database=SmartCvDb_A5Test;Trusted_Connection=True;TrustServerCertificate=True'
$env:CvUpload__StorageRoot = 'Uploads/CV-test'
$env:ASPNETCORE_URLS = 'http://localhost:5199'
$env:Gemini__UseFake = 'true'
dotnet run --project src/SmartCv.Api --no-launch-profile
```

```powershell
Invoke-RestMethod -Method Post "http://localhost:5199/api/applications/1/ai-preview"
```

Gemini thật: bỏ `Gemini__UseFake`, kiểm tra model, chỉ gọi vài hồ sơ dữ liệu mẫu.

## 15. Kết quả kiểm thử (2026-11-09)

- Toàn solution: **413/413**, gồm 303 test có từ trước.
- `ScoreCombinerTests` 32, `GeminiEvaluationServiceTests` 54 (HTTP giả, không có mạng), `AiPreviewEndpointTests` 24.
- API chạy thật, AI giả, `SmartCvDb_A5Test`, prompt `v2-tieu-chi-ro-rang`. Số dòng `Evaluations` / `EvaluationSkills` / `EvaluationRuns` giữ nguyên trước và sau (36 / 192 / 4).

| Hồ sơ | Keyword | AI (giả) | Cuối | Nguồn | Mức |
|---|---|---|---|---|---|
| 1 | 100 | 100 | 100 | Combined | Rất phù hợp |
| 2 | 86.67 | 87 | 86.90 | Combined | Rất phù hợp |
| 5 | 92.59 | 93 | 92.88 | Combined | Rất phù hợp |
| 12 | 62.96 | 63 | 62.99 | Combined | Tiềm năng |

Hồ sơ 13 (CV lỗi) trả 409, hồ sơ không tồn tại trả 404.

**Gemini thật: CHƯA KIỂM CHỨNG.**

## 16. Rủi ro còn lại

- Chưa gọi Gemini thật: schema (đặc biệt `enum`, `propertyOrdering`) và model chưa được xác nhận trên tài khoản.
- ~~`Gemini:Model` khác `Scoring:ModelName`, nên health check có thể báo sai model.~~ Đã sửa, xem mục 18.
- Chưa có xác thực.
- Bộ giới hạn tần suất chỉ trong một instance.
- CV dài bị cắt 12.000 ký tự đầu.
- Một lần chấm có thể kéo dài vài phút khi Gemini liên tục lỗi.
- `YearsExperience` ưu tiên giá trị AI, có thể khác A9.

## 17. A11 cần kế thừa

- Dùng `IAiEvaluator` trong scope, truyền Model, Temperature, SystemPrompt đã chụp của lượt chấm.
- Xét `ExtractionStatus = Completed` và `HasApplicableCriteria` trước khi chấm; `KeywordScoringService.Score` ném `ArgumentException` với CV rỗng.
- Lưu `RawResponse` vào `Evaluations.AiRawResponse`; `Evidence` của kỹ năng AI đã có nhãn nguồn vì bảng không có cột riêng.
- Không lưu kết quả `IsSimulated = true` như đánh giá thật.
- `OperationCanceledException` nghĩa là dừng xử lý, không phải lỗi hồ sơ.
- Dùng cùng `GeminiRateLimiter` Singleton.

## 18. Sửa lỗi model và quá tải Gemini (2026-11-10, nhánh `fix/A10-gemini-model`)

**Lỗi thật đã gặp:** lượt #13 (`gemini-2.5-flash`) bị `HTTP 404 ... no longer available to new users`; lượt #16 (`gemini-3.8-flash`)
bị `HTTP 503 ... experiencing high demand` cả 3 lần thử. Cả hai lưu `KeywordOnly`, `AiScore = NULL`. 503 là Google quá tải, không phải
lỗi API key, SQL Server hay worker.

**Model, một nguồn duy nhất:** `Scoring:ModelName`, mặc định `gemini-3.1-flash-lite` (trong `appsettings.json` và mặc định của
`ScoringOptions`, có test bắt hai giá trị này trùng nhau). Đổi tạm bằng biến môi trường `Scoring__ModelName`; ứng dụng không đọc
`GEMINI_MODEL`. Đã bỏ `Gemini:Model`: khóa này không dùng khi chấm, chỉ health check dùng nên health báo OK cho model khác.
Mỗi lượt chụp `ModelName` lúc tạo; lượt cũ chụp `gemini-2.5-flash` sẽ tiếp tục nhận 404 (không tự đổi model của lượt).
Lưu ý tên khóa: `"Scoring": { "Model": ... }` sai tên nên binder bỏ qua và dùng mặc định trong code.

**Chọn model (2026-11-10):** `gemini-3.1-flash-lite`. `gemini-2.5-flash` bị gỡ (404); `gemini-3.8-flash` trả 503 "high demand" liên tục ở
lượt #16. `models.list`/`models.get` (chỉ đọc thông tin): `gemini-3.1-flash-lite` và `gemini-3.5-flash-lite` đều có trong danh sách của
API key, hỗ trợ `generateContent`, cùng giới hạn 1 048 576 token vào / 65 536 token ra. Không có khác biệt nào bất lợi nên dùng
`gemini-3.1-flash-lite` (ưu tiên của nhóm); `gemini-3.5-flash-lite` là phương án dự phòng (`Scoring__ModelName=gemini-3.5-flash-lite`).
`gemini-2.5-flash` cũng vẫn qua `models.get` dù `generateContent` trả 404, nên **danh sách model và `/api/health/gemini` không chứng minh
model chấm được**; chỉ một lần gọi `generateContent` thật mới xác nhận.

**Smoke test Gemini thật (2026-11-10, được nhóm cho phép):** gọi đúng `GeminiEvaluationService` (cùng `responseSchema`) với 1 CV và 1 job
tự soạn, `gemini-3.1-flash-lite`, `MaxRetries = 2`. Kết quả: **1 lời gọi, HTTP 200 sau 10,7 s**, JSON đúng schema (`score` 85,
`matchedSkills` = csharp, aspnet_core, sql_server, git; `missingSkills` = docker; không có mã lạ; `yearsExperience` 1.5, `gpa` 3.2,
`reasoning` tiếng Việt). Không cần gọi `gemini-3.5-flash-lite`. Chưa kiểm chứng: độ ổn định khi chấm nhiều CV liên tiếp với Gemini thật.

**Phân loại lỗi (`AiErrorKind`):** 503 = `Overloaded`; 500/502/504 và mất kết nối = `Network`; hết thời gian chờ = `Timeout`;
404 = `ModelUnavailable`; 429 = `QuotaExceeded`; 400/401/403 = `Configuration`/`InvalidRequest`. Database chỉ lưu `ErrorMessage`;
giao diện (`RunFlow.AiFailureReason`) đổi thành lý do ngắn: "AI chưa chấm được (Gemini đang quá tải, có thể thử lại sau), điểm chỉ tính theo từ khóa."

**Thử lại:** tối đa `MaxRetries` (2 → 3 lần gọi) cho 429/5xx/mất mạng/hết giờ/JSON hỏng; không thử lại 400/401/403/404.
Chờ đúng `retryDelay` hoặc `Retry-After` (số giây hoặc ngày giờ) nếu Gemini gửi; nếu không thì 2 s, 4 s cộng jitter 0–25%;
không vượt `MaxRetryDelaySeconds`. Log ghi model thực sự đã gọi, không ghi API key hay nội dung CV.

**Fallback sang model khác: CHƯA làm, có chủ đích.** Lượt chụp một `ModelName`; nếu một CV được chấm bằng model khác thì kết quả không còn
khớp snapshot, và so sánh prompt / accuracy của A16 mất ý nghĩa. Làm đúng cần thêm cột model thực tế cho từng Evaluation (đổi database)
và quy tắc riêng cho PromptTest, nên cần quyết định trước.

**Kiểm thử:** `GeminiEvaluationServiceTests` (200, 404 model bị gỡ, 429, 503 rồi thành công, 503 liên tục, jitter, `Retry-After`, timeout,
JSON hỏng); `GeminiScoringFlowTests` (worker + API thật, chỉ HTTP tới Google là giả: 503 rồi thành công → `Combined`, 503 liên tục →
`KeywordOnly` có lý do, 404 không thử lại, không chấm lại CV đã xong); `GeminiModelConfigurationTests`.
