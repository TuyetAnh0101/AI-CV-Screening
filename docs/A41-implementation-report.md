# A41 – Hỗ trợ trang System prompt (backend/API) – Báo cáo triển khai

> Trạng thái: **chờ duyệt**. Code nằm trên nhánh `feature/a41-prompt-tools-api`, **chưa** `git add`, **chưa** commit, **chưa** push, **chưa** tạo PR.
> Không có migration mới, không đổi schema, không ghi vào `SmartCvDb`, không gọi Gemini thật, không chạy OCR thật.

## 1. Nhánh, base và tiền đề

| Mục | Giá trị |
|---|---|
| Repo | `C:\TTCN\project\AI-CV-Screening`, remote `https://github.com/TuyetAnh0101/AI-CV-Screening.git` |
| Nhánh | `feature/a41-prompt-tools-api` (tạo mới một lần, chưa có ở local/remote trước đó) |
| Base SHA | `df7acf52afe500fbca64e63ebb8bbc03be3b6297` = `origin/main` mới nhất lúc bắt đầu (`feat(A39) … (#60)`), ghi ở `artifacts/a41-base-sha.txt` |
| Trạng thái ban đầu | `git status --porcelain=v1 -uall` rỗng; không có merge/rebase/cherry-pick dở dang |
| Cách tạo nhánh | Đang ở nhánh `feature/a39-cv-anonymization-retention` (sạch, đã squash-merge thành #60, nội dung giống hệt `df7acf5`) → `git switch main` → `git pull --ff-only origin main` (main local đi sau 12 commit, fast-forward) → `git switch -c feature/a41-prompt-tools-api` |
| Tiền đề A37 | `a7883a6 feat(A37): mask candidate personal data before Gemini (#54)` là tổ tiên của base; `PersonalDataMasker`, `Gemini:MaskPersonalData` có trong code |
| A39/A42/B39/B41 | Không làm, không sửa. Không sửa `src/SmartCv.Web/` |

## 2. Phân tích thực trạng và rủi ro của mẫu ZIP

ZIP `A36_A42_B38_B42_SmartCv.zip` (mục A41, snippet A41, `check_a41_khong_tao_dong.sql`) được dựng trên snapshot cũ. So với `main`:

| Mẫu ZIP | Thực tế `main` | Cách xử lý |
|---|---|---|
| `AiPreviewEndpoints` map thẳng, **không** chốt Development/`CvUpload:Endpoint:Enabled`, **không** rate limiter A10 | Có chốt Development + Enabled, `.RequireRateLimiting("ai-preview")`, kiểm tra ẩn danh hóa A39 trước/sau khi chấm | Giữ nguyên toàn bộ, chỉ chèn phần A41 |
| Bỏ `AiPreviewDto`, DTO mới **mất** `aiErrorMessage`, `aiSimulated`, `aiCvTruncated`, `aiInvalidSkillCodes`, `skills`, trọng số; lại **trả `aiRawResponse`** | `AiPreviewDto` A10/A37 cố ý không trả JSON gốc của AI | Giữ `AiPreviewDto` và mọi trường cũ, chỉ **thêm** trường A41 (init-property) |
| `PreviewQuota.Refund()` bỏ **vé mới nhất** | — | Viết lại: mỗi lượt có vé riêng, trả **đúng vé** của request |
| `Math.Max(1, …)` tự sửa cấu hình sai | Program.cs dùng `Validate … ValidateOnStart` | Cấu hình sai dừng lúc khởi động, không tự sửa |
| `AiPromptAssembler(request, GeminiOptions)` với `GeminiOptions` ở Core | `GeminiOptions` ở Infrastructure, thuộc tính `MaxCvCharacters` | Assembler ở Core nhận `(request, maxCvCharacters, maskPersonalData)` |
| `PromptTestQueries`/`PromptComparer`/`PromptContracts` bản A16/A17 cũ | A16 dùng `PromptTestRunDto`, khóa bộ đánh giá, A17 có quy tắc "đủ cơ sở" (KeywordOnly/Unscored) | Chỉ thêm số đo; không đổi công thức, `verdict`, `summary` |
| Endpoint quota/assembled không chốt | — | Cả hai nằm trong cùng chốt Development + Enabled |

Không chép đè file nào từ ZIP; mọi thay đổi là sửa nhỏ trên `main`.

### Danh sách file (15 sửa, 12 mới; đúng như `artifacts/a41-commit-files.txt`)

| Loại | File | Lý do |
|---|---|---|
| M | `src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs` | Body không bắt buộc (nháp / phiên bản / đang dùng), quota + 429, `currentEvaluation`, `GET …/ai-preview/quota`; giữ chốt chặn, rate limiter A10, kiểm tra ẩn danh hóa |
| M | `src/SmartCv.Api/Endpoints/PromptEndpoints.cs` | `POST /api/prompts/assembled-preview` (fail-closed khi tắt che dữ liệu) |
| M | `src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs` | Thêm route assembled-preview vào danh sách trả dữ liệu CV (khóa đọc A39, `Cache-Control: no-store`) |
| M | `src/SmartCv.Api/Program.cs` | Options `AiPreview` có kiểm tra fail-fast; đăng ký `PreviewQuota` singleton |
| M | `src/SmartCv.Api/appsettings.json` | Mục `AiPreview` (30 lượt / 60 phút / 2 chạy cùng lúc) |
| M | `src/SmartCv.Core/Ai/AiPromptBuilder.cs` | `Limit` từ `private` → `internal` để assembler đếm đúng số ký tự gửi đi (không nhân bản logic cắt) |
| M | `src/SmartCv.Core/Contracts/AiPreviewDto.cs` | Thêm trường A41, giữ mọi trường A10 |
| M | `src/SmartCv.Core/Contracts/PromptContracts.cs` | `ConfusionMatrixDto`; `Matrix/AccuracyLow/AccuracyHigh` cho `PromptTestSummaryDto`; `Conclusive/ConclusionNote` cho `PromptCompareDto` |
| M | `src/SmartCv.Core/Contracts/PromptTestRunContracts.cs` | `Matrix/AccuracyLow/AccuracyHigh` cho `PromptTestRunDto` (list + detail) |
| M | `src/SmartCv.Core/Evaluation/PromptAccuracyCalculator.cs` | `TruePass`, `TrueFail`, `Confidence95`, hàm `Wilson` (A16 giữ nguyên) |
| M | `src/SmartCv.Core/Evaluation/PromptComparer.cs` | `Conclusive`, `ConclusionNote`, `IsConclusive`, `InconclusiveText` (A17 giữ nguyên) |
| M | `src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs` | Dùng `AiPromptAssembler` cho system/user/mã kỹ năng; log chỉ số lượng như A37 |
| M | `src/SmartCv.Infrastructure/Prompts/PromptTestQueries.cs` | Điền ma trận + khoảng tin cậy từ kết quả A16 đã tính (không thêm truy vấn) |
| M | `src/SmartCv.Infrastructure/Prompts/PromptCompareQueries.cs` | Điền `Conclusive/ConclusionNote`, ma trận + khoảng tin cậy hai bên |
| M | `tests/SmartCv.Tests/CvUploadEndpointTests.cs` | Xem mục 4.4 (bất biến cũ được cập nhật có lý do) |
| A | `src/SmartCv.Api/Endpoints/PromptToolSupport.cs` | Phần dùng chung: đọc body JSON có lỗi tiếng Việt, kiểm tra nguồn prompt, nạp hồ sơ, kết quả đã lưu gần nhất |
| A | `src/SmartCv.Core/Ai/AiPromptAssembler.cs` | Một nguồn sự thật cho nội dung gửi Gemini |
| A | `src/SmartCv.Core/Ai/PreviewQuota.cs` | Bộ đếm cửa sổ trượt, vé riêng, giới hạn chạy cùng lúc, `TimeProvider` |
| A | `src/SmartCv.Core/Contracts/PromptToolContracts.cs` | `AiPreviewRequest`, `AssembledPreviewRequest`, `CurrentEvaluationDto`, `PreviewQuotaDto`, `AssembledPromptDto`, `PromptSourceKinds` |
| A | `src/SmartCv.Core/Options/AiPreviewOptions.cs` | Mục cấu hình `AiPreview` |
| A | `database/check_a41_khong_tao_dong.sql` | SQL chỉ đọc, đếm số dòng trước/sau |
| A | `docs/A41-implementation-report.md` | Báo cáo này |
| A | `tests/SmartCv.Tests/A41PromptToolsEndpointTests.cs` | 50 test HTTP (InMemory, AI giả / Gemini với HTTP giả) |
| A | `tests/SmartCv.Tests/A41SqlTests.cs` | 1 test SQL Server opt-in (catalog mới, giữ lại) |
| A | `tests/SmartCv.Tests/Ai/AiPromptAssemblerTests.cs` | 13 test che dữ liệu + so payload Gemini |
| A | `tests/SmartCv.Tests/Ai/PreviewQuotaTests.cs` | 15 test quota (đồng hồ giả, đồng thời, trả đúng vé) |
| A | `tests/SmartCv.Tests/Evaluation/WilsonIntervalTests.cs` | 21 test Wilson, ma trận, mức độ kết luận |

Test mới giữ `namespace SmartCv.Tests` (không dùng `SmartCv.Tests.Evaluation` để tránh CS0118).

## 3. Bốn nhóm API

Mọi API A41 chỉ có ở **Development** khi `CvUpload:Endpoint:Enabled = true` (giống A8–A17, đến M6 mới có xác thực). Production/Staging: 404.

### 3.1. Chấm thử prompt nháp – `POST /api/applications/{id}/ai-preview`

Body **không bắt buộc** (UTF-8, tối đa 256 KB):

| Body | Prompt dùng | `promptSource` |
|---|---|---|
| không gửi / `{}` / `null` | bản đang kích hoạt (giống A10) | `Active` |
| `{ "systemPrompt": "..." }` | bản nháp, bỏ khoảng trắng đầu/cuối, 1–20.000 ký tự | `Draft` |
| `{ "promptVersionId": 3 }` | phiên bản đã lưu (kể cả chưa kích hoạt) | `Version` |

Thứ tự kiểm tra (đều **trước** khi giữ lượt): body → hồ sơ → CV (ẩn danh hóa / chưa đọc xong / không chữ) → prompt → job → tiêu chí keyword → đọc kết quả đã lưu → **giữ lượt** → gọi AI.
Không ghi bảng nào (chỉ `AsNoTracking`), không đổi prompt đang dùng, không ghi đè kết quả đã lưu.

Phản hồi = toàn bộ JSON A10 cũ + trường A41 (mẫu thật qua Kestrel với AI giả, CV tổng hợp; rút gọn `skills`):

```json
{
  "promptVersion": "Nháp (chưa lưu)", "model": "fake-ai (giả lập)",
  "keywordScore": 100, "aiScore": 100, "finalScore": 100.0, "scoreSource": "Combined", "fitLabel": "Rất phù hợp",
  "predictedPass": true, "yearsExperience": 3.9, "reasoning": "(AI giả lập) CV đáp ứng đủ các kỹ năng của vị trí.",
  "aiSucceeded": true, "aiErrorKind": "None", "aiErrorMessage": null, "aiAttempts": 1, "aiSimulated": true, "aiCvTruncated": false,
  "aiMatchedSkills": ["aspnetcore", "csharp", "sqlserver", "docker", "efcore", "git"], "aiMissingSkills": [], "aiInvalidSkillCodes": [],
  "skills": ["…"], "keywordWeight": 0.3, "aiWeight": 0.7, "passThreshold": 70, "missingMustHaveCap": 69,
  "promptSource": "Draft", "promptVersionId": null, "isDraft": true, "persisted": false,
  "remainingTries": 1, "tryLimit": 3, "quotaResetsAt": "2026-11-13T07:05:37.3547331Z", "tryCounted": true,
  "currentEvaluation": {
    "evaluationId": 1, "runId": 1, "runType": "Screening", "promptVersionId": 2, "promptVersionName": "a41-dang-dung",
    "modelName": "gemini-saved", "keywordScore": 80.00, "aiScore": 75.00, "finalScore": 76.50, "scoreSource": "Combined",
    "fitLabel": "Phù hợp", "predictedPass": true, "yearsExperience": null, "reasoning": "Kết quả tổng hợp đã lưu.",
    "matchedSkills": ["csharp"], "missingSkills": [], "createdAt": "2026-11-13T05:52:52.9274521Z", "runCompletedAt": "2026-11-13T05:52:52.9274521Z"
  }
}
```

- `persisted` luôn `false`; `currentEvaluation` là kết quả **đã lưu** (Completed) gần nhất của **đúng** `ApplicationId` (lượt Screening hoặc PromptTest, có `runType`), `null` nếu chưa có. Lượt Pending và hồ sơ khác không bao giờ được chọn (có test).
- `tryCounted = false` khi lượt được trả lại (xem 3.2).
- Không có `aiRawResponse`.

### 3.2. Giới hạn lượt chấm thử – `GET /api/applications/ai-preview/quota`

```json
{ "remaining": 0, "limit": 3, "windowMinutes": 60, "resetsAt": "2026-11-13T07:05:37.3547331Z", "inFlight": 0, "maxConcurrent": 2, "aiSimulated": true }
```

Cấu hình `AiPreview` (appsettings): `LimitPerWindow` 30 (1–1000), `WindowMinutes` 60 (1–1440), `MaxConcurrent` 2 (1–20). Ngoài khoảng → ứng dụng **dừng lúc khởi động** với thông báo tiếng Việt (không tự sửa).

Quy tắc hạch toán:

| Tình huống | Gọi AI? | Mất lượt? |
|---|---|---|
| Body/JSON sai (400/415), hồ sơ/prompt/job không có (404), CV chưa sẵn sàng / ẩn danh hóa / không có tiêu chí / không có prompt đang dùng (409), body > 256 KB (413) | Không | **Không** |
| Bị rate limiter A10 theo IP chặn (429 "Quá nhiều yêu cầu chấm thử") | Không | **Không** |
| Hết lượt (429 `PreviewQuotaExhausted`) hoặc đang có đủ lượt chạy cùng lúc (429 `PreviewBusy`) | Không | **Không** |
| AI trả `Attempts = 0` (ví dụ thiếu API key: chưa gửi gì) | Không gửi | **Trả lại đúng vé của request đó** (`tryCounted: false`) |
| Đã gửi Gemini rồi lỗi 503 / timeout / 429 Gemini / 403 | Có | Có (Gemini có thể đã nhận và tính hạn mức) |
| Ngoại lệ không lường trước, người dùng hủy request giữa chừng | Không biết chắc | Có (thận trọng); chỗ chạy vẫn được trả |

- Bộ đếm: cửa sổ trượt dùng chung cả ứng dụng, vé có số riêng, kiểm tra + giữ vé trong **một khóa** (nguyên tử), `TimeProvider` để test điều khiển thời gian. 100 request đồng thời với giới hạn 30 → đúng 30 được cấp (có test).
- 429 hết lượt có header `Retry-After` (giây) và trường máy đọc được `code`, `remaining`, `limit`, `windowMinutes`, `resetsAt` (UTC), `retryAfterSeconds`, `aiSimulated`. `detail` làm tròn **lên** phút theo giờ Việt Nam: "Đã dùng hết 3 lượt chấm thử trong 60 phút. Thử lại sau 14:06 (giờ Việt Nam, UTC+7)." (khác ngày thì thêm "ngày dd/MM").
- 429 đang bận: `Retry-After: 5`, `code: PreviewBusy`, "Đang chạy 1/1 lượt chấm thử cùng lúc…".
- Khoảng giãn 4,5 giây giữa hai lần gọi Gemini (A10 `GeminiRateLimiter`) và rate limiter theo IP (`Gemini:PreviewPermitPerMinute`) **giữ nguyên**.
- **AI giả** (`Gemini:UseFake = true`, chỉ Development): lượt vẫn được đếm để thử giao diện/429 an toàn, nhưng `aiSimulated: true` ở cả quota và kết quả; 429 ghi thêm "Đang dùng AI giả lập nên không tốn hạn mức Gemini." Không có chỗ nào nói đã tiêu hạn mức Gemini thật.
- **Giới hạn đã biết:** bộ đếm trong bộ nhớ của MỘT tiến trình; khởi động lại API thì đếm lại từ đầu; chạy nhiều instance thì mỗi instance đếm riêng. Không thêm Redis/database (ngoài phạm vi).

### 3.3. Nội dung gửi Gemini – `POST /api/prompts/assembled-preview`

Body: `{ "applicationId": 5 }` kèm `systemPrompt` (nháp) hoặc `promptVersionId` (cùng quy tắc 3.1). Không gọi Gemini, không tốn lượt, không ghi database. Mẫu thật (CV tổng hợp từ ảnh `cv-anh-ro.png`):

```json
{
  "applicationId": 1, "jobId": 1, "jobTitle": "Backend Developer (.NET)",
  "promptSource": "Draft", "promptVersionId": null, "promptVersion": "Nháp (chưa lưu)", "isDraft": true,
  "model": "gemini-3.1-flash-lite", "temperature": 0.2, "aiSimulated": true,
  "systemPrompt": "Bản nháp A41: không trừ điểm vì thông tin bị che.",
  "userPrompt": "## VỊ TRÍ TUYỂN DỤNG … <<<CV\n[CANDIDATE]\nLập trình viên Backend (.NET)\nEmail: [EMAIL] · ĐT: [PHONE]\n…CV>>> …",
  "personalDataMasked": true, "maskedNames": 1, "maskedEmails": 1, "maskedPhones": 1, "maskedLinks": 0,
  "maskedBirthDates": 0, "maskedGenders": 0, "maskedTotal": 3, "nameMaskSource": "Provided",
  "cvCharacters": 498, "cvCharactersSent": 468, "maxCvCharacters": 12000, "truncated": false,
  "allowedSkillCodes": ["aspnetcore", "csharp", "sqlserver", "docker", "efcore", "git"]
}
```

- `AiPromptAssembler.Assemble(request, MaxCvCharacters, MaskPersonalData)` là hàm duy nhất ghép phần system/user và danh sách mã kỹ năng; `GeminiEvaluationService` và API này cùng gọi nó với cùng cấu hình → không lệch do che hai lần, khoảng trắng, độ dài cắt hay họ tên. Test so **từng ký tự** `systemPrompt`, `userPrompt` và `enum` của `responseSchema` giữa assembled-preview và payload HTTP thật gửi Gemini (giả) cho 3 nguồn prompt.
- **Fail-closed:** `Gemini:MaskPersonalData = false` → 409 `code: PersonalDataMaskingDisabled`, không bao giờ trả CV chưa che.
- Đi qua `CvPrivacyReadMiddleware` (khóa đọc A39 trên SQL Server, `Cache-Control: no-store`).
- Ghi chú `aiSimulated: true`: máy chủ đang dùng AI giả nên thực tế không gửi gì; nội dung là bản sẽ gửi khi dùng Gemini thật.

### 3.4. Số đo prompt

`GET /api/prompt-tests`, `GET /api/prompt-tests/{runId}` thêm (null khi lượt chưa Completed):

```json
{ "accuracyPercent": 70, "accuracyLow": 39.68, "accuracyHigh": 89.22,
  "matrix": { "truePass": 5, "missed": 1, "wrongPick": 2, "trueFail": 2, "unscored": 0, "total": 10 }, "totalLabeledCount": 10 }
```

- Ma trận dùng đúng `PredictionOutcome`/mẫu số A16 (nhãn HR hiện tại): `truePass + missed + wrongPick + trueFail + unscored = totalLabeledCount`; KeywordOnly có dự đoán thì vào ô, Pending/Failed/không có dự đoán vào `unscored` và vẫn ở mẫu số. Không thêm truy vấn (N+1), không tải CV.
- Wilson 95% (z = 1,96), k = CV đúng, n = CV có nhãn, `double` rồi làm tròn 2 chữ số, kẹp trong 0–100; n = 0 → `null` (không NaN). Không dùng Wald. Đã kiểm: 35/50 → 56,25–80,90; 7/10 → 39,68–89,22; 8/10 → 49,02–94,33; 10/10 → 72,25–100; 0/10 → 0–27,75; 1/1 → 20,65–100; toàn bộ k/n với n ≤ 80 nằm trong 0–100 và chứa tỷ lệ quan sát.

`GET /api/prompt-tests/compare?baseRunId=&candidateRunId=` và `GET /api/prompts/compare?baseVersionId=&candidateVersionId=&evaluationSetId=` thêm `conclusive`, `conclusionNote`, và `matrix/accuracyLow/accuracyHigh` cho `base`, `candidate` (tính trên CV chung, giống `accuracyPercent` của compare). Dữ liệu demo v1 7/10 – v2 8/10:

```json
{ "verdict": "Better", "conclusive": false,
  "summary": "v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). …",
  "conclusionNote": "Chưa đủ dữ liệu để kết luận bản nào tốt hơn, cần thêm CV có nhãn. Chênh lệch 10 điểm phần trăm trên 10 CV chỉ là kết quả quan sát, chưa đủ cơ sở kết luận (khoảng tin cậy 95%: 39,68–89,22% và 49,02–94,33% chồng nhau)." }
```

Quy tắc: `verdict` NotComparable (chưa xong, khác điều kiện, phụ thuộc CV không chấm được) → `conclusive=false` "Chưa thể kết luận…"; hai khoảng chồng nhau (chạm cũng tính) → `false` + câu bắt buộc, với Better/Worse thêm "chỉ là kết quả quan sát"; không chồng → `true` kèm nhắc "tiêu chí thận trọng, không phải kiểm định thống kê chính thức". `verdict`, `summary`, quy tắc A17 **không đổi** (B16 không phải sửa).

### Bảng lỗi

| HTTP | `title` | Khi nào | Mất lượt |
|---|---|---|---|
| 400 | Chỉ chọn một nguồn prompt | gửi cả `systemPrompt` và `promptVersionId` | Không |
| 400 | Prompt nháp đang trống | `systemPrompt` rỗng/chỉ khoảng trắng | Không |
| 400 | Prompt quá dài | > 20.000 ký tự sau khi bỏ khoảng trắng đầu/cuối | Không |
| 400 | Mã phiên bản không hợp lệ / Mã hồ sơ không hợp lệ | id ≤ 0 | Không |
| 400 | JSON không hợp lệ | sai cú pháp, sai kiểu, không phải UTF-8 (không lộ stack trace hay nội dung prompt) | Không |
| 400 | Thiếu nội dung yêu cầu | assembled-preview không có body | Không |
| 404 | Không tìm thấy hồ sơ / Không tìm thấy prompt / Không tìm thấy vị trí tuyển dụng | id không tồn tại | Không |
| 409 | CV đã được ẩn danh hóa / CV chưa có nội dung / CV trích xuất lỗi (`extractionStatus`) | A9/A10/A39 | Không |
| 409 | Chưa có prompt đang dùng / Vị trí chưa có tiêu chí chấm | A10 | Không |
| 409 | Chưa bật che dữ liệu cá nhân (`PersonalDataMaskingDisabled`) | assembled-preview khi tắt che | Không |
| 409 | CV đã được ẩn danh hóa trong lúc chấm thử | A39 (sau khi đã gọi AI) | Có |
| 413 | (Kestrel) | body > 256 KB | Không |
| 415 | Nội dung yêu cầu phải là JSON | có body nhưng không phải JSON | Không |
| 429 | Quá nhiều yêu cầu chấm thử | rate limiter A10 theo IP | Không |
| 429 | Hết lượt chấm thử (`PreviewQuotaExhausted`) / Đang có lượt chấm thử khác (`PreviewBusy`) | A41 | Không |

### Tương thích ngược

- A10: POST không body (không `Content-Type`) chạy như cũ; mọi trường JSON cũ còn nguyên (test liệt kê 24 trường A10 + 9 trường A41); test cũ `AiPreviewEndpointTests`, `AiPreviewMaskingTests`, `ScoringConsistencyEndpointTests`, `A39SqlTests` vẫn đọc `AiPreviewDto` bình thường. 404 hồ sơ nay có ProblemDetails tiếng Việt (mã trạng thái không đổi).
- A14–A17: chỉ thêm trường; `accuracyPercent`, các trường đếm, `verdict`, `summary`, quy tắc fair/reliability không đổi. `SmartCv.Web` (B12–B16, `AccuracyComparisonView`) không phải sửa.
- A37 che dữ liệu, A35 stages, A36 đề xuất nhãn, A38 consent/upload, A39 ẩn danh hóa: không đổi hành vi (full suite xanh).

## 4. Bằng chứng kiểm thử (số liệu thật)

| Lần chạy | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Baseline `df7acf5` trước khi sửa (`artifacts/a41-baseline-test.log`) | 1662 | 0 | 72 | 1734 |
| Cuối, `dotnet test SmartCv.slnx` mặc định (`artifacts/a41-full-test.log`) | **1761** | **0** | 73 | 1834 |
| Cuối, bật SQL opt-in giữ catalog + demo ZIP (`artifacts/a41-full-optin-test.log`) | 1802 | 2 (*) | 38 | 1842 |

- `dotnet restore SmartCv.slnx`: exit 0. `dotnet build SmartCv.slnx`: **0 Warning(s), 0 Error(s)**.
- 100 test mới: `A41PromptToolsEndpointTests` 50, `WilsonIntervalTests` 21, `PreviewQuotaTests` 15, `AiPromptAssemblerTests` 13, `A41SqlTests` 1 (skip ở lần mặc định vì opt-in, pass ở lần opt-in).
- Test quota/đồng thời chạy lặp 3 lần liên tiếp: 58/58 pass mỗi lần.
- (*) 2 lỗi **có sẵn từ trước, không do A41**: `RuntimeScriptDeploymentTests.Sqlcmd_default_decoding…` (dòng 98) và `RuntimeSchemaSqlTests.Run25_schema_mismatch…` (dòng 108), cả hai `DatabaseSchemaDiagnostics.IsReady == false` sau script runtime A35. Đã chạy lại đúng 2 test này trên mã nguồn base `df7acf5` (xuất bằng `git archive` vào thư mục tạm, không tạo worktree): **fail y hệt, cùng dòng**. A41 không sửa migration, script runtime hay schema diagnostics. Đề xuất mở việc riêng (khả năng script runtime A35 chưa gồm các cột A38 mà diagnostics hiện yêu cầu).
- Skipped (73 ở lần mặc định): toàn bộ là test opt-in SQL/demo ZIP chưa đặt biến môi trường. Ở lần opt-in vẫn **cố ý không chạy** các bộ tự `DROP`/`EnsureDeleted` database (A11, A14, A15, A16, A17, A35: 38 test) theo yêu cầu không tự chạy thao tác phá hủy database.

Nhóm test chính:

| Nội dung | Test |
|---|---|
| A10 không body / `{}` / `null`, đủ trường cũ + mới, không `aiRawResponse` | `Without_prompt_choice_uses_active_prompt_and_keeps_every_a10_field` (4 biến thể) |
| Nháp (unicode, emoji, CRLF, trim) gửi đúng tới AI, không ghi DB, prompt đang dùng không đổi | `Draft_prompt_is_trimmed_sent_to_ai_and_writes_nothing` |
| Phiên bản đã lưu chưa kích hoạt | `Saved_version_is_used_even_when_it_is_not_active` |
| Cả hai / rỗng / khoảng trắng / id ≤ 0 → 400, không gọi AI, không mất lượt | `Invalid_prompt_choice_is_400_without_ai_call_or_lost_try` (5) |
| 20.000 ký tự tiếng Việt OK, có khoảng trắng OK, 20.001 → 400 | `Draft_length_limit_is_20000_characters_after_trimming` (3) |
| JSON sai, sai kiểu, byte không UTF-8, sai Content-Type → 400/415 tiếng Việt, không stack trace | `Broken_body_gets_a_vietnamese_problem…` (7) |
| 404/409 không gọi AI, không mất lượt | `Not_found_and_conflicts_never_call_ai_or_consume_tries` |
| Kết quả đã lưu đúng hồ sơ, bỏ Pending và hồ sơ khác, không bị ghi đè | `Current_evaluation_is_the_latest_saved_result_of_this_application_only` |
| Hết lượt 429 + Retry-After + trường máy đọc, không gọi AI | `Exhausted_quota_returns_429_with_retry_after_and_never_calls_ai` |
| Đồng hồ giả: hết hạn trả lượt, làm tròn giờ hiển thị | `Tries_come_back_when_the_clock_passes_the_window` |
| Trả đúng vé khi chưa gửi; giữ lượt khi đã gửi; ngoại lệ trả chỗ chạy | `Try_is_refunded_only_when_nothing_was_sent_to_ai`, `Real_gemini_error_after_sending_keeps_the_try` (503, 403) |
| 40 request HTTP đồng thời không vượt giới hạn, request bị từ chối không gọi AI | `Concurrent_requests_never_exceed_the_limit…` |
| Đang bận 429 không mất lượt | `Busy_returns_429_without_consuming_a_try` |
| 100 reservation đồng thời đúng 30; trả song song đúng vé | `PreviewQuotaTests` |
| Cấu hình sai dừng khởi động (6 trường hợp); mặc định 30/60/2; singleton | `Invalid_quota_configuration_stops_startup`, `Appsettings_define_the_default_quota` |
| assembled-preview = payload Gemini thật (3 nguồn), đã che, không gọi Gemini, không tốn lượt, `no-store` | `Assembled_preview_is_exactly_what_gemini_receives_and_is_masked` (3) |
| CV ảnh rõ / ảnh mờ (OCR mất dấu): `[CANDIDATE]`, `[EMAIL]`, `[PHONE]`, không còn email/SĐT/tên | `AiPromptAssemblerTests` |
| Tắt che → 409 fail-closed | `Assembled_preview_fails_closed_when_masking_is_disabled` |
| Production/Staging/Enabled=false → 404; giữ rate limiter A10 và giới hạn body | 3 test bảo vệ + `A10_ip_rate_limit_still_applies_before_the_quota` |
| Ma trận + Wilson ở list/detail; trạng thái lẫn Failed/Pending/KeywordOnly; lượt Running → null | `Prompt_test_list_and_detail…`, `Failed_pending_and_keyword_only…` |
| So sánh: verdict/summary giữ nguyên, conclusive=false với demo; non-overlap → true; NotComparable/Running/Same | `Compare_endpoints_add_conclusion…`, `WilsonIntervalTests` |

### 4.1. Không ghi database (SQL Server thật)

`A41SqlTests` trên **LocalDB**, catalog mới `SmartCv_A41_Test_<GUID>` (migration thật), chạy nguyên văn `database/check_a41_khong_tao_dong.sql` trước/sau khi gọi ai-preview (không body, nháp, phiên bản), assembled-preview và quota:

```
before: PromptVersions=2, EvaluationRuns=1, Evaluations=1, EvaluationSkills=1, HrLabels=1, ApplicationEvents=1, ActivePromptVersionId=2
after:  PromptVersions=2, EvaluationRuns=1, Evaluations=1, EvaluationSkills=1, HrLabels=1, ApplicationEvents=1, ActivePromptVersionId=2
```

### 4.2. Kiểm tra tay qua Kestrel (Development + AI giả)

Chạy `SmartCv.Api.dll` trên `127.0.0.1:5419`, trỏ catalog thử nghiệm `SmartCv_A41_Test_21d478ff…` (không phải `SmartCvDb`), `Gemini:UseFake=true`, `Scoring:WorkerEnabled=false`, `AiPreview:LimitPerWindow=3`; xong thì dừng tiến trình. Kết quả (`artifacts/a41-tests/manual2/`):
quota 3 → không body 200 → nháp 200 → assembled 200 (`Cache-Control: no-store`, quota vẫn 1) → phiên bản #1 200 → 20.001 ký tự 400 → lần thứ tư **429** (`Retry-After: 3597`) → body 300 KB **413** (Kestrel thật) → hai nguồn 400 → byte không UTF-8 400 → `text/plain` 415 → phiên bản 999 404. `sqlcmd -i database/check_a41_khong_tao_dong.sql` trước/sau: `2|1|1|1|1|1|2` cả hai lần. Log API không chứa email, SĐT, nội dung prompt hay API key (grep 0 kết quả).

### 4.3. Lỗi đã phát hiện và sửa trong lúc làm

1. Lần kiểm tay đầu, `curl` trong Git Bash gửi tiếng Việt theo code page Windows → framework trả 400 kèm **stack trace** trang lỗi Development. Đã thêm `PromptToolSupport.ReadJsonBodyAsync`: 400 "JSON không hợp lệ"/415 tiếng Việt, không lộ nội dung; giữ nguyên cách nhận biết "không body" của minimal API nên A10 không đổi.
2. Khai báo `.Accepts(...)` làm routing tự trả 415 rỗng trước handler → bỏ, mô tả body bằng `WithDescription`.
3. Mẫu ZIP trả nhầm vé mới nhất khi hoàn lượt → thay bằng vé riêng (test "Refund_returns_exactly_this_ticket_not_the_newest_one").

### 4.4. Test cũ phải cập nhật (cần bạn xác nhận)

`CvUploadEndpointTests.Only_upload_endpoint_has_large_request_limit` (A4) khẳng định "ngoài upload, không endpoint nào có metadata giới hạn body". A41 **siết** ai-preview và assembled-preview xuống 256 KB (yêu cầu "giới hạn body size") nên test cũ fail. Đã sửa tối thiểu, **giữ và làm chặt** ý đồ: mọi endpoint khác không được nới hay tắt giới hạn (≤ 30.000.000 byte mặc định của Kestrel) và danh sách endpoint có giới hạn riêng phải đúng bằng 2 endpoint A41. Đây không phải hợp thức hóa lỗi; nếu bạn muốn giữ test cũ nguyên văn thì phương án thay thế là bỏ `RequestSizeLimit` (chỉ còn kiểm 20.000 ký tự sau khi đã đọc body tối đa 30 MB).

## 5. An toàn

- Chốt Development + `CvUpload:Endpoint:Enabled` cho cả 3 route (test 404 ở Production/Staging/Enabled=false); ai-preview giữ `.RequireRateLimiting("ai-preview")` (test metadata + test 429 theo IP); 2 POST có `RequestSizeLimit` 256 KB (413 qua Kestrel).
- assembled-preview fail-closed khi tắt che; đi qua khóa đọc A39 và `no-store`.
- Không log CV, prompt, API key: thông báo lỗi JSON không chứa nội dung; `GeminiEvaluationService` vẫn chỉ log số lượng đã che.
- Không có migration mới, không đổi schema, không có file nhị phân/CV thật/credential trong file thay đổi (CV test là dữ liệu tổng hợp `@example.com`, chép từ ảnh giả của gói).
- Quota trong bộ nhớ một tiến trình (mục 3.2); quota dùng chung cả ứng dụng nên một người có thể dùng hết lượt của mọi người cho đến M6 (rate limiter theo IP 10/phút giảm thiểu).

## 6. Bàn giao UX cho B39/B41 (đề xuất, chưa code frontend)

Chung: Inter, Deep Teal `#0F766E` cho hành động chính và trạng thái chọn, nền trung tính, tương phản ≥ 4.5:1; màu semantic chỉ cho lỗi/cảnh báo; responsive từ 320px (hai cột → xếp dọc); focus rõ, điều khiển được bằng bàn phím, vùng chạm ≥ 44px; có trạng thái loading/empty/error/409/429; theo Design System/Figma hiện có, không tự chế layout mới.

**B39 – "Thử với hồ sơ…"**
- Trái: ô soạn prompt + bộ đếm "x/20.000 ký tự"; nhãn trạng thái "Bản nháp · chưa lưu" khi khác bản gốc (gửi `systemPrompt`), giống hệt thì gửi `promptVersionId`. Chọn hồ sơ (`GET /api/evaluation-sets/{id}/applications`, chỉ hồ sơ `hasExtractedText`). CTA "Thử với hồ sơ" (Deep Teal), cạnh đó "Còn 27/30 lượt · trả lượt lúc 14:06" từ quota; CTA vô hiệu khi `remaining = 0` hoặc đang chấm.
- Phải: hai panel cạnh nhau **"Kết quả đã lưu"** (`currentEvaluation`, ghi `runType` + tên phiên bản + thời điểm) và **"Kết quả thử"** (huy hiệu "Chưa lưu", `persisted=false`); chênh điểm "+6,5"; khi `currentEvaluation = null` hiển thị "Hồ sơ chưa có kết quả đã lưu". `aiSimulated` → nhãn "AI giả lập"; `scoreSource = KeywordOnly` → cảnh báo vàng "AI lỗi, điểm chỉ theo từ khóa" kèm `aiErrorMessage`.
- Accordion "Xem nội dung sẽ gửi AI" gọi assembled-preview (không tốn lượt): biểu tượng khiên + "Đã che: 1 họ tên, 1 email, 1 số điện thoại"; `nameMaskSource = None` → cảnh báo "Không xác định được họ tên để che"; `truncated` → "Đã cắt còn 12.000 ký tự"; liệt kê `allowedSkillCodes`.
- 429: `PreviewQuotaExhausted` → banner "Hết lượt chấm thử, thử lại sau 14:06" (dùng `resetsAt`, không parse `detail`); `PreviewBusy` → toast "Đang có lượt khác chạy" và tự cho bấm lại sau `retryAfterSeconds`; 429 không có `code` là rate limiter A10. 409/404 hiện `title` + `detail`.

**B41 – Số đo**
- Tile "70% (40–89%)" (làm tròn hiển thị, giữ 2 số lẻ trong tooltip); n nhỏ → ghi "trên 10 CV".
- Ma trận 2×2: hàng HR Đạt/Không đạt × cột AI Đạt/Không đạt; ô Bỏ sót nhấn mạnh (lỗi nặng hơn), hàng phụ "Chưa chấm được: n (tính là sai)"; tổng = `totalLabeledCount`.
- So sánh: tách **"Kết quả quan sát"** (`verdict`, `summary`) và **"Mức độ kết luận"** (`conclusive`, `conclusionNote`). `verdict = Better` + `conclusive = false` → không dùng màu thành công, hiển thị "Chưa đủ dữ liệu để kết luận…"; vẽ hai thanh khoảng tin cậy chồng nhau trên cùng trục 0–100%. Không dùng chữ "chắc chắn".

**Thử tay (PowerShell, gửi UTF-8)** — dùng hàm `Send-Api` của hướng dẫn tuần 2 (`[Text.Encoding]::UTF8.GetBytes(...)`); không dán tiếng Việt thẳng vào `curl` của Git Bash (sẽ ra 400 "JSON không hợp lệ"):

```powershell
Get-Api "/api/applications/ai-preview/quota" | Format-List
$draft = (Get-Api "/api/prompts/active").systemPrompt + "`n5. Nếu CV không ghi số năm kinh nghiệm thì yearsExperience = null."
$r = Send-Api Post "/api/applications/$app/ai-preview" @{ systemPrompt = $draft }
$r | Format-List promptVersion, promptSource, isDraft, persisted, finalScore, remainingTries, tryLimit, tryCounted
$r.currentEvaluation | Format-List runType, promptVersionName, finalScore, fitLabel, createdAt
$a = Send-Api Post "/api/prompts/assembled-preview" @{ applicationId = $app; systemPrompt = $draft }
$a | Format-List personalDataMasked, maskedNames, maskedEmails, maskedPhones, cvCharacters, cvCharactersSent, truncated
Get-Api "/api/prompt-tests/1" | Format-List accuracyPercent, accuracyLow, accuracyHigh
(Get-Api "/api/prompt-tests/compare?baseRunId=1&candidateRunId=2") | Format-List verdict, conclusive, conclusionNote
```

Chạy `database/check_a41_khong_tao_dong.sql` (`sqlcmd -I -f 65001`) trước/sau trên **database thử nghiệm**; thử 429 bằng `--AiPreview:LimitPerWindow=2` và `Gemini:UseFake=true` để không tốn hạn mức thật.

## 7. Trạng thái Git (khi viết báo cáo)

```
$ git status --short --branch --untracked-files=all
## feature/a41-prompt-tools-api
 M src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs
 M src/SmartCv.Api/Endpoints/PromptEndpoints.cs
 M src/SmartCv.Api/Middleware/CvPrivacyReadMiddleware.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Ai/AiPromptBuilder.cs
 M src/SmartCv.Core/Contracts/AiPreviewDto.cs
 M src/SmartCv.Core/Contracts/PromptContracts.cs
 M src/SmartCv.Core/Contracts/PromptTestRunContracts.cs
 M src/SmartCv.Core/Evaluation/PromptAccuracyCalculator.cs
 M src/SmartCv.Core/Evaluation/PromptComparer.cs
 M src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs
 M src/SmartCv.Infrastructure/Prompts/PromptCompareQueries.cs
 M src/SmartCv.Infrastructure/Prompts/PromptTestQueries.cs
 M tests/SmartCv.Tests/CvUploadEndpointTests.cs
?? database/check_a41_khong_tao_dong.sql
?? docs/A41-implementation-report.md
?? src/SmartCv.Api/Endpoints/PromptToolSupport.cs
?? src/SmartCv.Core/Ai/AiPromptAssembler.cs
?? src/SmartCv.Core/Ai/PreviewQuota.cs
?? src/SmartCv.Core/Contracts/PromptToolContracts.cs
?? src/SmartCv.Core/Options/AiPreviewOptions.cs
?? tests/SmartCv.Tests/A41PromptToolsEndpointTests.cs
?? tests/SmartCv.Tests/A41SqlTests.cs
?? tests/SmartCv.Tests/Ai/AiPromptAssemblerTests.cs
?? tests/SmartCv.Tests/Ai/PreviewQuotaTests.cs
?? tests/SmartCv.Tests/Evaluation/WilsonIntervalTests.cs

$ git diff --check                 → không có lỗi (exit 0)
$ git diff --cached --name-status  → rỗng (chưa stage gì)
```

`git diff --stat` (file đã theo dõi): `15 files changed, 442 insertions(+), 58 deletions(-)`; log build/test đầy đủ ở `artifacts/a41-*.log` và `artifacts/a41-tests/`. File mới (`??`) đã rà riêng: không có khoảng trắng cuối dòng, không có dòng trống thừa cuối file, mỗi file kết thúc đúng một xuống dòng; file mới dùng LF, file cũ giữ CRLF như trước (không đổi hàng loạt).

`artifacts/a41-commit-files.txt`: 27 đường dẫn (15 M + 12 A), không gồm thư mục `artifacts/`. `artifacts/a41-base-sha.txt`: `df7acf52afe500fbca64e63ebb8bbc03be3b6297`. Cả hai bị `.gitignore` bỏ qua.

## 8. Kết luận

**SẴN SÀNG DUYỆT**, với các điểm cần bạn quyết định:

1. Đồng ý cập nhật test A4 `Only_upload_endpoint_has_large_request_limit` (mục 4.4).
2. Hai test Runtime SQL lỗi có sẵn trên `main` (mục 4) – ngoài phạm vi A41, nên mở việc riêng.
3. Database thử nghiệm được **giữ lại**, không xóa: trong phiên này các fixture opt-in đã tạo 113 catalog LocalDB `(localdb)\MSSQLLocalDB` (A36 2, A38 24, A39 72, A40 2, A41 3, Runtime 10), danh sách ở `artifacts/a41-tests/retained-localdb-catalogs-this-session.txt`. Không đụng `SmartCvDb` hay instance `localhost\BB`. Chỉ dọn khi bạn cho phép.
4. Chưa kiểm với Gemini thật và OCR thật (cố ý, để không tốn hạn mức và không dùng dữ liệu cá nhân); bước thủ công ở mục 6.

Chưa `git add`, chưa commit, chưa push, chưa tạo PR, không merge/rebase/reset/stash/clean.
