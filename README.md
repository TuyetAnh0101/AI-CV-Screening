<div align="center">

# 🤖 SmartCv – AI CV Screening

**Hệ thống sàng lọc và xếp hạng CV ứng dụng trí tuệ nhân tạo**

Quản lý hồ sơ · Chấm điểm Keyword + AI · Xếp hạng theo vị trí · So sánh phiên bản prompt

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-Web_API-239120?logo=dotnet&logoColor=white)
![EF Core](https://img.shields.io/badge/EF_Core-10.0-512BD4?logo=dotnet&logoColor=white)
![SQL Server](https://img.shields.io/badge/SQL_Server-SmartCvDb-CC2927?logo=microsoftsqlserver&logoColor=white)
![Gemini](https://img.shields.io/badge/AI-Google_Gemini-4285F4?logo=googlegemini&logoColor=white)
![Status](https://img.shields.io/badge/trạng_thái-đã_xong_A2-orange)

</div>

---

> [!NOTE]
> **Trạng thái hiện tại (sau A4):** Backend đã có solution, cấu hình SQL Server và Gemini qua User Secrets, hai API kiểm tra kết nối, **12 Entity, EF Core Migration `InitialCreate`, dữ liệu nền 5 job nạp tự động qua Migration `SeedJobs`, bộ dữ liệu demo** và **API `POST /api/cvs/upload` nhận một hoặc nhiều file** (mới nhận file, chưa kiểm tra và chưa lưu). Các chức năng nghiệp vụ còn lại (kiểm tra/lưu CV, chấm điểm, xếp hạng, quản lý prompt, giao diện) được mô tả **theo thiết kế** và **chưa được lập trình**.

## 📌 Mục lục

1. [Giới thiệu](#gioi-thieu)
2. [Mục tiêu và phạm vi](#muc-tieu)
3. [Chức năng](#chuc-nang)
4. [Công nghệ sử dụng](#cong-nghe)
5. [Kiến trúc và cấu trúc thư mục](#kien-truc)
6. [Cơ sở dữ liệu](#co-so-du-lieu)
7. [Quy trình xử lý CV và chấm điểm](#cham-diem)
8. [Cài đặt môi trường](#cai-dat)
9. [Cấu hình SQL Server và User Secrets](#cau-hinh)
10. [Tạo database và nạp dữ liệu](#database)
11. [Build, chạy và kiểm thử](#chay-du-an)
12. [Danh sách API](#api)
13. [Kết quả nghiệm thu A2, A3, A4](#nghiem-thu-a2)
14. [Lộ trình phát triển](#lo-trinh)
15. [Quy trình làm việc với Git](#git)
16. [Xử lý lỗi thường gặp và bảo mật](#loi-thuong-gap)

---

<a id="gioi-thieu"></a>

## 1. 🎯 Giới thiệu

**SmartCv** hỗ trợ bộ phận nhân sự (HR) khi phải xử lý nhiều hồ sơ ứng viên. Hệ thống quản lý CV và vị trí tuyển dụng, chấm mức độ phù hợp theo tiêu chí của từng vị trí bằng cách kết hợp **so khớp từ khóa** và **Google Gemini**, xếp hạng ứng viên để HR tham khảo, đồng thời đo được độ chính xác của từng phiên bản system prompt so với nhãn do HR gán.

Đây là dự án nhóm sinh viên, phát triển theo từng mã công việc (A – Backend/AI, B – Giao diện/kiểm thử, C – Phối hợp).

> [!IMPORTANT]
> AI chỉ **hỗ trợ** sàng lọc. Quyết định tuyển dụng cuối cùng thuộc về **HR**.

---

<a id="muc-tieu"></a>

## 2. 🧭 Mục tiêu và phạm vi

**Mục tiêu**

- Giảm thời gian đọc CV thủ công bằng cách chấm điểm và xếp hạng tự động theo từng vị trí.
- Cho HR thấy lý do của từng điểm số: kỹ năng đạt, kỹ năng thiếu, nhận định của AI.
- Đo và so sánh chất lượng các phiên bản prompt bằng tỷ lệ đúng so với nhãn HR.

**Phạm vi giai đoạn 1**

- Ba trang chính: **Tải CV**, **Kết quả**, **System prompt**.
- Định dạng CV: PDF (có chữ hoặc scan) và ảnh PNG, JPG/JPEG.
- Dùng 5 vị trí tuyển dụng nhập sẵn; trang quản lý job thuộc giai đoạn 2.

**Ngoài phạm vi giai đoạn 1:** đăng nhập/phân quyền, xuất báo cáo, phân cụm CV, triển khai lên máy chủ (xem [Giai đoạn 2](#lo-trinh)).

---

<a id="chuc-nang"></a>

## 3. ✨ Chức năng

### 3.1. ✅ Đã triển khai

| Mã | Nội dung |
|---|---|
| A1 | Solution .NET 10 gồm 4 project; kết nối SQL Server; cấu hình Gemini qua User Secrets, kiểm tra cấu hình khi khởi động; 2 API kiểm tra kết nối |
| A2 | 12 Entity, hằng số trạng thái, một `SmartCvDbContext`, 12 Fluent API Configuration, Migration `InitialCreate` khớp `database/SQL.sql` |
| A3 | Dữ liệu nền 5 Jobs, 29 JobSkills, 52 JobSkillAliases (`JobSeedData` + `HasData`) nạp tự động qua Migration `SeedJobs`; tách dữ liệu demo sang `seed_demo.sql` (282 dòng); script kiểm tra `check_jobs.sql`; unit test dữ liệu nền |
| A4 | API `POST /api/cvs/upload` nhận một hoặc nhiều file qua `multipart/form-data`, trả thông tin từng file với trạng thái `Received`; giới hạn số file và dung lượng request đọc từ `appsettings.json`; hợp đồng dùng chung `CvUploadResponse` cho giao diện Blazor. **Chưa** kiểm tra định dạng/dung lượng/file trùng (A5), **chưa** lưu file và ghi bảng `Cvs` (A6) |

### 3.2. ⏳ Dự kiến – giai đoạn 1 (chưa lập trình)

| Trang | Chức năng |
|---|---|
| 📤 **Tải CV** | Kéo thả nhiều file PDF, PNG, JPG/JPEG. Báo lỗi khi sai định dạng, file quá lớn hoặc file trùng (so mã hash SHA-256). Chọn job cho CV, bấm chấm điểm và xem tiến độ. |
| 📊 **Kết quả** | Lọc theo job, xem bảng xếp hạng có màu theo mức đánh giá. Xem chi tiết từng CV: kỹ năng đạt, kỹ năng thiếu, nhận định của AI. Hiển thị trạng thái lỗi và biểu đồ số CV theo mức. |
| 🧠 **System prompt** | Sửa prompt và lưu thành phiên bản mới (không ghi đè bản cũ). Xem lịch sử, kích hoạt một phiên bản, chạy đánh giá, so sánh tỷ lệ đúng giữa các phiên bản và xem những CV mà AI đánh giá khác HR. |

### 3.3. 💡 Đề xuất – giai đoạn 2 (cần chốt với quản lý)

| Mã | Trang / phần | Chức năng |
|---|---|---|
| M1 | Tổng quan | Số CV, số job, số CV đang chấm, phân bố mức đánh giá |
| M2 | Quản lý vị trí tuyển dụng | Thêm, sửa, ngừng tuyển job; quản lý kỹ năng và từ khóa |
| M3 | Ứng viên và kho CV | Tìm kiếm, xem file CV, xem các job đã ứng tuyển |
| M4 | Gán nhãn HR | Tạo bộ CV đánh giá, gán nhãn Đạt/Không đạt |
| M5 | Lịch sử lượt chấm | Xem lại lượt chấm, prompt đã dùng, trạng thái và lỗi |
| M6 | Đăng nhập, phân quyền | Tài khoản HR và quản trị viên (cần thêm bảng `Users`) |
| M7 | Xuất báo cáo | Xuất bảng xếp hạng ra Excel hoặc PDF |
| M8 | CV trùng, phân cụm | Làm sau cùng nếu còn thời gian |
| M9 | Triển khai | Đưa web lên máy chủ của công ty |

---

<a id="cong-nghe"></a>

## 4. 🧰 Công nghệ sử dụng

Phiên bản lấy theo các file `.csproj` và `global.json` hiện tại.

| Thành phần | Công nghệ / package | Trạng thái |
|---|---|---|
| Nền tảng | .NET SDK 10 (`global.json`: `10.0.xxx`, `rollForward: latestFeature`) | ✅ |
| Backend | ASP.NET Core Web API (`net10.0`), `Microsoft.AspNetCore.OpenApi` 10.0.14 | ✅ |
| Truy cập dữ liệu | `Microsoft.EntityFrameworkCore.SqlServer` 10.0.* (hiện phân giải 10.0.14) | ✅ |
| Migration | `Microsoft.EntityFrameworkCore.Design` 10.0.*, công cụ `dotnet-ef` | ✅ |
| Cơ sở dữ liệu | Microsoft SQL Server, database `SmartCvDb` | ✅ |
| AI | Google Gemini API (gọi qua `HttpClient`, cấu hình `GeminiOptions`) | ✅ Đã cấu hình · ⏳ Chấm điểm ở A10 |
| Kiểm thử | xUnit 2.9.3, `Microsoft.NET.Test.Sdk` 17.14.1, coverlet 6.0.4 | ✅ Đã có project |
| Giao diện | Blazor (Razor, HTML, CSS) | ⏳ B2 |
| Đọc PDF / OCR | PdfPig, Tesseract cho .NET (`eng` + `vie`) | ⏳ A7 |
| Chạy nền | BackgroundService hoặc Hangfire | ⏳ A11 |
| Biểu đồ | Chart.js | ⏳ B11 |
| Mã nguồn | Git, GitHub (nhánh + Pull Request) | ✅ |

> Trước khi cài một thư viện mới, kiểm tra giấy phép và mức độ cập nhật của thư viện đó.

---

<a id="kien-truc"></a>

## 5. 🏗️ Kiến trúc và cấu trúc thư mục

```mermaid
flowchart LR
    HR([👤 HR]) --> WEB[SmartCv.Web<br/>Blazor – dự kiến]
    WEB -->|HTTP| API[SmartCv.Api<br/>ASP.NET Core]
    API --> CORE[SmartCv.Core<br/>Entity, hằng số, quy tắc]
    API --> INFRA[SmartCv.Infrastructure]
    INFRA --> CORE
    INFRA --> DB[(SQL Server<br/>12 bảng)]
    INFRA --> FS[/Uploads/CV<br/>file gốc, tên GUID – dự kiến/]
    INFRA --> GEM[Gemini API]
```

| Project | Vai trò |
|---|---|
| `SmartCv.Api` | Điểm khởi chạy, cấu hình HTTP, controllers |
| `SmartCv.Core` | Entity, hằng số trạng thái và quy tắc nghiệp vụ. **Không phụ thuộc** project nào khác |
| `SmartCv.Infrastructure` | EF Core / SQL Server, cấu hình Gemini; sau này thêm đọc PDF/OCR, gọi Gemini |
| `SmartCv.Tests` | Kiểm thử tự động (xUnit) |
| `SmartCv.Web` | Giao diện Blazor *(tạo ở B2)* |

```text
AI-CV-Screening/
├── database/
│   ├── SQL.sql                         # Thiết kế CSDL gốc (C2) – chỉ dùng để đối chiếu
│   ├── seed_demo.sql                   # Dữ liệu demo 9 bảng (282 dòng), trọng số 30/70 – chạy tay, tùy chọn
│   ├── check_jobs.sql                  # Kiểm tra dữ liệu nền 5 job / 29 kỹ năng / 52 từ khóa
│   ├── migration_InitialCreate.sql     # Script SQL sinh từ Migration InitialCreate
│   ├── migration_SeedJobs.sql          # Script SQL sinh từ Migration SeedJobs
│   ├── update_seed_weights_30_70.sql   # Chỉ dùng cho DB đã seed bằng bản cũ 40/60
│   ├── erdplus.png                     # Sơ đồ ERD
│   └── README.md
├── src/
│   ├── SmartCv.Api/
│   │   ├── Controllers/HealthController.cs   # 2 API kiểm tra kết nối
│   │   ├── Endpoints/CvEndpoints.cs          # POST /api/cvs/upload (Minimal API)
│   │   ├── Properties/launchSettings.json
│   │   ├── CvUpload.http                     # Thử API tải CV bằng REST Client
│   │   ├── Program.cs                        # Giới hạn dung lượng request (Kestrel, FormOptions)
│   │   └── appsettings.json                  # Cấu hình KHÔNG nhạy cảm (Gemini, CvUpload)
│   ├── SmartCv.Core/
│   │   ├── Constants/StatusValues.cs         # Giá trị khớp các ràng buộc CHECK
│   │   ├── Contracts/CvUploadResponse.cs     # Hợp đồng API tải CV, dùng chung với Blazor
│   │   ├── Entities/                         # 12 Entity
│   │   ├── Options/CvUploadOptions.cs        # Giới hạn tải CV
│   │   └── Uploads/                          # CvUploadFile, ICvUploadService
│   └── SmartCv.Infrastructure/
│       ├── Data/
│       │   ├── Configurations/               # 12 Fluent API Configuration
│       │   ├── Migrations/                   # InitialCreate, SeedJobs + ModelSnapshot
│       │   ├── Seed/JobSeedData.cs           # Dữ liệu nền 5 job (HasData)
│       │   └── SmartCvDbContext.cs           # DbContext duy nhất
│       ├── Gemini/GeminiOptions.cs
│       ├── Uploads/CvUploadService.cs        # Xử lý file tải lên (A4: Received)
│       └── DependencyInjection.cs            # AddInfrastructure(): DbContext, Gemini, CvUpload
├── test-files/                               # CV GIẢ để thử API tải CV (không đặt CV thật)
├── tests/SmartCv.Tests/                      # xUnit: JobSeedDataTests, CvUploadServiceTests
├── .gitignore
├── CONTRIBUTING.md                           # Quy trình nhóm, danh sách nhánh
├── global.json
├── SmartCv.slnx
└── README.md
```

---

<a id="co-so-du-lieu"></a>

## 6. 🗃️ Cơ sở dữ liệu

Database `SmartCvDb` gồm **12 bảng nghiệp vụ**, được tạo bằng **EF Core Migration** `20261106132922_InitialCreate`. Cấu trúc migration đã được đối chiếu và khớp với thiết kế gốc [`database/SQL.sql`](database/SQL.sql). Sơ đồ: [`database/erdplus.png`](database/erdplus.png).

| Nhóm nghiệp vụ | Bảng | Mục đích |
|---|---|---|
| Vị trí tuyển dụng | `Jobs` | Vị trí, GPA và số năm kinh nghiệm tối thiểu |
| | `JobSkills` | Kỹ năng của job: `MustHave` / `NiceToHave` |
| | `JobSkillAliases` | Từ khóa tương đương của một kỹ năng |
| Ứng viên và hồ sơ | `Candidates` | Ứng viên |
| | `Cvs` | File CV, mã hash, nội dung trích xuất, trạng thái trích xuất |
| | `Applications` | CV ứng tuyển vào job |
| Prompt và đánh giá | `PromptVersions` | Các phiên bản system prompt |
| | `EvaluationSets` | Bộ CV dùng để đánh giá prompt |
| | `HrLabels` | Nhãn Đạt/Không đạt do HR gán |
| Chấm điểm | `EvaluationRuns` | Một lượt chấm (`Screening` hoặc `PromptTest`), lưu model, trọng số, ngưỡng |
| | `Evaluations` | Kết quả chấm của từng hồ sơ trong một lượt |
| | `EvaluationSkills` | Kỹ năng đạt / thiếu của từng kết quả |

**Đối tượng ràng buộc:** 12 PRIMARY KEY · 13 FOREIGN KEY (`NO ACTION`) · 6 UNIQUE · 25 CHECK · 18 DEFAULT · 8 index `IX_` · 1 filtered unique index.

**Ràng buộc quan trọng**

- Chỉ **một** prompt đang dùng tại một thời điểm: `UX_PromptVersions_OneActive` (UNIQUE có lọc `WHERE IsActive = 1`).
- UNIQUE: `Jobs(Title)`, `Cvs(FileHash)`, `Applications(CvId, JobId)`, `PromptVersions(VersionName)`, `EvaluationSets(Name)`, `Evaluations(RunId, ApplicationId)`. Trong EF Core, các cột này được cấu hình là *alternate key* nên **không sửa được** sau khi đã lưu.
- `KeywordWeight + AiWeight = 1`.
- `PromptTest` bắt buộc có `EvaluationSetId`; `Screening` bắt buộc `EvaluationSetId` là NULL.
- Prompt đã chạy thì không sửa nữa, mà lưu thành phiên bản mới.
- Ngày giờ lưu theo **UTC** (`SYSUTCDATETIME()`), khi hiển thị thì cộng 7 giờ.

**Nguyên tắc lưu trữ CV (thiết kế)**

- File gốc lưu trên máy chủ và đổi tên theo GUID, ví dụ `Uploads/CV/2026/11/<GUID>.pdf`. `CvId` do code tạo, trùng với tên file.
- SQL Server lưu tên file gốc, đường dẫn, mã hash SHA-256, nội dung trích xuất, ngày tải và trạng thái xử lý.
- Thư mục lưu file (`/Uploads/`, `src/SmartCv.Api/Uploads/`) nằm trong `.gitignore`; các thư mục mã nguồn tên `Uploads` trong `src/` vẫn được commit bình thường. **Không đưa CV thật lên GitHub.**
- Hiện chưa có cơ chế xóa CV tự động; sẽ bổ sung khi công ty có quy định.

---

<a id="cham-diem"></a>

## 7. 🧮 Quy trình xử lý CV và chấm điểm

> [!NOTE]
> Phần này là **thiết kế**. Code tải CV, trích xuất nội dung và chấm điểm chưa được lập trình. Quy tắc dưới đây đã được dùng để tạo và kiểm tra dữ liệu mẫu.

### 7.1. Luồng xử lý

```mermaid
flowchart LR
    A[HR chọn vị trí<br/>tuyển dụng] --> B[Tải lên<br/>một hoặc nhiều CV]
    B --> C[Gắn CV<br/>với job]
    C --> D[Trích xuất nội dung<br/>PdfPig / OCR]
    D --> E[Chấm điểm<br/>Keyword + Gemini]
    E --> F[Bảng xếp hạng<br/>và chi tiết CV]
    F --> G[HR ra quyết định]
```

1. HR chọn vị trí tuyển dụng và tải lên một hoặc nhiều CV. File trùng (cùng SHA-256) bị từ chối.
2. HR gắn CV với vị trí phù hợp. Một CV có thể gắn với nhiều vị trí.
3. Hệ thống trích xuất nội dung: PDF có chữ (`PdfText`), PDF scan (`PdfOcr`), ảnh (`ImageOcr`). CV không trích xuất được thì ghi `ExtractionStatus = Failed` và không chấm.
4. Một lượt chấm (`EvaluationRuns`) chạy dưới nền, ghi lại prompt, model, temperature, trọng số và ngưỡng đang dùng:
   - **KeywordScore**: tính bằng C# từ `JobSkills` và `JobSkillAliases` (A9; công thức chi tiết chốt khi triển khai).
   - **AiScore**: Gemini đọc CV và mô tả job, trả về JSON có cấu trúc (A10).
5. HR xem bảng xếp hạng, chi tiết đánh giá, rồi tự đưa ra quyết định.
6. HR gán nhãn Đạt/Không đạt cho một bộ CV để so sánh chất lượng giữa các phiên bản prompt.

### 7.2. Công thức chính thức

```text
FinalScore = ROUND(0.30 × KeywordScore + 0.70 × AiScore, 2)
```

| Tham số | Giá trị |
|---|---|
| `KeywordWeight` | **0.30** |
| `AiWeight` | **0.70** |
| `PassThreshold` (ngưỡng Đạt) | **70** – lưu riêng cho từng lượt chạy |
| `PredictedPass` | `FinalScore ≥ PassThreshold` |

| Điểm cuối | Mức đánh giá |
|:---:|---|
| ≥ 85 | 🟢 **Rất phù hợp** |
| 70 – < 85 | 🔵 **Phù hợp** |
| 55 – < 70 | 🟡 **Tiềm năng** |
| < 55 | 🔴 **Chưa phù hợp** |

**Trường hợp đặc biệt**

- **AI lỗi** (vượt hạn mức sau khi thử lại): `ScoreSource = KeywordOnly`, `AiScore = NULL`, `FinalScore = KeywordScore`. Giao diện phải **hiển thị rõ** điều này, không âm thầm dùng mỗi điểm keyword.
- **`Failed` / `Pending`**: không có điểm, không phân loại.

### 7.3. Độ chính xác của prompt (so với nhãn HR)

```text
Tỷ lệ đúng (%) = Số CV mà AI kết luận Đạt/Không đạt giống HR / Tổng số CV có nhãn × 100
```

Ngoài tỷ lệ đúng, hệ thống báo thêm:

- **Bỏ sót**: HR đánh giá Đạt nhưng AI đánh giá Không đạt.
- **Chọn nhầm**: HR đánh giá Không đạt nhưng AI đánh giá Đạt.

> [!TIP]
> Để so sánh công bằng, chỉ đổi **nội dung prompt**. Giữ nguyên bộ CV, nhãn HR, model, temperature, trọng số và ngưỡng. Chỉ số này đo mức trùng khớp với nhãn HR; nó không phải thước đo duy nhất về chất lượng tuyển dụng.

---

<a id="cai-dat"></a>

## 8. ⚙️ Cài đặt môi trường

### 8.1. Yêu cầu

- **Windows** và **.NET 10 SDK**
- **SQL Server** (Express hoặc Developer) và công cụ kết nối: SSMS, hoặc extension *SQL Server (mssql)* cho VS Code
- **sqlcmd** (đi kèm SQL Server / SSMS) để chạy script dữ liệu mẫu
- **Công cụ EF Core**: `dotnet tool install --global dotnet-ef`
- **Git**
- **VS Code** (kèm *C# Dev Kit*) hoặc Visual Studio
- **Gemini API key** lấy từ [Google AI Studio](https://aistudio.google.com)
- *(Khi làm OCR – A7)* Tesseract và dữ liệu ngôn ngữ `eng`, `vie`

```powershell
dotnet --version      # 10.0.xxx
dotnet ef --version   # 10.0.xxx
git --version
```

### 8.2. Clone, restore, build và test

```powershell
git clone https://github.com/TuyetAnh0101/AI-CV-Screening.git
cd AI-CV-Screening

dotnet restore
dotnet build
dotnet test
```

> Mọi lệnh trong README chạy từ thư mục gốc `AI-CV-Screening`.

---

<a id="cau-hinh"></a>

## 9. 🔐 Cấu hình SQL Server và User Secrets

### 9.1. Chuẩn bị SQL Server

1. Cài SQL Server và ghi lại **tên instance**, ví dụ `localhost\SQLEXPRESS`.
2. Bảo đảm tài khoản Windows đang dùng có quyền tạo database (hoặc nhờ quản trị tạo sẵn database rỗng `SmartCvDb`).

> [!WARNING]
> Bảng được tạo bằng **EF Core Migration**. **Không chạy** `database/SQL.sql` vào `SmartCvDb`, vì migration sau đó sẽ báo trùng bảng. Nếu cần một bản để đối chiếu, chạy script vào database khác tên, ví dụ `SmartCvDb_Script`.

### 9.2. User Secrets

Thông tin nhạy cảm (chuỗi kết nối, API key) được lưu bằng **User Secrets**, nằm ở `%APPDATA%\Microsoft\UserSecrets\`, tức là **ngoài repo**. `SmartCv.Api.csproj` đã có sẵn `UserSecretsId`, mỗi thành viên chỉ cần đặt giá trị trên máy của mình:

```powershell
# Chuỗi kết nối – thay TEN_SERVER bằng tên instance trên máy bạn
dotnet user-secrets set "ConnectionStrings:SmartCvDb" "Server=TEN_SERVER;Database=SmartCvDb;Trusted_Connection=True;TrustServerCertificate=True" --project src/SmartCv.Api

# Gemini API key
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project src/SmartCv.Api
```

> [!CAUTION]
> `TEN_SERVER` và `YOUR_GEMINI_API_KEY` chỉ là giá trị minh họa. **Không** để lộ key thật trong ảnh chụp terminal, Issue, Pull Request hay README, và tránh chạy `dotnet user-secrets list` khi đang chia sẻ màn hình. Nếu key đã lộ, hãy **thu hồi key trong AI Studio rồi tạo key mới**; xóa commit chứa key là chưa đủ vì lịch sử Git vẫn còn giữ nó.

Ứng dụng kiểm tra cấu hình ngay khi khởi động: thiếu `ConnectionStrings:SmartCvDb` sẽ báo lỗi cả khi chạy API lẫn khi chạy lệnh `dotnet ef`; thiếu `Gemini:ApiKey` sẽ báo lỗi khi chạy API.

### 9.3. Cấu hình không nhạy cảm

Nằm trong `src/SmartCv.Api/appsettings.json`:

```json
"Gemini": {
  "Model": "gemini-3.1-flash-lite",
  "Temperature": 0.2,
  "MaxCvCharacters": 12000,
  "MaxRetries": 2
},
"CvUpload": {
  "MaxFileSizeMB": 10,
  "MaxFilesPerRequest": 20,
  "AllowedExtensions": [ ".pdf", ".png", ".jpg", ".jpeg" ],
  "StorageRoot": "Uploads/CV"
}
```

Mỗi thành viên nên kiểm tra API key của mình có dùng được model này không bằng cách gọi `/api/health/gemini`.

Mục `CvUpload` quyết định giới hạn của API tải CV (xem [mục 12.2](#api-upload)). `MaxFilesPerRequest` có hiệu lực ngay từ A4; `MaxFileSizeMB` hiện chỉ dùng để tính giới hạn tổng của request, việc từ chối từng file quá lớn và kiểm tra `AllowedExtensions` là của A5; `StorageRoot` dùng ở A6. Có thể ghi đè tạm bằng biến môi trường, ví dụ `CvUpload__MaxFilesPerRequest=2`, mà không phải sửa file.

---

<a id="database"></a>

## 10. 🛢️ Tạo database và nạp dữ liệu

### 10.1. Hai loại dữ liệu

| Loại | Nội dung | Nạp bằng | Khi nào |
|---|---|---|---|
| **Dữ liệu nền** | 5 Jobs · 29 JobSkills · 52 JobSkillAliases | Migration `SeedJobs` – **tự động** khi chạy `dotnet ef database update` | Mọi môi trường |
| **Dữ liệu demo** | 12 Candidates · 12 Cvs · 13 Applications · 2 PromptVersions · 1 EvaluationSet · 10 HrLabels · 4 EvaluationRuns · 36 Evaluations · 192 EvaluationSkills (282 dòng) | `database/seed_demo.sql` – **chạy tay**, tùy chọn | Khi cần demo, chụp màn hình báo cáo |

Tổng khi có cả hai: 86 + 282 = **368 dòng**. 12 CV demo chỉ có bản ghi trong database, không có file thật trong `Uploads/`.

**5 job được nạp sẵn**

| JobId | Vị trí | GPA tối thiểu | Kinh nghiệm tối thiểu (năm) | MustHave | NiceToHave | Từ khóa |
|:---:|---|:---:|:---:|:---:|:---:|:---:|
| 1 | Backend Developer (.NET) | 2.50 | 0.5 | 3 | 3 | 11 |
| 2 | Frontend Developer (Blazor) | 2.50 | 0.0 | 3 | 3 | 11 |
| 3 | AI Engineer Intern | 3.00 | 0.0 | 3 | 3 | 9 |
| 4 | Tester (QA) | – | 0.0 | 3 | 2 | 9 |
| 5 | Business Analyst | 2.80 | 1.0 | 3 | 3 | 12 |

### 10.2. Thành viên mới: thiết lập database

```powershell
# 1. Tạo database, 12 bảng và dữ liệu nền (InitialCreate + SeedJobs)
dotnet ef database update --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --context SmartCv.Infrastructure.Data.SmartCvDbContext

# 2. (Tùy chọn) Nạp dữ liệu demo
sqlcmd -S TEN_SERVER -d SmartCvDb -E -C -b -I -f 65001 -i database/seed_demo.sql
```

| Tham số `sqlcmd` | Ý nghĩa |
|---|---|
| `-E` | Windows Authentication |
| `-C` | Tin chứng chỉ máy chủ (môi trường phát triển) |
| `-b` | Dừng và trả mã lỗi khi có lỗi |
| `-I` | Bật `QUOTED_IDENTIFIER` – cần cho bảng có filtered index (script cũng tự bật) |
| `-f 65001` | Đọc file UTF-8 để giữ đúng tiếng Việt |

- Chỉ bước 1 là đủ để có 5 job, 29 kỹ năng và 52 từ khóa; **không cần chạy file SQL nào**.
- `seed_demo.sql` chèn 282 dòng trong một transaction; lỗi thì ROLLBACK toàn bộ.
- `seed_demo.sql` **dừng và không chèn gì** khi chưa có 5 job nền (chưa chạy bước 1) hoặc khi bất kỳ bảng demo nào đã có dữ liệu. **Không chạy lại** trên database đã có dữ liệu demo.
- Script SQL tương đương của từng migration: [`database/migration_InitialCreate.sql`](database/migration_InitialCreate.sql), [`database/migration_SeedJobs.sql`](database/migration_SeedJobs.sql).

> Không xóa, sửa hay tạo lại các migration đã áp dụng (`InitialCreate`, `SeedJobs`). Thay đổi schema hoặc dữ liệu nền sau này phải thêm migration mới và báo cả nhóm.

### 10.3. Máy đã có dữ liệu từ A2

Database tạo ở A2 và đã chạy `seed.sql` cũ (368 dòng) **chỉ cần chạy lại bước 1**:

```powershell
dotnet ef database update --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --context SmartCv.Infrastructure.Data.SmartCvDbContext
```

Migration `SeedJobs` được viết để chạy an toàn trên database đã có dữ liệu:

- Dòng nền đã có và **đúng nội dung** thì giữ nguyên (kể cả `CreatedAt` cũ); chỉ chèn phần còn thiếu.
- Dòng **trùng khóa nhưng khác nội dung** (ví dụ job bị sửa tay): migration dừng với lỗi `SeedJobs: ...`, ROLLBACK, không ghi đè. Báo nhóm để xử lý, **không** tự xóa dữ liệu.
- Không cần xóa database, không chạy lại `seed_demo.sql`, không sửa tay bảng `__EFMigrationsHistory`.
- Rollback (`dotnet ef database update InitialCreate`) chỉ gỡ dữ liệu nền khi chưa có hồ sơ/lượt chấm nào dùng tới; ngược lại sẽ báo lỗi và không xóa gì.
- Database đã seed bằng **bản cũ 40/60**: chạy thêm một lần [`database/update_seed_weights_30_70.sql`](database/update_seed_weights_30_70.sql) (cùng tham số `sqlcmd`). Script kiểm tra trước, chạy trong transaction và chạy lại không làm thay đổi gì thêm.

### 10.4. Sửa dữ liệu nền về sau

1. Sửa trong [`src/SmartCv.Infrastructure/Data/Seed/JobSeedData.cs`](src/SmartCv.Infrastructure/Data/Seed/JobSeedData.cs). Chỉ dùng giá trị cố định, **không** dùng `DateTime.Now`, `DateTime.UtcNow`, `Guid.NewGuid()`.
2. Tạo migration mới, ví dụ `dotnet ef migrations add UpdateJobSkills ...`. Kiểm tra file sinh ra chỉ có `InsertData` / `UpdateData` / `DeleteData` đúng phần đã sửa; nếu cần chạy trên database đã có dữ liệu, xử lý an toàn như `SeedJobs`.
3. **Không** sửa job/kỹ năng bằng tay trong SSMS (code và database sẽ lệch nhau) và **không** sửa migration `SeedJobs`.
4. Xóa một kỹ năng đã được dùng trong `EvaluationSkills` sẽ bị khóa ngoại chặn – đây là chủ ý, để không mất lịch sử chấm.

### 10.5. Kiểm tra database và Migration

```powershell
# Có InitialCreate và SeedJobs, không còn chữ "Pending"
dotnet ef migrations list --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --context SmartCv.Infrastructure.Data.SmartCvDbContext

# Model và migration đồng bộ
dotnet ef migrations has-pending-model-changes --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --context SmartCv.Infrastructure.Data.SmartCvDbContext

# Dữ liệu nền: số dòng, MustHave/NiceToHave từng job, kỹ năng thiếu từ khóa, tiếng Việt
sqlcmd -S TEN_SERVER -E -C -I -f 65001 -i database/check_jobs.sql
```

```sql
-- Lịch sử migration: 20261106132922_InitialCreate, 20261106142155_SeedJobs
SELECT * FROM __EFMigrationsHistory;

-- Số ràng buộc: PK 12, FK 13, UNIQUE 6, CHECK 25, DEFAULT 18
SELECT type_desc, COUNT(*) AS SoLuong
FROM sys.objects
WHERE type IN ('PK', 'F', 'UQ', 'C', 'D')
  AND OBJECT_NAME(parent_object_id) <> '__EFMigrationsHistory'
GROUP BY type_desc;

-- Số dòng từng bảng (86 nếu chỉ có dữ liệu nền, 368 nếu có thêm dữ liệu demo)
SELECT t.name AS Bang, SUM(p.rows) AS SoDong
FROM sys.tables t
JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.name <> '__EFMigrationsHistory'
GROUP BY t.name ORDER BY t.name;
```

---

<a id="chay-du-an"></a>

## 11. 🚀 Build, chạy và kiểm thử

```powershell
dotnet build
dotnet run --project src/SmartCv.Api --launch-profile https
```

Theo `launchSettings.json`, API chạy tại `https://localhost:7269` và `http://localhost:5156` (môi trường `Development`). Luôn lấy URL mà terminal in ra ở dòng `Now listening on:`.

Nếu Windows chưa tin chứng chỉ HTTPS dùng khi phát triển: `dotnet dev-certs https --trust`. Muốn dừng API, nhấn `Ctrl + C`.

```powershell
dotnet test
```

> - `JobSeedDataTests` kiểm tra dữ liệu nền (số dòng, khóa, quan hệ, số kỹ năng/từ khóa từng job, ngày seed cố định, tiếng Việt) và thứ tự migration, không cần kết nối SQL Server.
> - `CvUploadServiceTests` kiểm tra service tải CV ở mức A4: một/nhiều file, bỏ đường dẫn trong tên file, trạng thái `Received`, số lượng, hủy request, giới hạn mặc định. Khi làm A5, A6 cần cập nhật các test ghi chú "A4".
> - Unit test cho phần tính điểm và tỷ lệ đúng được viết ở A18.

---

<a id="api"></a>

## 12. 🔌 Danh sách API

### 12.1. ✅ API kiểm tra kết nối (A1 – chỉ chạy ở Development, môi trường khác trả `404`)

| Phương thức | Endpoint | Mô tả |
|:---:|---|---|
| `GET` | `/api/health/database` | Mở kết nối tới SQL Server, trả tên server và database |
| `GET` | `/api/health/gemini` | Kiểm tra API key và model Gemini (chỉ hỏi thông tin model, không tốn lượt sinh nội dung); key trả về đã được che |

Kết quả mong đợi (`503` kèm thông báo lỗi nếu không kết nối được):

```json
{ "status": "OK", "server": "TEN_SERVER", "database": "SmartCvDb" }
{ "status": "OK", "model": "gemini-3.1-flash-lite", "key": "AIza...xxxx" }
```

Khi chạy ở Development còn có tài liệu OpenAPI tại `/openapi/v1.json` (đã có `/api/cvs/upload` với các mã 200, 400, 413).

<a id="api-upload"></a>

### 12.2. ✅ API tải CV – `POST /api/cvs/upload` (A4)

Nhận **một hoặc nhiều** file CV trong một request. Ở A4 API **chỉ nhận file và trả thông tin từng file**: chưa kiểm tra định dạng, chưa lưu file, chưa ghi database.

**Request**

```text
POST /api/cvs/upload
Content-Type: multipart/form-data
Mỗi file là một phần có name="files" (nhiều file thì lặp lại phần "files")
```

**Giới hạn** (đọc từ mục `CvUpload` trong `appsettings.json`)

| Giới hạn | Giá trị mặc định | Khi vượt |
|---|---|---|
| Số file mỗi request (`MaxFilesPerRequest`) | 20 | `400` "Quá nhiều file" |
| Dung lượng cả request = `MaxFileSizeMB` × `MaxFilesPerRequest` + 1 MiB | 10 × 20 + 1 = **201 MiB** | `413 Payload Too Large`, do máy chủ Kestrel trả, không có nội dung |
| Dung lượng từng file (`MaxFileSizeMB`) | 10 MiB | ⏳ A5 mới từ chối (`TooLarge`) |
| Đuôi file (`AllowedExtensions`) | `.pdf`, `.png`, `.jpg`, `.jpeg` | ⏳ A5 mới từ chối (`InvalidFormat`) |

> Giới hạn 201 MiB được đặt cho Kestrel nên áp dụng cho **mọi** endpoint của API. Nếu sau này chạy sau IIS, Nginx hoặc proxy khác, phải cấu hình thêm giới hạn của proxy đó (ví dụ IIS mặc định chỉ nhận khoảng 28,6 MB).

**Response 200** (JSON, tên trường viết thường chữ đầu) – ví dụ thực tế khi gửi 3 file:

```json
{
  "totalFiles": 3,
  "successCount": 3,
  "failedCount": 0,
  "files": [
    { "fileName": "cv-mau-1.pdf", "sizeBytes": 1421,  "contentType": "application/pdf", "status": "Received", "cvId": null, "errorCode": null, "errorMessage": null },
    { "fileName": "cv-mau-2.png", "sizeBytes": 8625,  "contentType": "image/png",       "status": "Received", "cvId": null, "errorCode": null, "errorMessage": null },
    { "fileName": "cv-mau-4.jpg", "sizeBytes": 17619, "contentType": "image/jpeg",      "status": "Received", "cvId": null, "errorCode": null, "errorMessage": null }
  ]
}
```

- `fileName`: tên file gốc **đã bỏ phần đường dẫn** client gửi kèm (ví dụ `C:\Users\...\cv.pdf` → `cv.pdf`). Không dùng tên này để lưu file.
- `contentType`: do client khai báo, chưa được kiểm tra.
- `successCount`: số file **chưa bị từ chối**. Ở A4 không có nghĩa là file đã được lưu.
- Từ A5, một request có thể vừa có file hợp lệ vừa có file bị từ chối; API vẫn trả **200**, mỗi file tự mang `status` riêng.

**Trạng thái từng file** (`SmartCv.Core.Contracts.CvUploadStatuses`)

| `status` | Ý nghĩa | Từ khi nào |
|---|---|---|
| `Received` | Đã nhận, **chưa** kiểm tra, **chưa** lưu. `cvId`, `errorCode`, `errorMessage` đều `null` | ✅ A4 (hiện tại mọi file đều ở trạng thái này) |
| `Accepted` | Hợp lệ và đã lưu file + bảng `Cvs`; `cvId` có giá trị | ⏳ A5 – A6 |
| `Rejected` | Bị từ chối; xem `errorCode`: `EmptyFile`, `InvalidFormat`, `TooLarge`, `Duplicate` (`CvUploadErrorCodes`) | ⏳ A5 |

**Response lỗi**

| Mã | Khi nào | Nội dung |
|:---:|---|---|
| `400` | Không có file nào trong trường `files` | ProblemDetails, `"title": "Chưa chọn file"` |
| `400` | Quá `MaxFilesPerRequest` file | ProblemDetails, `"title": "Quá nhiều file"` |
| `413` | Request vượt giới hạn dung lượng tổng | Không có nội dung (Kestrel chặn trước khi chạy code) |
| `415` | Request không phải `multipart/form-data` | Không có nội dung |

```json
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1", "title": "Chưa chọn file", "status": 400,
  "detail": "Hãy gửi ít nhất một file CV trong trường \"files\" (multipart/form-data)." }
```

**Thử bằng `curl.exe`** (chạy API trước, đứng ở gốc repo; file CV giả nằm trong `test-files/`)

> Trong PowerShell phải gõ **`curl.exe`**, vì `curl` là tên tắt của `Invoke-WebRequest`. `-k` bỏ qua kiểm tra chứng chỉ HTTPS trên localhost, `-F "files=@..."` gửi một file (lặp lại cho nhiều file), `-i` in cả dòng trạng thái.

```powershell
# Một file
curl.exe -k -F "files=@test-files/cv-mau-1.pdf" https://localhost:7269/api/cvs/upload

# Nhiều file
curl.exe -k -F "files=@test-files/cv-mau-1.pdf" -F "files=@test-files/cv-mau-2.png" -F "files=@test-files/cv-mau-4.jpg" https://localhost:7269/api/cvs/upload

# Không có file -> 400
curl.exe -k -i -F "note=khong co file" https://localhost:7269/api/cvs/upload
```

Hoặc mở [`src/SmartCv.Api/CvUpload.http`](src/SmartCv.Api/CvUpload.http) bằng extension **REST Client** (Huachao Mao) và bấm **Send Request**.

**Ghi chú bảo mật:** endpoint đang tắt kiểm tra antiforgery (`.DisableAntiforgery()`) và **chưa có xác thực**. Điều này chấp nhận được ở giai đoạn hiện tại vì API không dùng cookie đăng nhập (không có phiên để kẻ xấu lợi dụng qua CSRF), và được giao diện Blazor gọi từ phía máy chủ. Khi làm đăng nhập (M6) bằng cookie phải xem lại: bật antiforgery hoặc dùng token trong header, và yêu cầu xác thực cho endpoint này.

#### Tích hợp giao diện Blazor (B3, B5)

- Dùng chung class `SmartCv.Core.Contracts.CvUploadResponse` / `CvUploadItemResult` (project Web tham chiếu `SmartCv.Core`), không tự định nghĩa lại để tránh lệch tên trường.
- Mỗi file thêm vào `MultipartFormDataContent` với tên phần **`files`**: `content.Add(part, "files", file.Name)`.
- `IBrowserFile.OpenReadStream()` mặc định chỉ cho đọc **500 KB**: phải truyền `maxAllowedSize` bằng giới hạn API (10 MiB = `10 * 1024 * 1024`).
- Nên lọc trước ở giao diện: tối đa 20 file, mỗi file ≤ 10 MiB, `accept=".pdf,.png,.jpg,.jpeg"`; API vẫn là nơi kiểm tra cuối cùng (A5).
- Hiển thị theo `status` của từng file; từ A5 dựa vào `errorCode` để hiện thông báo tiếng Việt.

### 12.3. ⏳ Dự kiến

> Danh sách này là định hướng thiết kế, **chưa được chốt** (C2) và có thể điều chỉnh khi lập trình.

| Phương thức | Endpoint | Mô tả | Mã việc |
|:---:|---|---|:---:|
| `POST` | `/api/cvs/upload` (bổ sung) | Kiểm tra định dạng, dung lượng, file trùng; lưu file và ghi bảng `Cvs` | A5, A6 |
| `GET` | `/api/jobs` | Danh sách job | – |
| `POST` | `/api/applications` | Gắn CV với job | A8 |
| `POST` | `/api/scoring/start` | Bắt đầu chấm điểm | A11 |
| `GET` | `/api/scoring/progress` | Xem tiến độ chấm | A13 |
| `GET` | `/api/jobs/{id}/ranking` | Bảng xếp hạng theo job | A13 |
| `GET` | `/api/applications/{id}` | Chi tiết kết quả của một hồ sơ | A13 |
| `GET` `POST` | `/api/prompts` | Xem hoặc tạo phiên bản prompt | A14 |
| `POST` | `/api/prompts/{id}/activate` | Kích hoạt phiên bản prompt | A14 |
| `POST` | `/api/hr-labels` | Nhập nhãn HR | A15 |
| `POST` | `/api/evaluations/run` | Chạy đánh giá prompt | A16 |
| `GET` | `/api/evaluations/compare` | So sánh hai phiên bản prompt | A17 |

---

<a id="nghiem-thu-a2"></a>

## 13. ✅ Kết quả nghiệm thu

### 13.1. A2 – Entity, Migration InitialCreate, dữ liệu mẫu

Kiểm tra trực tiếp trên SQL Server sau khi chạy migration và dữ liệu mẫu.

| Hạng mục | Kết quả |
|---|---|
| Mã nguồn | 12 Entity · 1 `SmartCvDbContext` (`SmartCv.Infrastructure.Data`) · 12 Configuration · đã bỏ DbContext trùng trong `Persistence` |
| Migration | `20261106132922_InitialCreate` đã áp dụng; không còn pending model changes |
| Schema | 12 bảng + `__EFMigrationsHistory`; 12 PK, 13 FK, 6 UNIQUE, 25 CHECK, 18 DEFAULT, 8 `IX_`, 1 `UX_`; tên, kiểu dữ liệu, độ dài, NULL/NOT NULL khớp `SQL.sql` |
| Ràng buộc | CHECK, FK, UNIQUE đều bật và được tin cậy; thử vi phạm (2 prompt active, trùng FileHash, tổng trọng số ≠ 1, sai quy tắc `EvaluationSetId`, FK không tồn tại) đều bị chặn; `DBCC CHECKCONSTRAINTS` không có vi phạm |
| Dữ liệu mẫu | 368 dòng – xem bảng dưới |
| Chấm điểm | 4/4 lượt chấm dùng 0.30/0.70; 32/32 kết quả `Combined` đúng công thức, FitLabel và PredictedPass; `KeywordOnly` giữ nguyên 56.67; `Failed`/`Pending` không có điểm |
| So sánh prompt | `v1-co-ban` **70%** · `v2-tieu-chi-ro-rang` **80%** (tính lại từ `HrLabels`, khớp giá trị lưu) |
| Build / Test | `dotnet build` 0 lỗi, 0 cảnh báo · `dotnet test` đạt 1/1 |

| Bảng | Dòng | Bảng | Dòng | Bảng | Dòng |
|---|---:|---|---:|---|---:|
| Jobs | 5 | Cvs | 12 | HrLabels | 10 |
| JobSkills | 29 | Applications | 13 | EvaluationRuns | 4 |
| JobSkillAliases | 52 | PromptVersions | 2 | Evaluations | 36 |
| Candidates | 12 | EvaluationSets | 1 | EvaluationSkills | 192 |

<a id="nghiem-thu-a3"></a>

### 13.2. A3 – Dữ liệu nền qua Migration SeedJobs

| Hạng mục | Kết quả |
|---|---|
| Mã nguồn | `Data/Seed/JobSeedData.cs` (5 / 29 / 52, ngày seed cố định 2026-11-01 UTC); `HasData` trong 3 Configuration, giữ nguyên toàn bộ cấu hình A2 |
| Migration | `20261106142155_SeedJobs` chỉ chứa dữ liệu, **không** thay đổi schema; snapshot chứa `HasData`; không còn pending model changes |
| Database mới | Chỉ chạy `dotnet ef database update` (không chạy file SQL nào): 2 migration, Jobs 5, JobSkills 29, JobSkillAliases 52; mỗi job 3 MustHave, NiceToHave 3 (Tester 2); từ khóa 11/11/9/9/12; không có kỹ năng thiếu từ khóa; tiếng Việt đúng. Chạy thêm `seed_demo.sql` → 368 dòng, v1 70%, v2 80% |
| Database A2 có sẵn 368 dòng | `SeedJobs` áp dụng bình thường; dữ liệu 12 bảng **giống hệt từng byte** trước và sau; vẫn 368 dòng, 0.30/0.70, v1 70%, v2 80%; `DBCC CHECKCONSTRAINTS` không vi phạm |
| An toàn | Dòng trùng khóa nhưng khác nội dung → migration dừng và ROLLBACK; rollback bị chặn khi dữ liệu nền đang được dùng; chạy `database update` lần hai: *No migrations were applied* |
| Build / Test | `dotnet build` 0 lỗi, 0 cảnh báo · `dotnet test` đạt 10/10 (thêm 9 test dữ liệu nền và migration) |

<a id="nghiem-thu-a4"></a>

### 13.3. A4 – API `POST /api/cvs/upload`

Thử thật bằng `curl.exe` trên `https://localhost:7269` (profile `https`), dùng CV giả trong `test-files/`:

| # | Trường hợp | Mong đợi | Thực tế |
|:---:|---|---|---|
| 1 | 1 file PDF | 200, `totalFiles` = 1, `Received` | ✅ 200, `totalFiles` 1, `cv-mau-1.pdf`, 1421 byte, `application/pdf`, `Received` |
| 2 | PDF + PNG + JPG | 200, `totalFiles` = 3 | ✅ 200, 3 file, `application/pdf` / `image/png` / `image/jpeg` |
| 3 | Không có file | 400 "Chưa chọn file" | ✅ 400 ProblemDetails "Chưa chọn file" |
| 4 | File `.txt` | 200, `Received` (A5 mới từ chối) | ✅ 200, `text/plain`, `Received` |
| 5 | 3 file khi giới hạn tạm = 2 (biến môi trường `CvUpload__MaxFilesPerRequest=2`) | 400 "Quá nhiều file" | ✅ 400 "Mỗi lần chỉ được tải tối đa 2 file, bạn đã gửi 3 file."; gửi 2 file vẫn 200; `appsettings.json` giữ nguyên 20 |
| 6 | 1 file giả 250 MB | 413 | ✅ 413 ngay khi curl gửi header (`Expect: 100-continue`, 0 byte nội dung). Khi client gửi luôn nội dung (không `Expect`), máy chủ trả 413 sau khoảng 1,4 MB rồi đóng kết nối. API vẫn hoạt động bình thường sau đó; file thử đã xóa |

Kiểm tra thêm: tên file kèm đường dẫn `C:\Users\hr\Desktop\cv-mau-3.pdf` → `cv-mau-3.pdf`; request JSON → 415; `/api/health/database`, `/api/health/gemini` vẫn 200; OpenAPI có `/api/cvs/upload` (200, 400, 413); không tạo thư mục `Uploads`, bảng `Cvs` không đổi (database vẫn 368 dòng, 2 migration). `dotnet build` 0 lỗi, 0 cảnh báo · `dotnet test` đạt **22/22** (thêm 12 test `CvUploadServiceTests`).

---

<a id="lo-trinh"></a>

## 14. 🗺️ Lộ trình phát triển

| Thành viên | Vai trò | Phạm vi |
|---|---|---|
| Bạn A | Backend và AI | A1 – A18 |
| Bạn B | Giao diện và kiểm thử | B1 – B18 |
| Cả nhóm | Phối hợp | C1 – C5 |

### Thứ tự thực hiện giai đoạn 1

| Bước | Nội dung | Mã việc | Trạng thái |
|:---:|---|---|---|
| 1 | Thiết kế | C1, C2, C3, B1 | C2 (CSDL) đã có trong `database/` |
| 2 | Dựng nền | A1 – A3, B2 | ✅ A1, A2, A3 · ⏳ B2 |
| 3 | Trang tải CV | A4 – A8, B3 – B5 | ✅ A4 · ⏳ A5 – A8, B3 – B5 |
| 4 | Trang kết quả | A9 – A13, B6 – B10 | ⏳ |
| 5 | Trang prompt | A14 – A17, B12 – B16 | ⏳ |
| 6 | Hoàn thiện | C4, A18, B11, B17, B18, C5 | ⏳ |

### Việc tiếp theo của Backend

| Mã | Nội dung (theo [CONTRIBUTING.md](CONTRIBUTING.md)) |
|---|---|
| **A5** | Trong `CvUploadService` (chỗ `TODO A5`): từ chối file rỗng, sai đuôi, sai nội dung thật (magic bytes), quá `MaxFileSizeMB`, trùng SHA-256 → `Rejected` + `errorCode` |
| **A6** | Trong `CvUploadService` (chỗ `TODO A6`): lưu `Uploads/CV/yyyy/MM/<GUID>.ext`, ghi bảng `Cvs`, trả `Accepted` + `cvId` |
| A7 | Đọc chữ bằng PdfPig, OCR `eng+vie` |
| A8 | API gắn CV với job |
| A9 | Chấm keyword bằng C#, xử lý thiếu GPA hoặc kinh nghiệm |
| A10 | Gọi Gemini, JSON có cấu trúc, thử lại, xử lý vượt hạn mức |
| A11, A12 | Chấm điểm dưới nền, lưu điểm kèm phiên bản prompt |
| A13 | API tiến độ, bảng xếp hạng, chi tiết CV |
| A14 – A17 | API phiên bản prompt, nhãn HR, chạy đánh giá, so sánh prompt |
| A18 | Unit test tính điểm và tỷ lệ đúng |

> Cập nhật tên thành viên và tiến độ vào các bảng trên khi nhóm đã thống nhất chính thức.

---

<a id="git"></a>

## 15. 🌿 Quy trình làm việc với Git

- 🚫 **Không push trực tiếp** vào `main` (nhánh đã được bảo vệ).
- 🌱 Mỗi việc làm trên một nhánh riêng tạo từ `main`: `feature/<mã-việc>-<mô-tả>`, ví dụ `feature/A2-entities-migration`, `feature/A4-upload-cv`. Sửa lỗi: `fix/<mô-tả>`; tài liệu: `docs/<mô-tả>`.
- ✍️ Commit theo mẫu `loai(pham-vi): mo ta ngan`, với `loai` là `feat`, `fix`, `docs`, `test`, `refactor`, `style`. Ví dụ: `feat(A2): complete entities, EF Core migration and database seed`.
- 👀 Mỗi Pull Request phải được **thành viên còn lại review và Approve** trước khi merge; merge xong thì xóa nhánh.

```powershell
# 1. Tạo nhánh từ main mới nhất
git switch main
git pull origin main
git switch -c feature/<mã-việc>-<mô-tả>

# 2. Chỉ stage đúng các file của công việc, rồi xem lại
git status
git add <đường-dẫn-file-hoặc-thư-mục>
git diff --cached --name-only    # danh sách file sẽ commit
git diff --cached                # nội dung: không có API key, mật khẩu, chuỗi kết nối

# 3. Commit và đẩy lên GitHub
git commit -m "feat(A4): mô tả ngắn gọn"
git push -u origin feature/<mã-việc>-<mô-tả>
```

Sau đó tạo Pull Request vào `main` trên GitHub, ghi rõ đã thay đổi gì và cách kiểm tra. Quy định chi tiết, danh sách nhánh và tiêu chí review xem tại [CONTRIBUTING.md](CONTRIBUTING.md).

---

<a id="loi-thuong-gap"></a>

## 16. 🛠️ Xử lý lỗi thường gặp và bảo mật

| Hiện tượng | Cách kiểm tra / xử lý |
|---|---|
| `A network-related or instance-specific error` | Kiểm tra tên instance. Mở `services.msc` xem dịch vụ **SQL Server (…)** có đang chạy không. |
| `Cannot open database "SmartCvDb"` | Database chưa được tạo (chạy `dotnet ef database update`), hoặc tài khoản không có quyền. |
| `The certificate chain was issued by an authority that is not trusted` | Thêm `TrustServerCertificate=True` vào chuỗi kết nối, hoặc `-C` với `sqlcmd`. |
| `There is already an object named ...` khi chạy migration | Bảng đã được tạo thủ công bằng `SQL.sql`. Báo nhóm trước khi xử lý; không tự ý xóa database đang dùng chung. |
| `Msg 1934 ... 'QUOTED_IDENTIFIER'` khi chạy script | Thêm `-I` vào lệnh `sqlcmd`. |
| Tiếng Việt bị lỗi font sau khi seed | Thêm `-f 65001` để `sqlcmd` đọc file UTF-8. |
| `CSDL đã có dữ liệu demo ..., bỏ qua seed_demo.sql.` | Database đã có dữ liệu demo, không cần chạy lại. |
| `Chưa có 5 job nền. Hãy chạy "dotnet ef database update" trước.` | Chạy migration trước khi nạp dữ liệu demo, hoặc kiểm tra đang kết nối đúng database. |
| `SeedJobs: ... khác dữ liệu nền` khi `database update` | Job/kỹ năng trong database đã bị sửa tay. Migration đã ROLLBACK, không mất gì. Báo nhóm để thống nhất dữ liệu; không tự xóa database. |
| `Violation of PRIMARY KEY constraint 'PK_Jobs'` | Đang dùng migration cũ không có kiểm tra an toàn. Lấy code mới nhất từ `main` rồi chạy lại. |
| `InvalidOperationException` về `ConnectionStrings:SmartCvDb` hoặc `OptionsValidationException` khi khởi động | Chưa đặt User Secrets hoặc gõ sai tên khóa. Tên đúng là `ConnectionStrings:SmartCvDb` và `Gemini:ApiKey`. |
| Gemini trả về `400` / `403` | API key sai, đã bị thu hồi, hoặc dự án Google chưa bật Gemini API. |
| Gemini trả về `404` | Tên model không còn đúng. Kiểm tra trên AI Studio rồi sửa `Gemini:Model`. |
| Lỗi chứng chỉ HTTPS | Chạy `dotnet dev-certs https --trust`. |
| Tải CV trả `404` | Sai đường dẫn (đúng là `/api/cvs/upload`, có chữ `s`) hoặc thiếu `app.MapCvEndpoints()` trong `Program.cs`. |
| Tải CV trả `415` hoặc `400` *Expected a supported form media type* | Request không phải `multipart/form-data`. Với curl phải dùng `-F`, không dùng `-d`. |
| Tải CV trả `413` với file nhỏ | Giới hạn trong `Program.cs` (Kestrel, `FormOptions`) bị thiếu, hoặc proxy phía trước (IIS, Nginx) có giới hạn thấp hơn. |
| Tải CV trả `400` "Chưa chọn file" dù đã gửi | Tên phần multipart không phải `files`; trong REST Client thì sai `boundary` hoặc sai đường dẫn `< ../../test-files/...`. |
| `Invoke-WebRequest: A parameter cannot be found that matches parameter name 'F'` | Đã gõ `curl` thay vì `curl.exe` trong PowerShell. |
| Blazor báo *exceeds the maximum of 512000 bytes* | Chưa truyền `maxAllowedSize` cho `IBrowserFile.OpenReadStream()` (xem [mục 12.2](#api-upload)). |
| Cảnh báo `NU1902` / `NU1903` khi build | Package có lỗ hổng bảo mật. Nâng lên bản đã vá; **không tắt cảnh báo** để che lỗi. |

### 🔒 Bảo mật và dữ liệu

- Không commit Gemini API key, mật khẩu, chuỗi kết nối chứa bí mật, file `.env` hoặc User Secrets.
- Không commit thư mục build (`bin/`, `obj/`), file backup database (`*.bak`) hay CV thật trong `Uploads/`.
- Luôn xem lại `git diff --cached` trước khi commit.
- Chỉ dùng CV mà nhóm có quyền xử lý. Dữ liệu demo trong `seed_demo.sql` là dữ liệu giả lập.
- Khi thay đổi cấu hình chung, schema hoặc API contract, báo cho cả nhóm và cập nhật tài liệu.

---

<div align="center">

**SmartCv – AI CV Screening** · Built with .NET & Gemini

*Tài liệu được cập nhật theo tiến độ phát triển của nhóm.*

</div>
