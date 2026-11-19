# SCApp v2 – Bộ CV đánh giá, cờ HR, Kết quả và Tổng quan

Ngày thực hiện: 19/11/2026 · Nhánh: `feature/scapp-ux-workflow-v2`

## 1. Thông tin Git

| Mục | Giá trị |
|---|---|
| Nhánh mới | `feature/scapp-ux-workflow-v2` (tạo bằng `git switch -c … --no-track origin/main`) |
| Base commit | `f2079affacd584b9c74e74fe14e6cb11b1c395b8` – `feat(B23): implement CV library UI and candidate attachment (#79)` |
| HEAD hiện tại | `f2079af…` (trùng base: **không có commit mới**) |
| Push / PR / merge | **Không** thực hiện |
| `git add` / stash / reset / clean | **Không** thực hiện |
| Trạng thái cuối | 44 file sửa (` M`), 26 file mới (`??`), chưa stage |

Trước khi tạo nhánh: working tree sạch (nhánh cũ `feature/b23-cv-library-ui` đã commit/push), không có merge/rebase/cherry-pick dang dở, nhánh đề xuất chưa tồn tại ở local lẫn remote. Đường dẫn thực tế là `C:\TTCN\project\AI-CV-Screening` (không phải `D:\Projects\…` như trong yêu cầu).

## 2. Phân tích nguyên nhân

### 2.1. Vì sao bộ CV chỉ "giống bộ mặc định"
- **Lỗi thiết kế UX, không phải lỗi API.** `POST /api/evaluation-sets` vẫn tạo được nhiều bộ. Nhưng form tạo bộ nằm trong `<details>` **đóng sẵn** khi đã có ít nhất một bộ. Nút có nhãn "1. Lưu tên bộ" và nằm giữa nhiều khối khác, nên rất khó tìm.
- Mỗi bộ đều hiện **toàn bộ hồ sơ ứng tuyển của hệ thống** trong cùng một bảng, nên bộ nào trông cũng giống bộ nào.
- Danh sách bộ chỉ là một ô `<select>` ("Chọn bộ để tiếp tục"); không có thẻ, không có số liệu từng bộ.

### 2.2. Vì sao không chọn CV trực quan được
- Trước v2, **"có nhãn HR" chính là "thuộc bộ"**: bảng `HrLabels` là nơi duy nhất ghi thành viên. Muốn đưa hồ sơ vào bộ thì phải gán ngay Đạt/Không đạt, nên không có trạng thái "đã chọn, chưa đánh giá".
- `GET /api/evaluation-sets/{id}/applications` không có tìm kiếm, lọc giai đoạn, lọc ngày hay "chưa thuộc bộ". Giao diện cũng không truyền `jobId`.
- Không có checkbox chọn nhiều, và giao diện không phân biệt CV (`CvId`) với hồ sơ ứng tuyển (`ApplicationId`).

### 2.3. Vì sao gắn cờ gây khó hiểu
- `ReviewFlags` chỉ là **cảnh báo tự động**, tính lại mỗi lần đọc và không lưu. Hệ thống **không có cờ thủ công**.
- `ReviewFlagsBadge` dùng biểu tượng ⚑ và chữ "Cần xem kỹ". Bấm vào chỉ ghim popover, nhưng trông như một nút "gắn cờ", dễ tưởng là thao tác lưu.
- Số "Cần xem kỹ" **lệch nhau**: Tổng quan đếm hồ sơ Mới/Đang xem có cảnh báo trong lượt sàng lọc mới nhất. Bộ lọc ở Kết quả đếm mọi hồ sơ có cảnh báo ở mọi giai đoạn (gồm cả hồ sơ đã Loại / Qua vòng CV).

### 2.4. Vì sao Kết quả và Tổng quan chưa hợp lý
- **Kết quả:** 9 cột, 4 KPI cùng một khối "Cần chú ý" riêng, ba hàng bộ lọc tách rời, chú giải thanh điểm và đoạn giới thiệu chiếm chỗ. Đổi giai đoạn, xem chi tiết và (không có) gắn cờ không được tách thành các thao tác rõ ràng.
- **Tổng quan:** chỉ có phễu, lý do loại, thời gian và 5 lượt chấm. Không có biểu đồ theo thời gian và không có lối tắt 90 ngày.

## 3. Các cải tiến đã triển khai

### 3.1. Bộ CV đánh giá (Nhiệm vụ A)

**Trước:** nhãn = thành viên. Chưa gán nhãn thì chưa thuộc bộ. Bỏ nhãn = rời bộ.
**Sau:** bảng `EvaluationSetMembers` tách khỏi `HrLabels`. Nhãn có 3 trạng thái: Đạt / Không đạt / **Chưa đánh giá** (thành viên chưa có nhãn).

Luồng 5 bước trong `/prompts`:
1. **Tạo bộ CV**: nút `+ Tạo bộ CV` luôn hiện. Form có tên, vị trí tuyển dụng, mục tiêu, mô tả. Danh sách bộ dạng thẻ, mỗi thẻ ghi số hồ sơ / đã gán / chưa gán và chip "Sẵn sàng chạy / Chưa đủ điều kiện".
2. **Thêm CV đã quét** (tab): lọc theo vị trí, giai đoạn, ngày ứng tuyển (UTC+7), tìm kiếm không dấu. Có checkbox từng dòng và "chọn tất cả đang hiển thị". Hồ sơ đã thuộc bộ bị ẩn. Hồ sơ ẩn danh không chọn được. Có hướng dẫn CV chưa gán Job → mở Kho CV. Mỗi lần thêm tối đa 500 hồ sơ; API bỏ qua hồ sơ trùng và báo lại.
3. **Gán nhãn HR** (tab): mỗi thành viên có nút phân đoạn Đạt / Không đạt / Chưa đánh giá, ghi chú, "Xem CV gốc", "Nội dung đã trích xuất" (tái dùng `ExtractedTextDialog`) và "Bỏ khỏi bộ" (có xác nhận). Mặc định không chọn sẵn nhãn nào, không lấy kết quả AI. Hồ sơ chưa sẵn sàng bị khóa và hiện lý do. Panel đề xuất nhãn B41 giữ nguyên.
4. **Kiểm tra bộ dữ liệu**: 6 số liệu (tổng, đã gán, Đạt, Không đạt, chưa gán, chưa đủ điều kiện), các lý do chưa chạy được và cảnh báo (ví dụ "3 hồ sơ chưa gán nhãn sẽ KHÔNG được đưa vào lượt chạy", hoặc "< 10 nhãn").
5. **Chạy Prompt Test**: dùng lại `PromptTestPanel`, thêm số thành viên/chưa gán nhãn, phiên bản bộ và cảnh báo "Bộ CV hoặc nhãn HR đã thay đổi sau lượt này".

**Phiên bản bộ (revision/snapshot):**
- `EvaluationSets.Revision` tăng 1 mỗi khi thêm/bỏ thành viên hoặc thêm/sửa/bỏ nhãn (dưới khóa `EvaluationSetWriteScope` của A15).
- `EvaluationRuns.EvaluationSetRevision` lưu phiên bản lúc tạo lượt PromptTest. Danh sách hồ sơ của lượt vẫn chính là các dòng `Evaluations` lúc tạo (A15).
- `PromptTestRunDto` có thêm `EvaluationSetRevision`, `CurrentSetRevision`, `SetChangedSinceRun`.
- So sánh hai lượt (A17) thêm cảnh báo khi hai lượt cùng bộ nhưng khác phiên bản bộ. Cơ chế cũ vẫn giữ: khác bộ thì `NotComparable`, danh sách CV lệch thì cảnh báo.

**Tương thích dữ liệu cũ:** migration chép mọi `HrLabels` hiện có sang `EvaluationSetMembers` (giữ người và thời điểm gán). Các truy vấn đọc cũng coi "có nhãn" là thành viên (cho dữ liệu nhập thẳng như `seed_demo.sql`). `PUT …/labels` và nhập CSV vẫn chạy như cũ và tự thêm thành viên. Lượt chạy trước v2 có `EvaluationSetRevision = null`: không cảnh báo suy đoán.

### 3.2. Cờ HR và trang Kết quả (Nhiệm vụ B)

**Hai khái niệm tách rõ:**
- ⚠ **Cảnh báo tự động** (giữ nguyên quy tắc `ReviewFlags`: chênh lệch KW/AI, chỉ keyword, văn bản ngắn, AI cao nhưng bị giới hạn vì thiếu kỹ năng bắt buộc). Badge đổi sang ⚠ "Cảnh báo tự động · hệ thống tính từ kết quả chấm".
- ⚑ **Cờ HR** (mới, lưu trong `ApplicationFlags` + lịch sử `ApplicationFlagEvents`): chọn hồ sơ → menu `⋯` hoặc panel → **Đánh dấu cần kiểm tra** → chọn lý do (6 lý do; "Khác" bắt buộc ghi chú) → ghi chú → người thao tác → **Xác nhận**. Sau đó có thể **Đánh dấu đã xử lý** hoặc **Mở lại**. Mỗi hồ sơ có tối đa 1 cờ mở (chỉ mục duy nhất có lọc). Đổi trạng thái bắt buộc gửi `ExpectedVersion`: HR khác vừa thao tác thì nhận 409 và hệ thống không tự gửi lại. **Không đổi giai đoạn và không ghi `ApplicationEvents`.** Mở menu/hộp thoại không lưu gì.

**Định nghĩa thống nhất "Cần xem kỹ"** (`ReviewAttention.NeedsReview`): hồ sơ ở **Mới / Đang xem** **và** (có cảnh báo tự động **hoặc** cờ HR đang mở). Dùng chung cho `DashboardSummaryDto.FlaggedUnresolved` (mỗi hồ sơ đếm một lần, có thêm `ManualFlagsOpen`), `RankingResponse.NeedsReviewCount`, `RankingItemDto.NeedsReview` và bộ lọc `filter=flagged`. Trên giao diện, giá trị này tính theo giai đoạn hiện tại, nên quyết định vừa lưu được phản ánh ngay. `FlaggedCount` cũ (cảnh báo tự động, mọi giai đoạn) vẫn được giữ để tương thích.

**Thiết kế lại Kết quả:**
- Header gọn: lượt chấm, Job, Prompt, mô hình, thời điểm.
- 4 KPI không trùng nhau: CV trong vị trí (kèm "N lỗi xử lý · M chỉ keyword") · Dự đoán Đạt ("AI gợi ý, chưa phải quyết định HR") · Điểm trung bình (kèm điểm cao nhất) · **Cần xem kỹ**. Thẻ cuối là nút bật/tắt bộ lọc.
- Bộ lọc gộp một vùng, 3 nhóm có nhãn: *Dự đoán AI* · *Giai đoạn HR* · *Cần chú ý* (Cần xem kỹ, Cờ HR). Tìm kiếm và sắp xếp nằm trên cùng một hàng.
- Bảng còn 6 cột chính: Chọn · Hạng · Ứng viên (tên, file, ⚠/⚑, kỹ năng bắt buộc) · Điểm (cuối + KW/AI nhỏ) · Dự đoán AI (mức + Đạt/Không đạt) · Giai đoạn HR · `⋯`.
- Menu `⋯`: Xem nhanh chi tiết · Mở trang chi tiết · Đánh dấu cần kiểm tra / Đánh dấu cờ đã xử lý.
- **Panel "Xem nhanh"** (drawer bên phải) chia ba nhóm: Dự đoán AI (thanh điểm, KW/AI, kinh nghiệm, kỹ năng) · Cờ & cảnh báo (tự động / HR + lịch sử) · Quyết định HR (đổi giai đoạn + lịch sử A35).
- Thanh thao tác hàng loạt chỉ hiện khi có hồ sơ được chọn.
- Giữ nguyên: Ẩn danh (B36), xử lý 409 và không tự gửi lại (B34/B35), thao tác hàng loạt tối đa 200, lọc theo URL (`filter`, `stage`), tự cập nhật khi đang chấm. Trên điện thoại mỗi hồ sơ thành một thẻ; không cuộn ngang toàn trang ở 1440/820/390 px.

### 3.3. Tổng quan (Nhiệm vụ C)
- `GET /api/dashboard/trend?jobId=&from=&to=&bucket=day|week|auto`:
  - **Cột:** số hồ sơ ứng tuyển mới theo ngày Việt Nam (UTC+7); tuần bắt đầu Thứ Hai.
  - **Đường:** trong chính nhóm hồ sơ của ngày/tuần đó, % đã qua vòng CV (đang hoặc đã từng ở *Qua vòng CV* / *Phỏng vấn*, cùng quy ước với phễu A19).
  - Mỗi `ApplicationId` đếm một lần. Đếm hồ sơ, không đếm file CV.
  - Thiếu ngày bắt đầu thì lấy 30 ngày gần nhất (giao diện ghi rõ). Tối đa 120 điểm; `auto` chọn theo ngày nếu ≤ 31 ngày, dài hơn thì theo tuần.
- Biểu đồ **đường kết hợp** (theo yêu cầu bổ sung ngày 19/11, xem mục 9): SVG/HTML thuần, không thêm thư viện. Một khung, chung trục thời gian, hai đường và hai trục Y có tiêu đề và đơn vị. Có tooltip chung khi rê chuột, xem bằng bàn phím (Tab rồi ← →), trục bắt đầu từ 0, trạng thái tải / lỗi / không dữ liệu, và "Xem dạng bảng".
- Biểu đồ chính đặt trên phễu / lý do loại / thời gian sàng lọc / lượt chấm gần nhất. Thêm lối tắt **90 ngày**. Bộ lọc Job và ngày dùng chung với phần tóm tắt; lỗi tải xu hướng không làm hỏng phần còn lại.

## 4. Danh sách file

### File mới
| File | Mục đích |
|---|---|
| `src/SmartCv.Core/Entities/EvaluationSetMember.cs`, `ApplicationFlag.cs`, `ApplicationFlagEvent.cs` | Entity thành viên bộ, cờ HR, lịch sử cờ |
| `src/SmartCv.Core/Constants/ManualFlags.cs` | Trạng thái, lý do, hành động của cờ HR |
| `src/SmartCv.Core/Contracts/EvaluationSetMemberContracts.cs`, `ManualFlagContracts.cs` | DTO mới |
| `src/SmartCv.Core/Evaluation/EvaluationSetRules.cs` | Kiểm tra bộ (readiness), lý do chưa sẵn sàng, `ReviewAttention`, `ManualFlagRules` |
| `src/SmartCv.Core/Dashboard/TrendCalculator.cs` | Gom nhóm ngày/tuần UTC+7, tỷ lệ |
| `src/SmartCv.Infrastructure/Data/Configurations/EvaluationSetMemberConfiguration.cs`, `ApplicationFlagConfiguration.cs` | Cấu hình EF, CHECK, chỉ mục duy nhất có lọc |
| `src/SmartCv.Infrastructure/Data/Migrations/20261119024613_ScappWorkflowV2(.Designer).cs` | Migration (chỉ thêm) + chép nhãn cũ thành thành viên |
| `database/migration_ScappWorkflowV2.sql` | Script SQL idempotent của migration để triển khai thủ công |
| `src/SmartCv.Api/Endpoints/EvaluationSetMemberEndpoints.cs` | `POST/DELETE …/members`, đọc thành viên, readiness |
| `src/SmartCv.Api/Endpoints/ManualFlagEndpoints.cs` | API cờ HR |
| `src/SmartCv.Web/Components/Results/ManualFlagDialog.razor(.css)`, `CandidateDrawer.razor(.css)`, `RankingTable.Flags.cs` | Hộp thoại cờ, panel Xem nhanh, logic cờ/menu |
| `src/SmartCv.Web/Components/Dashboard/TrendChart.razor(.css)` | Biểu đồ xu hướng (đường kết hợp, hai trục Y) |
| `src/SmartCv.Web/Helpers/SetMembersWorkspace.cs`, `CandidatePickerFilter.cs` | Trạng thái bước 2–4 (bản nháp nhãn, chọn CV) |
| `tests/SmartCv.Tests/ScappWorkflowV2Tests.cs`, `Dashboard/ScappWorkflowV2SqlServerTests.cs` | Test mới |

### File sửa (quan trọng)
| File | Thay đổi |
|---|---|
| `Entities/EvaluationSet.cs`, `EvaluationRun.cs`, `Application.cs`, `SmartCvDbContext.cs`, `EvaluationSetConfiguration.cs`, snapshot | Purpose, JobId (FK `SET NULL`), Revision, UpdatedAt; `EvaluationSetRevision`; navigation |
| `Api/Endpoints/EvaluationSetEndpoints.cs` | List/Create/Get có thành viên, readiness, revision; lọc ứng viên; lưu nhãn tự thêm thành viên; bỏ nhãn giữ thành viên |
| `Api/Endpoints/PromptTestEndpoints.cs`, `Infrastructure/Prompts/PromptTestQueries.cs`, `PromptCompareQueries.cs` | Lưu và trả phiên bản bộ; cảnh báo so sánh khác phiên bản |
| `Api/Endpoints/RunEndpoints.cs`, `DashboardEndpoints.cs` | `ManualFlag`, `NeedsReview` trong ranking; "Cần xem kỹ" gồm cờ HR; endpoint xu hướng |
| `Infrastructure/Privacy/CvAnonymizer.cs` | Ẩn danh CV thì xóa luôn ghi chú cờ HR của hồ sơ đó |
| `Web/Components/Prompts/EvaluationSetManager.razor(.css)`, `PromptTestPanel.razor` | Thiết kế lại 5 bước |
| `Web/Components/Results/RankingTable.razor(.cs/.css)`, `Pages/Results.razor(.css)`, `Shared/ReviewFlagsBadge.razor` | Thiết kế lại Kết quả |
| `Web/Components/Pages/Home.razor(.css)`, `Helpers/DashboardWorkspace.cs`, `DashboardFilter.cs` | Biểu đồ, 90 ngày |
| `Web/Services/*ApiClient.cs`, `ApiClientBase.cs`, `Helpers/EvaluationSetWorkspace.cs` | Client cho API mới; tạo bộ có mục tiêu và Job |
| 7 file test cũ | Cập nhật theo hành vi mới có chủ đích (xem 5.2) |

## 5. Kiểm thử

### 5.1. Kết quả
| Hạng mục | Kết quả |
|---|---|
| `dotnet build SmartCv.slnx -c Release` | Thành công, 0 lỗi |
| Bộ test đầy đủ (Release) trước khi sửa | 2734 đạt · 1 lỗi (`RunScoringWorkerTests.Idle_worker_polls…`, test nhạy thời gian đã biết) · 121 bỏ qua |
| **Bộ test đầy đủ sau khi sửa** | **2764 đạt · 0 lỗi · 122 bỏ qua** (121 test SQL/demo opt-in có sẵn + 1 test SQL mới khi không đặt biến môi trường) |
| Test SQL Server mới (`ScappWorkflowV2SqlServerTests`, chạy riêng với `SMARTCV_A19_TEST_SQL_CONNECTION`) | 1/1 đạt trên catalog mới riêng (xem mục 6) |
| Chạy lại nhóm test liên quan sau chỉnh cuối (TrendChart, CSS) | 72/72 đạt |

Build và test chạy ở cấu hình **Release**, vì Web/API của bạn đang chạy (`dotnet watch`, `dotnet run`) và khóa thư mục `bin/Debug`. Không có tiến trình nào của bạn bị dừng.

### 5.2. Test mới / cập nhật
- **Mới (InMemory):** tạo nhiều bộ có Job và mục tiêu; thêm thành viên (trùng, không tồn tại, ẩn danh, chưa sẵn sàng; phiên bản chỉ tăng khi có thay đổi); lọc ứng viên theo Job và "chưa thuộc bộ"; nhãn tách khỏi thành viên (bỏ nhãn giữ thành viên, bỏ thành viên bỏ luôn nhãn); nhãn cũ không có dòng thành viên; PromptTest lưu phiên bản bộ, giữ danh sách hồ sơ của lượt cũ và báo khi bộ đổi; cờ HR tạo / trùng 409 / sai phiên bản 409 / thiếu phiên bản 400 / xử lý / mở lại / lịch sử, không đổi giai đoạn, không ghi `ApplicationEvents`; hồ sơ ẩn danh không gắn cờ được; ranking dùng chung định nghĩa "Cần xem kỹ"; xu hướng theo ngày UTC+7 (biên 23:30/00:30), tuần Thứ Hai, bucket sai và khoảng quá dài trả 400; quy tắc thuần (readiness, NeedsReview, ManualFlagRules, TrendCalculator); trạng thái bất đồng bộ của Web (bản nháp nhãn còn sau khi lưu lỗi, "Chưa đánh giá" gọi DELETE, chọn CV không gửi thành viên/ẩn danh, thiếu tên thì không gửi); trục biểu đồ; lối tắt 90 ngày.
- **Mới (SQL Server thật):** dịch truy vấn đếm thành viên, chỉ mục duy nhất có lọc của cờ, "Cần xem kỹ" trên Tổng quan (gồm truy vấn SQL thô thời gian sàng lọc), xu hướng, ghi phiên bản bộ khi tạo PromptTest.
- **Cập nhật có chủ đích:**
  - `JobSeedDataTests`, `JobCrudMigrationTests`: thêm tên migration mới.
  - `JobTitleIndexTests`: thêm FK thứ 3 tới Jobs (`EvaluationSets.JobId`).
  - `B36PrivacyTests`: hồ sơ đã Loại không còn "Cần xem kỹ".
  - `FitLevelUiTests`: thanh điểm chuyển sang panel (ScoreBar vẫn có test riêng).
  - `ResultsUiTests`: dòng bảng không còn hai cột "—" kỹ năng/kinh nghiệm.
  - `EvaluationSetWorkspaceTests`: nút "Tạo bộ CV".

### 5.3. Kiểm thử giao diện thật
Ảnh chụp từ ứng dụng thật (không phải mockup): API thật chạy trên Kestrel, AI giả lập, dữ liệu tổng hợp trong catalog SQL thử nghiệm riêng; Web Release; Chrome cài sẵn qua Playwright .NET. Đã kiểm tra:
- Ở 1440 / 820 / 390 px: không cuộn ngang toàn trang, không lỗi console.
- Gắn cờ HR qua hộp thoại thành công ("Giai đoạn không thay đổi").
- Bộ lọc Cần xem kỹ, thanh thao tác hàng loạt khi chọn.
- Thêm 3 CV đã quét vào bộ ("Đã thêm 3 hồ sơ vào bộ (tổng 6)").
- Rê chuột lên biểu đồ.

Sửa sau khi xem ảnh: nhãn trục cuối bị chồng ("18/1119/11"), chip "Bắt buộc" xuống dòng, chip trạng thái thẻ bộ xuống dòng, khoảng trắng trong nút "+ Tạo bộ CV". Hai chỉnh CSS cuối (chip thẻ bộ, khoảng trắng nút) làm sau lần chụp cuối nên chưa có trong ảnh.

### 5.4. Chưa thực hiện được
- Chưa chạy Gemini thật (đúng phạm vi: không đổi công thức/mô hình AI).
- Chưa chạy migration lên `SmartCvDb` thật (xem mục 6).
- Không chạy các test SQL opt-in cũ (A11–A43) vì cần các catalog/biến môi trường riêng.
- Kiểm tra trình đọc màn hình chỉ ở mức thuộc tính ARIA, chưa thử bằng NVDA/JAWS thật.

## 6. Rủi ro và điểm cần anh/chị xác nhận

1. **Migration `ScappWorkflowV2` cần triển khai** trước khi chạy code mới trên `SmartCvDb`. Migration chỉ thêm (3 bảng mới, 4 cột ở `EvaluationSets`, 1 cột ở `EvaluationRuns`) và chép nhãn cũ thành thành viên; không xóa hay sửa dữ liệu cũ. Có hai cách: `dotnet ef database update` hoặc chạy `database/migration_ScappWorkflowV2.sql` (idempotent). **Tôi chưa áp dụng lên bất kỳ database thật nào.** API đang chạy với code cũ không bị ảnh hưởng tới khi khởi động lại bằng code mới.
2. **Bỏ thành viên / bỏ nhãn làm tỷ lệ đúng của lượt cũ tính lại** theo nhãn hiện tại (đây là hành vi A16 có sẵn khi xóa nhãn). Điểm và kết quả chấm (`Evaluations`) không bị sửa. Giao diện và API cảnh báo rõ. Nếu anh/chị muốn "đóng băng" tỷ lệ đúng của lượt cũ, cần quyết định riêng (sẽ thay đổi quy tắc A16).
3. **Hành vi đổi có chủ đích:**
   - `DELETE …/labels/{id}` nay giữ hồ sơ trong bộ (Chưa đánh giá) thay vì đưa ra khỏi bộ.
   - Bộ lọc `filter=flagged` ở Kết quả nay chỉ gồm hồ sơ Mới/Đang xem (giống Tổng quan).
   - Lượt PromptTest chỉ chạy thành viên đã gán nhãn; thành viên chưa gán nhãn không chặn chạy nhưng có cảnh báo.
4. **Bảo mật:** API mới dùng cùng ranh giới tạm thời với A35 (chỉ Development và `CvUpload:Enabled`). Chưa có xác thực thật, nên tên người thao tác do HR tự nhập.
5. **Quyền riêng tư:** ghi chú cờ HR bị xóa khi CV được ẩn danh và không trả về với CV đã ẩn danh. Ghi chú nhãn HR (`HrLabels.Note`) cũ chưa bị xóa khi ẩn danh; trước v2 cũng vậy. Thành viên mới chỉ ẩn ghi chú khi hiển thị.
6. **Database thử nghiệm được giữ lại** trên `localhost\BB` (catalog mới, không dùng lại, không đụng `SmartCvDb`), cần anh/chị cho phép thì mới xóa:
   - `SmartCv_A19_Test_faa9c453469e47909992c7231b5f01d3`: test SQL tự động.
   - `SmartCv_A19_Test_ui6ddbc3f319cf`, `SmartCv_A19_Test_ui3c41f96dda73`: dữ liệu tổng hợp cho ảnh chụp.
7. Chưa làm: sửa tên/mục tiêu của bộ sau khi tạo (chưa có API PATCH); gắn cờ hàng loạt.

## 7. Bằng chứng giao diện
Ảnh thật lưu tại `artifacts/scapp-v2/` (thư mục bị `.gitignore`, không nằm trong commit), kèm `report.txt`:
- `dashboard-1440/820/390.png`, `dashboard-trend-hover-1440.png`
- `results-1440/820/390.png`, `results-row-menu-1440.png`, `results-drawer-1440.png`, `results-flag-dialog-1440.png`, `results-after-flag-1440.png`, `results-needs-review-filter-1440.png`, `results-bulk-bar-1440.png`
- `sets-labels-1440/820/390.png`, `sets-picker-1440/390.png`, `sets-create-1440.png`

Dữ liệu trong ảnh là dữ liệu tổng hợp; tên ứng viên là tên mẫu.

## 8. Hướng dẫn Git (chỉ liệt kê, chưa thực hiện)
Dự kiến đưa vào commit đúng các file ở mục 4: toàn bộ thay đổi `src/`, `tests/`, `database/migration_ScappWorkflowV2.sql` và báo cáo này. **Không** đưa `artifacts/`. Chưa stage, chưa commit, chưa push và chưa tạo PR; tôi chờ yêu cầu tiếp theo.

## 9. Bổ sung 19/11: biểu đồ đường kết hợp trên Tổng quan

Theo yêu cầu, biểu đồ xu hướng chuyển từ "hai biểu đồ xếp dọc" thành **một biểu đồ đường kết hợp**. Chỉ sửa `TrendChart.razor`, `TrendChart.razor.css` và thêm 1 test. API, dữ liệu, cách tính, bộ lọc 7/30/90 ngày, ngày/tuần (`auto`: ≤ 31 ngày theo ngày, dài hơn theo tuần) và các phần khác giữ nguyên.

**Thiết kế**
| Thành phần | Đường 1 – Hồ sơ mới | Đường 2 – Tỷ lệ qua vòng CV |
|---|---|---|
| Màu | Deep Teal `--sc-brand-700` | Mint `--sc-brand-500` |
| Nét / điểm | Liền, chấm đặc, có vùng nền mint rất nhạt | Nét đứt, chấm rỗng |
| Trục | Trái, tiêu đề "Hồ sơ", số nguyên làm tròn đẹp (NiceMax) | Phải, tiêu đề "%", cố định 0–100% |

**Cách tránh hiểu nhầm hai trục**
- Hai trục dùng chung 4 vạch lưới nên chỉ có một lớp lưới.
- Mỗi tiêu đề trục có mẫu nét của đường tương ứng.
- Chú giải ghi rõ "trục trái, đơn vị hồ sơ" / "trục phải, đơn vị %".
- Hai đường khác nhau cả về màu, kiểu nét và kiểu chấm (không phụ thuộc màu). Tooltip chung ghi cả số tuyệt đối (ví dụ 35,3% (6/17)).

**Chi tiết**
- Đường cong mềm kiểu đơn điệu (Fritsch–Carlson): không vọt quá giá trị thật, không có đỉnh/đáy giả, không vượt 0–100%.
- Mốc không có hồ sơ thì đường tỷ lệ bị **ngắt**, không coi là 0%.
- Đường, lưới và vùng nền vẽ bằng SVG co giãn; chữ, chấm và tooltip là HTML theo %, nên chữ luôn sắc nét ở cỡ 11px.
- Chiều cao vùng vẽ: 220px trên máy tính, 190px trên điện thoại, 170px dưới 380px.
- Trên máy tính tooltip nằm cạnh đường dóng, không che điểm đang xem; trên điện thoại tooltip canh giữa và kẹp trong khung. Điện thoại chỉ hiện chấm của mốc đang xem và ẩn bớt nhãn ngày xen kẽ.

**Kiểm tra**
- Build Web/Test (Release) thành công.
- Test mới `Combined_trend_curve_breaks_on_missing_rates_and_never_overshoots_real_values` đạt.
- Nhóm test Tổng quan/Dashboard: 105 đạt, 9 bỏ qua (SQL opt-in).
- Bộ test đầy đủ: 2764 đạt, 1 lỗi, 122 bỏ qua. Lỗi là `RunScoringWorkerTests.Idle_worker_polls…`, test nhạy thời gian đã lỗi cả ở baseline, chạy trùng lúc đang build song song. Chạy lại riêng thì 15/15 đạt.
- Giao diện thật (Chrome headless, dữ liệu tổng hợp trong catalog `SmartCv_A19_Test_ui3c41f96dda73`, dùng lại, không tạo catalog mới), ở 1440 / 1024 / 768 / 390 / 360 px với 7, 30 và 90 ngày:
  - không cuộn ngang, 0 nhãn ngày chồng nhau, tooltip nằm trong khung, 0 lỗi console;
  - 90 ngày tự chuyển theo tuần (14 điểm);
  - điều khiển bằng bàn phím hoạt động.
- Ảnh: `artifacts/scapp-v2/combined-chart/` (không đưa vào commit).

**Lưu ý:** biểu đồ hai trục Y dễ khiến người xem so sánh độ cao hai đường dù chúng khác đơn vị. Các biện pháp trên giảm rủi ro này nhưng không loại bỏ hoàn toàn; nếu cần so sánh chính xác, xem tooltip hoặc dạng bảng.

Không thực hiện `git add`, commit, push hay merge. Vẫn ở nhánh `feature/scapp-ux-workflow-v2`, HEAD `f2079af`.
