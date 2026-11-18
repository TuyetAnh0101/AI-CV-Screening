# A23 — API kho CV

Ngày bàn giao: 18/11/2026 (Asia/Saigon). Trạng thái: **chưa git add/commit/push**, chờ duyệt.

## 1. Nhiệm vụ theo hướng dẫn

Tên trong ZIP: **"A23 – API kho CV"**: tìm kiếm (tên file, tên, email, **nội dung CV**), lọc, phân trang, đo tốc độ. Cần trước: A35, A38 (đều đã có trên `main`).

- Loại công việc: **Backend/API + script SQL đo tốc độ**. Không có giao diện (trang `/cvs` là B23), không có migration.
- Ngoài phạm vi, không làm: A24 (ứng viên, gắn CV, **tìm theo số điện thoại "sau A24"**), A43, B23, B24, B25.

## 2. Tài liệu ZIP đã đọc

`C:\TTCN\A23_B25_SmartCv.zip` (104 mục), mở bằng .NET `ZipArchive` và giải nén vào scratchpad riêng.

- `HUONG_DAN_A23_A24_A43_B23_B24_B25.md`: bảng phân công, phần "tự quyết", **Chuẩn bị chung**, toàn bộ **A23**, các ghi chú liên quan A23 trong B23 (cách B23 gọi API, ghi chú tìm có dấu) và **Lỗi thường gặp** (dòng Nguyen/Nguyễn, search "a", CV ẩn danh, `UQ_Cvs_FileHash`, PERF_).
- `A23/src/SmartCv.Core/Contracts/CvLibraryContracts.cs`, `A23/src/SmartCv.Core/Cvs/CvListQuery.cs`, `A23/src/SmartCv.Api/Endpoints/CvEndpoints.cs`, `A23/tests/SmartCv.Tests/Cvs/CvListQueryTests.cs`.
- `database/a23_tao_du_lieu_hieu_nang.sql`, `a23_so_sanh_tim_kiem.sql`, `a23_xoa_du_lieu_hieu_nang.sql`; `snippets/Program.A23-A43.cs.txt` ("A23: KHÔNG cần thêm gì vào Program.cs").
- Đã liệt kê nhưng không dùng (thuộc việc khác): A24, A43, B23, B24, B25.

**Ảnh:** ZIP **không có tệp ảnh nào** (0 tệp .png/.jpg/.jpeg/.gif/.svg/.webp/.pdf). Không có mockup UI, sơ đồ luồng hay ảnh dữ liệu test, nên không có ảnh nào để đối chiếu.

## 3. Git và môi trường

- Repository duy nhất: `C:\TTCN\project\AI-CV-Screening`; remote `origin` = `https://github.com/TuyetAnh0101/AI-CV-Screening.git`. **Không tạo worktree/clone/thư mục mới.**
- Đầu phiên: nhánh `feature/b26-run-history`, HEAD `dadfcae`, working tree sạch, 0/0 so với upstream; không staged/unstaged/untracked/conflict, không merge/rebase/cherry-pick/revert/sequencer dở. Nhánh A23 chưa có ở local/remote. Stash cũ `stash@{0}` (A10) không thuộc việc này, không đụng tới.
- `git fetch origin --prune`: `main` đã lên `ca8e5e8` (B26 squash #75). Đã kiểm `git diff dadfcae ca8e5e8` = rỗng (B26 đã nằm trọn trong main) trước khi chuyển nhánh.
- `git switch --no-track -c feature/a23-implementation origin/main`. **SHA nền: `ca8e5e8fbe3d320470d8033a6bb962d1706562e9`**, không upstream.
- Tên nhánh theo yêu cầu của bạn (`feature/a23-implementation`); hướng dẫn ZIP gợi ý `feature/a23-cv-list-api`.
- Metadata: [SHA nền](../artifacts/a23-base-sha.txt), [manifest](../artifacts/a23-commit-files.txt) (đều bị `.gitignore`).

## 4. Đối chiếu yêu cầu A23 với phần đã làm

| Yêu cầu (hướng dẫn) | Đã làm | Kiểm chứng |
|---|---|---|
| `GET /api/cvs?search=&jobId=&stage=&source=&status=&page=&pageSize=` | `CvEndpoints.ListAsync`, route `GET /api/cvs` | Endpoint test (TestServer) |
| Mới nhất trước | `UploadedAt DESC, CvId` (ổn định khi trùng giờ) | Thứ tự A→H, phân trang không trùng |
| `page` từ 1, `pageSize` 1–100, mặc định 20; sai → 400 | `CvListQuery` + ProblemDetails tiếng Việt, có `errors` theo trường | 16 kiểu tham số sai → 400 |
| `search` 2–100 ký tự; tên file, tên, email ứng viên, `ExtractedText` | `Contains` → `LIKE`, gộp khoảng trắng trước khi đo độ dài | InMemory + SQL Server |
| Bỏ qua hồ sơ đã ẩn danh khi tìm | `AnonymizedAt IS NULL` khi có `search` | Kể cả tên file thay thế "Hồ sơ đã ẩn danh" và dòng ẩn danh cũ còn tên/chữ |
| `jobId`; `stage` (có `jobId` thì ở đúng job đó); `source`; `status` = Pending/Completed/Failed/Anonymized | Như hướng dẫn; Pending/Completed/Failed loại CV đã ẩn danh | 14 tổ hợp bộ lọc |
| Trường mỗi dòng + `applications[]` (giai đoạn mới nhất, lý do loại, `stageUpdatedAt`, điểm **sàng lọc** gần nhất) + `latestScore`; cả trang có `elapsedMs` | `CvPageDto`/`CvListItemDto`/`CvJobStatusDto` đúng tên trường | Kiểm tên trường JSON; PromptTest và lượt Failed không được tính |
| Collation: không phân biệt hoa thường, có phân biệt dấu | Dùng collation cột; không đổi schema | SQL Server `SQL_Latin1_General_CP1_CI_AS`: `react` = `React`, `Nguyen` ≠ `Nguyễn` |
| Đo tốc độ với vài trăm CV, tiêu chí 1–2 giây; 3 script SQL | Đưa 3 script vào `database/`; chạy trên catalog test riêng | 500 CV: mọi truy vấn < 0,6 s (mục 8) |
| Không cần sửa `Program.cs`, không migration | Đúng | Build 0 warning |

## 5. Thay đổi (endpoint, DTO, database)

- **Endpoint** `GET /api/cvs` nằm **trong cùng chốt chặn** của nhóm `/api/cvs`: chỉ được map khi `CvUpload:Endpoint:Enabled = true`, và ứng dụng dừng khởi động nếu bật ngoài Development (chờ M6 xác thực). `CvPrivacyReadMiddleware` có sẵn tự áp cho route này: khóa đọc A39 (ẩn danh hóa không chen ngang khi đang trả dữ liệu) và `Cache-Control: no-store`. Mẫu ZIP map endpoint ngoài chốt chặn; đã không làm theo vì danh sách trả tên/email ứng viên.
- **Truy vấn 3 bước** (thay cho mẫu ZIP, cùng hợp đồng):
  1. lấy mã CV của trang (lần quét duy nhất có điều kiện tìm);
  2. trang chưa đầy thì đó là trang cuối → tổng tính luôn, không `COUNT` lại;
  3. tải chi tiết theo khóa chính, giữ thứ tự bước 1.

  Lý do: với `AsSplitQuery` của mẫu, mỗi lần tìm quét chữ CV 3 lần (COUNT + trang + truy vấn hồ sơ lặp lại điều kiện). Đo trên SQL Server: lượt "khongcotrongcv" 678 → 303 ms, "perf_00250" 1.000 → 329 ms. Không N+1: số câu lệnh không phụ thuộc số dòng.
- **Bảo vệ dữ liệu:** CV đã ẩn danh luôn trả tên file thay thế và để trống `candidateId/Name/Email`, kể cả dòng cũ còn sót dữ liệu. Không trả `StoredPath`, `FileHash`, chữ CV. Chỉ đọc: không `SaveChanges`, interceptor test chặn mọi lệnh ghi của EF. Log chỉ ghi số ms và số kết quả khi chậm > 1,5 s, không ghi từ khóa. `Microsoft.AspNetCore` = Warning nên query string không vào log; EF không bật `EnableSensitiveDataLogging`. `CancellationToken` được truyền tới mọi truy vấn.
- **Tham số:** đọc thẳng query string giống A25. Tham số gửi lặp (kể cả khác hoa thường), số nguyên sai, `page × pageSize` tràn Int32 → 400 có `errors` theo trường thay vì 400 rỗng của framework. Giá trị rỗng = không lọc (B23 bỏ tham số rỗng). `stage/source/status` phân biệt hoa thường (đúng mã trong database, như test mẫu `new` ≠ `New`).
- **DTO** (`CvLibraryContracts.cs`): đúng tên trường của hướng dẫn để B23 dùng: `CvPageDto {items,page,pageSize,totalCount,totalPages,elapsedMs}`, `CvListItemDto` (+ `isAnonymized`, `latestScore` tính từ `applications`), `CvJobStatusDto`.
- **Database:** không entity/migration/index mới; không chạy gì trên `SmartCvDb`. Thêm 3 script ZIP vào `database/`, nội dung giữ nguyên (UTF-8 không BOM).
- **Giao diện:** không có (A23 chỉ là API).

## 6. File A/M/D

| Trạng thái | File |
|---|---|
| A | `database/a23_so_sanh_tim_kiem.sql` |
| A | `database/a23_tao_du_lieu_hieu_nang.sql` |
| A | `database/a23_xoa_du_lieu_hieu_nang.sql` |
| A | `docs/A23-implementation-report.md` |
| M | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` |
| A | `src/SmartCv.Core/Contracts/CvLibraryContracts.cs` |
| A | `src/SmartCv.Core/Cvs/CvListQuery.cs` |
| A | `tests/SmartCv.Tests/CvLibrary/A23CvListEndpointTests.cs` |
| A | `tests/SmartCv.Tests/CvLibrary/A23CvListSqlServerTests.cs` |
| A | `tests/SmartCv.Tests/CvLibrary/CvListQueryTests.cs` |
| D | (không có) |

Không sửa `Program.cs`, middleware, các endpoint khác, Infrastructure, Web, package, solution hay cấu hình.

## 7. Khác mẫu ZIP và lý do

| Mẫu ZIP | Thực tế | Lý do |
|---|---|---|
| Chép đè `CvEndpoints.cs` (bản A42 của gói) | Chỉ **thêm** route + `ListAsync` vào file hiện tại | File trong repo đã khác gói (chốt chặn M6, A39/A42) |
| `MapGet("/")` ngoài chốt chặn | Trong nhóm có chốt Development + `Enabled` | Không bỏ qua access gate; dữ liệu cá nhân |
| Bind `int? jobId/page/pageSize` | Đọc query string, lỗi theo trường | `?jobId=abc`, tham số lặp: 400 có thông báo |
| Trả tên file/ứng viên của CV ẩn danh nếu còn trong DB | Che khi `AnonymizedAt` có giá trị | Đồng bộ `GET /api/cvs/{id}` và A25 |
| `AsSplitQuery` trên truy vấn lọc | Mã trang → tổng → chi tiết theo khóa | Quét chữ CV 1–2 lần thay vì 3 |
| `Skip = (Page−1)×PageSize` | Chặn tràn Int32 trước khi chạy | `page` rất lớn không gây lỗi 500 |
| Test ở `tests/SmartCv.Tests/Cvs/`, namespace `SmartCv.Tests.Cvs` | Thư mục `CvLibrary/`, namespace gốc `SmartCv.Tests` | Namespace `…Tests.Cvs` sẽ che kiểu trang `Cvs` của B23 (lỗi CS0118, như `Runs` ở B26). 23 test mẫu giữ nguyên nội dung. |

## 8. Build và kiểm thử

`dotnet --info`: SDK 10.0.403, runtime 10.0.14 ([đầy đủ](../artifacts/a23/dotnet-info.txt)). Build baseline, dev và cuối (restore mới vào `artifacts/a23/final-build`): **0 warning, 0 error**. Runsettings giống B21/B26; không đặt biến `SMARTCV*` trong lượt full.

| Lượt | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| Baseline full (main `ca8e5e8`, chưa sửa) — lần 1 | 2565 | 2457 | 1 | 107 |
| Baseline full — lần 2 | 2565 | 2457 | 1 | 107 |
| A23 riêng (có SQL Server) | 94 | 94 | 0 | 0 |
| Focused (A23, CV, A38/A39/A42, B38, A25 Runs, B19–B21, B26, B36/B39/B41, Jobs, Privacy, Dashboard, Applications) | 1113 | 1044 | 0 | 69 |
| Full regression cuối (không đặt biến SQL) | 2659 | 2549 | 0 | 110 |

Đối chiếu baseline → cuối theo tên test (phân biệt hoa thường): **thêm đúng 94 test A23, bỏ 0 test cũ**. Danh sách skipped chỉ thêm 3 test SQL opt-in của A23 (107 → 110); 3 test này đã chạy riêng với SQL Server và pass. Test worker lỗi ở baseline đã pass trong lượt cuối. Lượt cuối mất 17m44s do máy đang tải nặng.

TRX: `artifacts/a23/baseline-tests/baseline.trx`, `baseline-tests-rerun/baseline-rerun.trx`, `sql-tests/a23-sql.trx`, `focused-tests/focused.trx`, `final-full-tests/full.trx`.

**Baseline có sẵn 1 test lỗi:** `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop` (`Assert.InRange` 2–5, thực tế 6) lỗi ở **cả hai** lượt baseline trên `main` chưa sửa. Chạy riêng: pass 3/3. Test đếm số lần worker hỏi việc trong `Task.Delay(3500)`; máy bận thì delay kéo dài nên đếm dư. Lúc chạy, máy có `dotnet watch --project src/SmartCv.Web` và `SmartCv.Api.exe` của bạn đang chạy (không phải của tôi, không tắt). Lượt full mất 3–4,5 phút so với 1m48s ở phiên trước. A23 không đụng worker. Không sửa hay skip test này.

**94 test A23:**
- 23 test mẫu `CvListQueryTests` giữ nguyên.
- 32 case parser: rỗng, gộp khoảng trắng, số sai, lặp tham số, tràn trang, wildcard là dữ liệu.
- 36 case endpoint qua pipeline API thật (TestServer + EF InMemory, không worker/Gemini):
  - hợp đồng JSON và giờ UTC;
  - điểm sàng lọc gần nhất (bỏ PromptTest và lượt Failed);
  - 9 kiểu tìm và 14 tổ hợp bộ lọc;
  - CV ẩn danh;
  - phân trang;
  - kho rỗng;
  - 16 lỗi 400;
  - chỉ đọc;
  - endpoint vắng mặt khi tắt chốt.
- 3 test **SQL Server** opt-in (`SMARTCV_A23_TEST_SQL_CONNECTION`, tiền tố `SmartCv_A23_Test_`).

**Kết quả SQL Server** (`localhost\BB`, mỗi test một catalog GUID mới, migrate đủ 6 migration; [chi tiết](../artifacts/a23-sql/)):

- Collation cột `SQL_Latin1_General_CP1_CI_AS`: `react` ra CV "React"; `Nguyen` **không** ra "Nguyễn" (đúng ghi chú hướng dẫn).
- `50%` không ra "500", `C_` không ra "CX", `[ab]` không phải lớp ký tự (EF tự thoát).
- `totalCount` = số đếm của `a23_so_sanh_tim_kiem.sql` cho React, SQL Server, perf_00250, khongcotrongcv.
- Câu lệnh EF mỗi request: trang đầy 4 (2 lần quét LIKE), trang cuối 3 (1 quét), không kết quả **1**. Như nhau với `pageSize` 1 hay 100; tất cả đều `SELECT`.
- `a23_tao_du_lieu_hieu_nang.sql` chạy được trên schema hiện tại: 500 CV, 2,93 MB chữ, 100 CV có React, 100 có SQL Server.
- `a23_xoa_du_lieu_hieu_nang.sql` đưa về `CvConLai = 0`, `UngVienConLai = 0`; số job không đổi.

Đo tốc độ (lần 3/3, TestServer trong tiến trình, máy đang có `dotnet watch`):

| Truy vấn | Thời gian (ms) | `elapsedMs` | totalCount |
|---|---:|---:|---:|
| `search=React` | 571 | 535 | 100 |
| `search=SQL%20Server` | 522 | 513 | 100 |
| `search=perf_00250` | 329 | 318 | 1 |
| `search=khongcotrongcv` | 303 | 291 | 0 |
| `jobId=1&stage=Rejected&page=5` | 39 | 27 | 125 |

Kết luận cho PR: **với 500 CV chưa cần Full-Text Search** (mọi truy vấn < 0,6 s, tiêu chí 1–2 s).

Lệnh tái hiện:

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/a23/final-build
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/a23/final-build --settings artifacts/a23/tests.runsettings
$env:SMARTCV_A23_TEST_SQL_CONNECTION = 'Server=localhost\BB;Database=SmartCv_A23_Test_;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/a23/final-build --settings artifacts/a23/tests.runsettings --filter "FullyQualifiedName~A23CvListSqlServerTests"
```

## 9. Lỗi phát sinh và cách xử lý

- Truy vấn mẫu quét chữ CV 3 lần mỗi lần tìm → đổi sang 3 bước (mục 5), đo trước/sau.
- 1 lỗi biên dịch trong test (thuộc tính kiểu ẩn danh từ indexer) → đặt tên thuộc tính. Không có lỗi logic sản phẩm nào cần sửa sau khi test chạy.
- Baseline worker test lỗi do tải máy → ghi nhận, không sửa (mục 8).

## 10. Giới hạn chưa kiểm chứng

- **Chưa chạy trên `SmartCvDb` thật** và chưa gọi qua HTTPS/Kestrel như lệnh `Measure-Command` của hướng dẫn. Tốc độ đo trên catalog test cùng SQL Server `localhost\BB` qua TestServer; `SmartCvDb` không bị đụng tới.
- Chưa thử với 2.000–5.000 CV; với OCR thật (chữ dài hơn) thời gian tăng gần tuyến tính theo dung lượng chữ.
- Tìm không dấu và tìm theo từ cần Full-Text Search hoặc collation `_AI`: việc riêng, đúng như hướng dẫn.
- Tìm theo số điện thoại thuộc A24.
- Endpoint chưa có giới hạn tần suất (giống các GET khác); B23 sẽ chờ 400 ms sau phím cuối.
- `latestEvaluatedAt` là `Evaluations.CreatedAt` (bảng không có thời điểm chấm xong riêng).
- **6 catalog test được giữ lại** trên `localhost\BB` (không tự xóa): `SmartCv_A23_Test_15d9232bbc4a41e8a863161350d57ff8`, `…e11677bf5790460c882344ba72eb3c44`, `…5dec9624ba4241bc86cc27b6c2bc6b3c`, `…c483304090044f90855b5031d35793cb`, `…4be6043778dc428cb8d1ec769b68274b`, `…d7c76af0c1a64d2db6ee522fd84f1764`. Chỉ xóa khi bạn đồng ý.
- `database/README.md` chưa liệt kê 3 script (các script A19–A21, A38 cũng chưa có). Có thể bổ sung nếu nhóm muốn.

## 11. Phạm vi

Chỉ A23. Không có thay đổi Web/B23, không có API ứng viên (A24), không xóa dữ liệu (A43), không đổi worker/Gemini/OCR/lưu trữ CV, không migration.

## 12. Git checkpoint

```text
$ git status --short --branch
## feature/a23-implementation
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
?? database/a23_so_sanh_tim_kiem.sql
?? database/a23_tao_du_lieu_hieu_nang.sql
?? database/a23_xoa_du_lieu_hieu_nang.sql
?? docs/A23-implementation-report.md
?? src/SmartCv.Core/Contracts/CvLibraryContracts.cs
?? src/SmartCv.Core/Cvs/                 (CvListQuery.cs)
?? tests/SmartCv.Tests/CvLibrary/        (3 file test)

$ git diff --check
(không có output)

$ git diff --cached --name-status
(không có output — chưa stage gì)

HEAD = SHA nền = ca8e5e8fbe3d320470d8033a6bb962d1706562e9
```

10 file: không BOM, không khoảng trắng cuối dòng; mỗi file thống nhất một kiểu xuống dòng (CRLF hoặc LF). `core.autocrlf=true` nên index lưu LF như các file hiện có. So khớp tự động `(git diff --name-only + untracked)` với manifest: **trùng khớp 10/10**.

## 13. Đối chiếu manifest

[`artifacts/a23-commit-files.txt`](../artifacts/a23-commit-files.txt): 10 đường dẫn tương đối, mỗi dòng một path, không trùng, không chứa artifact/log/bin/obj. Khớp đúng toàn bộ thay đổi trong `git status` (1 M + 9 A) — đối chiếu tự động ở mục 12.
