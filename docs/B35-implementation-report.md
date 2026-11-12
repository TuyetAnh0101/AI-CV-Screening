# Báo cáo triển khai B35 – Quyết định và lịch sử HR

Ngày thực hiện: 12/11/2026. Phạm vi: trang Chi tiết đánh giá CV và API Client phục vụ trang này.

## 1. Git, môi trường và dependency

- Repository: `TuyetAnh0101/AI-CV-Screening`.
- Nhánh: `feature/B35-hr-decision-history`.
- Commit nền và HEAD: `6cae92f98286e9099270f7e41036bd10c4596b75`, `fix(A35): harden database migration and runtime deployment (#53)`.
- Ban đầu working tree sạch, không staged/untracked/conflict. Đã kiểm tra origin, fetch `origin --prune`, xác nhận nhánh đích chưa tồn tại và tạo trực tiếp từ `origin/main`.
- Nền đã chứa A35 (#49), B34 (#50), Runtime Fix (#51), Stability (#52), hardening A35 (#53). Không merge nhánh khác; không đưa A37 chưa merge vào B35.
- Windows, .NET SDK `10.0.403` theo `global.json`, solution thực tế `SmartCv.slnx`.
- Restore vào output riêng vì thư mục artifacts mới cần assets; build/test dùng `artifacts/b35-build`, tránh DLL ứng dụng người dùng.

## 2. Đối chiếu ZIP và source mới nhất

Đã đọc mục B35 cùng phần A35/B34 liên quan trong `A35_B36/HUONG_DAN_A35_A37_A40_B34_B36.md`, mẫu StageTimeline, EvaluationDetail, CSS, API Client, contracts và các snippets liên quan. Source trên main mới hơn mẫu: có ExpectedStageVersion, xử lý lỗi API, trang chi tiết đầy đủ, bảo vệ dữ liệu và cơ chế nhớ HR của B34.

ZIP có 20 PDF demo trong `test-files/cv-20-backend`; không có ảnh mockup UI dạng PNG/JPG/SVG. Các PDF là dữ liệu ứng viên thử nghiệm. Test demo B34 đã mở và trích xuất nội dung cả 20 PDF; không dùng PDF làm mẫu giao diện. Không thêm CV vào Git.

Không ghi đè nguyên file bằng mẫu. Bỏ qua phần anonymity/ReviewFlag của B36 trong template; không triển khai B36. Hướng dẫn thao tác Git trong tài liệu mẫu không thay thế yêu cầu của người dùng: không stage/commit/push.

## 3. Bố cục trước và thiết kế mới

Trước khi sửa, trang có lưới các card điểm, AI, kỹ năng, metadata; chưa có quyết định hoặc timeline HR. Card AI ngắn nằm cạnh card điểm dài làm luồng đọc thiếu liên tục. Chèn thêm card theo lưới cũ sẽ đẩy quyền quyết định HR xuống dưới nội dung đánh giá.

Sau khi sửa:

- Giữ header ứng viên, chức danh, điểm, dự đoán, CV gốc và nút làm mới.
- Desktop: điểm, nhận xét AI, JSON và kỹ năng cùng cột trái; quyết định HR ở đầu cột phải, lịch sử ngay trong khối HR, metadata phía dưới.
- Dùng Deep Teal, font kế thừa Inter và token hiện có; badge sáu màu dùng chung StageText của B34. Form có nhãn rõ, focus outline, vùng bấm tối thiểu 44px, trạng thái disabled/loading/validation/error/success.
- Từ 1150px trở xuống chuyển một cột, đưa quyết định HR lên trước phần đánh giá. Mobile nút xếp dọc; chữ dài tự ngắt, ghi chú giữ xuống dòng.
- Timeline có vùng cuộn tối đa 640px, mobile 480px để nhiều quyết định không kéo lệch hai cột. Vùng cuộn có tên truy cập, nhận focus và thao tác bàn phím.

Không thay layout toàn ứng dụng hoặc Design System toàn cục.

## 4. Chức năng và tính nhất quán

- Hiện trạng thái đã xác nhận, lý do loại và lần cập nhật cuối theo UTC+7.
- Sáu giai đoạn: New, Reviewing, Shortlisted, Interview, Rejected, OnHold. Sáu lý do: MissingSkills, NotEnoughExperience, OtherRequirement, CandidateWithdrew, PositionFilled, Duplicate.
- Loại phải có lý do; rời Loại gửi reason null. Ghi chú tối đa 500 ký tự; tên HR bắt buộc, trim và nhớ bằng HrActorState/ProtectedSessionStorage hiện có.
- Chỉ bật Lưu khi hợp lệ, dữ liệu đã đồng bộ, StageVersion hợp lệ và giai đoạn/lý do thực sự đổi. Chỉ đổi ghi chú hoặc tên không tạo quyết định mới. Hủy chỉ reset form, không gọi PATCH.
- Gọi PATCH bằng ApplicationId, không dùng EvaluationId. Gửi đồng thời ExpectedStage và ExpectedStageVersion từ snapshot đã xác nhận.
- Sau PATCH, đọc lại evaluation và events. Không tự dựng event hoặc cập nhật thành công giả. Nếu không đọc lại được, khóa ghi và cung cấp nút tải lại dữ liệu; tải lại thành công xóa thông báo lỗi cũ.
- HTTP 409: báo HR khác vừa thay đổi, lấy trạng thái/version mới, giữ ghi chú để HR xem lại; không tự gửi lại quyết định cũ. 400/404 có thông báo riêng. 500/offline/timeout ghi nhận kết quả lưu chưa chắc chắn, không retry thao tác ghi.
- Timeline mới nhất trước, CreatedAt giảm dần, cùng thời điểm thì EventId giảm dần. Hiện từ/sang, lý do, ghi chú, HR, giờ Việt Nam; không có chức năng sửa/xóa lịch sử.
- Khi quay lại bảng xếp hạng, B34 tải dữ liệu từ API và hiển thị quyết định đã lưu. Không có cache giả đồng bộ hai trang.
- AI vẫn chỉ là gợi ý; không tự thay đổi giai đoạn.

## 5. API và database

Sử dụng API A35 hiện có:

| API | Mục đích |
|---|---|
| GET `/api/evaluations/{evaluationId}` | Đọc snapshot, ApplicationId, StageVersion |
| PATCH `/api/applications/{applicationId}/stage` | Lưu quyết định với hai điều kiện concurrency |
| GET `/api/applications/{applicationId}/events` | Timeline đã lưu |
| GET `/api/runs/{runId}/ranking` | B34 đọc lại stage và số đếm |

Không đổi backend endpoint, contract, entity, DbContext, migrations hoặc SQL. Không chạy migration trên database ứng dụng. B35 yêu cầu môi trường triển khai đã có schema A35; báo cáo này không xác nhận readiness của database ứng dụng người dùng.

Browser dùng SQL Server LocalDB thử nghiệm riêng: `SmartCv_Runtime_Test_39f68552fb1446bd8b0b1f97e747b940`, dữ liệu synthetic từ fixture thử nghiệm đã có. API readiness trên database này thành công. EvaluationId=4 nhưng ApplicationId=1: thực nghiệm xác nhận lưu đúng hồ sơ dù hai ID khác nhau. Chỉ dữ liệu synthetic này được thay đổi. Worker chấm điểm tắt, cấu hình Gemini fake với địa chỉ loopback không phục vụ AI; không gọi Gemini thật, không sửa secrets hoặc ConnectionStrings lưu trong source.

## 6. Danh sách chính xác file sửa/mới

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor` | Sửa | Nhóm nội dung đánh giá, thêm cột HR/history, giữ metadata và CV gốc |
| `src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css` | Sửa | Lưới desktop và thứ tự responsive riêng trang chi tiết |
| `src/SmartCv.Web/Services/ApplicationApiClient.cs` | Sửa | Thêm GetEventsAsync có CancellationToken |
| `src/SmartCv.Web/Components/Results/HrDecisionPanel.razor` | Mới | Form HR, validation, đồng bộ sau lưu, conflict/error/retry đọc |
| `src/SmartCv.Web/Components/Results/HrDecisionPanel.razor.css` | Mới | Style scoped của form, trạng thái và vùng cuộn lịch sử |
| `src/SmartCv.Web/Components/Results/StageTimeline.razor` | Mới | Lịch sử chỉ đọc, sắp xếp mới nhất và giờ Việt Nam |
| `src/SmartCv.Web/Components/Results/StageTimeline.razor.css` | Mới | Timeline, badge, ngắt dòng ghi chú |
| `tests/SmartCv.Tests/B35DecisionTests.cs` | Mới | 11 test case B35: validation, identity, version, lỗi API, khôi phục và timeline |
| `docs/B35-implementation-report.md` | Mới | Báo cáo này |

Ba file đang sửa và sáu file mới đều thuộc B35. Không sửa source B34, A35, upload/OCR/Gemini/Worker, không thêm package hoặc sửa cấu hình production.

## 7. Build và kiểm thử cuối

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/b35-build --no-restore
$env:SMARTCV_B34_DEMO_ZIP='C:\TTCN\A35_A37_A40_B34_B36_SmartCv.zip'
dotnet test SmartCv.slnx --artifacts-path artifacts/b35-build --no-restore --no-build --filter 'FullyQualifiedName~B35DecisionTests|FullyQualifiedName~EvaluationDetailUiTests|FullyQualifiedName~B34StageUiTests' --logger 'trx;LogFileName=focused.trx' --results-directory artifacts/b35-tests
dotnet test SmartCv.slnx --artifacts-path artifacts/b35-build --no-restore --no-build --logger 'trx;LogFileName=full.trx' --results-directory artifacts/b35-tests
```

Build thành công: **0 warning, 0 error**.

| Nhóm | Passed | Failed | Skipped | Ghi chú |
|---|---:|---:|---:|---|
| B35 component/client unit tests mới | 11 | 0 | 0 | Nằm trong focused và full |
| Focused B35 + EvaluationDetail + B34 | 55 | 0 | 0 | Có demo 20 PDF và API integration B34 |
| Full regression | 1368 | 0 | 42 | Tổng 1410; gồm các nhóm trên |
| Browser + API + SQL thực nghiệm B35, chạy cuối và lặp | 5 | 0 | 0 | Bốn lần chạy riêng, một lần bổ sung khi full suite chạy; mỗi lần ba viewport |

Không cộng các hàng chồng lấp thành tổng test. Không tính skipped là passed.

42 skipped là các bộ SQL opt-in chưa cấu hình connection thử nghiệm tương ứng: A35=7, A15=7, A16=10, FullAudit=2, Prompt=5, A11=6, RuntimeScriptDeployment=1, A17=3, RuntimeSchema=1. Không tự bật những bộ tạo/drop database hoặc Worker chỉ để có số xanh. Browser/API/SQL B35 đã chạy riêng trên database synthetic, không thay thế kết luận cho 42 case này.

Các tình huống xác nhận:

- Chưa có lịch sử: empty state đúng; không tuyên bố đã có quyết định.
- Không đổi trạng thái/lý do, note-only, hủy: không gọi ghi, không tạo event.
- Loại thiếu reason bị chặn; note 501 ký tự bị chặn; Unicode 500 ký tự lưu và đọc lại đúng.
- Chuyển sang Reviewing xóa reason; mở lại hồ sơ đã loại thành công.
- PATCH đúng ApplicationId và gửi ExpectedStage/ExpectedStageVersion.
- Nhiều event, thứ tự mới nhất kể cả trùng timestamp; chuyển UTC sang giờ Việt Nam.
- HTTP 400/404/500, mất mạng và timeout: mock HTTP xác nhận không retry, không dựng event giả.
- History GET lỗi: chặn ghi và hiển thị lỗi thay vì empty giả. Đồng bộ sau lưu lỗi: khóa ghi; retry đọc phục hồi mở lại form.
- Hai HR: browser tạo thay đổi thực bằng API trước khi submit form cũ; nhận 409, tải version mới, không ghi đè; HR chủ động chọn lại mới lưu tiếp.
- Reload giữ dữ liệu và tên HR; quay về Ranking thấy stage Interview; mở lại chi tiết từ Ranking vẫn đúng.
- Chromium headless 1440/768/390px: không document overflow ngang, form và vùng timeline nhận focus; không có PageError JavaScript/Blazor được browser ghi nhận.

Trong lúc phát triển có hai assert unit không thấy thông báo vì harness gọi private handler qua reflection mà chưa render lại; đã sửa harness thực hiện render, giữ nguyên assert. Khi browser chạy đồng thời với full regression, hai lần điều hướng về Ranking còn ở loading khi hết ngưỡng chờ 5 giây và 15 giây. Đã thu thêm log API/Web; sau khi full suite hoàn tất, lần chạy riêng và ba lần lặp liên tiếp đều pass cùng assertion stage/identity, không sửa code Ranking. Bổ sung một lần chạy đồng thời có trace cũng pass trên build cuối. Đối chiếu binary Web trước B35: năm lượt đi/về Detail/Ranking cũng pass. Chưa xác định được nguyên nhân gốc của hai lần chờ này; không kết luận chỉ tăng timeout là đã sửa lỗi. Không sửa test cũ để lấy xanh.

## 8. Bằng chứng và ảnh trước/sau

Output dưới `artifacts/` đã bị Git ignore, chỉ để duyệt cục bộ:

- `artifacts/b35-build-final.log`, `artifacts/b35-focused.log`, `artifacts/b35-full.log`.
- `artifacts/b35-tests/focused.trx`, `artifacts/b35-tests/full.trx`.
- `artifacts/b35-tests/browser-evidence.json`, `artifacts/b35-browser-final.log`.
- `artifacts/b35-browser-repeat-1.log` đến `b35-browser-repeat-3.log`, `artifacts/b35-tests/browser-trace-1.log` đến `browser-trace-3.log`.
- `artifacts/b35-browser-concurrent.log`, `artifacts/b35-tests/browser-concurrent-trace.log`, `artifacts/b35-baseline-navigation.log`.
- [Desktop trước B35](../artifacts/b35-tests/detail-desktop-before.png), [Desktop sau B35](../artifacts/b35-tests/detail-desktop-after.png).
- [Tablet 768px](../artifacts/b35-tests/detail-768-after.png), [Mobile 390px](../artifacts/b35-tests/detail-390-after.png).

Ảnh trước lấy từ binary Web trước B35 còn sẵn ở artifacts, đối chiếu markup trang cũ; cùng dữ liệu synthetic, không phải ảnh production hoặc mockup. Ảnh sau lấy từ build B35 cuối. Browser helper và project thử nghiệm nằm `artifacts/b35-browser`, không thuộc file cần commit. Không thêm binary, CV, SQL backup hoặc API key vào Git.

## 9. Giới hạn và hồi quy

- Hồi quy B34/A35 đã chạy trong suite, luồng thực SQL/API chứng minh lưu stage, lịch sử, 409 và đọc lại Ranking. Không đổi implementation của các nhiệm vụ này.
- Không thử trên database ứng dụng hoặc dữ liệu ứng viên thật. Không triển khai migration A35 trong nhiệm vụ B35.
- Không chạy Gemini thật, không bật Worker và không chạy lại xử lý OCR/AI end-to-end; các chức năng đó ngoài thay đổi B35.
- Lỗi offline/timeout/400/404/500 của form kiểm chứng bằng fake HTTP; browser dùng API thật cho thành công/409, không giả định các lỗi hạ tầng production đã được tái hiện.
- Kiểm tra responsive bằng Chromium, chưa kiểm tra Safari/Firefox, thiết bị vật lý hoặc screen reader. Timeline nhiều dữ liệu dùng cuộn cục bộ; API lịch sử hiện chưa phân trang, B35 không đổi contract.
- Hai lần chờ điều hướng Ranking khi chạy song song bộ test chưa được giải thích bằng root cause. Bốn lần browser chạy riêng cuối và lần bổ sung có trace khi chạy đồng thời đều pass; cần theo dõi khi máy tải cao, không dùng kết quả này để khẳng định toàn hệ thống hết lỗi loading.
- Tên HR dùng cơ chế phiên hiện tại, chưa phải danh tính tài khoản được xác thực. B35 giữ nguyên giới hạn này của hệ thống.

## 10. Git cuối và kết luận

`git status --short --branch`:

```text
## feature/B35-hr-decision-history...origin/main
 M src/SmartCv.Web/Components/Pages/EvaluationDetail.razor
 M src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css
 M src/SmartCv.Web/Services/ApplicationApiClient.cs
?? docs/B35-implementation-report.md
?? src/SmartCv.Web/Components/Results/HrDecisionPanel.razor
?? src/SmartCv.Web/Components/Results/HrDecisionPanel.razor.css
?? src/SmartCv.Web/Components/Results/StageTimeline.razor
?? src/SmartCv.Web/Components/Results/StageTimeline.razor.css
?? tests/SmartCv.Tests/B35DecisionTests.cs
```

`git diff --stat` (chỉ tracked, chưa gồm sáu file mới):

```text
 src/SmartCv.Web/Components/Pages/EvaluationDetail.razor     | 5 +++++
 src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css | 5 +++--
 src/SmartCv.Web/Services/ApplicationApiClient.cs            | 3 +++
 3 files changed, 11 insertions(+), 2 deletions(-)
```

`git diff --check`: không có lỗi whitespace. `git diff --cached --name-status`: rỗng, không staged. HEAD vẫn bằng commit nền. Danh sách file mới được liệt kê đầy đủ ở mục 6 và status trên; artifacts bị ignore, không nằm trong diff cần commit.

Không có thay đổi ngoài B35, không xóa code cũ, không thay thông tin điểm/AI/kỹ năng/metadata/CV gốc. Không tự stage, commit, push, merge, rebase hoặc tạo PR.

**SẴN SÀNG CHO NGƯỜI DÙNG DUYỆT VÀ COMMIT** trong phạm vi B35, với các giới hạn kiểm chứng ở mục 9. Dừng tại báo cáo; người dùng tự quyết định các thao tác Git tiếp theo.
