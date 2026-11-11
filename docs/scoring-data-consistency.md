# Tính nhất quán dữ liệu chấm điểm sau A18

## Trọng số hiện hành và lịch sử

Mặc định điểm cuối là `Keyword × 0.30 + AI × 0.70`. Trọng số bên trong
Keyword là `0.60/0.20/0.10/0.10`; giá trị `0.60` này không phải tỷ lệ AI.
Trọng số hợp lệ khác như `0.40/0.60` có thể thuộc một thử nghiệm hoặc lịch sử.
Không đổi snapshot đó chỉ vì mặc định hiện tại là 30/70. Tổng phải bằng 1,
hai trọng số không âm. Worker phải dùng trọng số đã lưu trong chính run.

`seed_demo.sql` trong repository đã dùng 30/70. Các migration đã áp dụng
không được viết lại. Khi kiểm tra database, đọc trọng số, điểm thành phần,
điểm cuối và dự đoán cùng nhau; một điểm thấp hơn công thức có thể do cap.

`database/update_seed_weights_30_70.sql` cũ từng đổi cả trọng số, điểm và
accuracy của lịch sử để chuyển 40/60 sang 30/70. File này nay chỉ đọc và
đã ngừng cập nhật dữ liệu. Dùng `database/check_scoring_consistency.sql`
để audit đầy đủ. Không chạy lại seed trên database đang có dữ liệu.

Nếu tương lai tìm được metadata sai, cần xác minh nguồn seed bằng CV/hash,
prompt, ngày tạo, điểm thành phần và khóa quan hệ; RunId 1..4 riêng lẻ không
đủ chứng minh là demo. Chỉ sau khi xác định đúng từng dòng và được chấp thuận
cho dữ liệu đang sử dụng mới viết transaction sửa metadata, kiểm tra trước/sau,
lưu giá trị cũ để rollback. Không tính lại FinalScore lịch sử để hợp với mặc định mới.

## 70%/80% là snapshot demo trước cap

Các điểm trong seed là fixture demo lịch sử, không phải kết quả được tạo lại
bằng pipeline/Gemini hiện tại. Snapshot lưu model `gemini-2.5-flash`,
temperature `0.20`, trọng số `0.30/0.70`, ngưỡng `70` ở cả hai lượt.
Run 1 có Correct/Missed/WrongPick/Unscored = `7/1/2/0`; run 2 = `8/1/1/0`.
Nhãn HR thực tế có **6 Đạt, 4 Không đạt**.

A16 đối chiếu **PredictedPass đã lưu** với nhãn HR hiện tại; không áp cap
hoặc suy lại dự đoán từ FinalScore. Vì thế dữ liệu cũ vẫn hiển thị 70%/80%
khi nhãn chưa thay đổi. Nhãn HR thay đổi có thể làm accuracy tính lại thay đổi,
nhưng không sửa điểm hoặc dự đoán cũ.

Trước đây seed được tạo chưa áp dụng cap 69 khi cả Keyword và AI đều không
xác nhận một kỹ năng MustHave. Kỹ năng đã lưu của hồ sơ #5 thiếu C#, #9 thiếu
SQL. Các điểm của #5 (v1/v2) là 72.50, #9 v1 là 75.60 nên được đánh dấu Đạt.
Khi **giữ nguyên điểm Keyword/AI và cờ kỹ năng đã lưu**, gọi combiner hiện tại
với cap69 sẽ giảm những điểm này xuống 69 và dự đoán Không đạt.

| Dữ liệu đóng băng / quy tắc gộp | v1 Correct/Missed/WrongPick/Unscored | v2 | Accuracy v1/v2 |
|---|---|---|---|
| 30/70, không cap (snapshot nguồn) | 7/1/2/0 | 8/1/1/0 | 70%/80% |
| 40/60, không cap (thử tính đối chứng) | 7/1/2/0 | 8/1/1/0 | 70%/80% |
| 30/70, cap69 | 9/1/0/0 | 9/1/0/0 | 90%/90% |
| 40/60, cap69 (thử tính đối chứng) | 9/1/0/0 | 9/1/0/0 | 90%/90% |

Như vậy với **các đầu vào đóng băng này**, cap gây đổi kết quả phân loại;
40/60 làm đổi điểm nhưng không làm đổi phân loại hoặc accuracy. Đây là
kết quả tính toán bằng `ScoreCombiner`, không có số liệu được sửa để đạt mục tiêu.

90%/90% **không phải kết quả chấm lại CV hoặc gọi lại Gemini thật**. CV #5
có chuỗi “Đang tự học C# và Razor”; matcher Keyword hiện tại có thể nhận C#,
khác cờ kỹ năng thiếu trong snapshot cũ. Trích xuất thời gian, alias, dữ liệu
job và phản hồi AI cũng có thể khác. Muốn báo cáo chấm mới phải tạo hai run
mới, chờ xử lý hoàn tất và đọc kết quả thực tế; không suy ra điểm đó từ bảng trên.

## Tái tạo an toàn

Chạy regression trực tiếp từ nguồn `seed_demo.sql`:

```powershell
dotnet test tests/SmartCv.Tests --filter FullyQualifiedName~HistoricalDemoReplayTests
```

Test đọc 20 điểm của hai lượt, 192 cờ kỹ năng từ seed, nhãn HR và định nghĩa
MustHave trong dữ liệu nền; không duy trì một bộ điểm đầu vào thứ hai. Test
gọi combiner/calculator/comparer thật, kiểm tra hai cặp trọng số và cap, đồng
thời thêm hai run replay mới vào EF InMemory rồi đối chiếu dữ liệu lịch sử
trước/sau. Database test riêng và phản hồi đóng băng không gọi Gemini.
Không có thao tác sửa/xóa trên SmartCvDb.

Nếu cần trình diễn trên SQL Server, dùng database thử nghiệm mới, chạy migration
và seed tại database đó (bản seed nguồn có `USE SmartCvDb`, cần đổi đích ở **bản
sao tạm** trước khi thực thi). Đọc đầu vào, gọi cùng thuật toán rồi INSERT hai
run replay với tên/phần mô tả ghi rõ “đầu vào đóng băng, không gọi Gemini”.
Lưu mọi field từ kết quả tính toán; kiểm tra accuracy từ nhãn HR và checksum
của run cũ. Không đặt mục tiêu 70%/80% hoặc chạy trên database đang sử dụng.

## A16, A17 và giao diện

A16 và danh sách prompt tiếp tục đọc dự đoán lịch sử, nên 70%/80% vẫn có ý
nghĩa khi xem fixture cũ. A17 trả `Same`, delta0 khi so hai replay90% với cùng
điều kiện; khác trọng số trả `isFairComparison=false`, `NotComparable` dù
hai tỷ lệ có thể bằng nhau.

`isFairComparison` hiện chứng minh các điều kiện **có snapshot** giống nhau:
bộ CV/nhãn HR, model, temperature, trọng số và ngưỡng. Nó chưa chứng minh
cap, bốn trọng số Keyword, phiên bản thuật toán, nội dung job/alias/CV giống
nhau vì những yếu tố đó không có snapshot độc lập. Do đó so sánh lịch sử
70%/80% không phải bằng chứng prompt mới tốt hơn với thuật toán hiện tại.
B15 cần hiển thị snapshot và cảnh báo phạm vi này, không thay lịch sử bằng
kết quả tái tính. B14–B16 phải dùng dữ liệu API thực tế thay vì cố định tỷ lệ demo.
