# A17 – So sánh hai phiên bản system prompt

## API

| API | Dùng khi |
|---|---|
| `GET /api/prompts/compare?baseVersionId=1&candidateVersionId=2&evaluationSetId=1` | Cách thường dùng: lấy lượt PromptTest **Completed** gần nhất của mỗi phiên bản trên bộ đã chọn |
| `GET /api/prompt-tests/compare?baseRunId=1&candidateRunId=2` | So hai lượt chạy cụ thể |

Cả hai chỉ **đọc** dữ liệu đã lưu: không gọi Gemini, không chấm lại, không sửa nhãn
HR, không ghi cache `AccuracyPercent`. Cùng chốt chặn với các API prompt khác
(chỉ Development và `CvUpload:Endpoint:Enabled = true`).

"Gần nhất" = `CompletedAt` giảm dần, rồi `RunId` giảm dần (giống
`latestAccuracyPercent` của A14). Lượt Queued / Running / Failed không bao giờ
được chọn thay lượt đã xong.

| Tình huống | Mã |
|---|---|
| Thiếu tham số, tham số ≤ 0 hoặc không phải số | 400 `Thiếu tham số` / `Tham số không hợp lệ` |
| Cùng RunId / cùng PromptVersionId | 400 `Cùng một lượt chạy` / `Cùng một phiên bản` |
| RunId là lượt Screening | 400 `Không phải lượt đánh giá prompt` |
| RunId, phiên bản hoặc bộ CV không tồn tại | 404 |
| Phiên bản chưa có lượt Completed trên bộ | 404 `Chưa có kết quả` (+ `activeRunId` nếu đang chạy) |
| RunId chưa Completed (Queued / Running / Failed) | 409 `Lượt chạy chưa hoàn thành` (+ `runId`, `runStatus`) |

Lượt chưa xong trả 409 thay vì số liệu tạm, giữ nguyên quy tắc A16: không công
bố tỷ lệ đúng khi lượt chưa hoàn thành.

## Cách tính

`PromptComparer` (SmartCv.Core, hàm thuần) ghép hai lượt theo **ApplicationId**,
không theo vị trí. Chỉ dùng CV còn nhãn HR trong bộ của lượt (cùng phạm vi A16)
và chỉ so trên CV có ở **cả hai** lượt, để hai tỷ lệ cùng mẫu số.

- Tỷ lệ đúng: `PromptAccuracyCalculator` của A16. CV không chấm được vẫn trong mẫu số.
- `accuracyDelta = candidate − base` (điểm phần trăm); `missedDelta`, `wrongPickDelta` âm là tốt.
- Từng CV: `Fixed` (gốc sai → thử đúng), `Broken` (gốc đúng → thử sai),
  `Unchanged`, `Unscored` (ít nhất một bên không chấm được; không bao giờ tính là Fixed/Broken).
- Thứ tự `items`: Fixed → Broken → Unscored → Unchanged, rồi tên job, rồi ApplicationId.

## So sánh công bằng

`isFairComparison = true` chỉ khi: cả hai Completed, cùng `EvaluationSetId`,
model (không phân biệt hoa thường), temperature, `KeywordWeight`, `AiWeight`,
`PassThreshold`, **cùng đúng danh sách ApplicationId có nhãn** và cùng nhãn HR
cho từng CV. Có chung vài CV là chưa đủ: danh sách lệch nhau thì `warnings` nêu
tên từng CV chỉ có ở một bên, số liệu tính trên phần chung chỉ để tham khảo,
`verdict = NotComparable`.

## Kết luận

`Better` / `Worse` / `Same` theo tỷ lệ đúng khi so sánh công bằng. Ngoài ra trả
`NotComparable` (kèm cảnh báo "Chưa đủ cơ sở kết luận") khi kết quả không phản ánh
prompt:

- CV được Gemini chấm ở **cả hai** lượt (Completed, không KeywordOnly) ít hơn một nửa; hoặc
- kết luận trên các CV đó khác chiều với chênh lệch tổng, tức là chênh lệch đến
  từ CV `KeywordOnly` (Gemini lỗi / hết hạn mức) hoặc CV không chấm được.

Có CV KeywordOnly hay không chấm được mà kết luận vẫn đứng vững thì giữ kết luận,
thêm cảnh báo và câu "Lưu ý" ở cuối `summary`.

`summary` cho dữ liệu demo:

> v2-tieu-chi-ro-rang đúng 80% (8/10 CV), cao hơn v1-co-ban 10 điểm phần trăm (70%, 7/10 CV). Sửa đúng thêm 1 CV, không làm sai thêm CV nào. Bỏ sót giữ nguyên (1 → 1), chọn nhầm giảm 1 (2 → 1).

## Hợp đồng cho B15

Dùng nguyên `PromptCompareDto` / `PromptCompareItemDto` trong
`PromptContracts.cs` (không đổi tên/kiểu trường). `base` / `candidate` là
`PromptTestSummaryDto`, số liệu tính trên các CV chung. Mở chi tiết lý do chấm của
một CV bằng `baseEvaluationId` / `candidateEvaluationId` (B9).

## Xác minh

- `PromptComparerTests`: hàm thuần (demo 70% → 80%, Better/Worse/Same/NotComparable,
  Fixed/Broken/Unchanged/Unscored, khác cài đặt, khác danh sách CV, KeywordOnly, null).
- `PromptCompareEndpointTests`: hai API trên InMemory, mã lỗi, chọn lượt gần nhất,
  thứ tự CV đổi, nhãn bị bỏ, chỉ đọc, hợp đồng JSON.
- `A17SqlServerTests`: SQL Server thật, bật bằng `SMARTCV_A17_TEST_SQL_CONNECTION`
  trỏ tới tên database bắt đầu bằng `SmartCv_A17_Test_`; mỗi test tạo rồi xóa
  database GUID riêng. Không cấu hình thì ghi SKIPPED / NOT RUN.
