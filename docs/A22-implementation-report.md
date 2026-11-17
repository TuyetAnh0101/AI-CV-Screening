# A22 — API kỹ năng và từ khóa Job

Ngày thực hiện: **17/11/2026**, Asia/Ho_Chi_Minh. Phạm vi chỉ A22; bàn giao để chủ dự án duyệt trước Git.

## 1. Nền Git, tài liệu và điều kiện triển khai

- Repository: `C:\TTCN\project\AI-CV-Screening`.
- Remote: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Nhánh: `feature/a22-job-skills-api`, tạo bằng `git switch --no-track -c feature/a22-job-skills-api origin/main` sau khi xác minh working tree/index sạch và fetch origin.
- **HEAD / SHA nền / origin/main đã fetch: `18cb5e096b1279379f6d3780d361fb29d63b67a5`**, commit `feat(A21): add job CRUD and recruitment status API (#67)`. Nhánh không có upstream và chưa có commit A22.
- Trước chuyển nhánh: HEAD A21 `5abf8b23fd50783a724204ea8850aa2c2d83fa36`, working tree sạch. Không sửa các worktree/nhánh nhiệm vụ khác, không có merge/rebase/cherry-pick dở dang.
- Đã kiểm tra source trên `origin/main`: A20 có unique index `UQ_Jobs_Title` và migration `20261117042418_JobTitleUniqueIndex`; A21 có request/CRUD/toggle, DTO thống kê, `Headcount`/`ClosingDate` và migration `20261117045748_JobHeadcountClosingDate` cùng Designer/ModelSnapshot. Đủ điều kiện làm A22 từ main; không đem A21 từ ZIP sang nhánh này.
- Đã đọc toàn bộ `C:\TTCN\A22_Codex_Prompt_20261117.md`, guide ZIP và sáu file mẫu A22 (Core/API/tests), đối chiếu các phần A20/A21/B21. ZIP SHA-256: `3E0C98598DA639AC0B4B27D6CC400C794E5A772B8154437741954A198FAC1327`. ZIP không chứa PNG/JPG/SVG/WebP/mockup; không tạo hình hoặc sửa frontend.

**Database dev thật chưa đủ schema A20/A21.** Chỉ SELECT metadata `SmartCvDb` trên `.\BB`: history mới đến `20261113014746_CvConsentRetention` (4 migration); chưa có `Headcount`/`ClosingDate`; `UQ_Jobs_Title` có `is_unique_constraint=1`, chưa phải unique index A20. Collation `SQL_Latin1_General_CP1_CI_AS`; hai FK JobSkillAliases/EvaluationSkills → JobSkills đều `NO_ACTION`. Bằng chứng: `artifacts/a22-live-schema-readonly.txt`.

Không chạy ứng dụng A22 trên database dev này để ghi/demo. Chủ dự án cần duyệt triển khai A20/A21 riêng trước khi chạy bản mới trên đó. A22 không tự sửa, migrate hoặc xử lý dữ liệu thật để vượt điều kiện này. Các database thử nghiệm A22 đều được áp dụng đủ 6 migration hiện có và xác minh schema thực.

## 2. API đã hoàn thành

Tất cả response ghi hợp lệ/lỗi có `Cache-Control: no-store`. Các API kỹ năng nhận JSON UTF-8, body tối đa 256 KiB; null/malformed/type sai/field ngoài hợp đồng trả lỗi tiếng Việt. `ProblemDetails.errors` dùng tên trường camelCase.

| Method | URL | Thành công | Lỗi chính |
| --- | --- | --- | --- |
| POST | `/api/jobs/{id}/skills` | 201 `JobSkillDto`, Location chứa mã đã chuẩn hóa | 400 validation; 404 Job; 409 trùng mã/alias theo SQL collation hoặc thay đổi FK đồng thời |
| PUT | `/api/jobs/{id}/skills/{code}` | 200 `JobSkillDto` | 400 validation/đưa SkillCode vào body; 404 Job/skill; 409 khi skill bị thay đổi/xóa đồng thời |
| DELETE | `/api/jobs/{id}/skills/{code}` | 204, không body | 400 id/code; 404 Job/skill; 409 khi có EvaluationSkills hoặc xung đột đồng thời |
| PUT | `/api/jobs/{id}/skills/{code}/aliases` | 200 `JobSkillDto` với toàn bộ aliases mới | 400 thiếu/null/sai aliases; 404 Job/skill; 409 xung đột SQL/concurrency |
| POST | `/api/jobs` | Giữ 201 A21; tùy chọn Job + skills + aliases nguyên tử | Giữ validation/trùng Title A21; lỗi skill/alias ở đúng field; 409 khi aliases tương đương theo SQL collation |

`GET /api/jobs` và `GET /api/jobs/{id}` giữ contract A21, trả kỹ năng/aliases đã lưu, `MustHaveCount`, `HasEvaluations` và thống kê theo chốt hiện có. Không thêm API xóa Job hoặc frontend.

Ví dụ tạo Job có hai kỹ năng bắt buộc và một kỹ năng ưu tiên:

```json
{
  "title": "Backend Developer — Hà Nội",
  "description": "Phát triển API tuyển dụng",
  "minGpa": 2.5,
  "minYearsExperience": 1,
  "headcount": 2,
  "closingDate": "2026-12-31",
  "skills": [
    { "displayName": "ASP.NET Core", "skillType": "MustHave", "aliases": ["dot net", "aspnet"] },
    { "skillCode": "SQL_SERVER", "displayName": "SQL Server", "skillType": "MustHave", "aliases": ["mssql"] },
    { "displayName": "Git", "skillType": "NiceToHave", "aliases": ["git workflow"] }
  ]
}
```

POST skill dùng cùng shape của một phần tử `skills`. PUT skill chỉ nhận `{"displayName":"ASP.NET Core API","skillType":"NiceToHave"}`. PUT aliases nhận `{"aliases":["dot net","aspnet"]}`; `{"aliases":[]}` xóa toàn bộ aliases, còn DisplayName/SkillCode vẫn tham gia match theo engine hiện có. Không có GET riêng tại Location của skill; đọc lại qua GET Job.

### Chuẩn hóa và validation

- SkillCode mới: chuẩn hóa IN HOA ASCII, bỏ dấu tiếng Việt/đ, các dải dấu câu/khoảng trắng thành `_`, bỏ `_` đầu/cuối. Mã cuối phải dài 2–50, bắt đầu bằng chữ/số; phần còn lại chỉ chữ/số/`_`. Mã client quá dài bị từ chối, không tự cắt.
- Bỏ trống mã: sinh từ tên, xử lý `# → SHARP`, `+ → PLUS`, bỏ dấu chấm trước chuẩn hóa; cắt gợi ý ở 50 và bỏ `_` cuối. Ví dụ C# → CSHARP; C++ → CPLUSPLUS; ASP.NET Core → ASPNET_CORE; Kiểm thử tự động → KIEM_THU_TU_DONG. Gợi ý rỗng/1 ký tự bị 400; gợi ý đụng mã đã có bị 409.
- DisplayName trim, bắt buộc, tối đa 100 UTF-16 code units phù hợp `nvarchar(100)`. SkillType chỉ đúng `MustHave` hoặc `NiceToHave`.
- Aliases: Unicode NFC, trim/gom whitespace, lowercase invariant, bỏ null/rỗng/trùng ordinal; tối đa 30 sau chuẩn hóa, mỗi mục tối đa 100. Alias mới bỏ trống/null tương đương danh sách rỗng. PUT thay danh sách bắt buộc gửi array; thiếu/null không tự xóa, `[]` là thao tác xóa rõ ràng.
- SQL collation vẫn là thẩm quyền cuối cho trùng khóa. Hai chuỗi khác ordinal như `ss`/`ß` có thể tương đương trong collation thật; được xử lý 409 và rollback, không lộ exception SQL.
- Route code chấp nhận khác hoa/thường, nhưng không tùy ý đổi dấu câu thành mã khác khi sửa/xóa. Giữ nguyên khóa lowercase của seed cũ, không đổi mã hoặc migrate seed. Lookup luôn có JobId; không tác động skill cùng mã của Job khác.
- PUT skill không có thuộc tính SkillCode và từ chối field đó. `PUT Job` chấp nhận thuộc tính `skills` để deserialize cùng DTO nhưng **bỏ qua toàn bộ kỹ năng**, kể cả danh sách rỗng; chỉ các endpoint skill riêng được phép sửa.
- POST Job không có/null skills vẫn hoạt động như A21. Không áp đặt thêm quy tắc server phải có MustHave; yêu cầu ít nhất một MustHave là hướng dẫn form B21 trong guide.

### Nguyên tử, concurrency và lịch sử

1. Validate tất cả kỹ năng trước khi track/ghi Job. Lỗi field có dạng `skills[1].skillType`; trùng mã sau normalize trả 400 `skills`. JSON lồng nhau sai kiểu có đường dẫn cụ thể như `skills[0].aliases[0]`.
2. Lưu cả graph bằng **một `SaveChangesAsync`**, sử dụng transaction mặc định của EF Core SQL Server. 201 chỉ trả sau khi transaction hoàn tất. SQL test đã quan sát được Job và JobSkill bên trong transaction trước khi cố ý gây lỗi INSERT alias; sau rollback tổng trở lại 5 Jobs / 29 skills / 52 aliases, không Job dở dang.
3. POST skill pre-check và xử lý SQL native 2601/2627. Batch có thể chứa cả lỗi PK skill và alias; ưu tiên phân loại PK skill để trả đúng 409 `skillCode`.
4. DELETE pre-check EvaluationSkills, rồi xóa aliases và skill trong cùng SaveChanges transaction. FK `NO_ACTION` là bảo vệ cuối nếu một EvaluationSkill vừa được commit sau pre-check: SQL 547 → 409, aliases bị xóa trước đó cũng rollback. Không cascade, không xóa EvaluationSkills.
5. Thay toàn bộ aliases dùng transaction Serializable trên SQL Server cho read/diff/write, giữ các dòng không đổi. Hai replacement cùng xuất phát từ danh sách rỗng không được trộn thành danh sách hợp nhất. Deadlock/native unique/FK/concurrency được trả 409 với hướng dẫn tải lại; bao gồm dạng lỗi 1205 do execution strategy EF bọc lại.
6. Không cập nhật Evaluations/EvaluationSkills, log HR, điểm/status của lượt cũ, không gọi chấm lại ngầm. Test chạy **RunScoringProcessor thật trên SQL** với evaluator offline trong test, tạo hai lượt lưu độc lập: Keyword **62,50 → 100,00** sau khi thêm `dot net`. Kết quả cũ/evidence giữ nguyên từng trường. SHA-256 snapshot tám bảng nghiệp vụ trước/sau sửa tiêu chí bằng nhau.
7. Audit hiện trạng: hệ thống không có criteria version/stale flag hoặc snapshot bất biến cho tên/loại kỹ năng. `HasEvaluations` cho phép UI cảnh báo. Worker tạo scope/processor riêng cho mỗi lượt; job profile được cache trong processor của lượt đó. Không đổi tiêu chí giữa lượt đang chạy; lượt mới đọc tiêu chí mới. Các lượt khôi phục sau restart có thể đọc cấu hình hiện tại vì hệ thống chưa snapshot tiêu chí.
8. **Giới hạn kế thừa:** một số màn hình đánh giá/A40 suy ra missing-must-have bằng SkillType hiện tại và hiển thị tên hiện tại. Sửa loại/tên không đổi điểm hoặc Evidence đã lưu, nhưng metadata/flag suy diễn của view cũ có thể đổi. Không tuyên bố metadata lịch sử đã được snapshot; thay đổi schema để version tiêu chí nằm ngoài A22 và chưa được thực hiện.

### Bảo mật và khác biệt có chủ đích so với ZIP

Chỉ map bốn route khi **Development và `CvUpload:Endpoint:Enabled=true`**, thống nhất A21. Production/Staging bật gate bị guard tải CV hiện có từ chối khởi động vì chưa có M6; khi gate tắt route skill trả 404. Header HR giả không cấp quyền. Không thêm xác thực giả hoặc mở ghi công khai; khi M6 được triển khai cần thay gate theo chính sách auth của dự án.

Giữ parser strict/body limit/no-store, Title conflict, precision số, ngày Việt Nam, DTO thống kê và seed/migration A21. Mẫu ZIP thiếu chốt truy cập, xử lý FK race lúc xóa và một số malformed/null handling; đã tích hợp vào source mới thay vì chép đè. Thêm Unicode NFC aliases, guard aliases-null khi replace, xử lý SQL collation và replacement đồng thời. Không log CV/email/điện thoại/prompt/raw AI/key. Không thêm package, sửa appsettings, bật worker/retention hoặc thay schema.

**Không có migration A22:** các entity, PK/FK và kích thước cột đã đủ. SQL tests xác nhận `HasPendingModelChanges=false`, đúng 6 migration đến A21, A20 vẫn là unique index; không sửa migration/snapshot/configuration/seed.

## 3. Kiểm thử và đối chiếu SQL

Môi trường: .NET SDK **10.0.403**, runtime **10.0.14**, EF Core **10.0.15**, SQL Server **16.0.1000.6 Developer Edition**, instance `.\BB`. Build/test dùng `artifacts/a22-build` để tránh DLL của bản demo đang chạy.

| Kiểm tra | Passed | Failed | Skipped | Kết quả/bằng chứng |
| --- | ---: | ---: | ---: | --- |
| Restore baseline | — | 0 | — | Thành công; `a22-baseline-restore.log` |
| Baseline build | — | 0 | — | 0 warning, 0 error; `a22-baseline-build.log` |
| Baseline full | 2.020 | 0 | 78 | 2.098 tổng, 2 phút 6 giây; A19/A20/A21 SQL bật |
| Focused A22 cuối | **58** | **0** | **0** | 14 giây; `a22-focused-verified.log` + TRX |
| Build sau tích hợp | — | 0 | — | **0 warning, 0 error**; `a22-build.log` |
| Full regression cuối | **2.078** | **0** | **78** | **2.156** tổng, 1 phút 30 giây; tăng đúng 58 test A22 |

58 test A22: **28 JobSkillRules**, **20 HTTP**, **3 SQL safety guard**, **7 SQL Server integration**. Bao gồm code gợi ý/giới hạn/Unicode/đ/emoji/dấu câu, alias 30×100 và NFC, engine thật, bốn endpoint, 201 Location, no-store, immutable key, legacy lowercase, id/code/cross-job, null/malformed/unpaired surrogate, nested JSON, body limit, unknown fields, type sai, trạng thái 400/404/409, production gate, PUT Job không ghi đè skill và graph validation.

Bảy SQL integration test sử dụng opt-in `SMARTCV_A22_TEST_SQL_CONNECTION`; guard yêu cầu prefix `SmartCv_A22_Test_`, cấm AttachDbFilename. Fixture luôn thay catalog bằng GUID mới, kiểm `DB_ID` chưa tồn tại trước CREATE, kiểm lại DB_NAME khi mở kết nối, áp dụng sáu migration chỉ trên catalog đó. Không dùng/xóa/reuse catalog có sẵn. Giữ lại toàn bộ database và CV tổng hợp sau test.

| SQL test | Bằng chứng thực tế |
| --- | --- |
| HTTP Kestrel + graph + hai lượt chấm | Tạo 2 MustHave/1 NiceToHave; SQL thấy đúng Job/skills/aliases; sửa tên/loại, alias/clear, add/delete, legacy code; 62,50→100; history hash bằng nhau |
| Hai POST skill đồng thời | Barrier sau cả hai pre-check; 2 arrivals, 1 lỗi native unique; đúng 1 skill/1 alias; 201/409 |
| Xóa skill vs EvaluationSkill mới | Pause sau pre-check, commit evaluation từ kết nối thứ hai, tiếp tục DELETE; native FK 547, HTTP 409; aliases và evaluation nguyên vẹn |
| Lỗi giữa transaction | Batch size 1 chỉ trong test; SQL đã có Job + Skill trong transaction trước THROW 51022 ở alias insert; cố ý HTTP 500 và xác nhận rollback toàn graph |
| Hai PUT aliases đồng thời | Cả hai tới SaveChanges sau read; 200/409; danh sách cuối đúng của request thành công, không hợp nhất |
| Alias tương đương theo SQL collation | SQL xác nhận `ss = ß`; thêm skill/tạo Job đều 409; không row dở dang, seed counts giữ nguyên |
| Validation graph và model | Skill sau sai type, mã normalize trùng, alias quá dài đều 400; SQL vẫn 5/29/52; không pending model/migration mới |

Evidence JSON theo catalog trong `artifacts/a22-sql/`: `*-catalog.json`, `*-http-sql.json`, `*-history-before-criteria.json`, `*-history-after-criteria.json`, `*-scoring-and-history.json`, `*-concurrent-insert.json`, `*-delete-evaluation-race.json`, `*-rollback-result.json`, `*-concurrent-alias-replace.json`, `*-alias-collation.json`, `*-validation.json`. Bằng chứng gồm hash/count/status, không đọc CV thật. Test cố ý HTTP 500 là fault injection để kiểm rollback; không phải failure của suite.

Các lỗi gặp trong vòng phát triển đều đã xử lý: thiếu namespace fixture khi build; test ban đầu chưa phản ánh guard startup Production/Staging; trùng VersionName của prompt tổng hợp; giả định soft-hyphen sai so với SQL collation; phân loại nhiều lỗi unique trong một batch và lỗi deadlock EF bọc. Các vòng cuối xác nhận lại các tình huống đó. Không giảm assertion hoặc skip test để che lỗi; test guard Production bật gate chuyển sang assert đúng exception M6 có sẵn.

Lệnh tái kiểm từ repository:

```powershell
dotnet restore SmartCv.slnx --artifacts-path artifacts/a22-build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/a22-build
$env:SMARTCV_A19_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A19_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A20_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A20_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A21_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A21_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
$env:SMARTCV_A22_TEST_SQL_CONNECTION='Server=.\BB;Database=SmartCv_A22_Test_OptIn;Integrated Security=True;TrustServerCertificate=True'
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --no-restore --artifacts-path artifacts/a22-build --filter 'FullyQualifiedName~JobSkill' --logger 'trx;LogFileName=a22-focused-verified.trx' --results-directory artifacts/a22-focused-results
dotnet test SmartCv.slnx --no-build --no-restore --artifacts-path artifacts/a22-build --logger 'trx;LogFileName=a22-full.trx' --results-directory artifacts/a22-full-results
```

Không bật các SQL fixture cũ có cleanup/delete; baseline và final giữ cùng 78 skipped đã có (73 SQL opt-in khác, 5 demo cần tài liệu ngoài repo). Không xem skipped là passed hoặc khẳng định mọi SQL suite khác đã chạy. Đối chiếu TRX: toàn bộ 2.098 test baseline giữ nguyên outcome (chuẩn hóa tên 12 case theory A21, một payload unknown chuyển skills → skillz); không mất test. A19 **44/44**, A20 **12/12**, A21 **52/52**, B38 **66/66**; A35 64 passed/7 skipped có sẵn. Các suite A8/A40 còn lại giữ outcome baseline. Chi tiết `artifacts/a22-test-summary.json`.


Fingerprint SQL cuối cho tám bảng nghiệp vụ: `92C01B8C5FBCA2C33294238BFD700526CF329F50C0C7B54E725E5303F98FF5D5`; file `artifacts/a22-sql/SmartCv_A22_Test_27779750ed5b4afca22e987f0d82aa82-scoring-and-history.json` xác nhận 62,50 → 100,00 và 0 Gemini calls. Tổng cộng **33 catalog A22 GUID mới** từ các vòng test được giữ nguyên; danh sách đầy đủ trong `artifacts/a22-final-test-catalogs.txt`.

## 4. Danh sách chính xác file M/A/D

**4 M / 8 A (file mới chưa stage) / 0 D = 12 file**. Manifest `artifacts/a22-commit-files.txt` có đúng một đường dẫn repo-relative mỗi dòng, không status/duplicate/artifacts/secret. SHA nền nằm trong `artifacts/a22-base-sha.txt`, UTF-8 không BOM, 40 ký tự một dòng.

| Trạng thái | File | Nội dung |
| --- | --- | --- |
| M | `src/SmartCv.Api/Endpoints/JobEndpoints.cs` | POST graph nguyên tử, validation skill/nested JSON, alias SQL conflict; giữ CRUD/toggle/GET A21 |
| M | `src/SmartCv.Api/Program.cs` | Đăng ký nhóm endpoint A22 có gate |
| M | `src/SmartCv.Core/Contracts/JobRequests.cs` | Skills tùy chọn cho POST; ghi rõ PUT không sửa skills |
| M | `tests/SmartCv.Tests/Jobs/JobCrudEndpointTests.cs` | Đổi payload unknown-field từ skills sang skillz vì skills đã hợp lệ ở A22; giữ assertion 400 và toàn bộ test còn lại |
| A | `src/SmartCv.Api/Endpoints/JobSkillEndpoints.cs` | Bốn API, parser strict, gate, 404/409, FK/unique/deadlock, transaction aliases |
| A | `src/SmartCv.Core/Contracts/JobSkillContracts.cs` | Create/update/replace DTO strict; mã immutable, clear aliases rõ ràng |
| A | `src/SmartCv.Core/Jobs/JobSkillRules.cs` | Normalize/suggest/validate code, tên, loại và aliases |
| A | `tests/SmartCv.Tests/Jobs/JobSkillEndpointTests.cs` | HTTP integration A22/atomic create/gate/backward compatibility |
| A | `tests/SmartCv.Tests/Jobs/JobSkillRulesTests.cs` | Unit rules/Unicode/limits và KeywordScoringService thật |
| A | `tests/SmartCv.Tests/Jobs/JobSkillSqlFixture.cs` | Guard GUID catalog, schema check, Kestrel, fingerprints và evidence không cleanup |
| A | `tests/SmartCv.Tests/Jobs/JobSkillSqlServerTests.cs` | SQL/FK/concurrency/rollback/score/history và safety guard |
| A | `docs/A22-implementation-report.md` | Báo cáo này, hợp đồng API và handoff B21 |

Không sửa Web/Razor/CSS/JS, A20/A21 migrations, snapshot/config/seed, appsettings hoặc dependency. Hai file SQL fixture/test mới cần thiết để kiểm chứng hành vi relational mà InMemory không kiểm tra được. Thay test unknown-field A21 là cập nhật tương thích A22, không triển khai thêm chức năng A21.

## 5. Handoff UX/UI B21 — đề xuất, chưa sửa frontend

Dùng token hiện có trong `src/SmartCv.Web/wwwroot/app.css`: Inter có dấu tiếng Việt; Deep Teal `--sc-brand-900` #0a2e2d và `--sc-brand-700` #0f766e; nền/surface/viền trung tính. AI Glow `--sc-grad-ai` #14b8a6→#06b6d4→#6366f1 chỉ nhấn nhẹ cho nội dung AI theo design system, tránh dùng cho thao tác kỹ năng thông thường.

1. Form tạo Job gồm thông tin chung và hai nhóm **Bắt buộc / Ưu tiên**. Mỗi dòng có tên hiển thị, mã, aliases dạng chip, sửa/xóa; counter kỹ năng và `x/30` từ khóa rõ ràng. Ít nhất một MustHave theo hướng dẫn UX B21; backend vẫn giữ tương thích Job không skill của A21.
2. Khi gõ tên mới, gợi ý mã theo đúng quy tắc ở mục 2; cho HR chỉnh mã trước khi tạo. Khi đã lưu, mã hiển thị cố định trong vùng chỉ đọc có giải thích ngắn; không gửi SkillCode trong PUT. Tên hiển thị vẫn là keyword dù aliases rỗng.
3. Tags nhận Enter hoặc dấu phẩy; trim, lowercase, bỏ trùng ở client để preview; server vẫn là nguồn validation chính. Giữ tiếng Việt, hiển thị bộ đếm ký tự/từ khóa, lỗi nằm cạnh chip/input; không âm thầm cắt alias quá dài. `[]` cần thao tác xóa toàn bộ rõ ràng; không gửi null để xóa.
4. Tạo Job gửi **một POST chứa graph**, giữ draft trong khi chờ; khóa nút Lưu khi đang gửi, không double-submit. Chỉ đóng form sau 201; hiển thị errors theo field lồng nhau. Nếu mất mạng sau gửi, GET kiểm tra danh sách trước khi HR gửi lại vì POST chưa có idempotency key.
5. Sửa Job: PUT scalar riêng; thêm/sửa/xóa skill và thay aliases qua bốn endpoint theo diff, giữ draft và báo trạng thái từng dòng. Backend không cung cấp transaction chung cho toàn bộ chuỗi edit; UI phải báo dòng đã lưu/chưa lưu khi một request lỗi, cho thử lại có kiểm soát. Không gửi skills vào PUT Job với kỳ vọng được cập nhật.
6. `HasEvaluations=true`: cảnh báo “Thay đổi tiêu chí áp dụng cho lượt chấm sau; điểm đã lưu giữ nguyên”. Nhắc không sửa khi một lượt đang chạy. Với hạn chế metadata lịch sử ở mục 2, không gắn nhãn “Tiêu chí lịch sử đã đóng băng”.
7. Xóa skill có lịch sử: hiển thị 409 ngay tại dòng, giữ nguyên skill/aliases/draft; gợi ý đổi loại hoặc từ khóa, không làm skill biến mất. Duplicate 409 focus vào mã; concurrency 409 tải lại dữ liệu trước khi HR áp dụng lại draft, không tự retry vô hạn.
8. Desktop: hai nhóm cạnh nhau nếu đủ rộng, card sạch và hàng thoáng; tablet chuyển một cột; mobile từ 320 px dùng card từng skill, chip wrap, nút thao tác dễ chạm ≥44 px, không bắt cuộn ngang bảng. Empty state “Chưa có kỹ năng” có nút thêm rõ ràng; loading/error khác empty.
9. Label gắn input, mô tả lỗi bằng chữ và `aria-describedby`; focus rõ bằng Deep Teal, tương phản văn bản ≥4.5:1; Enter thêm tag, Backspace xóa tag có focus, Esc hủy edit; thông báo lưu bằng `aria-live=polite`. Tôn trọng reduced motion, không dùng màu làm tín hiệu duy nhất, không glow/chuyển động liên tục.

## 6. Rủi ro, giới hạn và chốt bàn giao

- **Điều kiện triển khai còn thiếu trên SmartCvDb thật:** A20/A21 chưa được áp dụng. Báo rõ để chủ dự án quyết định; A22 không tự chạy migration hoặc sửa dữ liệu thật.
- Chưa có M6; API ghi chỉ dành cho Development có gate. Không triển khai production auth trong A22.
- Criteria version/stale snapshot và metadata lịch sử có giới hạn kế thừa như mục 2; không thêm migration hoặc tự chấm lại. Alias replacement dưới tranh chấp có thể trả 409 do Serializable; UI cần tải lại.
- Không benchmark tải lớn hoặc E2E frontend B21; đã kiểm tra SQL constraints, transaction và concurrency thực. API giữ body limit; lookup dựa trên JobId trước khi so mã, không tải toàn bộ catalog vào RAM.
- Build, focused và full regression cuối đều không có lỗi. Không quy 78 skipped có sẵn thành hoàn thành.

Trạng thái Git cuối: **feature/a22-job-skills-api; HEAD/base/origin/main `18cb5e096b1279379f6d3780d361fb29d63b67a5`; 4 M / 8 mới chưa stage / 0 D; index rỗng; không upstream, không merge/rebase dở dang**.

Các kiểm tra cuối: `git diff --check`, whitespace/EOF cả file mới, manifest khớp porcelain, index rỗng (`git diff --cached --name-status`), nhánh/HEAD/base/origin/main và không merge/rebase. Evidence cuối tại `artifacts/a22-git-verification.json`, `artifacts/a22-test-summary.json`, `artifacts/a22-final-test-catalogs.txt`.

Xác nhận: không ghi/migrate SmartCvDb, không drop/detach/delete catalog, không xóa CV/Job/Evaluations, không bật worker/retention, không gọi Gemini thật, không sửa secrets hay nhánh khác. Artifacts/log/TRX được ignore theo `.gitignore`, không nằm trong manifest. Không stage, commit, push, merge, rebase, reset, stash, clean, restore, pull hoặc tạo PR. **Dừng để chủ dự án review và tự thực hiện Git sau khi duyệt.**
