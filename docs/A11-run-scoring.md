# A11 – Background Worker chấm điểm CV

## 1. Mục tiêu

Tự động chấm các lượt `EvaluationRuns` dưới nền, cập nhật tiến độ thật, xử lý lỗi độc lập từng hồ sơ và chấm tiếp sau khi API khởi động lại. Trạng thái nằm trong database, không có hàng đợi trong bộ nhớ và không có bảng mới.

## 2. Kiến trúc

| Thành phần | Vai trò |
|---|---|
| `RunScoringWorker` (Api, `BackgroundService`) | Vòng lặp polling, tạo scope, nghỉ, chống sập khi lỗi |
| `RunScoringProcessor` (Infrastructure, Scoped) | Chọn lượt, chấm từng hồ sơ, lưu kết quả và tiến độ, khóa worker |

Mỗi vòng dùng một scope để giữ khóa và lấy danh sách lượt; mỗi lượt có scope riêng, nên `DbContext` không bị dùng chung hay dùng đồng thời.

## 3. Trạng thái

```
POST tạo lượt (chưa có, xem mục 17)
      │
   Queued ──> Running ──> (chấm từng Evaluation Pending) ──> Completed + CompletedAt (UTC)
                 ▲                      │
                 └── còn Pending ───────┘   (chờ vòng sau; khởi động lại cũng tiếp tục từ đây)
```

Lượt `Completed` và `Failed` không bao giờ được xử lý lại. Lượt chỉ `Completed` khi không còn hồ sơ `Pending` và số hồ sơ đạt `TotalItems`.

## 4. Quan hệ với A7, A9, A10

- **A7:** chỉ đọc `Cvs.ExtractionStatus` và `Cvs.ExtractedText`. Worker A7 vẫn chạy song song.
- **A9:** gọi `KeywordScoringService.Score`.
- **A10:** gọi `IAiEvaluator` (kèm retry, rate limit, FakeAI) và `ScoreCombiner`. Worker không tự thử lại AI.

Model, Temperature, SystemPrompt, `KeywordWeight`, `AiWeight`, `PassThreshold` lấy **từ lượt chấm**, không đọc prompt đang kích hoạt.

## 5. Polling

`Scoring:PollIntervalSeconds` (mặc định 3, tối thiểu 1). Vừa chấm được thì kiểm tra việc tiếp ngay; không có việc thì nghỉ đúng chu kỳ. Database lỗi thì nghỉ gấp đôi mỗi lần liên tiếp, tối đa 60 giây.

## 6. Bật / tắt

`Scoring:WorkerEnabled`, **mặc định `false`** (khác tài liệu ZIP, nơi worker luôn chạy). Lý do: khi bật, worker tự chấm mọi lượt Queued/Running của database đang kết nối và có thể tốn quota Gemini ngay lúc API khởi động.

Muốn chấm thật phải đặt `true` có chủ đích. Worker chỉ đăng ký một lần trong `Program.cs`.

Từ B6 (giao diện "Phân tích hồ sơ" tạo lượt qua `POST /api/runs`), hai profile `http`/`https` trong
`src/SmartCv.Api/Properties/launchSettings.json` đặt `Scoring__WorkerEnabled=true`. Nhờ vậy, khi chạy API để phát triển
(`dotnet run`, Visual Studio), lượt vừa tạo được chấm ngay thay vì đứng mãi ở `Queued`, 0%. `appsettings.json` vẫn để `false`,
nên test (WebApplicationFactory không đọc launchSettings) và các môi trường khác không tự tốn quota. Khi worker tắt, API ghi log
**Warning** "Tiến trình chấm điểm đang TẮT ... lượt phân tích mới sẽ đứng ở trạng thái Queued".
Chạy với `--no-launch-profile` thì tự đặt `$env:Scoring__WorkerEnabled = 'true'` như ví dụ dưới.
Lưu ý: lần đầu bật trên một database, worker chấm mọi lượt `Queued`/`Running` còn tồn trong database đó.

## 7. Xử lý từng CV

| Tình huống | Kết quả |
|---|---|
| `ExtractionStatus = Pending` (kể cả còn chữ cũ) | Giữ `Pending`, không tính vào tiến độ |
| `ExtractionStatus = Failed` | `Failed`, không gọi AI, không có điểm |
| `Completed` nhưng chữ rỗng | `Failed`, lý do riêng, không gọi AI |
| Job không có tiêu chí áp dụng được | `Failed`, không gọi AI |
| AI thành công | `Completed`, `Combined` |
| AI lỗi | `Completed`, `KeywordOnly`, `AiScore = null`, `ErrorMessage` ghi nguyên nhân |
| Lỗi bất ngờ khi chấm | `Failed` chỉ hồ sơ đó, chấm tiếp hồ sơ khác |

## 8. Tiến độ

Đếm lại từ các dòng `Evaluations` sau mỗi hồ sơ, không cộng dồn:

- `ProcessedItems` = số Evaluation không còn `Pending`.
- `FailedItems` = số Evaluation `Failed`.
- Phần trăm (`ProgressPercent`) = `ProcessedItems / TotalItems × 100`, tối đa 100; `TotalItems = 0` cho 100.

## 9. Lưu kết quả

Sau **mỗi** hồ sơ, `Evaluation`, `EvaluationSkills` và tiến độ của lượt được lưu trong một lần `SaveChanges` (một giao dịch). Kỹ năng cũ nếu có được cập nhật tại chỗ, không tạo trùng khóa.

## 10–11. CV Pending/Failed và Gemini lỗi

Xem mục 7. Gemini lỗi đi theo đúng cơ chế fallback của A10.

## 12. Tiếp tục sau khởi động lại

Khi API dừng, hồ sơ đã lưu giữ nguyên. Lần chạy sau tìm lượt `Running` và chỉ chấm hồ sơ `Pending`.

- API tắt lúc đang gọi AI: `CancellationToken` dừng cuộc gọi, hồ sơ giữ `Pending`, **không** bị đánh dấu `Failed`.
- Kết quả AI đã có thì được lưu xong với `CancellationToken.None` để không mất quota đã dùng.
- **Lỗi kết nối SQL không làm hồ sơ thành `Failed`** (khác ZIP): lỗi đi lên worker, hồ sơ giữ `Pending`, worker nghỉ rồi thử lại.
- Chỉ lỗi dữ liệu của chính hồ sơ (SQL 547, 2601, 2627, 2628, 8115, 8152) mới ghi `Failed`, để không gọi AI lặp vô hạn.

## 13. Kiểm soát đồng thời

- Trong một instance: một worker, xử lý tuần tự.
- Nhiều instance: mỗi vòng giữ khóa ứng dụng SQL Server (`sp_getapplock`, gắn với phiên, không cần sửa schema). Instance không lấy được khóa thì bỏ qua vòng đó. Tiến trình chết thì phiên đóng và khóa tự nhả.

Đây là chế độ **một worker hoạt động tại một thời điểm**, không phải chấm phân tán song song. `GeminiRateLimiter` vẫn theo từng instance, nhưng do chỉ một instance chấm nên không cộng dồn.

## 14. Kiểm thử bằng FakeAI

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__SmartCvDb = 'Server=localhost\BB;Database=SmartCvDb_A11Test;Trusted_Connection=True;TrustServerCertificate=True'
$env:CvUpload__StorageRoot = 'Uploads/CV-test'
$env:Scoring__WorkerEnabled = 'true'
$env:Gemini__UseFake = 'true'
dotnet run --project src/SmartCv.Api --no-launch-profile
```

Luôn kiểm tra connection string trước khi bật worker. Kết quả FakeAI có `"simulated":true` trong `AiRawResponse` và `Reasoning` bắt đầu bằng "(AI giả lập)".

## 15. Database test và kết quả (2026-11-09)

Dùng `SmartCvDb_A11Test` trên `localhost\BB`, tạo riêng bằng migration, chỉ chứa dữ liệu CV tự tạo. Không chạy worker trên `SmartCvDb`; số dòng `EvaluationRuns` / `Evaluations` / `EvaluationSkills` của DB này giữ nguyên 4 / 36 / 192.

Test tự động: **458/458**, trong đó có 45 test mới của A11 (InMemory, stub AI, FakeAI).

| Thử nghiệm trên SQL Server thật (FakeAI) | Kết quả |
|---|---|
| Lượt 6 hồ sơ | Queued → Running → 6/6, 2 lỗi, Completed |
| CV lỗi OCR, CV rỗng | `Failed` với lý do riêng, hồ sơ khác vẫn chấm |
| AI lỗi (`#loi-ai`) | `KeywordOnly`, không có `AiScore` |
| CV đang Pending | Lượt giữ `Running`, lượt khác vẫn xong; chấm tiếp khi CV có chữ |
| Kill cứng API sau 5/13 | Chạy lại chấm đúng 8 hồ sơ còn lại, 13/13, không trùng, không tạo lượt mới |
| Hai instance cùng lúc | Một instance chấm 10/10, instance kia 0 |
| Vi phạm CHECK tạm trong DB test (SQL 547) | Chỉ hồ sơ đó `Failed`, hồ sơ khác hoàn tất |
| Nhàn rỗi 15 giây (poll 1 giây) | 625 ms CPU |

## 16. Rủi ro còn lại

- Chưa chạy worker với Gemini thật.
- `MissingMustHaveCap` và trọng số bên trong Keyword lấy từ cấu hình hiện tại vì `EvaluationRuns` không có cột lưu.
- CV `Pending` không bao giờ đọc xong sẽ giữ lượt ở `Running` vĩnh viễn; chưa có timeout.
- Lượt thiếu hồ sơ so với `TotalItems` giữ `Running`.
- Các lượt chấm tuần tự, cũ trước: lượt lớn với Gemini thật làm lượt sau phải chờ.
- Log của EF Core khi lưu lỗi có thể chứa tên bảng/cột.

## 17. Giới hạn của A11

- `main` **chưa có** `POST /api/runs` và `GET /api/runs/{id}`. A11 không thêm vì đó là phạm vi B6/A13; hiện lượt phải được tạo trực tiếp trong database.
- Không có Ranking, chi tiết kết quả, UI.
- Không phải xử lý phân tán song song.

## 18. A12 cần tiếp tục

- Kiểm chứng prompt snapshot và tính bất biến phiên bản, gồm thử đổi prompt giữa lượt.
- Audit đầy đủ cấu hình chấm (bao gồm `MissingMustHaveCap`).
- Quy trình chấm lại có kiểm soát và các script kiểm tra dữ liệu.
- Quyết định cách lưu kết quả FakeAI khi nghiệm thu, vì `ModelName` của lượt không phản ánh AI giả.
