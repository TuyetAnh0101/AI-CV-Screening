# Báo cáo A38 — Nguồn CV, xác nhận đồng ý và thời hạn lưu

Ngày kiểm tra: 13/11/2026. Phạm vi: Backend A38; chưa triển khai B38 hoặc A39.

**Kết luận: SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT. Chưa triển khai lên database ứng dụng; cần phối hợp B38 trước khi phát hành API Upload mới.**

## 1. Git, commit nền và môi trường

- Repository: `TuyetAnh0101/AI-CV-Screening`, remote `origin` đã được kiểm tra.
- Nhánh làm việc duy nhất được tạo: `feature/a38-consent-retention`, trực tiếp từ `origin/main` sau `git fetch origin --prune`.
- HEAD và commit nền: `68c374c2e83a421f9a6cbaa08fe7c9311f59f18b` — A36, PR #58. HEAD không thay đổi sau triển khai.
- Đầu vào ở nhánh `feature/a36-label-suggestions`, working tree sạch: không staged, modified, untracked hoặc conflict. Nhánh A38 chưa tồn tại ở local/remote trước khi tạo.
- Đã đối chiếu lịch sử `origin/main`: A35 #49, B34 #50, Runtime Fix #51/#53, Stability #52, A37 #54, B35 #55, A40 #56, B36 #57 và A36 #58.
- Solution thực tế: `SmartCv.slnx`. `global.json` và SDK dùng để build: **10.0.403**; EF Core runtime/packages **10.0.15**.
- SQL integration: Windows LocalDB `(localdb)\MSSQLLocalDB`, chỉ các catalog thử nghiệm có tiền tố và GUID. Không dùng `SmartCvDb`.
- Restore một lần để tạo assets cho thư mục build riêng `artifacts/a38-build`; không sửa package references, SDK, connection string hoặc User Secrets.
- EF CLI hiện có là 10.0.14, phát cảnh báo cũ hơn runtime 10.0.15 khi sinh migration/script. Không tự nâng cấp công cụ. Build cuối không có warning; snapshot không có pending model changes.

## 2. Đối chiếu ZIP với source hiện tại

Đã đọc toàn bộ mục A38 trong `A36_B42_SmartCv/HUONG_DAN_A36_A38_A39_A41_A42_B38_B39_B41_B42.md`, các source/tests trong `A38/`, snippets Program/appsettings và SQL kiểm tra đi kèm. Tài liệu là nguồn tham khảo nghiệp vụ; không chạy các lệnh ghi dữ liệu trong tài liệu trên database ứng dụng.

| Nội dung mẫu | Cách tích hợp vào repository mới nhất |
|---|---|
| Entity/DTO và upload metadata | Bổ sung từng trường và đoạn xử lý, giữ nguyên extraction, file gốc, phân trang và mapping hiện có. |
| `CvConfiguration` cũ | Giữ alternate key `FileHash`, named default constraints, indexes và cấu hình hiện tại. Không thay bằng unique index trong bản mẫu. |
| Giữ hạn muộn hơn bằng giá trị đã đọc | Dùng UPDATE có điều kiện ngay trong SQL để tránh hai HR ở hai Job cùng ghi đè hạn của một CV. |
| Stage endpoints mẫu | Giữ validation, StageVersion, ExpectedStage, no-op, bulk outcomes, HTTP 409 và lịch sử bất biến của A35. |
| Retention options và phần anonymization | Chỉ thêm `Retention:Months=6` và metadata A38. Không thêm worker tự ẩn danh hoặc nghiệp vụ A39. |
| 14 test mẫu | Giữ nguyên nội dung hai file test mẫu, đối chiếu bằng nội dung văn bản; cả 14 case pass. |
| SQL kiểm tra trong ZIP | Chỉ tham khảo. Không chạy UPDATE/PATCH/DELETE mẫu lên dữ liệu ứng dụng. Xuất SQL migration riêng được kiểm thử trên database cô lập. |

Đã xem `cv-anh-ro.png` và `cv-anh-mo.png`: đây là hai CV ảnh demo rõ/mờ, không phải mockup UI. Cả hai được upload và đọc lại file qua TestServer, với consent/source hợp lệ; không chạy OCR hoặc Gemini cho hai ảnh trong test A38.

## 3. Yêu cầu đã triển khai

| Yêu cầu | Kết quả |
|---|---|
| Năm nguồn CV hợp lệ | `Email`, `Website`, `JobBoard`, `Referral`, `Other`; kiểm tra tại API, service và SQL CHECK. |
| Consent bắt buộc | Chỉ chấp nhận `consentConfirmed=true`. Không lấy timestamp từ client, không tự mặc định consent. |
| Chặn trước lưu file | Validate request trước khi tạo danh sách stream/call `ICvUploadService`. Spy xác nhận service được gọi 0 lần với các request sai, kể cả nhiều file. |
| Request nhiều file sai | Từ chối toàn bộ HTTP 400; không có bản ghi CV hoặc thao tác lưu file của upload service. |
| Upload hợp lệ | Lưu source chuẩn hóa và cùng timestamp xác nhận UTC cho request. Giữ kết quả thành công/thất bại theo từng file như A5/A6. |
| GET chi tiết CV | Bổ sung sáu trường metadata/hiển thị nêu ở phần 4. |
| CV legacy | Các cột nullable; không suy diễn nguồn hoặc đồng ý cho dữ liệu cũ. |
| Retention khi Rejected/OnHold | Mặc định cộng sáu tháng theo lịch, giữ deadline muộn nhất cho CV. |
| HR single/bulk và transaction | Deadline, quyết định và lịch sử cùng transaction SQL; rollback cả deadline khi conflict/lỗi. |
| Concurrency | Giữ kiểm tra StageVersion lúc SaveChanges; không retry âm thầm quyết định HR. |
| Chống trùng | Giữ hash/alternate key và xử lý duplicate hiện có; duplicate không cập nhật metadata của CV đã lưu. |

## 4. API Upload và metadata chi tiết

`POST /api/cvs/upload` vẫn là multipart, giữ nguyên phần `files` và response upload hiện có. Bổ sung hai form fields:

| Field | Điều kiện |
|---|---|
| `sourceChannel` | Bắt buộc, một trong năm mã trên; trim khoảng trắng ở API, phân biệt chữ hoa/thường. |
| `consentConfirmed` | Bắt buộc, boolean `true`; thiếu, false hoặc không parse được đều trả 400. |

Giữ thứ tự validation giới hạn số file hiện có. Request vượt giới hạn vẫn được thông báo đúng; request trong giới hạn thiếu source/consent bị chặn trước upload service. Kiểm tra file sai định dạng, dung lượng, duplicate, cleanup khi lỗi lưu và enqueue extraction vẫn hoạt động như trước khi request metadata hợp lệ.

`GET /api/cvs/{id}` bổ sung:

- `sourceChannel`, `sourceChannelName`.
- `consentConfirmedAt`, `retainUntil`, `anonymizedAt`: nullable, serialize UTC khi có giá trị.
- `isAnonymized`: suy ra từ `AnonymizedAt != null`.

CV cũ trả các metadata null, `sourceChannelName="Chưa ghi (CV tải trước A38)"`, `isAnonymized=false` nếu chưa có timestamp. Không backfill consent, không suy ra từ thời điểm upload. Không gửi thêm nội dung CV vào log và không mở thêm API ghi/xóa metadata.

## 5. Quy tắc thời hạn lưu và tính nguyên vẹn

- Chỉ một thay đổi HR thực sự sang `Rejected` hoặc `OnHold` mới xét gia hạn. Cùng stage và reason trả `Unchanged`, không gia hạn hoặc tạo lịch sử.
- Deadline đề xuất là thời điểm quyết định UTC `.AddMonths(Retention.Months)`, mặc định 6. Ví dụ 31/08/2026 → 28/02/2027.
- `RetentionRules` giữ giá trị lớn hơn giữa deadline hiện tại và đề xuất. Stage khác, kể cả mở lại hồ sơ, không xóa/rút ngắn hạn.
- Options validate khi khởi động: Months từ 1 đến 120; cấu hình sai dừng startup rõ ràng thay vì âm thầm đổi giá trị.
- Một CV dùng chung nhiều Job được cập nhật bằng một UPDATE có điều kiện `RetainUntil IS NULL OR RetainUntil < deadline`. SQL Server kiểm tra giá trị trong lúc ghi, tránh lost update từ hai lần đọc cũ.
- Single/bulk mở transaction rõ ràng, cập nhật retention rồi gọi một `SaveChanges` cho Application và ApplicationEvents, cuối cùng commit. Bulk chỉ lấy CvId của hồ sơ thực sự thay đổi và distinct trước UPDATE.
- Xung đột lúc lưu trả HTTP 409 và rollback retention. Lỗi sau một lệnh SQL ghi quyết định trả 500 và rollback toàn bộ, không ghi lịch sử giả hoặc kết quả Changed giả.
- Fallback EF InMemory chỉ phục vụ test, không được dùng làm bằng chứng về transaction/concurrency SQL. Các cam kết trên đã được kiểm tra riêng bằng SQL Server thật.

## 6. Migration và SQL

Migration mới: **`20261113014746_CvConsentRetention`**.

| Bổ sung vào `Cvs` | Kiểu |
|---|---|
| `SourceChannel` | `nvarchar(30) NULL` |
| `ConsentConfirmedAt` | `datetime2 NULL` |
| `RetainUntil` | `datetime2 NULL` |
| `AnonymizedAt` | `datetime2 NULL` |

Thêm đúng `CK_Cvs_SourceChannel` và `IX_Cvs_RetainUntil`, filter `[AnonymizedAt] IS NULL AND [RetainUntil] IS NOT NULL`. CHECK cho phép null legacy và chỉ năm mã chuẩn; binary collation cùng kiểm tra trailing space tránh SQL collation mặc định chấp nhận `email` hoặc `Email `.

Đã kiểm tra Up có đúng sáu operations: bốn cột, một index, một CHECK; Down đảo lại đúng sáu operations. Không có thay đổi schema bảng khác; snapshot chỉ thay phần `Cvs`. Không sửa/xóa migration cũ. Designer chứa toàn bộ target model theo cơ chế EF, không có nghĩa migration thay đổi các bảng còn lại.

SQL: [`database/a38_cv_consent_retention.sql`](../database/a38_cv_consent_retention.sql). Phần migration được sinh idempotent từ `ApplicationStages` đến `CvConsentRetention`, thêm SET options cho filtered index, `XACT_ABORT` và kiểm tra prerequisite A35. Có transaction và ghi migration history tiêu chuẩn; không backfill dữ liệu legacy, không xóa dữ liệu.

Đã áp dụng migration EF và thử riêng **chính file SQL này hai lần** trên database A38 cô lập có CV cũ. Hash/tên file và các metadata null của CV cũ được giữ nguyên; index filtered và CHECK enabled/trusted tồn tại đúng một lần. Lần chạy SQL thứ hai không tạo lại cột/index/lịch sử.

Down được kiểm tra cấu trúc; chưa chạy downgrade thực tế. Down sẽ xóa metadata A38 nên không dùng như rollback vận hành sau khi đã có dữ liệu consent.

## 7. Kiểm thử và bằng chứng

Các dòng trong bảng có thể là tập con của full regression, **không cộng dồn**.

| Nhóm/lần chạy | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Baseline trước sửa, toàn suite | 1597 | 0 | 47 |
| Đúng 14 case mẫu ZIP A38 | 14 | 0 | 0 |
| A38 endpoint/config/current Web/demo ảnh | 20 | 0 | 0 |
| A38 migration/model unit check | 1 | 0 | 0 |
| A38 SQL integration thực tế | 8 | 0 | 0 |
| SQL regression A35 | 7 | 0 | 0 |
| SQL regression A40 | 1 | 0 | 0 |
| Focused A38 + SQL A35 | 50 | 0 | 0 |
| Focused upload/client/assignment/seed sau cập nhật fixture | 54 | 0 | 0 |
| Full regression cuối | **1648** | **0** | **39** |
| Chạy riêng thêm ba test 20 PDF demo sau full regression | 3 | 0 | 0 |

Full regression tổng cộng 1687 case, gồm 43 case A38 mới so với baseline. Trong 39 skipped của lần full: 36 test SQL cũ chưa bật opt-in (A11/A14/A15/A16/A17, runtime deployment/full audit, A36) và 3 test demo PDF chưa đặt biến ZIP ở lần chạy đó. Sau đó đã đặt biến ZIP và chạy riêng ba test demo, tất cả pass; không đổi kết quả lịch sử của lần full thành passed. Không có test A38 skipped ở lần full cuối.

Regression trực tiếp trong full: A35 endpoints 57 pass + SQL 7 pass; B34 UI 15 pass (test demo còn lại pass ở lần riêng); B35 decision 11 pass; B36 privacy 16 pass; A40 endpoints 22 pass, bao gồm SQL. Các nhóm masker/Gemini payload A37 và scoring/upload còn lại cũng không có failure ở lần full cuối.

Các tình huống đã chạy:

1. Một/nhiều file thiếu consent, false, boolean sai, source thiếu/rỗng/lạ/sai case: 400, service calls=0, bảng CV rỗng.
2. Cả năm nguồn, mỗi request hai file: lưu/GET metadata UTC, đọc lại file; duplicate trả lỗi theo file và không ghi đè nguồn cũ.
3. Legacy null metadata; `isAnonymized` phản ánh timestamp được seed trong database test, không chạy anonymization worker.
4. Single/bulk Rejected/OnHold, no-op, đổi reason, mở lại, giữ deadline xa hơn khi một CV thuộc hai Job.
5. Hai SQL contexts cùng đọc deadline cũ, transaction có deadline muộn giữ khóa trước; writer deadline sớm chờ rồi không ghi đè hạn muộn.
6. Concurrency bị chèn đúng lúc SaveChanges cho single và bulk: 409, không retention/history của request thua; quyết định HR thắng còn nguyên.
7. Cố ý ném exception sau SQL ghi Application/Event đầu tiên: single/bulk 500 và rollback cả deadline, stage và lịch sử.
8. A35 SQL kiểm tra CHECK, trigger chống UPDATE/DELETE lịch sử, StageVersion, reason concurrency và bulk 20 hồ sơ.
9. Migration dữ liệu legacy; source CHECK từ chối mã sai bằng SQL error 547; script áp dụng lặp lại và snapshot không pending.
10. Hai CV PNG demo upload thành công và đọc lại; 20 PDF demo dùng kiểm tra HR UI/A37 masking/A40 text-length mà không chấm AI thật.
11. API Client Web hiện tại, không dùng handler bổ sung consent: HTTP 400 và không lưu CV. Đây là bằng chứng dependency B38, không phải một upload thành công.

Build cuối: **0 warning, 0 error**.

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a38-build
dotnet build SmartCv.slnx --artifacts-path artifacts/a38-build --no-restore
dotnet test SmartCv.slnx --artifacts-path artifacts/a38-build --no-restore --no-build --logger "trx;LogFileName=full-regression-final.trx" --results-directory artifacts/a38-tests
```

Lần full bật `SMARTCV_A35_TEST_SQL_CONNECTION`, `SMARTCV_A38_TEST_SQL_CONNECTION`, `SMARTCV_A40_TEST_SQL_CONNECTION` bằng LocalDB với catalog mẫu có tiền tố đúng; từng fixture tự tạo catalog GUID mới. Bật `SMARTCV_A38_DEMO_ZIP` trỏ ZIP A36/A42/B38/B42. Lần ba test PDF riêng đặt `SMARTCV_B34_DEMO_ZIP` và `SMARTCV_A40_DEMO_ZIP` trỏ ZIP A35/A37/A40/B34/B36.

Bằng chứng cục bộ, đều ignored:

- `artifacts/a38-baseline-build.log`, `artifacts/a38-baseline-test.log`, `artifacts/a38-tests/baseline.trx`.
- `artifacts/a38-final-build.log`, `artifacts/a38-focused-final.log`, `artifacts/a38-tests/a38-focused-final.trx`.
- `artifacts/a38-updated-contract-regression-local.log`, `artifacts/a38-tests/updated-contract-regression-local.trx`.
- `artifacts/a38-full-regression-final.log`, `artifacts/a38-tests/full-regression-final.trx`.
- `artifacts/a38-twenty-demo-regression.log`, `artifacts/a38-tests/twenty-demo-regression.trx`.
- Catalog A38 được giữ lại để đối chiếu: `artifacts/a38-tests/retained-sql-databases.txt`. Fixture A35 cũ chỉ cleanup catalog GUID do chính test tạo; không truy cập/xóa database ứng dụng.

### Các lỗi trung gian đã phân tích

- SQL A35 trước cập nhật fixture: 5 pass/2 fail. Một assert ghi cứng ba migration; một barrier chờ tại SaveChanges sau khi CV đã bị khóa tạo timeout nhân tạo. Đã kiểm tra đủ danh sách migrations thay cho số đếm; chuyển barrier tới sau SELECT để hai HR thực sự đọc cùng version. Giữ nguyên assert một OK/một 409 và tính nguyên vẹn lịch sử.
- Full đầu sau A38: 1644 pass/4 fail/39 skip. Một danh sách migration chưa bổ sung A38, ba fixture upload vẫn thiếu metadata. Chỉ cập nhật danh sách kỳ vọng và dữ liệu request thử nghiệm; không thay assert thành công/duplicate/validation/assignment.
- `A38TestData` có HTTP handler **chỉ trong tests** để các kiểm tra A4/A5/A8 dùng consent tổng hợp tường minh. Không đưa handler hoặc mặc định true vào Web/production. Test Web không handler vẫn phải nhận 400.
- Một lần SQL chạy trong sandbox bị lỗi LocalDB account/instance: 0 pass/7 fail; một focused run trong sandbox bị quyền Windows EventLog: 30 pass/23 fail/1 skip. Chạy lại bằng môi trường tài khoản hiện tại đã được công cụ phê duyệt, không sửa logging/validation sản phẩm để né quyền. Kết quả cuối ở các dòng trên.

## 8. Danh sách chính xác file mới/sửa

19 file sửa và 14 file mới, tổng **33 file** dự kiến để người dùng duyệt. Manifest chỉ liệt kê các file này, không gồm chính manifest hoặc artifacts.

| Trạng thái | Đường dẫn | Mục đích |
|---|---|---|
| Sửa | `src/SmartCv.Api/Endpoints/CvEndpoints.cs` | Chặn consent/source trước service; GET metadata UTC/legacy. |
| Sửa | `src/SmartCv.Api/Endpoints/ApplicationStageEndpoints.cs` | Transaction retention + quyết định/lịch sử, rollback 409 single/bulk. |
| Sửa | `src/SmartCv.Api/Program.cs` | Bind/validate Retention options khi startup. |
| Sửa | `src/SmartCv.Api/appsettings.json` | Thêm đúng Retention.Months mặc định 6. |
| Sửa | `src/SmartCv.Core/Entities/Cv.cs` | Bốn thuộc tính nullable. |
| Sửa | `src/SmartCv.Core/Contracts/CvDetailDto.cs` | Contract metadata chi tiết và isAnonymized. |
| Sửa | `src/SmartCv.Core/Uploads/ICvUploadService.cs` | Yêu cầu source đã xác nhận cho mọi call upload service. |
| Mới | `src/SmartCv.Core/Constants/CvSourceChannels.cs` | Năm mã và tên hiển thị, fallback legacy. |
| Mới | `src/SmartCv.Core/Uploads/CvUploadSource.cs` | Validate metadata request và timestamp UTC. |
| Mới | `src/SmartCv.Core/Options/RetentionOptions.cs` | Cấu hình số tháng A38. |
| Mới | `src/SmartCv.Core/Privacy/RetentionRules.cs` | Quy tắc cộng tháng và giữ hạn muộn hơn, độc lập EF. |
| Sửa | `src/SmartCv.Infrastructure/Uploads/CvUploadService.cs` | Validate domain source trước stream, lưu metadata khi tạo CV. |
| Sửa | `src/SmartCv.Infrastructure/Data/Configurations/CvConfiguration.cs` | Mapping, CHECK và filtered index, giữ các cấu hình cũ. |
| Mới | `src/SmartCv.Infrastructure/Privacy/CvRetentionWriter.cs` | UPDATE SQL có điều kiện trong transaction của HR, tránh lost update khác Job. |
| Mới | `src/SmartCv.Infrastructure/Data/Migrations/20261113014746_CvConsentRetention.cs` | Up/Down A38 đúng phạm vi Cvs. |
| Mới | `src/SmartCv.Infrastructure/Data/Migrations/20261113014746_CvConsentRetention.Designer.cs` | EF target model của migration mới. |
| Sửa | `src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs` | Đồng bộ model, chỉ thêm metadata Cvs. |
| Mới | `database/a38_cv_consent_retention.sql` | Script migration idempotent để duyệt triển khai. |
| Mới | `tests/SmartCv.Tests/A38TestData.cs` | Consent tổng hợp và HTTP handler riêng cho regression fixtures cũ. |
| Mới | `tests/SmartCv.Tests/A38ConsentEndpointTests.cs` | 20 case API/config/legacy/duplicate/current Web/demo ảnh. |
| Mới | `tests/SmartCv.Tests/A38RetentionSqlTests.cs` | Một migration/model unit check, tám SQL integration cases. |
| Mới | `tests/SmartCv.Tests/Uploads/CvUploadSourceTests.cs` | Bảy case gốc ZIP về metadata upload. |
| Mới | `tests/SmartCv.Tests/Privacy/RetentionRulesTests.cs` | Bảy case gốc ZIP về thời hạn lưu. |
| Sửa | `tests/SmartCv.Tests/CvUploadServiceTests.cs` | Truyền source/consent tổng hợp cho service, giữ assertions cũ. |
| Sửa | `tests/SmartCv.Tests/CvUploadEndpointTests.cs` | Thêm metadata hợp lệ cho request fixtures cũ. |
| Sửa | `tests/SmartCv.Tests/CvDetailEndpointTests.cs` | Fixture upload hợp lệ theo A38, giữ kiểm tra detail/file. |
| Sửa | `tests/SmartCv.Tests/CvExtractEndpointTests.cs` | Fixture upload hợp lệ, giữ kiểm tra extraction. |
| Sửa | `tests/SmartCv.Tests/ScoringConsistencyEndpointTests.cs` | Fixture upload có consent, không đổi scoring assertions. |
| Sửa | `tests/SmartCv.Tests/JobAssignmentTests.cs` | Consent test-only cho bước upload trước gán Job, giữ idempotency assertions. |
| Sửa | `tests/SmartCv.Tests/WebApiClientTests.cs` | Hợp đồng file/response cũ được kiểm tra với request metadata tổng hợp tường minh. |
| Sửa | `tests/SmartCv.Tests/JobSeedDataTests.cs` | Danh sách migration kỳ vọng thêm A38, giữ thứ tự cũ và kiểm tra snapshot. |
| Sửa | `tests/SmartCv.Tests/Applications/ApplicationStageSqlServerTests.cs` | Kiểm tra đầy đủ migrations và barrier concurrency trước ghi, giữ business assertions. |
| Mới | `docs/A38-implementation-report.md` | Báo cáo triển khai, kiểm thử và điều kiện phát hành. |

Không sửa source Web/B38, OCR/Gemini/worker, scoring, System Prompt, A36 label suggestions hoặc migration cũ. Những test nhiệm vụ khác được sửa chỉ để cung cấp đầu vào hợp lệ theo contract A38 hoặc đồng bộ migration/concurrency fixture như đã giải thích; không xóa hoặc làm yếu assert nghiệp vụ.

## 9. Tương thích B38 và phương án triển khai

**Rủi ro có chủ đích và đã tái hiện:** Web hiện tại không gửi source/consent; API A38 sẽ trả 400, upload chưa hoạt động qua UI đó. Chưa thể phát hành API A38 độc lập cho người dùng Upload hiện tại. Test `Current_Web_upload_without_B38_gets_400_and_saves_nothing_no_consent_is_fabricated` xác nhận điều này bằng chính `CvApiClient` hiện tại, không thêm handler test.

Phối hợp phát hành:

1. Người dùng/DBA xác nhận đúng server/database, quyền triển khai, backup thành công và khả năng restore. Chưa kiểm tra/khẳng định backup database thật trong nhiệm vụ này.
2. Duyệt script A38, trạng thái migration A35 và schema, ước lượng khóa/thời gian tạo index trên dung lượng thật; áp dụng trước trên bản sao thử nghiệm phù hợp.
3. Sau phê duyệt riêng, thêm schema nullable A38. Code cũ có thể tiếp tục chạy với các cột nullable này. Không lấy consent giả cho CV cũ.
4. Phát hành đồng bộ API A38 và Web B38: người dùng chọn nguồn, xác nhận đồng ý thực sự; B38 phải gửi hai fields cho mọi upload single/batch.
5. Xác minh readiness, một upload demo hợp lệ, request không đồng ý bị 400, đọc metadata và HR retention trên môi trường nghiệm thu đã được phép. Sau đó mới mở cho người dùng.
6. Nếu rollback ứng dụng, ưu tiên giữ schema nullable; không tự chạy Down làm mất metadata đã thu thập.

Ví dụ lệnh **để duyệt, chưa thực thi**, phải thay server/database bằng đích đã xác minh và chỉ chạy sau phê duyệt/backup:

```powershell
sqlcmd -S "REVIEWED_SQL_SERVER" -d "REVIEWED_DATABASE" -E -b -f 65001 -i "database/a38_cv_consent_retention.sql"
```

Không bỏ validation, không default consent=true và không triển khai thay UI B38. SQL/migration không được tự áp dụng khi API khởi động trong thay đổi này.

## 10. Giới hạn và rủi ro còn lại

- Database ứng dụng `SmartCvDb` chưa được migrate/ghi dữ liệu; tình trạng A38 trên database thật và health runtime sau triển khai chưa được kiểm chứng. Các kết quả SQL ở đây thuộc database cô lập.
- B38 chưa được thực hiện; dependency phát hành Upload được nêu ở phần 9. Không coi test có metadata tổng hợp là bằng chứng Web hiện tại upload thành công.
- Không chạy Gemini thật, không bật Worker trên database ứng dụng, không đánh giá độ chính xác OCR của hai ảnh demo. Các bài scoring hiện có dùng fake/mock theo fixture.
- Không thực hiện browser E2E/live API trên dữ liệu ứng dụng. A38 không đổi UI; regression component hiện có và TestServer đã chạy, không thay thế kiểm thử chấp nhận B38 sau này.
- Còn 36 test SQL cũ chưa opt-in ở lần kiểm tra này; không ghi nhận chúng là passed. Ba test PDF skipped trong lần full đã có kết quả pass riêng.
- Metadata source/consent null cho CV legacy là trạng thái được hỗ trợ; quyết định cũ không tự backfill RetainUntil. A39 sẽ cần nghiệp vụ riêng để dùng AnonymizedAt và xử lý dữ liệu hết hạn.
- Dữ liệu/backup thực và quy trình triển khai/khôi phục production cần người dùng duyệt riêng. Không có thay đổi ngoài các file được liệt kê.

## 11. Git cuối và manifest

`artifacts/a38-commit-files.txt` chứa đúng 33 đường dẫn file dự kiến duyệt ở phần 8, mỗi dòng một file. Manifest, build outputs, log, TRX, helper định dạng và CV demo trích ra đều bị ignore trong `artifacts/`, không thuộc danh sách commit. Không stage file nào.

Lệnh `git status --short --branch --untracked-files=all`:

```text
## feature/a38-consent-retention...origin/main
 M src/SmartCv.Api/Endpoints/ApplicationStageEndpoints.cs
 M src/SmartCv.Api/Endpoints/CvEndpoints.cs
 M src/SmartCv.Api/Program.cs
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Contracts/CvDetailDto.cs
 M src/SmartCv.Core/Entities/Cv.cs
 M src/SmartCv.Core/Uploads/ICvUploadService.cs
 M src/SmartCv.Infrastructure/Data/Configurations/CvConfiguration.cs
 M src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs
 M src/SmartCv.Infrastructure/Uploads/CvUploadService.cs
 M tests/SmartCv.Tests/Applications/ApplicationStageSqlServerTests.cs
 M tests/SmartCv.Tests/CvDetailEndpointTests.cs
 M tests/SmartCv.Tests/CvExtractEndpointTests.cs
 M tests/SmartCv.Tests/CvUploadEndpointTests.cs
 M tests/SmartCv.Tests/CvUploadServiceTests.cs
 M tests/SmartCv.Tests/JobAssignmentTests.cs
 M tests/SmartCv.Tests/JobSeedDataTests.cs
 M tests/SmartCv.Tests/ScoringConsistencyEndpointTests.cs
 M tests/SmartCv.Tests/WebApiClientTests.cs
?? database/a38_cv_consent_retention.sql
?? docs/A38-implementation-report.md
?? src/SmartCv.Core/Constants/CvSourceChannels.cs
?? src/SmartCv.Core/Options/RetentionOptions.cs
?? src/SmartCv.Core/Privacy/RetentionRules.cs
?? src/SmartCv.Core/Uploads/CvUploadSource.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261113014746_CvConsentRetention.Designer.cs
?? src/SmartCv.Infrastructure/Data/Migrations/20261113014746_CvConsentRetention.cs
?? src/SmartCv.Infrastructure/Privacy/CvRetentionWriter.cs
?? tests/SmartCv.Tests/A38ConsentEndpointTests.cs
?? tests/SmartCv.Tests/A38RetentionSqlTests.cs
?? tests/SmartCv.Tests/A38TestData.cs
?? tests/SmartCv.Tests/Privacy/RetentionRulesTests.cs
?? tests/SmartCv.Tests/Uploads/CvUploadSourceTests.cs
```

Lệnh `git diff --stat` (chỉ tính 19 file tracked; 14 file mới chưa stage được liệt kê đầy đủ ở trên và trong manifest):

```text
 .../Endpoints/ApplicationStageEndpoints.cs         | 44 +++++++++++++++++++---
 src/SmartCv.Api/Endpoints/CvEndpoints.cs           | 13 ++++++-
 src/SmartCv.Api/Program.cs                         |  5 +++
 src/SmartCv.Api/appsettings.json                   |  3 ++
 src/SmartCv.Core/Contracts/CvDetailDto.cs          |  6 +++
 src/SmartCv.Core/Entities/Cv.cs                    |  4 ++
 src/SmartCv.Core/Uploads/ICvUploadService.cs       |  2 +-
 .../Data/Configurations/CvConfiguration.cs         |  7 ++++
 .../Migrations/SmartCvDbContextModelSnapshot.cs    | 19 ++++++++++
 .../Uploads/CvUploadService.cs                     | 11 ++++--
 .../Applications/ApplicationStageSqlServerTests.cs | 14 ++++---
 tests/SmartCv.Tests/CvDetailEndpointTests.cs       |  2 +-
 tests/SmartCv.Tests/CvExtractEndpointTests.cs      |  2 +-
 tests/SmartCv.Tests/CvUploadEndpointTests.cs       |  6 +--
 tests/SmartCv.Tests/CvUploadServiceTests.cs        | 32 ++++++++--------
 tests/SmartCv.Tests/JobAssignmentTests.cs          |  3 +-
 tests/SmartCv.Tests/JobSeedDataTests.cs            |  4 +-
 .../ScoringConsistencyEndpointTests.cs             |  2 +-
 tests/SmartCv.Tests/WebApiClientTests.cs           | 10 ++---
 19 files changed, 145 insertions(+), 44 deletions(-)
```

`git diff --check`: exit code 0, không có output/lỗi whitespace.

`git diff --cached --name-status`: không có output, không có staged files.

## 12. Kết luận

Hoàn thành nghiệp vụ/backend A38, migration/SQL và kiểm thử cô lập. Build thành công; full regression cuối không có failure; giữ nguyên A35/A37/A40/B34/B35/B36 trong những luồng đã thực thi. Không ghi đè file bằng ZIP, không làm mất code cũ, không stage/commit/push/merge hoặc tạo PR.

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT.** Việc cập nhật database thật và phát hành Upload cần phê duyệt riêng, backup được xác minh và B38 tương thích. Dừng tại bước báo cáo.
