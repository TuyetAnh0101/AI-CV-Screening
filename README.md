# AI CV Screening – Hệ thống sàng lọc và xếp hạng CV bằng AI

Hệ thống giúp bộ phận nhân sự (HR) tải lên nhiều CV, gắn CV với vị trí tuyển dụng (job), tự động chấm điểm bằng **từ khóa + Gemini API**, xếp hạng ứng viên theo từng job và **so sánh độ chính xác giữa các phiên bản prompt** dựa trên nhãn do HR gán.

## 1. Chức năng chính

| Nhóm | Chức năng |
|---|---|
| Tải CV | Tải một hoặc nhiều file (PDF, PNG, JPG), kiểm tra định dạng, dung lượng, phát hiện file trùng bằng mã hash |
| Trích xuất nội dung | Đọc chữ bằng PdfPig, dùng OCR `eng+vie` với bản scan và ảnh |
| Chấm điểm | Chấm theo từ khóa và Gemini (JSON có cấu trúc), chạy dưới nền, có thanh tiến độ |
| Kết quả | Bảng xếp hạng theo job (hạng, tên CV, điểm, mức đánh giá); chi tiết kỹ năng đạt, kỹ năng thiếu, nhận định của AI |
| System Prompt | Quản lý phiên bản prompt (không ghi đè bản cũ), kích hoạt phiên bản đang dùng |
| Đánh giá prompt | Nhập nhãn HR, chạy prompt trên bộ CV đánh giá, tính tỷ lệ đúng, so sánh hai phiên bản |

**Mức đánh giá:** Rất phù hợp · Phù hợp · Tiềm năng · Chưa phù hợp

**Cách tính tỷ lệ đúng:**

```
Tỷ lệ đúng = Số CV AI đánh giá giống HR / Tổng số CV × 100%
```

Ví dụ với 50 CV: prompt A đúng 35 CV (70%), prompt B đúng 40 CV (80%). Khi so sánh, bộ CV, model và cài đặt Gemini được giữ nguyên, chỉ thay đổi prompt.

## 2. Công nghệ

- **Backend:** C#, ASP.NET Core Web API, Entity Framework Core
- **Cơ sở dữ liệu:** SQL Server
- **AI:** Google Gemini API
- **Giao diện:** Blazor (C#, HTML, CSS – Razor)
- **Đọc file:** PdfPig, OCR (Tesseract `eng+vie`)
- **Kiểm thử:** xUnit

## 3. Lưu trữ CV

- File gốc lưu trên máy chủ, được đổi tên bằng GUID: `Uploads/CV/2026/11/<GUID>.pdf`
- SQL Server lưu: tên file gốc, đường dẫn, mã hash, nội dung trích xuất, ngày tải, trạng thái

## 4. Cấu trúc thư mục

```
AI-CV-Screening/
├── database/              # Script tạo CSDL, dữ liệu mẫu, sơ đồ ERD (C2)
├── docs/                  # Tài liệu, thiết kế Figma, báo cáo
├── src/
│   ├── CvScreening.Api/   # ASP.NET Core Web API (Bạn A)
│   ├── CvScreening.Web/   # Blazor (Bạn B)
│   └── CvScreening.Tests/ # Unit test
├── .github/               # Mẫu Pull Request và Issue
├── CONTRIBUTING.md        # Quy trình làm việc nhóm với Git
└── README.md
```

## 5. Bảng dữ liệu chính

`Jobs`, `Cvs`, `Applications`, `PromptVersions`, `EvaluationRuns`, `EvaluationResults`, `HrLabels`

## 6. Danh sách API

| Phương thức | Đường dẫn | Mô tả |
|---|---|---|
| POST | `/api/cvs/upload` | Tải một hoặc nhiều CV |
| POST | `/api/applications` | Gắn CV với job |
| GET | `/api/jobs` | Danh sách job |
| POST | `/api/scoring/start` | Bắt đầu chấm điểm |
| GET | `/api/scoring/progress` | Tiến độ chấm điểm |
| GET | `/api/jobs/{id}/ranking` | Bảng xếp hạng theo job |
| GET | `/api/applications/{id}` | Chi tiết kết quả một CV |
| GET/POST | `/api/prompts` | Xem, tạo phiên bản prompt |
| POST | `/api/prompts/{id}/activate` | Kích hoạt phiên bản prompt |
| POST | `/api/hr-labels` | Nhập nhãn HR |
| POST | `/api/evaluations/run` | Chạy prompt trên bộ CV đánh giá |
| GET | `/api/evaluations/compare` | So sánh hai phiên bản prompt |

## 7. Cài đặt và chạy

1. Chạy script trong thư mục `database/` trên SQL Server.
2. Lưu Gemini API key bằng User Secrets, **không** ghi vào `appsettings.json`:
   ```
   dotnet user-secrets set "Gemini:ApiKey" "<API_KEY>" --project src/CvScreening.Api
   ```
3. Chạy `CvScreening.Api`, sau đó chạy `CvScreening.Web`.

## 8. Thành viên và phân công

| Thành viên | Vai trò | Phụ trách |
|---|---|---|
| Bạn A | Backend và AI | A1 – A18 |
| Bạn B | Giao diện và kiểm thử | B1 – B18 |
| Cả nhóm | | C1 – C5 |

## 9. Quy trình làm việc

Mỗi tính năng làm trên một nhánh riêng, tạo Pull Request và được thành viên còn lại kiểm tra trước khi gộp vào `main`. Xem chi tiết tại [CONTRIBUTING.md](CONTRIBUTING.md).
