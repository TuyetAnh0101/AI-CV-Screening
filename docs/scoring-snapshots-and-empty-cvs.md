# Truy vết cấu hình chấm và CV không có nội dung

## Trọng số được dùng

Mặc định Keyword/AI là **0,30/0,70** trong `ScoringOptions` và `appsettings.json`.
`appsettings.Development.json` không ghi đè hai giá trị này. Cấu hình môi trường có thể
chọn tỷ lệ khác nếu không âm và tổng bằng 1. Bốn trọng số **bên trong Keyword** vẫn là
0,60/0,20/0,10/0,10; chúng không phải tỷ lệ Keyword/AI.

POST `/api/runs` và POST `/api/prompt-tests` chép KeywordWeight, AiWeight, model,
temperature và ngưỡng Đạt vào EvaluationRuns. Worker gộp điểm bằng trọng số và ngưỡng
đã lưu trên Run, kể cả sau khi mặc định thay đổi. Các endpoint tiến độ/chi tiết trả
các giá trị đã lưu. Web hiển thị trọng số từ DTO; không thay bằng mặc định hiện hành.
Kết quả Completed không được chấm lại khi worker tiếp tục lượt chạy.

POST `/api/applications/{id}/ai-preview` dùng cấu hình tại thời điểm yêu cầu, không tạo
EvaluationRun. DTO preview trả thêm KeywordWeight, AiWeight, PassThreshold và
MissingMustHaveCap để client biết chính xác cấu hình của phép tính vừa xem.

## Giới hạn của snapshot và so sánh A17

A17 kiểm tra cùng bộ CV có nhãn HR, cùng model, temperature, trọng số Keyword/AI và
ngưỡng Đạt, cùng danh sách hồ sơ và nhãn. `isFairComparison=true` xác nhận các điều kiện
được lưu và kiểm tra đó, không chứng minh có thể gọi lại Gemini để tái tạo điểm.

Theo giới hạn đã ghi trong A12, Run chưa lưu cap, bốn trọng số Keyword, phiên bản
thuật toán, bản sao job/CV hoặc ngày dùng để tính khoảng kinh nghiệm. Thay các yếu tố
này có thể làm thay đổi điểm của **hồ sơ Pending** khi chạy tiếp; không làm thay đổi
điểm của hồ sơ Completed. Không suy ra cap lịch sử từ cap hiện hành và không dùng
cấu hình hiện hành để tính lại FinalScore lịch sử trong API đọc.

Model demo lịch sử có thể khác model mặc định hiện tại. Đổi tên model trong snapshot
không chứng minh model đó từng sinh kết quả. Seed lưu 70%/80% minh họa lịch sử phải được
đọc theo cấu hình/thuật toán của nó; so sánh với lượt mới chỉ công bằng khi kiểm chứng
được các điều kiện liên quan. Xem thêm `A12-scoring-persistence.md` và tài liệu demo.

## CV trống

- File 0 byte bị validation upload từ chối trước khi lưu.
- File có định dạng hợp lệ nhưng không nhận ra chữ qua PDF/OCR: extraction Failed,
  không có ExtractedText. Scoring ghi Evaluation Failed, không gọi AI, không cho 0 điểm.
- Nếu extraction Completed nhưng text null/trắng (ví dụ dữ liệu nhập ngoài), worker
  cũng ghi Failed với lý do riêng trước khi gọi Keyword hoặc AI.
- Trong lúc extraction Pending, hồ sơ chờ đọc chữ; sau khi extraction kết thúc mà không
  có chữ, worker tính hồ sơ đó vào ProcessedItems/FailedItems. Run Completed khi đã xử
  lý đủ hồ sơ, kể cả khi hồ sơ Failed. Điểm và PredictedPass của hồ sơ lỗi là null.

Đây là hợp đồng hiện hành của A7/A9/A11, khác ZIP mẫu cho CV trống 0 điểm. Không thay
đổi kết quả thất bại thành một lần chấm 0 điểm. Regression test
`ScoringConsistencyEndpointTests` nối HTTP upload → gắn job → tạo Run → extraction
thất bại/text trắng → worker → API tiến độ, kiểm tra Run kết thúc và không gọi AI.
