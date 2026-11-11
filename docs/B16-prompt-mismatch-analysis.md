# B16 — Phân tích CV mà AI đánh giá khác HR

## A. Git và phạm vi

- Repository/worktree: `D:\Projects\AI-CV-Screening` (remote `TuyetAnh0101/AI-CV-Screening`).
- Nhánh: `feature/B16-prompt-mismatch-analysis`, tạo từ `origin/main` sau khi fetch.
- Base và HEAD: `f85cada3213a805f83afd4aaa3619abf67d96233` (merge PR #46 của B15). B12–B15 đã có trên nền này.
- Khi bàn giao: 0 staged, 7 file tracked sửa, 5 file mới chưa stage (gồm tài liệu này).
- Không merge/rebase/cherry-pick/conflict đang dở. Không commit/push/tạo PR/merge.
- ZIP đọc được và giải nén tại `%TEMP%\SCApp-B16-reference`, ngoài repository. Không chép đè source mẫu.

## B. Chức năng

- Thêm B16 bên dưới bảng B15. Nút **Xem CV lệch** truyền `RunId`, không dùng `PromptVersionId` thay thế.
- Mặc định chọn lượt Completed gần nhất của phiên bản active; nếu chưa có, chọn Completed gần nhất toàn bộ. Sắp theo CompletedAt (fallback CreatedAt), rồi RunId giảm dần. Không có lượt hợp lệ thì hiện empty state.
- Ma trận: hàng AI, cột HR; TP/TN đúng, FN là Bỏ sót, FP là Chọn nhầm.
- Chỉ tính hồ sơ Completed, trích xuất không Failed, có nhãn HR và PredictedPass. Không đổi null/Pending/Failed thành Không đạt. KeywordOnly hợp lệ vẫn được tính, có chú thích riêng.
- Đơn vị backend là ApplicationId (CV ứng tuyển một Job). Loại tất cả dòng của ApplicationId bị lặp, tránh chọn tùy tiện hoặc đếm trùng cùng hồ sơ.
- Kiểm tra IsMatch/MismatchType với nhãn gốc. Nếu mâu thuẫn: cảnh báo, ma trận vẫn dựa vào các nhãn hợp lệ, không đưa dòng mâu thuẫn vào danh sách lệch và không sửa DTO.
- Danh sách chỉ nhận các dòng dự đoán hợp lệ, IsMatch=false, loại lệch nhất quán; hiển thị ứng viên/file/Job, nhãn, điểm, FitLabel, Reasoning và HrNote gốc. Thiếu thông tin có lời mô tả rõ ràng.
- Ba bộ lọc có số lượng, lọc cục bộ, không thay ma trận và không gọi API. Đổi lượt hoặc làm mới trả về bộ lọc Tất cả.
- Chỉ hiện liên kết B9 `/results/evaluations/{id}` sau GET xác minh EvaluationId, RunId, ApplicationId và RunType=PromptTest. Kiểm tra tối đa 4 yêu cầu đồng thời; ID thiếu/sai/404/lỗi thì không hiện liên kết chết. Có thể làm mới để kiểm tra lại.
- Khi đổi lượt: xóa dữ liệu cũ ngay, hủy request trước, kiểm tra thế hệ request trước khi áp dụng kết quả. Dispose hủy request. Không thêm polling; dùng tín hiệu refresh sẵn có từ B14.
- Chỉ kết luận “AI khớp HR ở tất cả” khi Completed, đủ số hồ sơ, không trùng, không thiếu nhãn/dự đoán, không có mâu thuẫn và không có FP/FN. Lượt chưa hoàn thành không hiện ma trận như kết quả hoàn chỉnh.
- B16 chỉ GET API thật qua PromptApiClient. Không bật fake, không tạo lượt, không gọi Gemini, không kích hoạt hay thay đổi dữ liệu chấm cũ. B12 editor/draft và logic B13–B15 được giữ nguyên.

## C. File

| File | Trạng thái | Mục đích trong B16 |
|---|---|---|
| `src/SmartCv.Web/Components/Prompts/MismatchTable.razor` | Mới | Tải đúng lượt, ma trận, bộ lọc, ghi chú, trạng thái và xác minh liên kết |
| `src/SmartCv.Web/Components/Prompts/MismatchTable.razor.css` | Mới | Card, ma trận, badge, bảng responsive, focus |
| `src/SmartCv.Web/Helpers/MismatchView.cs` | Mới | Phân loại, loại trùng, kiểm tra cờ, chọn mặc định, lọc và kiểm tra chi tiết |
| `src/SmartCv.Web/Components/Pages/Prompts.razor` | Sửa | Nối sự kiện B15 với B16, tái sử dụng tín hiệu B14 |
| `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor` | Sửa | Thêm thao tác chọn RunId và trạng thái aria-pressed |
| `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor.css` | Sửa | Style nhỏ cho nút tích hợp B16 |
| `src/SmartCv.Web/Services/PromptApiClient.cs` | Sửa | Ba phương thức GET với DTO A15/A16 hiện có |
| `tests/SmartCv.Tests/MismatchAnalysisTests.cs` | Mới | 27 trường hợp unit/component/HTTP fixture cho B16 |
| `tests/SmartCv.Tests/AccuracyComparisonTests.cs` | Sửa | Kiểm tra nút B16 và callback truyền đúng RunId |
| `tests/SmartCv.Tests/PromptVersioningUiTests.cs` | Sửa | Cho phép GET lịch sử B16, vẫn cấm lưu prompt tự kích hoạt/tạo lượt |
| `tests/SmartCv.Tests/PromptActivationUiTests.cs` | Sửa | Cho phép GET lịch sử B16, vẫn cấm kích hoạt tự tạo lượt |
| `docs/B16-prompt-mismatch-analysis.md` | Mới | Bàn giao, bằng chứng và giới hạn kiểm thử |

Không sửa API/backend, schema/migration, Worker, Gemini, OCR, Docker, Results hoặc nội dung PromptEditor.

## D. Kiểm thử thực tế

Build mặc định `dotnet build --no-restore` thất bại vì DLL Core/Infrastructure trong output API/Web bị ứng dụng đang chạy giữ khóa (MSB3021/MSB3027). Không dừng các tiến trình của người dùng. Thử artifacts ngoài repository cần restore mới và gặp lỗi NuGet TLS NU1301. Chạy lại với output riêng **trong** repository để các test tìm được root và test-files:

```powershell
Set-Location -LiteralPath 'D:\Projects\AI-CV-Screening'
dotnet build --no-restore '-p:BaseOutputPath=D:\Projects\AI-CV-Screening\artifacts\B16\'
$env:Logging__EventLog__LogLevel__Default = 'None'
dotnet test --no-build --no-restore '-p:BaseOutputPath=D:\Projects\AI-CV-Screening\artifacts\B16\' --logger 'trx;LogFileName=B16-regression.trx' --results-directory "$env:TEMP\SCApp-B16-testresults"
git diff --check
```

- Build cuối: **0 warning, 0 error**.
- Suite cuối: **1219 pass / 0 fail / 31 skip / 1250 tổng**, khoảng 1 phút 6 giây. 31 test SQL Server được framework skip vì chưa cấu hình database test; không coi là pass.
- Gồm **27 test B16**, **17 test B15/tích hợp**, cùng regression suite có sẵn của B7–B14.
- `git diff --check`: đạt. Có thông báo Git chuyển LF sang CRLF ở một số file, không có lỗi whitespace.
- Fixture xác minh TP=5, FP=1, FN=1, TN=3; Bình 66 điểm Bỏ sót, Em 72,5 điểm Chọn nhầm; lọc bằng event Blazor; đổi sang lượt khác 3 dòng lệch; ma trận không đổi khi lọc.
- Các test khác: thiếu HR/prediction, Pending/Failed/extraction Failed, KeywordOnly, trùng ApplicationId, cờ mâu thuẫn, thiếu dòng/tổng 0, all-match có điều kiện, default active/fallback, lỗi API/sai RunId, ID chi tiết 0/sai ownership, đổi lượt liên tiếp, dispose, refresh B14 và không POST.
- Lượt thử đầu phát hiện assertion cũ cấm cả GET lịch sử; đã điều chỉnh đúng phạm vi đọc của B16 và chạy lại toàn suite thành công.
- Không chạy Gemini thật. Không kiểm thử ghi trên database thật. Read-only smoke API cục bộ gặp HTTP 307 từ 5156 sang HTTPS 7269; chứng chỉ tự ký không được môi trường công cụ tin cậy (`DEPTH_ZERO_SELF_SIGNED_CERT`). Không tắt xác minh TLS hoặc thay cấu hình ứng dụng. Chưa xác minh end-to-end bằng database đang chạy.

Log cuối: `%TEMP%\SCApp-B16-build-final.log`, `%TEMP%\SCApp-B16-tests-final.log`; TRX: `%TEMP%\SCApp-B16-testresults\B16-regression.trx`.

## E. Giao diện

Đã mở trực tiếp cả bốn ảnh ZIP: `1-ket-qua-xep-hang.png`, `2-ket-qua-chi-keyword.png`, `3-chi-tiet-cv.png`, `4-prompt-do-chinh-xac.png`. Ảnh 4 là tham khảo chính.

Giữ vị trí dưới B15 và ngôn ngữ teal của SCApp. Bổ sung chú thích TP/TN/FP/FN, độ đầy đủ dữ liệu, phân biệt nguồn AI/HR, badge keyword, loading/error/empty, nhãn bảng và focus bàn phím. Dùng CSS variables hiện có; không thêm chart/JS/thư viện cho tính năng.

Ảnh chụp từ **component thực và CSS cô lập đã build**, sử dụng **HTTP fixture kiểm thử**, không phải dữ liệu SQL/Gemini thật hay ảnh toàn trang ứng dụng:

- `%TEMP%\SCApp-B16-desktop.png`: viewport 1440px, document rộng 1425px (trừ scrollbar).
- `%TEMP%\SCApp-B16-tablet.png`: viewport 768px, document rộng 753px.
- `%TEMP%\SCApp-B16-mobile.png`: viewport 390px, document rộng 375px.

Đã xem trực tiếp các ảnh. Không tràn ngang toàn trang; bảng CV rộng tối thiểu 990px và cuộn trong vùng được gắn nhãn, focus được. Matrix và bộ lọc tự xếp lại. Bộ dựng ảnh ban đầu dùng CSS chưa cô lập gây tràn giả; đã sửa bộ dựng ảnh để dùng đúng CSS ứng dụng, không sửa trang khác.

## F. Rủi ro và giới hạn

- API PromptTest hiện chỉ map trong Development khi CvUpload endpoint Enabled; B16 báo lỗi khi endpoint không sẵn sàng, không tự bật fake.
- HR labels hiện tại có thể khác lúc chạy; B16 hiển thị đúng dữ liệu API hiện tại, không tự tạo snapshot lịch sử HR.
- Lượt Completed vẫn có thể thiếu dự đoán hoặc HR đã bị xóa. UI báo mức đầy đủ, không khẳng định AI đúng toàn bộ.
- Xác minh B9 phát sinh thêm GET cho các dòng lệch, tối đa 4 đồng thời. Không tải file CV, không ghi log nội dung CV/nhận xét trong code B16.
- B9 hiện có breadcrumb quay về Results. B16 giữ nguyên trang B9; chưa thêm đường quay về Prompt.
- B14 hiện dùng các DTO PromptTest cũ cho tiến độ; B16 dùng phương thức mới với DTO A15/A16 nullable để tránh thay đổi B14. Không sửa vấn đề hợp đồng ngoài phạm vi này.
- Chưa xác minh live SQL/Gemini hoặc toàn trang qua Blazor circuit với dữ liệu thật do giới hạn HTTPS/database test nêu trên. Kiểm thử tương tác component và regression đã chạy; ảnh fixture không thay thế kiểm thử end-to-end thật.

## G. Git do người dùng thực hiện

Chỉ stage đúng 12 file trong mục C, kiểm tra staged diff rồi commit `feat(B16): implement AI HR mismatch analysis`, push `feature/B16-prompt-mismatch-analysis` và tạo PR vào `main`. Codex chưa thực hiện các thao tác này. Các lệnh cụ thể nằm trong báo cáo bàn giao.
