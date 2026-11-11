# A9 – Chấm điểm CV bằng Keyword

## Mục tiêu

Tính **KeywordScore (0–100)** cho một hồ sơ từ chữ CV đã trích xuất, kèm kỹ năng có/thiếu, đoạn bằng chứng, GPA và số năm kinh nghiệm. A9 không gọi Gemini, không tính điểm cuối và không ghi `Evaluations` / `EvaluationSkills`.

## Luồng xử lý

```
CV gốc ─> A7 (PdfPig / PDFtoImage + Tesseract eng+vie) ─> Cvs.ExtractedText
                                                              │
                                                              ▼
                       A9 KeywordScoringService ─> KeywordScore (30% điểm cuối)
                                                              │
                       A10 Gemini ─> AiScore (70%) ──────────>┴─> FinalScore
```

PdfPig và OCR thuộc **A7**. A9 chỉ đọc `Cvs.ExtractedText`: không mở file trong `Uploads`, không OCR lại.

## Công thức

Có hai tầng trọng số, không được nhầm lẫn:

| Tầng | Công thức | Cấu hình | Thực hiện ở |
|---|---|---|---|
| Điểm cuối | `FinalScore = KeywordScore × 0.30 + AiScore × 0.70` | `Scoring:KeywordWeight`, `Scoring:AiWeight` | A10 |
| Bên trong KeywordScore | `Σ(trọng số × tỷ lệ đạt) / Σ(trọng số tiêu chí áp dụng) × 100` | `Scoring:Keyword*Weight` | A9 |

| Tiêu chí | Trọng số | Tỷ lệ đạt | Bỏ khỏi mẫu số khi |
|---|---|---|---|
| Kỹ năng bắt buộc | 60% | tìm thấy / tổng | job không có kỹ năng bắt buộc |
| Kỹ năng ưu tiên | 20% | tìm thấy / tổng | job không có kỹ năng ưu tiên |
| GPA | 10% | ≥ yêu cầu → 1, thấp hơn → GPA / yêu cầu | job không yêu cầu, hoặc CV không ghi |
| Kinh nghiệm | 10% | ≥ yêu cầu → 1, thấp hơn → năm / yêu cầu | job yêu cầu 0 năm, hoặc CV không ghi |

Ứng dụng dừng ngay lúc khởi động nếu trọng số âm hoặc một trong hai nhóm có tổng khác 1.

### Thiếu GPA hoặc kinh nghiệm

GPA / kinh nghiệm không đọc được thì trả `null` (không phải 0), tiêu chí bị bỏ và trọng số được chia lại, kèm ghi chú trong `notes`.

CV ghi rõ `0 năm`, `Chưa có kinh nghiệm` hoặc `Không có kinh nghiệm làm việc` trả **0**, là dữ liệu hợp lệ: khi job yêu cầu kinh nghiệm, tiêu chí vẫn ở mẫu số và có tỷ lệ đáp ứng `0 / yêu cầu = 0`. Không suy ra 0 từ việc CV là sinh viên / mới tốt nghiệp, thiếu thông tin hoặc chỉ chưa có kinh nghiệm với một kỹ năng cụ thể.

| Nội dung CV | Số năm trích xuất | Khi job yêu cầu 2 năm |
|---|---:|---|
| Không đề cập kinh nghiệm | `null` | Bỏ tiêu chí, chia lại trọng số |
| `0 năm` / `Chưa có kinh nghiệm` | `0` | Giữ tiêu chí, tỷ lệ 0 |
| `6 tháng` | `0.5` | Tỷ lệ 0.25 |
| `1 năm` | `1` | Tỷ lệ 0.5 |
| `2 năm` | `2` | Tỷ lệ 1 |

Ví dụ job chỉ có một kỹ năng bắt buộc C# (60%) và yêu cầu 2 năm (10%), CV đã có C#: không ghi kinh nghiệm đạt 100; ghi rõ 0 năm đạt `0.6 / 0.7 × 100 = 85.71`; 6 tháng đạt `(0.6 + 0.1 × 0.25) / 0.7 × 100 = 89.29`. Công thức và trọng số Keyword không thay đổi.

> **Cần phê duyệt nghiệp vụ:** quy tắc "không ghi thì không phạt" lấy từ `HUONG_DAN_A9_A13.md` và chưa được HR chốt. Trọng số 60/20/10/10 cũng là đề xuất của tài liệu đó.

## Đọc GPA và kinh nghiệm

**GPA** (`CvFactExtractor.ExtractGpa`): `GPA 3.2/4`, `GPA 3.20`, `GPA 8.1/10`, `CPA`, `Điểm trung bình tích lũy`, `ĐTB`, `GPA (thang 4): 3.3`. Thang 10 quy về thang 4 bằng `điểm / 10 × 4`. Không ghi thang mà điểm > 4 thì coi là thang 10. Thang khác 4 và 10 bị bỏ qua vì chưa có quy tắc quy đổi.

**Kinh nghiệm** (`ExtractYearsOfExperience`) lấy giá trị lớn nhất giữa:

- Thời lượng ghi thẳng: `1.5 năm`, `2+ years`, `6 tháng`, `18 months`, `1 năm 6 tháng`.
- Tổng các khoảng thời gian: `06/2024 - nay`, `2020 - 2022`, `Jun 2024 - Present`, `tháng 6 năm 2024 - nay`. Phần chồng lấn chỉ tính một lần, mốc tương lai cắt về hôm nay, mốc không hợp lệ bị bỏ.

Không tính: mục Học vấn, câu về đại học / khóa học / bootcamp / tự học, dòng ngày tháng sát tên trường, mục tiêu tương lai, địa chỉ ("Quận 7 Nam Định"), ngày tháng ("3 tháng 5 năm 2024"). Khoảng thời gian trong mục Dự án / Hoạt động không tính; thời lượng ở các mục đó chỉ tính khi câu nói rõ về kinh nghiệm.

## So khớp kỹ năng

- Chỉ dùng `SkillCode`, `DisplayName` và `JobSkillAliases` của đúng job; không có alias ngoài dữ liệu.
- `TextNormalizer` đổi 1 ký tự thành 1 ký tự (thường, bỏ dấu, `đ → d`, thống nhất gạch ngang), nên vị trí khớp dùng lại được trên chữ gốc.
- Cụm dài được thử trước. Khoảng trắng, xuống dòng, gạch nối trong cụm là tương đương.
- Khớp nguyên từ: `Java` ≠ `JavaScript`, `SQL` ≠ `MySQL` (trừ khi job có alias), `C` ≠ `C#` / `C++`.
- `Evidence` cắt khoảng 120 ký tự từ chữ gốc còn dấu, không cắt giữa từ hoặc giữa emoji.

Từ khóa được escape và không có lượng từ lồng nhau, nên thời gian khớp tuyến tính theo độ dài CV (có test CV trên 1 triệu ký tự).

## API

`GET /api/applications/{id}/keyword-score` — xem trước, không ghi gì.

**Kiểm soát truy cập:** cùng chốt chặn với API hồ sơ (A8): endpoint chỉ tồn tại ở Development khi `CvUpload:Endpoint:Enabled = true`, cho đến khi có xác thực (M6).

```json
{
  "applicationId": 7,
  "fileName": "CV_DangThuGiang.jpg",
  "jobTitle": "AI Engineer Intern",
  "keywordScore": 33.33,
  "gpa": 3.10,
  "yearsExperience": null,
  "skills": [
    { "skillCode": "python", "displayName": "Python", "skillType": "MustHave", "isMatched": true, "evidence": "GPA 3.10. Lập trình Python cơ bản..." }
  ],
  "notes": [ "Vị trí không yêu cầu số năm kinh nghiệm.", "Thiếu kỹ năng bắt buộc: LLM API, Machine Learning." ]
}
```

| Tình huống | HTTP |
|---|---|
| `ExtractionStatus = Completed`, có chữ | 200 |
| Không có hồ sơ | 404 |
| `Pending`, `Failed`, hoặc `Completed` nhưng chữ rỗng | 409, có `extractionStatus` |
| Job không có tiêu chí nào áp dụng được | 409 |

Trạng thái được xét trước nội dung: khi đọc lại CV, A7 đưa về `Pending` nhưng vẫn còn chữ cũ, nên không chấm trên chữ đó.

`KeywordScoringService.Score` ném `ArgumentException` với CV rỗng, để không bao giờ sinh điểm 0 giả.

## Chạy thử

Dùng database test riêng, không dùng `SmartCvDb`:

```powershell
$env:ConnectionStrings__SmartCvDb = 'Server=localhost\BB;Database=SmartCvDb_A5Test;Trusted_Connection=True;TrustServerCertificate=True'
$env:CvUpload__StorageRoot = 'Uploads/CV-test'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5199'
dotnet run --project src/SmartCv.Api --no-launch-profile
```

```powershell
Invoke-RestMethod "http://localhost:5199/api/applications/7/keyword-score"
```

## Kết quả kiểm thử (2026-11-09)

- **Unit:** 126/126 test trong `KeywordScoringTests`.
- **Integration (TestServer + InMemory, 5 job nền):** 17/17 test trong `KeywordScoreEndpointTests`.
- **Toàn bộ solution:** 303/303, gồm các test A6, A7, A8.
- **HTTP thật** trên `SmartCvDb_A5Test`: 23 hồ sơ. Số dòng `Evaluations` / `EvaluationSkills` giữ nguyên trước và sau (36 / 192).

| Hồ sơ | Job | Keyword | Tài liệu | GPA | Năm KN | Thiếu bắt buộc |
|---|---|---|---|---|---|---|
| 1 | Backend | 100.00 | 100.00 | 3.2 | 1.5 | |
| 2 | Backend | 86.67 | 86.67 | 2.9 | 0.5 | |
| 3 | Backend | 18.52 | 18.52 | 2.6 | | csharp, aspnetcore, sqlserver |
| 4 | Frontend | 100.00 | 100.00 | 3.4 | 1 | |
| 5 | Frontend | 92.59 | 92.59 | 2.7 | | |
| 6 | AI Engineer | 100.00 | 100.00 | 3.6 | | |
| 7 | AI Engineer | 33.33 | 33.33 | 3.1 | | machine_learning, llm_api |
| 8 | Tester | 100.00 | 100.00 | 2.8 | 1 | |
| 9 | Tester | 50.00 | 50.00 | 3.0 | 0.3 | sql |
| 10 | BA | 100.00 | 100.00 | 3.3 | 2 | |
| **11** | BA | **61.67** | **55.00** | 2.85 | 0.5 | uml |
| 12 | AI Engineer | 62.96 | 62.96 | 3.2 | 1.5 | machine_learning |
| 13 | Backend | 409 (CV lỗi) | 409 | | | |

**Khác biệt ở hồ sơ 11:** CV ghi "TOEIC 750", và TOEIC là alias của `english` trong `JobSeedData`. Tính theo công thức: 0.6 × 2/3 + 0.2 × 1/3 + 0.1 + 0.1 × 0.5 = 61.67. Con số 55.00 của tài liệu chỉ đúng nếu không tính TOEIC. Giữ kết quả theo thuật toán, không chỉnh để khớp bảng.

CV mẫu của A7 đúng job đạt 100: `cv-backend-pdf-chu.pdf` (PdfText, hồ sơ 14), `cv-tester-anh.png` (ImageOcr, hồ sơ 18). `cv-ai-pdf-scan.pdf` (PdfOcr) trong DB test đang gắn job BA nên chỉ xác nhận đọc đúng GPA 3.55 và 2.3 năm, chưa đối chiếu được mức 100.

## Hạn chế của Keyword Scoring

- Không hiểu mức độ thành thạo: "Đang tự học C#" vẫn tính là có C#. Đây là phần của Gemini.
- Từ khóa phụ thuộc alias: "HTML, CSS" không khớp kỹ năng `HTML/CSS` vì job chỉ có alias `HTML5`, `CSS3`.
- Lỗi OCR đổi ký tự không tự sửa được: `OpenAl API` (chữ l), `APl`. `C #` có khoảng trắng và từ bị ngắt giữa dòng (`Mach-\nine`) cũng không khớp.
- `Java Script` viết rời vẫn khớp `Java`; `T-SQL` vẫn khớp `SQL`.
- Thời lượng ghi rời ("Công ty A 1 năm. Công ty B 6 tháng.") lấy giá trị lớn nhất, không cộng, để tránh đếm trùng với câu tổng.
- Khoảng chỉ có năm tính trọn năm (`2020 - 2022` = 3 năm).
- Thực tập được tính là kinh nghiệm (theo dữ liệu demo).
- Công việc tại trường (ví dụ giảng viên ở "Đại học X") bị bỏ qua vì câu có tên trường.
- Câu xác nhận không có kinh nghiệm chỉ nhận khi diễn đạt rõ và nói về kinh nghiệm tổng, có thể kèm nhãn `Kinh nghiệm:` / `Experience:`. `Chưa có kinh nghiệm với Docker`, `0 năm kinh nghiệm với Docker` hoặc `Docker: 0 years of experience` không được coi là 0 năm kinh nghiệm tổng. CV có nhiều bằng chứng thời lượng dương vẫn lấy giá trị lớn nhất theo quy tắc trên.

## Đề xuất (chưa áp dụng)

- Thêm alias `HTML`, `CSS` cho kỹ năng `html_css` trong seed.
- Chốt với HR: có phạt khi thiếu GPA / kinh nghiệm không, thực tập có tính là kinh nghiệm không.

## A10 cần làm tiếp

- Gọi Gemini, gộp `KeywordWeight` / `AiWeight` trong `ScoreCombiner`.
- Thêm `MissingMustHaveCap` (A10) và `PollIntervalSeconds` (A11) vào `ScoringOptions` và `appsettings.json`; A9 chưa thêm vì không dùng.
- ~~Thống nhất tên model: `Gemini:Model` đang là `gemini-3.1-flash-lite`, `Scoring:ModelName` là `gemini-2.5-flash`.~~ Đã xong (2026-11-10): chỉ còn `Scoring:ModelName`, xem A10 mục 18.
- `Program.A10.cs.txt` neo vào dòng `Configure<ScoringOptions>`; ở A9 đăng ký bằng `AddOptions<ScoringOptions>()` trong `Program.cs`.
- Bắt `ArgumentException` khi CV rỗng và xét `HasApplicableCriteria` trước khi lưu điểm.
