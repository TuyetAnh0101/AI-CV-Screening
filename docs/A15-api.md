# A15 – Bộ CV đánh giá, nhãn HR và PromptTest

Phạm vi: quản lý bộ/nhãn, CSV và khởi chạy/đọc lượt chấm. Không tính accuracy,
không phân loại Missed/WrongPick, không so sánh prompt. Không có migration mới.

## API

Các endpoint dùng cùng giới hạn với API CV/A14: chỉ Development và
`CvUpload:Endpoint:Enabled=true`. Chưa mở cho production khi chưa có xác thực.

| Method | URL | Chức năng |
|---|---|---|
| GET | `/api/evaluation-sets` | Danh sách mới nhất trước, số nhãn và số Đạt |
| POST | `/api/evaluation-sets` | Tạo bộ, tên duy nhất, trả 201 |
| GET | `/api/evaluation-sets/{id}` | Chi tiết bộ và metadata nhãn HR |
| GET | `/api/evaluation-sets/{id}/applications?jobId=` | Hồ sơ và nhãn hiện tại |
| PUT | `/api/evaluation-sets/{id}/labels` | Upsert tối đa 2.000 nhãn, kiểm tra trước ghi |
| POST | `/api/evaluation-sets/{id}/labels/import` | Multipart `file` CSV và `labeledBy` |
| GET | `/api/evaluation-sets/{id}/labels/template?jobId=` | CSV BOM cho Excel, loại CV Failed |
| DELETE | `/api/evaluation-sets/{id}/labels/{applicationId}` | Bỏ một nhãn, trả 204 |
| POST | `/api/prompt-tests` | Tạo lượt Queued từ prompt và bộ có đủ hai loại nhãn |
| GET | `/api/prompt-tests?evaluationSetId=` | Danh sách lượt PromptTest mới nhất trước |
| GET | `/api/prompt-tests/{runId}` | Tiến độ và kết quả đã lưu của từng hồ sơ |

## Gán nhãn và CSV

JSON phải có `labeledBy`, `labels` và `isPass` của từng nhãn. Thiếu `isPass`
bị từ chối thay vì tự hiểu là Không đạt. `ApplicationId` phải tồn tại, không
trùng; ghi chú tối đa 500 ký tự. Kết quả có `added`, `updated`, `unchanged`.
Gửi lại cùng kết luận/ghi chú không thay người hoặc thời điểm gán nhãn trước.

CSV tối đa 1 MiB, đuôi `.csv`, UTF-8 có/không BOM, delimiter `,` hoặc `;`.
Cột định danh: `ApplicationId`/`MaHoSo` hoặc `TenFile`/`FileName`.
Cột kết quả: `KetQua`/`Kết quả`/`IsPass`; ghi chú: `GhiChu`/`Note`.
Chấp nhận Đạt/Không đạt, dat/khongdat, pass/fail, 1/0 và các alias trong parser.
Kết quả trống là chưa quyết định nên bỏ qua. Tên file mơ hồ phải dùng ID.
Lỗi cú pháp, hồ sơ, nhãn hoặc trùng sau khi tìm theo tên file đều không lưu
một phần và được báo theo số dòng vật lý. Quoted fields và ghi chú nhiều dòng
được hỗ trợ. Ghi chú quá dài bị từ chối, không cắt âm thầm.

Template có các cột `ApplicationId;TenFile;UngVien;ViTri;KetQua;GhiChu`.
Nội dung có thể thành công thức Excel được thêm dấu nháy đơn khi xuất; khi
import không tự xóa dấu nháy này khỏi nội dung người dùng.

## Ranh giới với A16/A17

`PromptTestRunDto`/`PromptTestProgressDto` chỉ có danh tính, snapshot cấu hình,
trạng thái, số CV, thời điểm và kết quả chấm đã lưu. `AccuracyPercent` chỉ đọc
giá trị lịch sử; `AccuracyStatus` là `Stored` hoặc `NotCalculated`.
Không trả các counter A16 bằng số 0 giả, không trả `isMatch`/`mismatchType`.
Các DTO A16/A17 đã có trong `PromptContracts.cs` được giữ để các nhiệm vụ sau
tiếp tục phát triển; không đăng ký endpoint compare.

Khi nhãn thêm/sửa/xóa, accuracy đã lưu của bộ được đặt `null` trong cùng giao
dịch. `invalidatedRunIds` liệt kê lượt bị vô hiệu hóa; `recalculatedRunIds`
luôn rỗng. A16 chịu trách nhiệm tính/lưu lại. Danh sách Evaluation của lượt cũ
giữ nguyên: bỏ nhãn không làm mất kết quả, nhưng `hrIsPass` hiện tại có thể null.
Thêm hồ sơ sau khi chạy cần tạo lượt mới nếu muốn đánh giá toàn bộ bộ hiện tại.

SQL Server dùng giao dịch Serializable và khóa hàng EvaluationSets để đồng bộ
ghi nhãn và tạo PromptTest. Hai yêu cầu cùng prompt/bộ chỉ tạo một lượt đang
Queued/Running; 409 có `runId` của lượt đã có. PromptTest sao chép cài đặt như
Screening và dùng RunScoringWorker/RunScoringProcessor hiện có, không đổi prompt active.

## Kiểm thử

Chạy `dotnet restore SmartCv.slnx`, `dotnet build SmartCv.slnx --nologo`,
`dotnet test SmartCv.slnx --nologo`.

Test SQL A15 chỉ chạy khi `SMARTCV_A15_TEST_SQL_CONNECTION` chỉ định database
có prefix `SmartCv_A15_Test_` và tài khoản có quyền tạo database thử. Fixture
tự tạo database GUID riêng, đặt riêng database đó thành MULTI_USER và dọn sau
test. Không dùng/xóa database được ghi trong biến môi trường hoặc dữ liệu demo.
Thiếu cấu hình được ghi SKIPPED/NOT RUN. Test A14 có biến cấu hình riêng
`SMARTCV_TEST_SQL_CONNECTION` với prefix `SmartCv_A14_Test_`.
Luồng chấm trong test dùng FakeAiEvaluator, không gọi Gemini thật.
