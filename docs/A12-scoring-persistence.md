# A12 – Lưu kết quả chấm theo phiên bản prompt

## Mục tiêu và nền mã

A9 tính keyword; A10 gọi Gemini và gộp điểm; A11 chấm nền, khóa worker và tiếp tục Pending;
A12 lưu cấu hình lúc tạo Run, kiểm chứng dữ liệu kết quả và nêu rõ giới hạn truy vết.
Không thêm ranking, so sánh prompt, quản lý prompt, giao diện hoặc migration.

Nhánh `feature/A12-prompt-snapshot` tạo từ `origin/main` tại `046514d` (PR #20 đã merge A11).
Hai commit A11 trước squash là `15ed81a` và `dba9cd7` (bổ sung cấu hình và test).
Working tree sạch trước khi tạo nhánh. Không commit, push, merge hay tạo PR.

Đã đọc ZIP `C:\TTCN\A9_A13_SmartCv.zip`, hướng dẫn, processor mẫu và hai script SQL.
Không chép đè processor A11: bản thực tế đã có khóa SQL, xử lý shutdown, lỗi lưu và progress tốt hơn mẫu.
`cham_lai_luot.sql` chỉ đọc để đối chiếu, không sao chép, không thực thi.

## Snapshot khi tạo Run

Repository ở commit nền **chưa có POST /api/runs, GET tiến độ, DTO Run hoặc RunApiClient B6**.
Vì vậy A12 bổ sung POST `/api/runs` để lưu snapshot, không có hợp đồng API cũ để thay đổi.
Chưa thể khẳng định tích hợp với một phiên bản B6 nằm ngoài repository; cần B6 đối chiếu DTO mới.
Không bổ sung GET tiến độ, danh sách, latest, ranking hay chi tiết kết quả của A13.

POST nhận `{ "applicationIds": [123, 124] }`, bỏ trùng, yêu cầu 1–500 ID dương và tất cả phải tồn tại.
Không có đúng một prompt active trả 409. Giá trị cấu hình phải hợp lệ và lưu chính xác trong decimal schema hiện tại.
Không làm tròn ngầm trọng số/temperature/ngưỡng khi snapshot.
Run Screening/Queued cùng các Evaluation Pending được tạo trong **một SaveChangesAsync**.
Trả 201, Location định danh tài nguyên `/api/runs/{id}` và `RunProgressDto` tại thời điểm tạo.
Route GET đọc tài nguyên này chưa tồn tại; A13 sẽ bổ sung API tiến độ.
DTO có snapshot, trạng thái, tổng/đã xử lý/lỗi, Percent và mốc UTC; không trả CV, raw JSON hay PII.
Route POST chỉ mở Development khi `CvUpload:Endpoint:Enabled=true`, cùng ranh giới truy cập Applications.
POST dùng rate limit hiện có. Không thêm cơ chế xác thực giả; cần hoàn thiện quyền truy cập ở nhiệm vụ bảo mật.

| EvaluationRuns | Nguồn khi tạo | Khi worker chạy |
|---|---|---|
| PromptVersionId | Phiên bản active được chọn lúc tạo | Đọc SystemPrompt từ đúng FK này |
| ModelName | Scoring.ModelName | Run.ModelName |
| Temperature | Scoring.Temperature | Run.Temperature |
| KeywordWeight | Scoring.KeywordWeight (mặc định 0.30) | Run.KeywordWeight |
| AiWeight | Scoring.AiWeight (mặc định 0.70) | Run.AiWeight |
| PassThreshold | Scoring.PassThreshold (mặc định 70) | Run.PassThreshold |

Không đổi mặc định 30/70. Cấu hình khác trong test chỉ là dữ liệu kiểm thử việc snapshot.
Worker A11 đã đọc sáu giá trị đúng từ Run nên giữ nguyên logic này.

## Evaluation, kỹ năng và raw response

Giữ cơ chế A10/A11: lưu KeywordScore, AiScore, FinalScore, ScoreSource, FitLabel,
PredictedPass, YearsExperience, Reasoning, AiRawResponse, ErrorMessage và Status.
Combined: điểm cuối được gộp bằng trọng số Run, làm tròn và áp dụng cap theo A10.
KeywordOnly: Completed hợp lệ, AiScore NULL, giữ KeywordScore, điểm cuối theo fallback/cap,
Reasoning và ErrorMessage ghi lý do AI lỗi. Không gán AI lỗi thành 0.
AiRawResponse giữ nguyên `AiEvaluationResult.RawResponse`, có thể NULL; không tạo JSON Gemini giả.
Không log CV, tên file, raw response hoặc API key. Các test dùng văn bản tổng hợp và IAiEvaluator stub.

Kỹ năng lấy từ kết quả ScoreCombiner: duyệt đúng JobProfile; mã AI ngoài job không tạo dòng mới.
Keyword evidence là đoạn trích; AI-only dùng marker `AiConfirmedEvidence`, không giả làm trích dẫn CV.
Mỗi Evaluation lưu một dòng/mã kỹ năng; cập nhật dòng đúng job và xóa dòng thừa của chính Evaluation.
A12 sửa trường hợp dòng cũ cùng SkillCode nhưng khác JobId: xóa dòng sai job rồi tạo/cập nhật dòng đúng.
Không xóa kỹ năng của Run/Evaluation khác. PK gồm EvaluationId, JobId, SkillCode;
FK ghép JobId/SkillCode trỏ JobSkills; Evaluation có unique RunId/ApplicationId.

CV extraction Failed/không có chữ: Failed, không gọi AI. A12 xóa các điểm/raw/kỹ năng còn sót
trên chính Evaluation Pending bị lỗi để không lưu điểm cũ dưới trạng thái Failed.
Không sửa bất kỳ Evaluation Completed nào.

## Tính nhất quán và restart

Mỗi hồ sơ: Evaluation + kỹ năng + progress lưu cùng một SaveChangesAsync.
EF Core SQL Server dùng transaction tự động, không thêm giao dịch lồng nhau.
Các lần SaveChanges đánh dấu Run Running và refresh cuối vòng không phải là lần lưu tách đôi kết quả hồ sơ.
Progress đếm lại từ database cộng trạng thái hiện tại, chỉ Completed khi đủ số hồ sơ và hết Pending.
Worker dùng khóa session SQL Server để tuần tự trên nhiều instance.
A12 thêm kiểm tra Evaluation.RunId trước khi xử lý nhằm tránh ghi sang Run khác.

Lỗi SQL ràng buộc vĩnh viễn: A11 clear tracker, tải lại Evaluation, lưu Failed không có điểm/kỹ năng cùng progress.
Lỗi kết nối/SaveChanges khác: không ghi thành công giả; exception đi ra worker và được retry ở scope tiếp theo.
Nếu database không ghi được, không thể cam kết lưu ErrorMessage vào chính database; log chỉ ghi loại lỗi/ID.
Thất bại trước commit để hồ sơ Pending; có thể gọi AI lại (tốn quota). Không cam kết exactly-once cho lời gọi Gemini.
Sau commit, worker mới chỉ chọn Pending; Completed và dữ liệu kỹ năng đã lưu giữ nguyên.
Khóa worker không bảo vệ khỏi SQL thủ công hay writer bên ngoài không tuân thủ khóa.

## PromptVersion và giới hạn tái lập

**Chỉ snapshot PromptVersionId; không có bản sao nội dung prompt độc lập trong Run.**
Chưa có endpoint sửa/tạo/quản lý PromptVersion trong repository. Entity có setter công khai;
database không có trigger chặn sửa SystemPrompt khi đã tham chiếu. FK Restrict chỉ bảo vệ việc xóa.
Do đó đổi active sang v2 không ảnh hưởng Run v1, nhưng sửa trực tiếp nội dung v1 bằng EF/SQL
vẫn ảnh hưởng hồ sơ Pending của Run v1. Test A12 thể hiện rõ giới hạn này, không tuyên bố bất biến tuyệt đối.
Chưa thêm schema/trigger/API lớn của A14. Quy tắc vận hành: nội dung đã dùng phải giữ nguyên, đổi nội dung bằng phiên bản mới.

Những yếu tố **chưa snapshot**: MissingMustHaveCap, bốn trọng số Keyword*, JobSkills và aliases,
yêu cầu GPA/kinh nghiệm và dữ liệu job, CV.ExtractedText/trạng thái trích xuất, ngày hiện tại
trong tính kinh nghiệm, phiên bản thuật toán/prompt builder, thông số provider ngoài Run,
chế độ FakeAI và hành vi/nondeterminism của model Gemini.
JobProfile được cache trong một processor; restart/scope khác có thể đọc dữ liệu job mới.
Test đổi cap từ 40 lên 60 chứng minh hồ sơ Pending có thể nhận điểm khác trong cùng Run;
kết quả Completed vẫn được giữ. Không cam kết tái lập điểm tuyệt đối khi chấm lại.
ModelName là model yêu cầu, không phải chứng nhận revision thực tế do provider phục vụ.

Đề xuất riêng cho tương lai: snapshot nội dung/hash prompt, job/CV/hash, cấu hình keyword/cap,
phiên bản thuật toán và metadata provider; đánh dấu simulated độc lập; bảo vệ phiên bản prompt tại DB.
A14 cần tạo phiên bản mới thay vì sửa nội dung đã dùng và xử lý quyền sửa trực tiếp/concurrency.
A13 cần trình bày nguồn điểm, lý do/evidence và metadata Run; không coi AI-only là đoạn trích CV.
Đây là đề xuất, không có migration/API tương ứng trong A12.

## Kiểm thử an toàn và FakeAI

Chạy `dotnet restore`, `dotnet build`, `dotnet test --logger "trx;LogFileName=A12.trx"`.
`RunSnapshotEndpointTests` chạy TestServer với EF InMemory, worker mặc định tắt;
`A12PersistenceTests` chạy processor trực tiếp bằng stub, không gọi HTTP Gemini.
Database trong bộ nhớ có tên GUID, HTTP snapshot test có prefix `A12-`; không phải SQL Server.
Không dùng API key thật để test; factory thay connection và API key bằng giá trị giả.
Không bật worker trỏ database thật, không thay key thật để thử lỗi.

Nếu thử FakeAI thủ công, chỉ dùng database test do người vận hành chủ động cung cấp,
Development + Gemini:UseFake=true; chỉ bật WorkerEnabled sau khi kiểm tra connection test.
Text tổng hợp chứa `#loi-ai` kích hoạt lỗi giả để xem KeywordOnly.
FakeAI ghi reasoning giả lập và raw JSON có `simulated=true`; không trình bày là Gemini thật.
Schema chưa có cột IsSimulated riêng, không suy luận model thật chỉ từ Run.ModelName.

## SQL chỉ đọc

Mở connection tới **database test** trong SQL client, xác minh `SELECT DB_NAME()` rồi chạy
`database/check_a12.sql`. Có thể đặt @RunId theo dữ liệu test; NULL kiểm tra tất cả.
Script không USE database, không bỏ RunId demo, không UPDATE/DELETE và không chuẩn bị/chấm lại dữ liệu.
Nhóm 1 xem snapshot/progress/count; nhóm 2 kiểm tra số học và trường NULL/nguồn điểm;
nhóm 3 kiểm tra label/pass; nhóm 4 kiểm tra kỹ năng/sai job/trùng mã.
Điểm thấp hơn công thức chưa cap là **cần xem xét**, không kết luận sai vì thiếu cap snapshot.
Không dùng Reasoning làm chứng cứ số học. So kỹ năng với JobSkills hiện tại chỉ là chẩn đoán,
không chứng minh job lúc chấm. Không tự chấm lại khi thấy chênh lệch.

**Chưa chạy check_a12.sql trên SQL Server**: chưa có database test được xác nhận an toàn.
Không kết nối/chỉnh sửa database chính, không chạy SQL UPDATE/DELETE, không mất kết quả thật.
Đã kiểm tra cấu hình mã nguồn: WorkerEnabled mặc định false; không mở hoặc in connection secret.
Không thể xác nhận trạng thái dữ liệu database đang dùng ngoài ứng dụng vì không truy cập database đó.

## Kết quả kiểm thử

Kết quả ngày 09/11/2026: restore PASS; build PASS (0 warning, 0 error).
Toàn bộ suite: 467 test, 467 PASS, 0 FAIL, 0 SKIP; gồm 9 trường hợp test mới của A12.
HTTP snapshot test kiểm tra Run mới dùng prompt/config mới và dữ liệu Run cũ giữ nguyên;
processor test kiểm tra restart, bảo toàn Completed, đúng job của kỹ năng và xóa dữ liệu Pending lỗi.
Test A11 hiện có kiểm tra Combined/raw JSON, KeywordOnly, extraction failure, shutdown/restart,
progress và lỗi SaveChanges; bổ sung kiểm tra không lưu điểm/raw/kỹ năng khi lỗi và phục hồi bằng context mới.
Lần chạy trong sandbox lỗi quyền Windows EventLog; chạy lại ngoài sandbox thành công,
không sửa hoặc bỏ test cũ để né lỗi. File TRX nằm trong TestResults (được Git ignore).
InMemory kiểm tra hành vi lưu/khôi phục và fault injection trước SaveChanges;
không mô phỏng transaction SQL Server, FK/check/collation/decimal hoặc lỗi giữa các câu lệnh SQL.
Rollback khi insert kỹ năng SQL Server lỗi, session lock nhiều instance và crash thật cần integration test
trên database test riêng; không coi InMemory là bằng chứng những trường hợp này đã PASS.
