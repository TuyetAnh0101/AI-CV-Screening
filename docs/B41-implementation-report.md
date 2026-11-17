# B41 — Số đo Prompt và duyệt nhãn HR đề xuất

Ngày: 17/11/2026 (Asia/Saigon). Phạm vi duy nhất: B41. Báo cáo bàn giao thay đổi chưa stage/commit.

## Phạm vi và bố cục

Tiếp tục trực tiếp trên `feature/b41-prompt-metrics`, HEAD/base/origin/main được kiểm tra lúc bắt đầu đều là `3cba955a8f4e3017c7d881514175e655413b900c`. Không có upstream, staged files hay merge/rebase/cherry-pick. Có đúng 3 file M và 5 file mới của Claude, đều thuộc B41. Không chuyển nhánh, tạo worktree hoặc thao tác Git xuất bản.

Wireframe: A — giữ editor, lịch sử, kích hoạt và playground B39; B — chọn/tạo bộ CV, bảng nhãn thủ công, panel duyệt đề xuất với bốn số tổng hợp và nút lưu chủ động; C — luồng chạy đánh giá hiện có; D — bảng tỷ lệ/Wilson, ma trận HR × AI của lượt được chọn và khung kết luận bên cạnh (stack trên màn hình hẹp), rồi danh sách CV lệch B16. Bảng dài cuộn trong vùng riêng.

Luồng đề xuất: chưa tải → GET → New tích sẵn / Same khóa / Conflict chủ động duyệt → xác thực tên HR → PUT chỉ dòng chọn → GET cập nhật nhãn và thống kê. Timeout/lỗi có thể đã ghi: khóa ghi, GET nhãn và đề xuất trước khi cho chọn lại. Không tự gửi lại PUT.

## Nguồn đã đối chiếu

Đọc prompt giao việc, nội dung guide và mẫu B41/B42/A36/A41 trong ZIP bằng ZipArchive; không sao chép file mẫu vào source. ZIP không có ảnh tham chiếu UI. DTO và kiến trúc mẫu khác repo: dùng `PromptApiClient` thật, không thêm IPromptApi/FakePromptApi. Backend A36/A41 và PUT A15 đã có, không sửa backend.

## Code Claude được bảo toàn và tiếp tục

Đã kiểm tra cả 8 file đang dở trước khi chỉnh sửa. Build lần đầu của chính working tree đó thành công, 0 warning / 0 error; baseline cũ không được dùng thay cho lần build này.

| File Claude đã sửa/tạo | Phần tiếp tục hoàn thiện |
|---|---|
| `Components/Prompts/AccuracyCompare.razor` | Giữ B15 Latest/Matches/Best và nút B16 theo RunId; giữ dữ liệu hợp lệ khi refresh lỗi, bỏ ranking stale, hủy request và cập nhật focus/kết luận. |
| `Components/Prompts/AccuracyCompare.razor.css` | Typography, spacing, loading state, touch target, bố cục ma trận/kết luận. |
| `Components/Prompts/ConfusionMatrix.razor` | Giữ chiều HR hàng / AI cột; chuyển giải thích ưu tiên ra khỏi ô, Unscored riêng, diễn giải KeywordOnly không cộng thêm. |
| `Components/Prompts/ConfusionMatrix.razor.css` | Số 24/26px và 28px, bốn ô bằng nhau, chữ phụ 12px, nền/viền theo ý nghĩa. |
| `Helpers/AccuracyText.cs` | Giữ định dạng vi-VN/CI từ API; kiểm tra ma trận dùng tổng long để tránh overflow. |
| `Helpers/ComparisonConclusion.cs` | Bổ sung chặn kết luận trái hướng hoặc CI chồng nhau dù API báo Conclusive. |
| `Helpers/LabelSuggestionSelection.cs` | Thêm xác minh quyết định, ẩn danh, eligibility từ DTO thật; khóa duplicate và sự kiện chọn cũ sau khi quyết định đổi. |
| `Services/PromptApiClient.cs` | Giữ phương thức GET đề xuất của Claude, dùng lại PUT/compare có sẵn; không thay kiến trúc B39. |

Các path trong bảng trên nằm dưới `src/SmartCv.Web/`. Không thay nguyên file bằng mẫu ZIP, không xóa phần làm dở.

## Tính năng và đường dẫn triển khai

1. [AccuracyCompare](../src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor): tỷ lệ quan sát, Wilson 95%, Đúng/tổng nhãn, Bỏ sót, Chọn nhầm, Unscored và KeywordOnly (kể cả 0). Thiếu dữ liệu không thành 0. Bảng chỉ chứa lượt Completed hợp lệ gần nhất mỗi phiên bản trong cùng bộ, giữ quy tắc công bằng B15 và phát đúng RunId sang B16.
2. [ConfusionMatrix](../src/SmartCv.Web/Components/Prompts/ConfusionMatrix.razor): HR Đạt/Không đạt theo hàng; AI Đạt/Không đạt theo cột; dùng nguyên Matrix API và kiểm tra khớp tổng. Unscored ngoài bốn ô nhưng ở mẫu số theo A16/A41. KeywordOnly là thuộc tính nguồn điểm, không cộng thêm và không mặc định cho rằng mọi dòng KeywordOnly có dự đoán hợp lệ.
3. [ComparisonConclusion](../src/SmartCv.Web/Helpers/ComparisonConclusion.cs): các trạng thái không có lượt, thiếu hai phiên bản, thiếu bản đang dùng, chọn chính lượt mốc, không công bằng, lỗi, stale, thiếu CI, inconclusive và conclusive. So lượt chọn với lượt Completed hợp lệ mới nhất của bản đang dùng. Không gọi compare cùng RunId; không tự kết luận từ chênh lệch %. Conclusive chỉ hiển thị khi API cho phép và dữ liệu/CI nhất quán; dùng Summary/ConclusionNote của API.
4. [LabelSuggestionPanel](../src/SmartCv.Web/Components/Prompts/LabelSuggestionPanel.razor) và [LabelSuggestionWorkspace](../src/SmartCv.Web/Helpers/LabelSuggestionWorkspace.cs): GET không ghi, New tích sẵn, Same khóa, Conflict vàng và phải tự tích. Bulk chỉ chọn New. Chỉ một PUT sau thao tác lưu chủ động; validate actor, giới hạn 100/500/2000, loại duplicate/trạng thái lạ. Hiển thị Added/Updated/Unchanged/LabelCount và warnings sau xác nhận thành công.
5. Hồ sơ được đối chiếu với `LabelCandidateDto` từ endpoint applications khi tải và ngay trước PUT: chỉ Completed, có extracted text, không có tên file ẩn danh, không trùng ID. Nếu hồ sơ biến mất/không sẵn sàng hoặc GET xác minh lỗi, không gửi PUT; yêu cầu tải lại. Không sửa DTO/backend.
6. HTTP 400/429 có lỗi rõ, giữ lựa chọn; 429 tôn trọng Retry-After nếu có. 409 hoặc lỗi có thể đã commit (timeout, 5xx, mất kết nối) bắt buộc đọc detail và suggestions trước lần ghi tiếp theo. Nếu GET lỗi thì khóa ghi; nếu đã đọc được thì HR phải chọn lại. Xác minh nhãn/ghi chú/actor, không giả lập số Added/Updated khi mất xác nhận. Nội dung lỗi đề xuất được kiểm soát, tránh đưa problem detail có thông tin ứng viên ra UI.
7. [EvaluationSetManager](../src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor) vẫn tạo bộ CV và gán nhãn thủ công. Workspace/lựa chọn đề xuất được giữ riêng theo bộ trong phiên trang. [RefreshLabelsPreservingDraftAsync](../src/SmartCv.Web/Helpers/EvaluationSetWorkspace.cs) cập nhật dòng chưa sửa và metadata, giữ choice/note đang sửa. Request chọn bộ/đề xuất cũ được hủy và có generation guard.
8. [Prompts](../src/SmartCv.Web/Components/Pages/Prompts.razor) chia sẻ bộ CV giữa manager, B14, B15 và B39. Sau lưu nhãn tăng refresh signals và cập nhật lịch sử/tỷ lệ trên header qua `ApplyVersionsPreservingDraft`; không tải lại trang hay thay nội dung Prompt. Không đổi bộ đang xem nếu một lần lưu của bộ trước vừa hoàn tất.
9. [MismatchTable](../src/SmartCv.Web/Components/Prompts/MismatchTable.razor) giữ bộ lọc và kiểm chứng liên kết chi tiết. Trang Prompt tắt ma trận B16 lặp bằng `ShowMatrix=false`, dùng ma trận A41 tại vùng phân tích. Component vẫn có giá trị mặc định cũ khi dùng độc lập.

## UI/UX và accessibility

- Dùng Inter và tokens Deep Teal hiện có; AI Glow chỉ ở số liệu/điểm nhấn vùng AI. Không thêm font/package/design system mới.
- Tiêu đề trang 24–28px, khu vực B41/B16 19px, panel 17px, nội dung bảng 13px, chữ phụ 12px, số tổng hợp 24px. Số ma trận 26px desktop / 24px mobile; Bỏ sót 28px.
- Padding card 20px; panel 16–20px; gap 12/16/20px, radius 12–16px. Grid dùng minmax(0,1fr), các vùng có min-width:0; matrix/conclusion stretch trên desktop, stack ở chiều hẹp.
- Bốn ô ma trận đo thực tế đều cao 140px ở cả sáu viewport. Bỏ badge dư làm hàng Bỏ sót cao hơn hàng còn lại; giữ viền đỏ rõ, nhãn chữ và giải thích ngoài ma trận.
- Bảng có caption, scope cho header, vùng cuộn riêng có tên và tabindex; nút/label checkbox chính có target tối thiểu 44px. Focus-visible rõ, aria-busy/status/alert, skeleton theo dòng thực, reduced-motion cho animation B41.
- Không giảm cỡ chữ để nhét bảng mobile. Bảng rộng cuộn trong container, không cuộn ngang toàn trang. Trạng thái có nhãn chữ cùng màu.
- Hình đã xem trực tiếp: ma trận 1440/320px, panel 1440/768px, toàn trang 1440/320px. Chưa có đánh giá bằng screen reader/axe hoặc zoom 200%; không tuyên bố chứng nhận WCAG toàn ứng dụng.

## Minh chứng trình duyệt

Dùng Chromium/Playwright đã có trên máy, production Razor components của Web chạy trong host kiểm thử Blazor Server riêng và API giả trong bộ nhớ. Không khởi động SQL Server/API production/Gemini. Host dùng khóa DataProtection tạm để không đọc/ghi kho DPAPI của tài khoản máy; middleware triển khai production không phải đối tượng bài test browser này. Không sửa Program.cs hay cấu hình ứng dụng để phục vụ harness.

| Viewport | Toàn trang | Ma trận/kết luận | Nhãn đề xuất |
|---|---|---|---|
| 1440 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-1440.png) | [ảnh](../artifacts/b41-browser-evidence/insight-1440.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-1440.png) |
| 1024 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-1024.png) | [ảnh](../artifacts/b41-browser-evidence/insight-1024.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-1024.png) |
| 768 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-768.png) | [ảnh](../artifacts/b41-browser-evidence/insight-768.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-768.png) |
| 390 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-390.png) | [ảnh](../artifacts/b41-browser-evidence/insight-390.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-390.png) |
| 360 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-360.png) | [ảnh](../artifacts/b41-browser-evidence/insight-360.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-360.png) |
| 320 × 1000 | [ảnh](../artifacts/b41-browser-evidence/prompts-320.png) | [ảnh](../artifacts/b41-browser-evidence/insight-320.png) | [ảnh](../artifacts/b41-browser-evidence/suggestions-320.png) |

Ảnh là AFTER, dữ liệu tổng hợp; không có ảnh BEFORE của working tree Claude. Ảnh crop component tạm ẩn riêng fixed shell để thanh điều hướng không che nội dung; ảnh toàn trang giữ shell thật. Log/chỉ số: `artifacts/b41-browser-final2.log`, `artifacts/b41-browser-evidence/evidence.json`. Đã đo không overflow toàn trang ở sáu viewport, không PageError/console error, các ô bằng nhau, font đúng nêu trên.

Browser xác nhận: GET không PUT; New checked / Conflict unchecked / Same disabled; lần lưu đầu chỉ gửi ba New; lần lưu sau chỉ gửi Conflict HR tự chọn. Nội dung Prompt và nhãn/note thủ công chưa lưu được giữ sau lưu và đổi bộ CV qua lại; các selector B14/B15 cùng bộ. Bật lại Ẩn danh sau interactive render che cả note đang soạn, tên, file khỏi DOM. Tổng hai PUT chủ động vào fixture, không ghi dữ liệu thật.

## Kiểm thử và các lần chẩn đoán

SDK 10.0.403, runtime 10.0.14. Dùng output riêng `artifacts/b41-build`, không dùng bin/obj của phiên demo. Restore nằm trong `dotnet build` và đã thành công từ package cache.

| Lần chạy | Passed | Failed | Skipped | Bằng chứng |
|---|---:|---:|---:|---|
| Baseline người dùng cung cấp | 2206 | 0 | 107 | Prompt giao việc |
| Baseline code Claude, đã chạy lại | 2206 | 0 | 107 | `artifacts/b41-baseline-tests/b41-baseline.trx` |
| Focused B41 + B12–B16/B39 | 192 | 0 | 0 | `artifacts/b41-focused-tests/b41-focused.trx` (trước bổ sung preflight eligibility) |
| B41 cuối | 33 | 0 | 0 | `artifacts/b41-unit-tests/b41-unit-final.trx` |
| Worker chạy riêng | 15 | 0 | 0 | `artifacts/b41-focused-tests/b41-worker-recheck.trx` |
| Full tuần tự | 2239 | 0 | 107 | `artifacts/b41-full-tests/b41-serial.trx` (10 phút 41 giây) |
| Focused trên source bàn giao | 193 | 0 | 0 | `artifacts/b41-focused-tests/b41-focused-final.trx` |
| Full trên source bàn giao, tối đa 2 collection song song | 2239 | 0 | 107 | `artifacts/b41-full-tests/b41-final2.trx` (3 phút 37 giây) |

Build đầu working tree Claude và build cuối đều 0 warning / 0 error. Log cuối: `artifacts/b41-final2-build.log`.

Các ca B41 nằm trong [B41PromptMetricsTests](../tests/SmartCv.Tests/B41PromptMetricsTests.cs): format/null/0/CI, ma trận đúng chiều và Unscored riêng, kết luận đủ/chưa đủ/không công bằng/stale/thiếu CI, response bộ cũ, chọn New/Conflict/Same, note 500 ký tự, actor trim/validation, không ghi trước thao tác, payload chọn đúng, double-submit có request gate, 400/409/429/500, timeout sau commit, mất kết nối, đọc lại thất bại khóa ghi, duplicate, ẩn danh/unavailable và thay đổi eligibility trước PUT, optional job filter, giữ draft nhãn thủ công. Browser kiểm tra thêm toàn trang và Prompt draft.

Những lần chẩn đoán không bị che giấu:

- Lần full đầu trong sandbox: 1878 passed / 328 failed / 107 skipped do quyền Windows Event Log `.NET Runtime`; baseline chạy lại với `Logging__EventLog__LogLevel__Default=None` đạt 2206/0/107. Biến chỉ áp dụng tiến trình test, không sửa source cấu hình logging.
- Full trung gian đã đạt 2234/0/107 trước bốn ca biên bổ sung và preflight.
- Hai lần full song song mặc định về sau gặp đúng test `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`: số scope 6 hoặc 7, assertion gốc yêu cầu 2–5 sau Task.Delay(3500). Không sửa worker/test/assertion. Bộ Worker chạy riêng đạt 15/15. Lượt tuần tự dùng `artifacts/b41-serial.runsettings` đạt 2239/0/107. Sau khóa UI tạo bộ CV, chạy lại focused/browser rồi full với `artifacts/b41-final.runsettings` giới hạn tối đa hai collection song song. Đây là cấu hình thực thi, không lọc/bỏ test.
- Một lần focused cho thấy nút retry của AccuracyCompare mang tên chung “Thử lại” va vào kiểm tra trang B39 “không có nút thử lại khi hết quota”. Đổi nhãn đúng ngữ cảnh thành “Tải lại so sánh”; không sửa test B39.
- Có một lần chẩn đoán hang dùng timeout 45s rồi hủy test B39 vốn đợi HTTP timeout khoảng 100s; chạy riêng test đó pass. Không thêm skip hay thay timeout/assertion test gốc.
- Harness browser được sửa để chờ interactive circuit sau SSR và chờ DOM hoàn tất chuyển Ẩn danh trước assertion, tránh kiểm tra trước render. Khóa DPAPI của tài khoản máy không dùng được trong sandbox nên đổi riêng host kiểm thử sang khóa tạm. Main source không phụ thuộc workaround này.
- Một lỗi Razor do biến `warning` trùng scope được sửa ngay; build cuối sạch. Một lỗi dispose do truy cập IServiceProvider đã disposed được sửa bằng giữ reference AnonymityState đã resolve.

[MismatchAnalysisTests](../tests/SmartCv.Tests/MismatchAnalysisTests.cs) chỉ được bổ sung đăng ký rõ chế độ có tên cho fixture B16 cần đọc tên/note; giữ toàn bộ assertion cũ. Test mới và browser xác minh chế độ Ẩn danh. Không giảm assertion, xóa hoặc skip test cũ.

### Lệnh tái kiểm chứng

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/b41-build -v minimal
$env:Logging__EventLog__LogLevel__Default = 'None'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b41-build --settings artifacts/b41-final.runsettings --logger 'trx;LogFileName=b41-final2.trx' --results-directory artifacts/b41-full-tests
dotnet test tests/SmartCv.Tests/SmartCv.Tests.csproj --no-build --artifacts-path artifacts/b41-build --filter 'FullyQualifiedName~B41PromptMetricsTests'
dotnet run --no-restore --project artifacts/b41-browser/B41Browser.csproj --artifacts-path artifacts/b41-browser-build -p:NuGetAudit=false -- 'C:\TTCN\project\AI-CV-Screening'
```

`NuGetAudit=false` chỉ dành cho harness đã dùng Playwright có sẵn, tránh truy cập dịch vụ audit khi mạng bị hạn chế; không thay setting project chính. Browser harness và runsettings nằm dưới artifacts đã ignore, giữ để kiểm chứng cục bộ, không đưa vào manifest source.

## API, bảo mật và chức năng lân cận

- GET A36 và compare A41 đúng URL/DTO thật; PUT dùng `SaveHrLabelsRequest`/`HrLabelInput`, boolean đúng field `IsPass`, không gửi Same/dòng không chọn/duplicate. Không tạo IPromptApi/FakePromptApi, không sửa ApiClientBase/Core/backend/migration.
- Tên HR lấy từ HrActorState sau interactive render; nhập tay khi storage không sẵn sàng, có thông báo. Không lưu actor khi validation lỗi.
- B36 dùng chung AnonymityState; panel, bảng thủ công, note và CV lệch che tên/file/nội dung tự do, aria-label checkbox chỉ dùng ID. API A36 tự loại anonymized; frontend thêm eligibility và preflight theo candidates hiện tại. Đây là bảo vệ trình bày và kiểm tra trước gửi; hợp đồng hiện tại không có ETag/transaction giữa GET preflight và PUT, nên không tuyên bố loại bỏ hoàn toàn race với thay đổi đồng thời ở server.
- Giữ nguyên source PromptEditor/PromptHistory/PromptPlayground/PromptTestPanel. Không tự chạy thử CV, không POST Gemini, không bấm Chạy đánh giá hay kích hoạt trên dữ liệu thật. B12/B13/B14/B15/B16 và B39 được kiểm chứng bằng các test sẵn có trong focused/full regression.
- Không ghi SQL Server thật, không migration, không xóa CV/Job, không đọc CV cá nhân để chụp ảnh. Các bài SQL/OCR opt-in giữ trạng thái skipped cũ.

## Manifest và kiểm tra Git

21 file source/docs/test: **11 M, 10 A, 0 D**. A là file untracked mới dự kiến đưa vào commit, chưa git add. Toàn bộ 8 file ban đầu của Claude nằm trong danh sách.

| M/A/D | Path |
|---|---|
| A | `docs/B41-implementation-report.md` |
| M | `src/SmartCv.Web/Components/Pages/Prompts.razor` |
| M | `src/SmartCv.Web/Components/Pages/Prompts.razor.css` |
| M | `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor` |
| M | `src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor.css` |
| A | `src/SmartCv.Web/Components/Prompts/ConfusionMatrix.razor` |
| A | `src/SmartCv.Web/Components/Prompts/ConfusionMatrix.razor.css` |
| M | `src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor` |
| M | `src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor.css` |
| A | `src/SmartCv.Web/Components/Prompts/LabelSuggestionPanel.razor` |
| A | `src/SmartCv.Web/Components/Prompts/LabelSuggestionPanel.razor.css` |
| M | `src/SmartCv.Web/Components/Prompts/MismatchTable.razor` |
| M | `src/SmartCv.Web/Components/Prompts/MismatchTable.razor.css` |
| A | `src/SmartCv.Web/Helpers/AccuracyText.cs` |
| A | `src/SmartCv.Web/Helpers/ComparisonConclusion.cs` |
| M | `src/SmartCv.Web/Helpers/EvaluationSetWorkspace.cs` |
| A | `src/SmartCv.Web/Helpers/LabelSuggestionSelection.cs` |
| A | `src/SmartCv.Web/Helpers/LabelSuggestionWorkspace.cs` |
| M | `src/SmartCv.Web/Services/PromptApiClient.cs` |
| A | `tests/SmartCv.Tests/B41PromptMetricsTests.cs` |
| M | `tests/SmartCv.Tests/MismatchAnalysisTests.cs` |

Codex tạo mới panel/CSS, controller workspace đề xuất, test B41 và báo cáo. Codex sửa thêm trang/CSS, manager/CSS, MismatchTable/CSS, EvaluationSetWorkspace và fixture test B16; các file Claude tiếp tục được liệt kê riêng ở đầu báo cáo.

Metadata [base SHA](../artifacts/b41-base-sha.txt) chứa đúng SHA 40 hex; [manifest](../artifacts/b41-commit-files.txt) chứa mỗi dòng một path relative, duy nhất, gồm đúng 21 file trên. `artifacts/` đã được check-ignore: metadata, harness, logs, TRX, screenshot, bin/obj không thuộc source manifest theo prompt gốc. Không có file ngoài B41 trong danh sách thay đổi Git.

Kiểm tra cuối:

- `git branch --show-current`: `feature/b41-prompt-metrics`.
- `git rev-parse HEAD`: `3cba955a8f4e3017c7d881514175e655413b900c`, không đổi so với đầu phiên; nội dung file base đúng SHA này.
- `git rev-parse origin/main`: `3cba955a8f4e3017c7d881514175e655413b900c` (local ref, không fetch/pull). Nhánh B41 không có upstream.
- `git status --porcelain=v1 --untracked-files=all`: đúng 11 M và 10 `??`, khớp từng path bảng/manifest; `git diff --name-status`: đúng 11 M, không D/R.
- `git diff --cached --name-status`: rỗng; không có staged file. Không có MERGE_HEAD/REBASE_HEAD/CHERRY_PICK_HEAD/rebase-merge/rebase-apply/sequencer.
- `git diff --check`: exit 0. Git có advisory LF sẽ được chuyển CRLF khi chạm hai file AccuracyCompare của Claude; không phải lỗi whitespace/build, không tự ép đổi line ending toàn repo.
- Manifest đối chiếu union của diff tracked + untracked: đúng 21 path unique, không thiếu/thừa. Chi tiết status trong [audit Git](../artifacts/b41-final-git-audit.txt).
- So tên test baseline và full cuối: **thêm 33, bỏ 0; 107 tên skipped giống hệt, thay đổi 0**. Số pass tăng đúng từ 2206 lên 2239. Xem [tổng hợp](../artifacts/b41-test-summary.json), `artifacts/b41-skipped-baseline.txt` và `artifacts/b41-skipped-final.txt`. Không có nguyên nhân skipped mới vì không có test skipped mới.

Không thực hiện git add/commit/push/pull/merge/rebase/reset/stash/clean, không tạo PR hay chuyển nhánh. Người dùng tự review và thực hiện Git sau khi nhận báo cáo.

## Giới hạn bàn giao

Chưa kiểm tra với API/SQL production, Gemini thật, screen reader hoặc mọi cấu hình trình duyệt. Ảnh/HTTP fixtures tổng hợp không thay thế xác nhận vận hành trên dữ liệu thật. Draft chỉ được giữ trong phiên trang; rời trang/tải lại vẫn dùng NavigationLock hiện có để cảnh báo. Các bài opt-in bị skip giữ nguyên, đã đối chiếu tên chính xác. Không có thay đổi backend hoặc phần triển khai B19/B39/B42.
