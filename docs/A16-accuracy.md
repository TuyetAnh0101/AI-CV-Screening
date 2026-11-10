# A16 – Độ chính xác của PromptTest

## Một công thức, một phạm vi đối chiếu

`PromptAccuracyCalculator` là hàm thuần dùng chung bởi worker, API và cập nhật
nhãn. Đầu vào là nhãn HR hiện tại và `Evaluations.PredictedPass`, ghép chính xác
theo `EvaluationSetId` của run và `ApplicationId`, không theo tên file.

- Correct: Evaluation Completed, PredictedPass khác null và trùng HR.
- Missed: HR Đạt, AI Không đạt.
- WrongPick: HR Không đạt, AI Đạt.
- Unscored: Pending/Failed hoặc thiếu PredictedPass; vẫn nằm trong mẫu số.
- Completed KeywordOnly vẫn đối chiếu bình thường và được đếm riêng.

Accuracy = Correct / Total × 100, làm tròn hai chữ số AwayFromZero. Total là
số hồ sơ vừa có Evaluation trong run vừa còn nhãn trong đúng bộ. Total=0 trả
null; Total>0 nhưng mọi hồ sơ Unscored trả 0%. Không lấy AiScore/FinalScore
để suy ra PredictedPass lần nữa.

HR thêm hồ sơ sau khi tạo run không làm phát sinh Evaluation cho run cũ và
không đưa hồ sơ mới vào mẫu số run cũ. API cảnh báo cần chạy lại. HR bỏ nhãn
thì hồ sơ đó ra khỏi mẫu số nhưng kết quả chấm cũ vẫn được trả với nhãn null.

## Lưu, phục hồi và đồng thời

RunScoringProcessor giữ nguyên scoring/snapshot và lưu từng CV. Với PromptTest,
phần lưu kết quả/tiến độ/final accuracy dùng transaction và khóa EvaluationSet
của A15. Không giữ khóa trong khi gọi AI. PUT/import/delete nhãn cũng dùng cùng
khóa và tính lại cache của các run Completed trước khi commit.

Run Completed thiếu cache được worker tính bù mà không chấm lại CV. Truy vấn
khôi phục chỉ lấy run có ít nhất một nhãn còn đối chiếu được, nên trường hợp
xóa hết nhãn không lặp vô hạn. Chạy lại không tạo Evaluation hoặc sửa điểm cũ.

GET danh sách/chi tiết đọc snapshot dưới cùng khóa bộ; nếu cache Completed
thiếu hoặc không còn khớp, đồng bộ cache bằng cùng calculator. Đây chỉ là sửa
cache accuracy, không gọi AI, không sửa CV/nhãn/prompt. Không công bố accuracy
cuối cùng hoặc counter tổng hợp khi run Queued/Running.

`recalculatedRunIds` là các run Completed có cache thay đổi khi HR lưu nhãn.
`invalidatedRunIds` giữ để tương thích A15 và rỗng. Không có migration/package
mới, không có endpoint compare của A17.

## Xác minh

`database/check_a16.sql` chỉ SELECT, đối chiếu SQL độc lập với cache/API, gồm
Unscored và KeywordOnly. Không chạy script xóa dữ liệu demo.

Chạy restore/build/test với SmartCv.slnx. Test SQL A16 được bật bằng biến
`SMARTCV_A16_TEST_SQL_CONNECTION` có database prefix `SmartCv_A16_Test_`;
fixture chỉ tạo/dọn database GUID riêng. SQL A14/A15 tiếp tục dùng biến riêng
của chúng. Không có cấu hình SQL thì test tương ứng ghi SKIPPED/NOT RUN.
Các kiểm thử chấm dùng AI giả lập; không gọi Gemini thật.
