# Báo cáo kiểm tra và sửa tính nhất quán SCApp sau A18

Ngày kiểm tra: 11/11/2026 (Asia/Saigon). Không commit, stage, push, merge, rebase hoặc stash.

## A. Kết luận

- Sửa lỗi extractor đồng nhất kinh nghiệm đã xác nhận 0 với không có thông tin. Không sửa công thức Keyword hoặc mặc định thiếu thông tin thành 0.
- API AI preview trả thêm đúng trọng số Keyword/AI, ngưỡng và cap thực dùng. Hai endpoint tạo run và worker vốn đã lưu/đọc đúng trọng số riêng của run.
- Ngừng chức năng ghi của updater seed cũ; file tại đường dẫn cũ chỉ còn SELECT. Không chuyển snapshot lịch sử hợp lệ từ 40/60 sang 30/70.
- Demo 70%/80% được ghi rõ là snapshot trước cap 69. Bổ sung replay tính toán thật từ đầu vào đóng băng, SQL audit và thông tin lịch sử trên trang chi tiết; không đổi màu, CSS hoặc thao tác.
- Không tái tạo được mặc định 40/60 hoặc dữ liệu SQL đang dùng có 40/60. Không tái tạo được CV trống làm treo run. Giữ Evaluation Failed, điểm/dự đoán null.
- Không có cập nhật dữ liệu đang dùng cần phê duyệt. Việc mở rộng schema để snapshot toàn bộ cap/Keyword/thuật toán/job/CV là giới hạn A12 đã có, không triển khai ở bản sửa này.

## B. Trọng số 30/70

| Vị trí | Trước | Sau | Lý do |
|---|---|---|---|
| appsettings.json, ScoringOptions | 0.30/0.70 | Giữ nguyên | Mặc định đã đúng |
| appsettings.Development.json | Không override | Giữ nguyên | Không có mặc định 40/60 |
| User Secrets và biến môi trường được kiểm tra | Không có override Scoring | Không sửa | Chỉ đọc các khóa Scoring; không xuất key/connection string |
| ScoreCombiner | Tham số của lượt, không âm, tổng 1 | Giữ nguyên | Hỗ trợ thử nghiệm 40/60 hợp lệ |
| Hai endpoint tạo run, RunScoringProcessor | Lưu/đọc KeywordWeight, AiWeight và ngưỡng của run | Giữ nguyên, thêm regression | Đổi mặc định không đổi kết quả Completed |
| Trọng số bên trong Keyword | 0.60/0.20/0.10/0.10 | Giữ nguyên | Khác tầng với Keyword/AI |
| seed_demo.sql, 4 run nguồn | 0.30/0.70 | Giữ nguyên | Sửa mô tả lịch sử, không sửa điểm |
| SmartCvDb, 21 run hiện hữu | Cả 21 là 0.30/0.70 | Không cập nhật | Không có 40/60 để sửa |
| update_seed_weights_30_70.sql | Đổi snapshot, 32 điểm và accuracy lịch sử | SELECT-only | Tránh ghi lại lịch sử theo mặc định hiện hành |
| DTO AI preview | Thiếu metadata trọng số/ngưỡng/cap | Trả bốn trường thực dùng | Client không phải suy mặc định |

Không sửa migration đã áp dụng. Database có CHECK không âm/tổng 1; API từ chối cấu hình không lưu chính xác ở độ chính xác cột. Test chứng minh 80/60→66, 100/60→72, 70/70→70 và custom 40/60 vẫn hợp lệ.

SQL đang dùng chỉ được đọc. Không có bằng chứng metadata sai nên không chuẩn bị/chạy UPDATE suy đoán. Nếu tương lai có bằng chứng, phải xác minh CV/hash, prompt, thời điểm và quan hệ từng dòng; có transaction/giá trị cũ/rollback và phê duyệt riêng trước khi sửa dữ liệu đang sử dụng. RunId riêng lẻ không chứng minh dữ liệu demo.

## C. Demo và accuracy

Hai snapshot nguồn dùng cùng model gemini-2.5-flash, temperature 0.20, ngưỡng 70 và trọng số 30/70. Điểm được sinh trước quy tắc giới hạn69 khi cả Keyword và AI không xác nhận đủ MustHave.

| Phép đối chiếu trên đầu vào đóng băng | v1 Correct/Missed/WrongPick/Unscored | v2 | Accuracy v1/v2 |
|---|---|---|---|
| Snapshot nguồn, chưa cap | 7/1/2/0 | 8/1/1/0 | 70%/80% |
| Công thức40/60, chưa cap | 7/1/2/0 | 8/1/1/0 | 70%/80% |
| Combiner hiện tại 30/70, cap 69 | 9/1/0/0 | 9/1/0/0 | 90%/90% |
| Combiner hiện tại 40/60, cap 69 | 9/1/0/0 | 9/1/0/0 | 90%/90% |

Theo cờ kỹ năng đã lưu: #5 thiếu C#, điểm 72.50 ở cả hai prompt xuống 69; #9 thiếu SQL, v1 từ 75.60 xuống 69. #9 v2 vốn 60.20 nên không đổi. Các điểm này được tính qua ScoreCombiner, không sửa để ép tỷ lệ.

90%/90% chỉ là **re-combine điểm Keyword/AI và cờ kỹ năng đã lưu**, chưa phải chấm lại CV bằng Gemini. CV #5 có chuỗi “Đang tự học C# và Razor”; matcher hiện tại có thể nhận C#, khác snapshot cũ. Không tuyên bố một lần gọi AI mới sẽ tái tạo 90%/90%.

Trên SQL thử riêng, seed nguồn được giữ nguyên số liệu; thêm run 5/run 6 replay 30/70 và run 7 replay 40/60. Cả ba đạt 90% bằng phép tính thật. So sánh trạng thái tuần tự của run 1..4, evaluations, kỹ năng và nhãn HR trước/sau không đổi. Không xóa dữ liệu hoặc database.

A16 tiếp tục tính Correct/TotalLabeled×100 từ PredictedPass đã lưu và nhãn HR hiện tại; Pending/Failed/null prediction nằm trong Unscored và mẫu số. A17 trên run 1/2 trả Better,+10,true; replay 5/6 trả Same,0,true; replay 5/7 khác trọng số trả NotComparable,0,false.

isFairComparison chỉ kiểm tra các điều kiện có snapshot. Cap, bốn trọng số Keyword, phiên bản thuật toán, job/alias/CV và ngày tính kinh nghiệm chưa snapshot độc lập. 70%/80% là so sánh fixture lịch sử theo metadata đã lưu, không chứng minh prompt tốt hơn với thuật toán hiện tại. B14–B16 chưa có implementation trong repo; Prompts.razor là placeholder. Hợp đồng B15 được làm rõ trong tài liệu. Trang chi tiết hiện có nhắc kết quả/trọng số thuộc lượt đã lưu.

## D. Kinh nghiệm và CV trống

Nguyên nhân: explicitYears và LargestDuration trước đây dùng0 cho cả “không tìm thấy”; cuối extractor chỉ trả số dương. Bản sửa giữ trạng thái nullable và nhận câu xác nhận kinh nghiệm tổng bằng 0. Không coi sinh viên, thiếu dữ liệu hoặc chưa có kinh nghiệm với một kỹ năng riêng là 0 năm tổng. Thời lượng dương, khoảng ngày, hợp nhất khoảng chồng lấn và làm tròn giữ nguyên.

| CV | Trích xuất | Điểm Keyword ví dụ: có C#, job yêu cầu2 năm |
|---|---:|---:|
| Không đề cập kinh nghiệm | null | 100 |
| 0 năm / chưa có kinh nghiệm | 0 | 85.71 |
| 6 tháng | 0.5 | 89.29 |
| 1 năm | 1 | 92.86 |
| 2 năm | 2 | 100 |

Công thức giữ nguyên: tổng(weight×tỷ lệ đáp ứng)/tổng trọng số áp dụng×100; tỷ lệ kinh nghiệm = min(thực tế/yêu cầu, 1). Null bỏ tiêu chí theo hợp đồng cũ; 0 giữ tiêu chí với tỷ lệ 0. Job không yêu cầu kinh nghiệm bỏ tiêu chí. Trong ví dụ chỉ C# 60% và kinh nghiệm 10% áp dụng: 0 năm=0.6/0.7×100.

70 regression cases chứng minh extraction, ngày truyền vào cho 06/2025–nay, mẫu số, job không yêu cầu kinh nghiệm, bằng chứng dương không bị 0 ghi đè và không suy numeric0 của Docker thành 0 kinh nghiệm tổng. Mệnh đề xác nhận rõ 0 trước dấu phẩy vẫn được nhận, ví dụ “Tôi có 0 năm kinh nghiệm, thành thạo C#”.

CV 0 byte bị upload từ chối. File hợp lệ nhưng extraction/OCR không có chữ dẫn đến Evaluation Failed, không gọi AI; Completed nhưng text trắng cũng được guard. Run hoàn tất khi mọi hồ sơ đã được xử lý, không biến lỗi thành 0 điểm. Test nối upload→gắn job→tạo run→extraction processor→scoring→GET progress dùng extractor stub hai nhánh failure/whitespace; không tuyên bố OCR file trống thật đã chạy trong test mới.

## E. File thay đổi

Chỉnh sửa (12):

- README.md
- database/README.md
- database/seed_demo.sql
- database/update_seed_weights_30_70.sql
- docs/A16-accuracy.md
- docs/A17-prompt-comparison.md
- docs/A9-keyword-scoring.md
- src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs
- src/SmartCv.Core/Contracts/AiPreviewDto.cs
- src/SmartCv.Core/Scoring/CvFactExtractor.cs
- src/SmartCv.Web/Components/Pages/EvaluationDetail.razor
- tests/SmartCv.Tests/ScoreCombinerTests.cs

Tạo mới (7):

- database/check_scoring_consistency.sql
- docs/scoring-data-consistency.md
- docs/scoring-snapshots-and-empty-cvs.md
- docs/scoring-data-consistency-report.md
- tests/SmartCv.Tests/HistoricalDemoReplayTests.cs
- tests/SmartCv.Tests/Scoring/ExperienceFactRegressionTests.cs
- tests/SmartCv.Tests/ScoringConsistencyEndpointTests.cs

Không thay CSS, authentication/authorization, Gemini model, migration hoặc secrets. artifacts/scoring-consistency chứa log/TRX và harness SQL tạm, đã được gitignore; không thuộc danh sách commit.

## F. Kiểm thử

Lệnh chính (chạy từ gốc repository):

```powershell
dotnet build SmartCv.slnx --no-restore --nologo -v minimal
$env:Logging__EventLog__LogLevel__Default='None'
dotnet test SmartCv.slnx --no-build --no-restore --nologo --logger 'trx;LogFileName=full-final.trx' --results-directory artifacts/scoring-consistency/full-final --verbosity quiet
dotnet test SmartCv.slnx --no-build --no-restore --nologo --logger 'trx;LogFileName=repeat-final.trx' --results-directory artifacts/scoring-consistency/repeat-final --verbosity quiet
```

| Kiểm tra | Exit | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| Build baseline | 0 | — | 0 errors, 0 warnings | — |
| Baseline sau cấu hình EventLog | 0 | 1000 | 0 | 31 |
| Build cuối | 0 | — | 0 errors, 0 warnings | — |
| Test liên quan sau bản sửa cuối | 0 | 437 | 0 | 0 |
| Toàn suite cuối | 0 | 1088 | 0 | 31 |
| Chạy lại toàn suite cuối | 0 | 1088 | 0 | 31 |
| Riêng 95 cases thuộc 5 class A18 (đã nằm trong suite) | 0 | 95 | 0 | 0 |

Tổng suite cuối: 1.119 test, tăng 88 so với baseline 1.031. 88 cases mới: 70 kinh nghiệm, 8 HTTP snapshot/preview/blank, 7 replay demo, 3 dòng công thức 30/70. Không bỏ test hoặc đổi assertion cũ. 31 SQL integration tests tùy chọn vẫn Skip vì không cấu hình các biến SMARTCV_*_TEST_SQL_CONNECTION; kiểm tra SQL riêng bên dưới bổ sung cho suite này.

Filter targeted: ExperienceFactRegressionTests, ScoringConsistencyEndpointTests, ScoreCombiner, HistoricalDemoReplayTests, PromptComparer, PromptAccuracy, RunScoringProcessorTests, A12, KeywordScoring, EvaluationDetailUiTests. Các lệnh đều dùng --no-build/--no-restore sau build thành công.

Các lần chưa đạt được ghi nhận, đã khắc phục và chạy lại:

- Baseline không cấu hình EventLog: exit 1, 899 Passed/101 Failed/31 Skipped do tài khoản sandbox không ghi Windows Event Log; lỗi worker timeout kéo theo. Chỉ tắt provider trong môi trường tiến trình test, không sửa production/assertion. Baseline chạy lại đúng 1000/0/31.
- Một lượt build bị API kiểm tra do tôi đang chạy khóa DLL: exit 1; đã dừng đúng API tạm và build lại thành công 0 warnings/errors. Log lỗi giữ tại build-locked.log.
- Harness SQL tạm ban đầu gặp format JSON khi dùng ExecuteSqlRaw; đổi utility sang SqlCommand, chạy lại thành công trên database mới. Database thử kế thừa SINGLE_USER từ model làm một audit kết nối đồng thời thất bại; chỉ database thử được chuyển MULTI_USER và audit chạy lại exit 0.

SQL Server localhost\\BB:

- SmartCvDb chỉ đọc: 21 run, tất cả 30/70; 69 evaluations, 362 evaluation skills, 10 HR labels. 62 evaluations Completed, 0 sai lệch FinalScore/PredictedPass.
- Snapshot, số dòng và CHECKSUM_AGG(BINARY_CHECKSUM(*)) của 4 bảng trên trước/sau giống hệt; file live-before.txt và live-after.txt có cùng SHA256.
- check_scoring_consistency.sql trên live và SQL thử: exit 0. updater đã retire trên live: exit 0, chỉ SELECT.
- Database thử đã seed và thêm replay: SmartCv_Consistency_Test_20261111_2d384d11. Run 5/6 dùng 30/70, run 7 dùng 40/60; tất cả 90%, Correct 9/Missed 1/WrongPick 0/Unscored 0. Lịch sử không đổi.
- Database khởi tạo ở lần thử harness đầu: SmartCv_Consistency_Test_20261111_1fb91bfc; được giữ lại. Không DROP/DELETE database nào.
- 8 HTTP GET trên API tạm 15158 đều 200: 4 run detail, 3 comparison, 1 evaluation detail; assertion xác nhận SQL/API cùng trọng số, điểm và accuracy. API tạm đã dừng. AI giả chỉ là cấu hình test; các GET không gọi Gemini.

Không gọi Gemini thật, không thay model. API tests thường dùng InMemory và stub; SQL harness/API GET dùng SQL Server thật. Xem log/TRX trong artifacts/scoring-consistency.

## G. Git

A18 đã squash merge PR #39 vào main trước khi tạo nhánh. Commit nền và HEAD chưa commit: 390a9408d64e2591d1f11225d6664dbc1d51c464. Tree A18 local 005d429 và origin/main đã được đối chiếu giống nhau; working tree sạch trước khi tạo nhánh.

```text
## fix/scoring-data-consistency...origin/main
```

Staged: rỗng. Unstaged: 12 file chỉnh sửa ở mục E. Untracked: 7 file mới ở mục E. Không thay đổi ngoài phạm vi. git diff --check exit 0; cảnh báo chuyển LF/CRLF không phải lỗi whitespace.

git diff --stat (chỉ các file đã tracked): 12 files changed, 118 insertions(+), 216 deletions(-). Các file mới chưa stage không nằm trong thống kê này. git status --short --branch, diff --stat và danh sách đầy đủ được ghi trong artifacts/scoring-consistency/git-final.txt.

## H. Bàn giao

READY FOR REVIEW. Build và hai lần chạy full suite của bản sửa cuối đã thành công; không có thay đổi dữ liệu đang dùng chờ phê duyệt. Giữ nguyên lịch sử và toàn bộ 88 regression cases mới. Người dùng tự chọn file để commit/push theo mục E.
