div align="center">AI CV SCREENING
Hệ thống sàng lọc và xếp hạng CV bằng AI
Quản lý hồ sơ · Đánh giá ứng viên · Xếp hạng theo vị trí · So sánh phiên bản Prompt
Dự án hỗ trợ bộ phận nhân sự quản lý hồ sơ ứng viên, gắn CV với vị trí tuyển dụng,
đánh giá mức độ phù hợp bằng từ khóa kết hợp Google Gemini API và so sánh
hiệu quả của các phiên bản prompt dựa trên nhãn đánh giá của HR.
Chức năng · Công nghệ · Cài đặt · Chạy dự án · Đóng góp
</div>[!NOTE]
Trạng thái dự án: Backend đã có cấu hình kết nối SQL Server và Gemini cùng API kiểm tra kết nối. Các chức năng nghiệp vụ khác trong tài liệu được mô tả theo thiết kế/dự kiến, không đồng nghĩa đã triển khai xong.

📌 Mục lục
1. Tổng quan
2. Chức năng chính
3. Công nghệ sử dụng
4. Kiến trúc và cấu trúc thư mục
5. Dữ liệu và lưu trữ CV
6. Danh sách API
7. Cài đặt môi trường
8. Cấu hình SQL Server và Gemini
9. Build, chạy và kiểm thử
10. Quy trình làm việc với GitHub
11. Phân công công việc
12. Lưu ý và xử lý lỗi thường gặp
1. 🎯 Tổng quan
AI CV Screening được xây dựng nhằm hỗ trợ quy trình tuyển dụng khi cần xử lý nhiều hồ sơ ứng viên. Hệ thống cho phép quản lý CV và vị trí tuyển dụng, chấm điểm theo tiêu chí của từng vị trí, trình bày kết quả để HR tham khảo và đánh giá chất lượng prompt AI.
[!IMPORTANT]
AI chỉ hỗ trợ sàng lọc. Quyết định tuyển dụng cuối cùng thuộc về HR.

Luồng nghiệp vụ dự kiến:
1. HR tạo vị trí tuyển dụng và thiết lập tiêu chí đánh giá.
2. HR tải lên một hoặc nhiều CV.
3. HR gắn CV với vị trí tuyển dụng phù hợp (một CV có thể gắn với nhiều vị trí).
4. Hệ thống trích xuất nội dung và chấm điểm bằng từ khóa kết hợp Gemini API.
5. HR xem bảng xếp hạng, chi tiết đánh giá và đưa ra quyết định.
6. HR có thể gán nhãn chuẩn để so sánh chất lượng giữa các phiên bản prompt.
2. ✨ Chức năng chính
Các chức năng dưới đây mô tả phạm vi sản phẩm; không đồng nghĩa tất cả đã được lập trình hoàn tất.

Nhóm chức năng	Mô tả
Quản lý vị trí tuyển dụng	Tạo, xem và quản lý job cùng các tiêu chí đánh giá.
Tải CV	Tải một hoặc nhiều file PDF, PNG, JPG/JPEG; kiểm tra định dạng, dung lượng và phát hiện file trùng bằng mã hash.
Trích xuất nội dung	Đọc PDF có lớp văn bản bằng PdfPig; sử dụng Tesseract OCR (eng+vie) đối với ảnh và bản scan.
Gắn CV với job	Liên kết CV với một hoặc nhiều vị trí trước khi chấm điểm.
Chấm điểm	Kết hợp đối sánh từ khóa và Gemini API; nhận kết quả AI dưới dạng JSON có cấu trúc.
Theo dõi xử lý	Xử lý tác vụ chấm điểm dưới nền và hiển thị trạng thái/tiến độ.
Kết quả và xếp hạng	Hiển thị thứ hạng, điểm, mức đánh giá, kỹ năng đáp ứng, kỹ năng còn thiếu và nhận định AI theo từng job.
Quản lý system prompt	Tạo và lưu các phiên bản prompt, không ghi đè lịch sử; chọn phiên bản đang sử dụng.
Đánh giá prompt	HR gán nhãn chuẩn; chạy các phiên bản prompt trên cùng bộ CV để so sánh tỷ lệ trùng khớp.


Mức đánh giá
Rất phù hợp · Phù hợp · Tiềm năng · Chưa phù hợp
Cách tính tỷ lệ trùng khớp với nhãn HR
Tỷ lệ trùng khớp (%) = (Số CV có mức đánh giá AI trùng nhãn HR / Tổng số CV được đánh giá) × 100
Ví dụ, với 50 CV được gán nhãn: prompt A trùng 35 CV (70%), prompt B trùng 40 CV (80%). Khi so sánh cần giữ nguyên tập CV, nhãn HR, phiên bản model và các tham số Gemini; chỉ thay đổi nội dung prompt. Chỉ số này phản ánh mức độ trùng khớp với nhãn HR, không phải thước đo duy nhất về chất lượng tuyển dụng.
3. 🧰 Công nghệ sử dụng
Thành phần	Công nghệ	Ghi chú
Backend	C#, ASP.NET Core Web API (.NET 10)	Đã khởi tạo project.
Truy cập dữ liệu	Entity Framework Core, SQL Server Provider	Đã cấu hình kết nối cơ bản.
Cơ sở dữ liệu	Microsoft SQL Server	Database phát triển: SmartCvDb.
AI	Google Gemini API	Đã cấu hình tích hợp và API kiểm tra kết nối.
Giao diện	Blazor, Razor, HTML, CSS	Dự kiến phát triển; chưa có project giao diện trong cấu trúc hiện tại.
Đọc PDF	PdfPig	Dự kiến sử dụng cho chức năng trích xuất.
OCR	Tesseract (eng+vie)	Dự kiến sử dụng cho ảnh và PDF scan.
Kiểm thử	xUnit	Đã có project kiểm thử.
Quản lý mã nguồn	Git, GitHub	Làm việc theo nhánh và Pull Request.


4. 🏗️ Kiến trúc và cấu trúc thư mục
Backend được chia thành các project nhằm tách phần API, nghiệp vụ và hạ tầng:
- SmartCv.Api: Điểm khởi chạy ứng dụng, cấu hình HTTP, controllers và endpoints.
- SmartCv.Core: Thành phần nghiệp vụ và các kiểu dữ liệu cốt lõi.
- SmartCv.Infrastructure: Kết nối SQL Server, EF Core và tích hợp dịch vụ bên ngoài như Gemini.
- SmartCv.Tests: Các bài kiểm thử tự động.
AI-CV-Screening/
├── .github/                       # Mẫu Issue, Pull Request và cấu hình GitHub
├── database/                      # Script SQL, dữ liệu mẫu, ERD (theo tiến độ nhóm)
├── docs/                          # Tài liệu dự án (nếu đã tạo)
├── src/
│   ├── SmartCv.Api/
│   │   ├── Controllers/            # API controllers
│   │   ├── Program.cs             # Cấu hình và khởi chạy API
│   │   └── appsettings.json       # Cấu hình không nhạy cảm
│   ├── SmartCv.Core/              # Nghiệp vụ cốt lõi
│   └── SmartCv.Infrastructure/
│       ├── Gemini/                # Cấu hình tích hợp Gemini
│       ├── Persistence/           # DbContext và truy cập dữ liệu
│       └── DependencyInjection.cs
├── tests/
│   └── SmartCv.Tests/             # xUnit tests
├── CONTRIBUTING.md                # Quy trình đóng góp
├── global.json                    # Phiên bản .NET SDK (nếu có)
├── SmartCv.slnx                   # Solution .NET
└── README.md
Sơ đồ thể hiện cấu trúc Backend đã khởi tạo và các thư mục tài liệu dự kiến; nội dung thực tế có thể thay đổi theo tiến độ. Project Blazor sẽ được bổ sung khi nhóm triển khai giao diện.

5. 🗃️ Dữ liệu và lưu trữ CV
Các bảng nghiệp vụ chính (theo thiết kế)
Bảng	Mục đích
Jobs	Lưu thông tin vị trí tuyển dụng.
Cvs	Lưu metadata và nội dung trích xuất của CV.
Applications	Liên kết CV với job.
PromptVersions	Lưu các phiên bản system prompt.
HrLabels	Lưu nhãn đánh giá do HR gán.
EvaluationRuns	Lưu thông tin mỗi lượt đánh giá prompt.
EvaluationResults	Lưu kết quả đánh giá theo từng CV/lượt chạy.


Danh sách trên là các bảng chính theo thiết kế, không phải danh sách đầy đủ mọi bảng đã được tạo trong database. Chi tiết khóa, quan hệ và các bảng bổ sung xem tại thư mục database/ khi tài liệu/schema được cập nhật.
Nguyên tắc lưu trữ CV (dự kiến)
- File gốc lưu trên máy chủ, đổi tên bằng GUID, ví dụ: Uploads/CV/2026/11/<GUID>.pdf.
- SQL Server lưu metadata như tên file gốc, đường dẫn, mã hash, nội dung trích xuất, ngày tải và trạng thái xử lý.
- Không đưa thư mục Uploads/ chứa hồ sơ ứng viên thật lên GitHub.
- Việc truy cập, lưu giữ và xóa CV cần tuân thủ chính sách bảo vệ dữ liệu của dự án.
6. 🔌 Danh sách API
API kiểm tra kết nối đã triển khai
Phương thức	Endpoint	Mô tả
GET	/api/health/database	Kiểm tra kết nối đến SQL Server.
GET	/api/health/gemini	Kiểm tra khả năng kết nối Gemini API.


API nghiệp vụ dự kiến
Các đường dẫn dưới đây là định hướng thiết kế, chưa được xác nhận là đã triển khai. Có thể điều chỉnh khi lập trình.

Phương thức	Endpoint dự kiến	Mô tả
POST	/api/cvs/upload	Tải một hoặc nhiều CV.
POST	/api/applications	Gắn CV với job.
GET	/api/jobs	Xem danh sách job.
POST	/api/scoring/start	Bắt đầu chấm điểm.
GET	/api/scoring/progress	Xem tiến độ chấm điểm.
GET	/api/jobs/{id}/ranking	Xem xếp hạng theo job.
GET	/api/applications/{id}	Xem kết quả chi tiết của một hồ sơ ứng tuyển.
GET, POST	/api/prompts	Xem hoặc tạo phiên bản prompt.
POST	/api/prompts/{id}/activate	Kích hoạt phiên bản prompt.
POST	/api/hr-labels	Nhập nhãn đánh giá của HR.
POST	/api/evaluations/run	Chạy đánh giá prompt.
GET	/api/evaluations/compare	So sánh các phiên bản prompt.


7. ⚙️ Cài đặt môi trường
7.1. Yêu cầu
- Windows với .NET SDK 10.
- SQL Server và công cụ kết nối (VS Code SQL Server extension hoặc SSMS).
- Git.
- VS Code hoặc Visual Studio.
- Gemini API key từ Google AI Studio.
- Nếu làm tính năng OCR: Tesseract OCR và dữ liệu ngôn ngữ tiếng Anh/tiếng Việt.
Kiểm tra môi trường trong Terminal:
dotnet --version
git --version
7.2. Clone repository
git clone https://github.com/TuyetAnh0101/AI-CV-Screening.git
cd AI-CV-Screening
Hướng dẫn chạy Backend từ nhánh main áp dụng sau khi Pull Request cấu hình hạ tầng đã được gộp. Trước thời điểm đó, dùng git switch feature/A1-config-infrastructure sau khi clone và fetch các nhánh.

8. 🔐 Cấu hình SQL Server và Gemini
Các lệnh dưới đây được chạy từ thư mục gốc AI-CV-Screening, trừ khi có ghi chú khác.
8.1. Chuẩn bị database
1. Cài SQL Server và xác định tên instance trên máy (ví dụ localhost\SQLEXPRESS hoặc localhost\BB).
2. Tạo database SmartCvDb hoặc chạy script tương ứng trong database/ khi nhóm đã cung cấp script.
3. Đảm bảo tài khoản đang chạy ứng dụng có quyền truy cập database.
4. Database cần ở trạng thái ONLINE và thông thường là MULTI_USER.
DbContext hiện tại phục vụ cấu hình kết nối; không mặc định rằng toàn bộ bảng nghiệp vụ hoặc migration đã được tạo. Không chạy lệnh cập nhật schema khi chưa thống nhất với nhóm.

8.2. Cấu hình User Secrets
Project API sử dụng User Secrets để lưu thông tin nhạy cảm trên máy phát triển. Không đưa khóa API hoặc mật khẩu thật vào Git.

Nếu project chưa có UserSecretsId, khởi tạo một lần:
dotnet user-secrets init --project src/SmartCv.Api
Thiết lập chuỗi kết nối bằng Windows Authentication (thay TEN_SERVER bằng tên instance trên máy):
dotnet user-secrets set "ConnectionStrings:SmartCvDb" "Server=TEN_SERVER;Database=SmartCvDb;Trusted_Connection=True;TrustServerCertificate=True" --project src/SmartCv.Api
Thiết lập Gemini API key:
dotnet user-secrets set "Gemini:ApiKey" "YOUR_GEMINI_API_KEY" --project src/SmartCv.Api
Quan trọng: YOUR_GEMINI_API_KEY chỉ là giá trị minh họa. Không chia sẻ khóa thật trong ảnh chụp Terminal, Issue, Pull Request hoặc README. Nếu khóa đã lộ, cần thu hồi và tạo khóa mới.

Model Gemini và các tùy chọn không nhạy cảm được cấu hình trong src/SmartCv.Api/appsettings.json. Mỗi thành viên kiểm tra model có được tài khoản/API key của mình hỗ trợ hay không.
9. 🚀 Build, chạy và kiểm thử
9.1. Restore và build
dotnet restore
dotnet build
9.2. Chạy API
dotnet run --project src/SmartCv.Api --launch-profile https
Với launch profile hiện tại của môi trường phát triển, API có thể chạy tại:
- https://localhost:7269
- http://localhost:5156
Lưu ý: Port phụ thuộc vào launchSettings.json trên nhánh đang dùng. Hãy ưu tiên URL được in trong Terminal (Now listening on:).

Kiểm tra endpoint:
GET https://localhost:7269/api/health/database
GET https://localhost:7269/api/health/gemini
Nếu thành công, API trả về trạng thái OK theo cấu trúc phản hồi của endpoint.

Nếu Windows chưa tin cậy chứng chỉ HTTPS phát triển:
dotnet dev-certs https --trust
Nhấn Ctrl + C trong Terminal để dừng API.
9.3. Chạy kiểm thử
dotnet test
Project hiện đã có kiểm thử xUnit cơ bản. Việc chạy thành công các bài kiểm thử hiện có không có nghĩa toàn bộ chức năng nghiệp vụ đã được kiểm thử.
10. 🌿 Quy trình làm việc với GitHub
Mỗi tính năng được phát triển trên một nhánh riêng; không push trực tiếp vào main.
git switch main
git pull origin main
git switch -c feature/ten-chuc-nang
Sau khi hoàn thành và kiểm tra thay đổi:
git status
git add .
git diff --cached --name-only
git commit -m "feat: mo ta chuc nang"
git push -u origin feature/ten-chuc-nang
Tạo Pull Request trên GitHub, mô tả nội dung thay đổi và nhờ thành viên còn lại review trước khi merge. Quy định chi tiết xem [CONTRIBUTING.md](CONTRIBUTING.md).
11. 👥 Phân công công việc
Thành viên	Vai trò	Phạm vi công việc
Bạn A	Backend và AI	A1–A18
Bạn B	Giao diện và kiểm thử	B1–B18
Cả nhóm	Phối hợp	C1–C5


Bảng này giữ nguyên ký hiệu phân công hiện tại của nhóm; cập nhật tên và mô tả công việc khi thống nhất chính thức.

12. 🛠️ Lưu ý và xử lý lỗi thường gặp
Hiện tượng	Hướng kiểm tra
Không kết nối được SQL Server	Kiểm tra instance, database SmartCvDb, chuỗi kết nối trong User Secrets và quyền đăng nhập.
Database báo SINGLE_USER	Kiểm tra các phiên đang kết nối; chỉ đổi sang MULTI_USER khi có quyền và hiểu ảnh hưởng đến phiên khác.
Gemini trả về lỗi xác thực	Kiểm tra API key, quyền truy cập và model cấu hình. Không dán khóa thật vào log công khai.
Lỗi chứng chỉ HTTPS trên máy phát triển	Chạy dotnet dev-certs https --trust.
dotnet build có cảnh báo NU1902/`NU1903`	Kiểm tra package có lỗ hổng và nâng lên phiên bản đã vá; không tắt cảnh báo để che lỗi.
Thiếu bảng nghiệp vụ	Kiểm tra script/migration đã được nhóm thống nhất; không tự ý xóa hoặc tạo lại database đang dùng chung.


Bảo mật và dữ liệu
- Không commit Gemini API key, mật khẩu, connection string chứa bí mật, file .env hoặc User Secrets.
- Không commit thư mục build (bin/, obj/) hay CV thật trong Uploads/.
- Kiểm tra git diff --cached trước khi commit.
- Chỉ sử dụng dữ liệu CV có quyền xử lý; hạn chế lưu thông tin cá nhân trong log và dữ liệu mẫu.
- Khi thay đổi cấu hình chung, schema hoặc API contract, cần thông báo cho nhóm và cập nhật tài liệu.
<div align="center">AI CV Screening · Built with .NET & Gemini

Tài liệu được cập nhật theo tiến độ phát triển của nhóm.

⬆ Về đầu trang
</div>