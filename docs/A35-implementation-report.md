# A35 — Giai đoạn hồ sơ và lịch sử quyết định HR

Ngày thực hiện: 12/11/2026. Phạm vi chỉ A35.

## 1. Git và môi trường

- Nhánh: `feature/a35-application-stages`.
- Commit nền: `303142afb6d41d846cc954f709a233936f184f65`.
- Working tree sạch trước khi triển khai; không có conflict.
- Solution thực tế: `SmartCv.slnx`; `global.json`: SDK `10.0.403`, `rollForward: latestFeature`; SDK đang dùng `10.0.403`.
- Migrations nền: `20261106132922_InitialCreate`, `20261106142155_SeedJobs`.
- Đã đọc trọn mục A35 trong hướng dẫn ZIP, source A35, snippets, hai SQL mẫu và unit tests mẫu. Các lệnh Git và script xóa lịch sử trong tài liệu không được thực hiện. Các file có sẵn được sửa từng phần, không chép đè bằng file mẫu.

## 2. Nghiệp vụ đã triển khai

- Sáu giai đoạn và sáu lý do loại, nhãn tiếng Việt theo yêu cầu.
- Cho phép mọi cặp chuyển giai đoạn, kể cả mở lại hồ sơ bị loại.
- Bắt buộc lý do hợp lệ khi `Rejected`; các giai đoạn khác không có lý do loại.
- Actor bắt buộc, tối đa 100 ký tự; note tối đa 500 ký tự; chuẩn hóa khoảng trắng.
- Mỗi thay đổi giai đoạn hoặc lý do tạo đúng một event. Chỉ đổi actor/note không tạo quyết định mới; trả `Unchanged`, không đổi timestamp/version.
- History chỉ thêm: guard của DbContext và trigger SQL chống UPDATE/DELETE; FK Restrict và API bỏ gắn bảo vệ hồ sơ có history.
- `Stage`, `RejectReason`, `StageVersion` là concurrency tokens trong UPDATE của EF. `StageVersion` đổi khi lưu quyết định; không tự retry ghi đè khi xung đột.
- Một `SaveChangesAsync` lưu application và events trong cùng giao dịch SQL; bulk xung đột hủy toàn bộ thay đổi của request.
- Điểm và kết quả AI không đổi giai đoạn; không thay đổi worker, scoring, OCR, Gemini hoặc UI.

Thiết kế token kiểm tra tại thời điểm ghi đối chiếu với [tài liệu concurrency của EF Core](https://learn.microsoft.com/en-us/ef/core/saving/concurrency); bảng có trigger tắt OUTPUT theo [tài liệu SQL Server provider](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/misc#savechanges-triggers-and-the-output-clause). Các hành vi này đã được xác minh thêm bằng integration tests trên SQL Server.

## 3. Toàn bộ file thay đổi

`M`: tracked, sửa chưa stage. `??`: file mới, chưa theo dõi. Các đường dẫn tương đối tính từ gốc repository.

| Git | File | Mục đích |
|---|---|---|
| M | `src/SmartCv.Core/Constants/StatusValues.cs` | Mã và nhãn sáu giai đoạn/sáu lý do. Giữ các hằng số cũ. |
| M | `src/SmartCv.Core/Entities/Application.cs` | Stage, RejectReason, StageUpdatedAt, StageVersion và navigation Events. |
| ?? | `src/SmartCv.Core/Entities/ApplicationEvent.cs` | Entity lịch sử quyết định, actor, thời gian, lý do, note. |
| ?? | `src/SmartCv.Core/Applications/StageChangeRules.cs` | Validation và lập kế hoạch Changed/Unchanged/NotFound. |
| ?? | `src/SmartCv.Core/Contracts/StageContracts.cs` | Request/response/options/events; version dự kiến tùy chọn. |
| M | `src/SmartCv.Core/Contracts/ResultContracts.cs` | Stage fields/version trên ranking/detail; StageCounts. |
| M | `src/SmartCv.Infrastructure/Data/Configurations/ApplicationConfiguration.cs` | Columns, CHECK, concurrency tokens, index JobId/Stage; giữ alternate key và default cũ. |
| ?? | `src/SmartCv.Infrastructure/Data/Configurations/ApplicationEventConfiguration.cs` | Event schema, CHECK, actor, index, Restrict, trigger metadata; tắt SQL OUTPUT. |
| M | `src/SmartCv.Infrastructure/Data/SmartCvDbContext.cs` | DbSet events, chặn sửa/xóa event, đổi StageVersion khi lưu stage/reason. |
| ?? | `src/SmartCv.Infrastructure/Data/Migrations/20261112045258_ApplicationStages.cs` | Migration columns/table/indexes/CHECK/trigger; Down gỡ trigger trước table. |
| ?? | `src/SmartCv.Infrastructure/Data/Migrations/20261112045258_ApplicationStages.Designer.cs` | Target model do EF sinh cho migration A35. |
| M | `src/SmartCv.Infrastructure/Data/Migrations/SmartCvDbContextModelSnapshot.cs` | Đồng bộ snapshot, không chỉnh migrations nền. |
| ?? | `src/SmartCv.Api/Endpoints/ApplicationStageEndpoints.cs` | Bốn API A35, concurrency 409, bulk atomic, history mới nhất trước. |
| M | `src/SmartCv.Api/Endpoints/ApplicationEndpoints.cs` | Chặn bỏ gắn khi có quyết định HR; xử lý race với quyết định mới. |
| M | `src/SmartCv.Api/Endpoints/RunEndpoints.cs` | Đọc stage fields/version và tính StageCounts theo các dòng ranking của job đang chọn. |
| M | `src/SmartCv.Api/Endpoints/EvaluationEndpoints.cs` | Đọc stage fields/version của application hiện tại. |
| M | `src/SmartCv.Api/Program.cs` | Map API A35. |
| ?? | `tests/SmartCv.Tests/Applications/StageChangeRulesTests.cs` | Unit tests nghiệp vụ dựa trên mẫu A35, không đưa A36 vào. |
| ?? | `tests/SmartCv.Tests/Applications/ApplicationStageEndpointTests.cs` | HTTP validation/status, 36 transitions, history, bulk, no-op, versions, access boundary, bảo vệ xóa. |
| ?? | `tests/SmartCv.Tests/Applications/ApplicationStagePersistenceTests.cs` | Hai context cạnh tranh, guard history sync/async, kiểm tra SQL migration/model. |
| ?? | `tests/SmartCv.Tests/Applications/ApplicationStageSqlServerTests.cs` | SQL thật: migration upgrade, CHECK/trigger, bulk, ranking/detail, race, rollback sau ghi. |
| M | `tests/SmartCv.Tests/ResultEndpointTests.cs` | Regression stageCounts và ranking/detail, giữ nguyên điểm/xếp hạng cũ. |
| M | `tests/SmartCv.Tests/JobSeedDataTests.cs` | Cập nhật đúng danh sách ba migrations sau khi thêm A35; giữ kiểm tra model và seed. |
| ?? | `database/a35_application_stages.sql` | SQL sinh từ SeedJobs đến ApplicationStages để người dùng duyệt. |
| ?? | `database/check_a35.sql` | Kiểm tra chỉ đọc: trạng thái, history, consistency, constraints, trigger, indexes. |
| ?? | `docs/A35-implementation-report.md` | Báo cáo triển khai, validation, giới hạn và trạng thái bàn giao. |

Không thêm script tắt trigger/xóa history. `artifacts/` là thư mục bị Git ignore: chứa log/TRX kiểm thử, bản sao commit nền phục vụ xác minh regression và chương trình tạm đọc PDF; không thuộc danh sách file để commit.

## 4. API và database

| API | Kết quả |
|---|---|
| `GET /api/applications/stage-options` | 200: sáu stages và sáu rejectReasons, code và displayName. |
| `PATCH /api/applications/{id}/stage` | 200 Changed/Unchanged; 400 validation; 404 không có hồ sơ; 409 expected state/version hoặc race lúc lưu. |
| `POST /api/applications/stage` | Tối đa 200 ID dương, không trùng; 200 kết quả từng ID theo thứ tự gửi, có Changed/Unchanged/NotFound; 400 input; 409 hủy toàn bộ request khi conflict. |
| `GET /api/applications/{id}/events` | 200 danh sách mới nhất trước; 404 không có hồ sơ; thời gian UTC có `Z`. |
| `GET /api/runs/{id}/ranking` | Thêm stage, rejectReason, stageUpdatedAt, stageVersion từng dòng; stageCounts luôn có đủ sáu mã. |
| `GET /api/evaluations/{id}` | Thêm stage, rejectReason, stageUpdatedAt, stageVersion. |

API A35 giữ chốt chặn truy cập dữ liệu hồ sơ hiện tại: chỉ Development và `CvUpload:Endpoint:Enabled=true`. Chưa có tài khoản đăng nhập ở baseline, nên actor là tên HR client truyền theo contract mẫu, được kiểm tra và ghi lại. Không tự triển khai xác thực ngoài A35.

Request PATCH cũ tiếp tục dùng được:

```json
{
  "toStage": "Rejected",
  "rejectReason": "MissingSkills",
  "actor": "HR - Trần Thu Hà",
  "note": "Thiếu SQL Server",
  "expectedStage": "Reviewing"
}
```

Client mới nên gửi thêm `expectedStageVersion` (GUID lấy từ ranking/detail hoặc kết quả đổi giai đoạn). Bulk hỗ trợ `expectedStageVersions` dạng `{ "123": "<GUID>" }`, khóa phải thuộc applicationIds. Version nhận ra cả đổi lý do trong cùng giai đoạn và chuỗi A→B→A. Client cũ chỉ gửi expectedStage không nhận ra hai trường hợp trang đã cũ này; kiểm tra đồng thời giữa đọc và lưu vẫn luôn được EF thực hiện, kể cả không gửi expectedStage/version.

Bulk 409 dùng ProblemDetails có `rolledBack=true` và `results` cho từng hồ sơ (`Conflict`, `RolledBack`, `NotFound`). Không trả Changed hoặc eventId của giao dịch đã hủy. Hồ sơ không tồn tại đơn thuần là kết quả NotFound riêng trong bulk 200, không cản các hồ sơ hợp lệ.

Migration `20261112045258_ApplicationStages` chỉ bổ sung bốn cột, bảng events, hai indexes, tám CHECK constraints và `TR_ApplicationEvents_NoChange`. Hồ sơ cũ mặc định New, reason/timestamp null, version được sinh riêng. Giữ nguyên dữ liệu CV, kết quả AI, Jobs/seed, alternate key, FK/default cũ. Trigger dùng dynamic SQL để tương thích cả script idempotent. Chưa apply lên database ứng dụng.

## 5. Build/test và migration

| Kiểm tra | Pass | Fail | Skipped | Bằng chứng |
|---|---:|---:|---:|---|
| Build `SmartCv.slnx --no-restore` cuối | — | 0 | — | Build succeeded, 0 Warning(s), 0 Error(s). |
| Toàn bộ suite, lần cuối | 1320 | 1 | 31 | `artifacts/a35-tests/a35-full-final.trx`, `full-final.log` |
| Toàn bộ tests mới A35 (nằm trong suite trên) | 87 | 0 | 0 | Các test namespace `SmartCv.Tests.Applications` trong TRX cuối. |
| SQL Server A35 (nằm trong 87 test trên) | 7 | 0 | 0 | `ApplicationStageSqlServerTests` trong TRX cuối. |
| Test UI đang lỗi trên nguyên commit nền | 0 | 1 | 0 | `artifacts/a35-tests/a35-baseline-ui.trx`, `baseline-ui.log` |

31 skipped là các SQL tests cũ chưa được opt-in: A11: 6, A15: 7, A16: 10, A17: 3, Prompt: 5. Không ghi chúng thành pass. Bảy SQL test A35 đều đã thực sự chạy.

Lần chạy toàn bộ đầu có thêm một lỗi do JobSeedDataTests kỳ vọng chỉ hai migrations; đã cập nhật đúng ba migrations cho A35, giữ nguyên kiểm tra model/seed. Lần chạy cuối không còn lỗi này. Lỗi quyền Windows Event Log trong sandbox đã được xử lý bằng chạy test ngoài sandbox, không sửa logging của sản phẩm.

Các lệnh chính:

```powershell
dotnet build SmartCv.slnx --no-restore
dotnet test SmartCv.slnx --no-build --no-restore --logger "trx;LogFileName=a35-full-final.trx" --results-directory artifacts/a35-tests
dotnet ef migrations has-pending-model-changes --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --no-build
dotnet ef migrations list --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --no-build --no-connect
dotnet ef migrations script 20261106142155_SeedJobs ApplicationStages --project src/SmartCv.Infrastructure --startup-project src/SmartCv.Api --no-build --output database/a35_application_stages.sql
```

Design-time EF dùng connection giả `SmartCv_A35_DesignOnly`, không kết nối database ứng dụng. `has-pending-model-changes` báo không có thay đổi model chưa migrate. `migrations list --no-connect` liệt kê đủ ba migration; không xác định applied/pending của database ứng dụng vì cố ý không kết nối.

EF CLI `10.0.14` cảnh báo thấp hơn EF runtime `10.0.15` đã restore theo package wildcard hiện có. Không tự thay SDK/package/tool ngoài nhiệm vụ; tạo migration, sinh SQL và kiểm tra model vẫn thành công.

SQL integration opt-in bằng biến môi trường `SMARTCV_A35_TEST_SQL_CONNECTION`. Chỉ chấp nhận InitialCatalog bắt đầu `SmartCv_A35_Test_`, cấm AttachDBFilename. Mỗi test tự sinh tên database GUID khác, migrate rồi drop đúng database sinh riêng. Database ghi trong setting và database ứng dụng không bị thay đổi. Test đã chạy trên `(localdb)\MSSQLLocalDB` với Windows identity bình thường; sandbox không có quyền Event Log/SSPI nên test HTTP/SQL được chạy ngoài sandbox sau khi hệ thống phê duyệt.

## 6. Các tình huống đã kiểm thử

- 36 cặp chuyển giai đoạn; cùng giai đoạn/lý do là Unchanged, không event.
- Reject hợp lệ, thiếu/sai lý do, lý do ở giai đoạn khác, thiếu actor, actor/note quá dài, JSON lỗi.
- Đổi lý do Rejected→Rejected, mở lại hồ sơ, timestamp/version ổn định khi chỉ đổi note/actor.
- PATCH và history 404; 400 validation; 409 expected stage/version cũ.
- Bulk trộn Changed/Unchanged/NotFound, giữ thứ tự; gửi lặp không tăng lịch sử; 200 ID hợp lệ; 201/null/rỗng/trùng/ID không dương bị từ chối.
- Version cũ sau A→B→A; version cũ trong bulk hủy cả request.
- Hai context cùng đọc trước khi lưu; hai HR cùng PATCH không expectedStage; hai HR cùng đổi lý do; bulk cạnh tranh với PATCH: chỉ một quyết định thắng và số history đúng.
- SQL rollback do lỗi được chèn sau lệnh ghi thành công đầu tiên: không hồ sơ nào đổi và không lưu event.
- Migration nâng cấp application đã có từ schema SeedJobs; giữ dữ liệu CV cũ, mặc định New.
- SQL CHECK chặn dữ liệu stage/reason sai và actor rỗng; trigger UPDATE/DELETE báo 51000; FK chặn xóa application có event.
- SQL bulk 20 application: đủ một event mỗi quyết định, không lỗi OUTPUT với trigger, thử lại Unchanged.
- Ranking/detail đồng bộ stage/version và stageCounts; kết quả AI/điểm/rank giữ nguyên khi HR quyết định.
- Bảo toàn chốt chặn Development/Enabled và API bỏ gắn hồ sơ chưa có quyết định.
- Đọc đủ 20 PDF demo trực tiếp từ ZIP bằng PdfPig: mỗi file một trang, đọc được 168–743 ký tự. Không gọi Gemini hoặc upload vào database thật; bulk SQL 20 hồ sơ dùng fixture độc lập, không tuyên bố đã chạy upload→Gemini với bộ PDF này.

## 7. Lỗi còn lại và giới hạn

Lỗi duy nhất còn lại:

```text
SmartCv.Tests.PromptTestFlowTests.Parent_render_with_previous_query_does_not_restore_old_run_or_reset_new_prompt
tests/SmartCv.Tests/PromptTestFlowTests.cs:192
Assert.Contains: không tìm thấy "#8" trong nội dung render.
```

Đã chạy riêng và tái hiện cùng lỗi; sau đó xuất nguyên commit nền `303142afb6d41d846cc954f709a233936f184f65` bằng `git archive` vào `artifacts/a35-baseline`, build và chạy đúng test đó: cũng fail tại dòng 192 với thiếu `#8`. Không checkout/reset nhánh đang làm. Đây là lỗi có sẵn ngoài A35, không phải regression do thay đổi A35. Không sửa UI hoặc luồng Prompt để giải quyết lỗi không liên quan. Giải pháp: xử lý thành nhiệm vụ riêng rồi chạy lại regression đầy đủ; không bỏ/disable test để đạt kết quả xanh.

Chưa kiểm thử trên database triển khai thật và trình duyệt/UI HR (B34/B35 không thuộc phạm vi). Người dùng cần duyệt SQL rồi tự lên lịch apply migration trước khi chạy code A35 trên database ứng dụng. Các SQL tests cũ opt-in khác không được bật nếu chưa có setting đúng tiền tố của chúng.

## 8. Git cuối phiên

`git status --porcelain=v1 --untracked-files=all`: 12 file `M`, 14 file `??`, danh sách đầy đủ và mục đích ở mục 3. Không có file staged, bị xóa, rename hoặc conflict.

`git diff --stat`: **12 files changed, 250 insertions(+), 10 deletions(-)**. Đây chỉ là tracked diff; 14 file mới chưa theo dõi không nằm trong thống kê này. Các dòng xóa là thay thế cấu hình/kiểm tra tương ứng A35, không xóa tính năng cũ. Bản diff tracked đầy đủ: `artifacts/a35-tests/a35-tracked.diff`.

`git diff --cached --name-only`: rỗng. `git diff --check`: thành công, không có lỗi whitespace. Nhánh và HEAD cuối vẫn là `feature/a35-application-stages` / `303142afb6d41d846cc954f709a233936f184f65`.

## 9. Bảo toàn phạm vi và mã cũ

Không triển khai A37/A40/B34/B35/B36, không sửa file Web, upload, OCR, Gemini hay thuật toán đánh giá. Thay đổi ApplicationEndpoints chỉ để bảo vệ history HR thuộc A35. Không xóa hoặc chép đè file cũ, không sửa migrations InitialCreate/SeedJobs, không đổi key/seed cũ. Không tạo/chuyển nhánh, merge, stash, reset, clean, rebase, cherry-pick, stage, commit, push hoặc mở PR.

## 10. Kết luận

**CHƯA SẴN SÀNG** cho xác nhận toàn bộ regression xanh: code A35 đã hoàn thành, build thành công và 87 test A35 pass, nhưng suite tổng thể còn một test UI Prompt lỗi có sẵn ở commit nền. Không sửa ngoài phạm vi để che lỗi này. Không áp dụng migration lên database ứng dụng; không stage/commit/push/merge hoặc mở PR. Công việc kết thúc ở báo cáo; người dùng tự quyết định xử lý lỗi ngoài phạm vi và các bước Git tiếp theo.
