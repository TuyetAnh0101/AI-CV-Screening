# B19 — Trang Tổng quan tuyển dụng

Ngày bàn giao: 17/11/2026, Asia/Saigon. Phạm vi: **B19 frontend**, chờ người dùng duyệt trước Git. Chưa stage, commit, push hoặc tạo PR.

## 1. Nhánh, nguồn và bảo vệ công việc khác

- Worktree B19: `C:\TTCN\project\AI-CV-Screening-B19`.
- Nhánh: `feature/b19-dashboard-page`.
- Đã kiểm tra repository gốc, danh sách nhánh/worktree, trạng thái index/untracked/unmerged và thao tác Git dở dang; sau đó chạy `git fetch origin --prune` theo yêu cầu.
- Tạo nhánh và worktree trực tiếp từ `origin/main` vừa fetch. Worktree sạch trước triển khai.
- **Base = HEAD = origin/main đã fetch: `fd8d82dad5850c79f246ca1f04dc3ce7aa2700d3`** (merge B41 #71). Không dùng SHA main cũ trong prompt làm giả định.
- Origin: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`. B19 không có upstream hoặc remote branch.
- Repository gốc vẫn ở `feature/b41-prompt-metrics`, HEAD `3c40b201bb07c6fb384e2160292c54b05f1b9704`, working tree sạch khi kiểm tra lại. Không đổi nhánh, sửa source hoặc tác động các process Web/API đang chạy của nhiệm vụ khác.
- Không dùng add/commit/push/pull/merge/rebase/reset/stash/clean/restore. Chỉ có thao tác tạo B19 worktree/branch và fetch đã được yêu cầu.

## 2. Tài liệu và đối chiếu mẫu với source thực tế

Đã đọc toàn bộ `C:\TTCN\B19_Codex_Prompt_20261117.md`; kiểm kê ZIP `C:\TTCN\A19_B26_SmartCv.zip`, phần B19 trong guide, code mẫu B19, hợp đồng A19 và các kiểm thử B22 liên quan. ZIP không có ảnh mẫu; báo cáo không dùng ảnh tham khảo tưởng tượng.

Source hiện tại có A19, B39 và B41. Đã đối chiếu DashboardContracts/Endpoints/Period, ApiClientBase, JobApiClient, Results/RankingTable, StageText, StatusText/StatusBadge, AnonymityState và design tokens/shell. Khác biệt quan trọng với mẫu:

- Trang chủ cũ có các khối điều hướng lớn, chưa có Dashboard. Thay nội dung trang chủ bằng số liệu A19 và CTA nhỏ.
- Results hiện hành có polling, chọn lượt/job, HR stages và ẩn danh. Chỉ bổ sung truyền ý định lọc cờ qua parameter/callback; không chép Results/RankingTable cũ từ ZIP.
- Tái sử dụng `StageText`, `StatusText`, `RankingView.VnTime`, `VietnamTime` và `DashboardPeriod`. Không tạo helper RunText hoặc route B26 ngoài phạm vi.
- API A19/DTO thật là nguồn số liệu. App thật không chứa fixture, không tổng hợp KPI bằng các API khác. Fake API chỉ nằm trong tests và artifacts kiểm thử.

## 3. Nghiệp vụ, file và kiểm chứng

Các đường dẫn dưới đây tương đối với worktree B19.

| Yêu cầu | Cách triển khai / file | Kiểm chứng |
|---|---|---|
| Job kể cả inactive | `Services/DashboardApiClient.cs`, `Helpers/DashboardWorkspace.cs`, `Components/Pages/Home.razor`: GET summary A19 và GET jobs với activeOnly=false độc lập | Services_use_A19_and_load_inactive_jobs_without_writes; browser chọn job ngừng tuyển |
| URL + ngày Việt Nam | `Helpers/DashboardFilter.cs`: ISO chính xác, ID dương, từ ≤ đến, từ chối query lặp; 7/30 ngày gồm hôm nay theo UTC+7; Tất cả giữ Job; Xóa bộ lọc xóa toàn bộ | Filters_are_strict…; Duplicate_URL_filters…; Quick_ranges…; Home_handles_back_forward…; browser F5/Back/Forward, bàn phím Space |
| Request chậm / lỗi | `DashboardWorkspace.cs`: cancellation + generation, đối chiếu scope của response, dispose khi rời trang; giữ dữ liệu hợp lệ cùng scope khi refresh lỗi, ẩn dữ liệu scope khác | Slow_old_response…; Mismatched_response…; API_failures…400/404/429/500; Network_and_timeout…; browser gate/retry |
| Đúng 5 KPI | `Home.razor`, `DashboardText.cs`: hiển thị trực tiếp TotalCvs, FailedCvs, ActiveJobs, RunningRuns, FlaggedUnresolved | Dashboard_renders_exactly_five_API_KPIs…; browser đếm 5 thẻ |
| Phễu 4 bước | `Components/Dashboard/StageFunnel.razor(.css)`: Reached và Current riêng, ConversionPercent từ API; null ở bước sau hiển thị —; footer Loại/Dự phòng | Chart_preserves_reached_current…; browser số 0/1/int.MaxValue |
| 6 lý do loại | `Components/Dashboard/RejectReasonChart.razor(.css)`: count + % API, đỏ nhẹ chuyên môn/xám khác; tổng 0 có empty state, không bịa tỷ lệ | Chart_preserves…; browser six reasons/zero/large |
| Thời gian | `DashboardText.cs`, `Home.razor`: trung vị, trung bình, số hồ sơ có quyết định; dấu phẩy Việt Nam, null/Count=0 là —, số 0 thật vẫn là 0 phút | Screening_duration_distinguishes_null_from_real_zero; Full_empty_and_filtered_empty… |
| 5 lượt gần nhất | `Home.razor`: lấy tối đa 5 từ API, loại lượt/trạng thái, ngày giờ Việt Nam, tổng/lỗi, tên Job wrap và +N; không đọc tên ứng viên/file | Dashboard_renders… có fixture 6 lượt nhưng chỉ render 5; browser tên Job dài, 0 lượt |
| Trống / tải / lỗi | `Home.razor(.css)`: skeleton, onboarding toàn hệ thống, filter-empty riêng, retry và giữ filter | Unit/component tests và ảnh loading/empty/filter-empty/error/invalid |
| Cờ vào Results | `Results.razor`, `RankingTable.razor(.cs)`: query filter=flagged bật bộ lọc thật; đổi URL khi bật/tắt/xóa; giữ cờ khi đổi lượt/Job, không reset do polling | Flagged_deep_link_uses_B7_latest_job…; ResultsUi/B34/B36 regression; browser click KPI → đúng Job/latest → 1 dòng → poll → bỏ cờ/F5 |
| Bảo toàn privacy/B39 | Không sửa scoring, stage mutation, anonymity hay PromptPlayground | B36 + full regression; 82/82 test có tên B39, 33/33 test có tên B41; browser ẩn danh không có tên/file tổng hợp trong DOM |

Các file trong bảng được đặt dưới `src/SmartCv.Web/`; kiểm thử mới nằm tại `tests/SmartCv.Tests/B19DashboardTests.cs`.

### Phạm vi số liệu và liên kết

- Tổng CV là CV duy nhất; dòng phụ là số hồ sơ ứng tuyển. Không nhầm lỗi trích xuất với lỗi AI.
- Khi không chọn Job, tổng CV theo ngày tải lên, các phân tích hồ sơ theo ngày ứng tuyển. Chọn Job thì tổng CV là CV khác nhau trong cohort hồ sơ của Job.
- ActiveJobs không chịu lọc ngày; RunningRuns là số toàn hệ thống, không chịu Job/ngày. RecentRuns theo Job nếu chọn, không chịu lọc ngày. Giao diện ghi rõ các phạm vi này.
- Thẻ vị trí → `/jobs`; lượt đang chấm → `/runs?status=Running`.
- Cần xem kỹ ưu tiên JobId đang chọn, rồi FlaggedJobId API, rồi `/results?filter=flagged`. Không tính lại số cờ.
- Lượt gần nhất → `/runs/{id}`; onboarding có `/upload` và `/jobs/new` khi danh sách Job đã tải thành công và rỗng.
- Giữ lối tắt nhỏ tới `/upload`, `/results`, `/prompts`.

### Tương thích B7/B34/B36

Giữ nguyên `@key=(RunId,JobId)`, cơ chế B7 tìm latest run và tuple tải dữ liệu run/job. Thay đổi query cờ không gọi lại summary/ranking hoặc khởi động lại polling. RankingTable chỉ áp dụng lại cờ khi parameter URL thay đổi; rerender/poll không ghi đè lựa chọn HR. Callback đồng bộ URL khi bỏ cờ hoặc xóa bộ lọc để F5 không tự bật lại. Parameter nullable giữ tương thích các nơi dùng RankingTable độc lập. Không sửa thứ tự xếp hạng, thuật toán scoring, mutation HR hoặc CSS của RankingTable.

## 4. UI/UX và accessibility

Giữ shell, NavMenu và Inter local hiện có. Deep Teal làm màu chính; nền trắng/xám xanh, viền mảnh, shadow token nhẹ, radius 14–16px. AI glow chỉ ở biểu tượng lượt chấm nhỏ.

- H1 28px desktop, 24px mobile; panel 17px; KPI/trung vị 28px; nội dung 13px, chú thích 12px.
- Panel padding 20px, mobile 18px/16px; KPI 18px desktop; gap 14px cho KPI, 20px cho panel.
- Desktop đủ chỗ có 5 KPI một hàng. Laptop 3+2, tablet 2 cột; mobile một cột gọn, nhãn trái/số phải, mô tả hàng dưới. Các KPI cùng hàng có chiều cao bằng nhau.
- Số rất lớn tự tăng chiều rộng tối thiểu thẻ và giảm số cột; không thu nhỏ font, làm tròn số hoặc cắt chữ số. Browser kiểm tra tới int.MaxValue tại sáu độ rộng.
- Phễu/lý do loại cùng hàng desktop, panel xếp dọc khi không đủ chỗ. Thẻ thời gian dùng chiều cao nội dung để tránh khoảng trắng lớn do kéo theo danh sách lượt chấm.
- Nhãn và số thời gian trung bình/số hồ sơ dùng subgrid để giữ baseline khi label xuống dòng. Tên Job dài được wrap trong vùng có min-width:0.
- Control cao tối thiểu 44px; label thật, heading rõ, status text, biểu đồ có count/% bằng chữ và chú giải loại lý do. Focus 3px, loading/status/error dùng role/aria phù hợp.
- Skeleton chỉ chạy ba chu kỳ rồi dừng; reduced motion tắt animation. Không thêm thư viện chart/CSS/JS hay ảnh bên ngoài.

### Kiểm thử trình duyệt đã thực hiện

Chromium headless qua Playwright, dùng chính Razor components và static assets của Web. Hai host test tự sở hữu: Web và API giả trong bộ nhớ trên cổng loopback động; API chỉ GET. Không chạy app thật với DB/worker/Gemini. Đã dừng các host này sau test.

| Viewport | Bố cục chính | Kết quả |
|---|---|---|
| 1440×1000 | 5 KPI; 2 cột phân tích | Không tràn, KPI cùng hàng bằng nhau |
| 1024×1000 | 3+2 KPI; panel stack | Không tràn, filter cùng chiều cao |
| 768×1000 | 2 cột KPI; panel stack | Không tràn, giữ nguyên sidebar/shell |
| 390×1000 | KPI một cột gọn; panel stack | Không tràn, title 24px, số 28px |
| 360×1000 | KPI một cột gọn | Không tràn, không tách số lớn |
| 320×1000 | Giới hạn hẹp | Không tràn, label/biểu đồ tự xuống dòng |

Đã kiểm trực quan ảnh desktop/tablet/mobile, ảnh số lớn, empty và error. Browser còn kiểm URL/F5/Back/Forward, Space kích hoạt chip, focus, reduced motion, phản hồi cũ, lỗi 500/retry, empty toàn hệ thống khác filter-empty, median null, flagged Results qua polling và clear/F5, ẩn danh. Không có page/console error. Tất cả request API trong browser là GET.

Ảnh sau triển khai (không có bộ ảnh baseline trước sửa):

- [1440px](../artifacts/b19-screenshots/dashboard-1440.png), [1024px](../artifacts/b19-screenshots/dashboard-1024.png), [768px](../artifacts/b19-screenshots/dashboard-768.png).
- [390px](../artifacts/b19-screenshots/dashboard-390.png), [360px](../artifacts/b19-screenshots/dashboard-360.png), [320px](../artifacts/b19-screenshots/dashboard-320.png).
- [Số lớn 1024px](../artifacts/b19-screenshots/large-1024.png), [số lớn 320px](../artifacts/b19-screenshots/large-320.png); bộ one/large có đủ sáu độ rộng.
- [Loading](../artifacts/b19-screenshots/loading-1440.png), [full-empty](../artifacts/b19-screenshots/empty-1440.png), [filter-empty](../artifacts/b19-screenshots/filter-empty-1440.png), [error](../artifacts/b19-screenshots/error-1440.png), [query sai](../artifacts/b19-screenshots/invalid-1440.png).
- [Browser evidence](../artifacts/b19-screenshots/evidence.json), [log](../artifacts/b19-browser.log). Harness tái chạy: `artifacts/b19-browser/B19Browser.csproj`; đây là artifact kiểm tra, không thuộc manifest commit.

Dữ liệu ảnh/log đều tổng hợp; Dashboard không tải danh tính CV. Kiểm tra privacy tại Results bật Ẩn danh và xác nhận tên/file tổng hợp không xuất hiện trong DOM.

## 5. Build và kiểm thử

SDK .NET 10.0.403, runtime 10.0.14. Restore/build/test dùng output riêng dưới `artifacts/b19-build`. Mỗi process test tắt EventLog provider qua biến môi trường cục bộ và xUnit tối đa hai collection song song; không sửa config ứng dụng hoặc test cũ, không bật SQL/Gemini opt-in.

| Lượt | Passed | Failed | Skipped | Tổng |
|---|---:|---:|---:|---:|
| Baseline trước sửa | 2235 | 0 | 111 | 2346 |
| Focused B19 + ResultsUi + B34 + B36 | 124 | 0 | 1 | 125 |
| Full regression | 2277 | 0 | 111 | 2388 |
| B19 riêng | 42 | 0 | 0 | 42 |

Restore thành công. Build cuối **0 warnings, 0 errors**. TRX baseline so với full: **42 test case mới, 0 test bị mất, danh sách 111 skipped giống hệt**, không thêm Skip/giảm assertion/xóa test cũ.

Lệnh chính:

```powershell
dotnet restore SmartCv.slnx -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening-B19/artifacts/b19-build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/b19-build
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b19-build --settings artifacts/b19-tests.runsettings --filter 'FullyQualifiedName~B19DashboardTests|FullyQualifiedName~ResultsUiTests|FullyQualifiedName~B34|FullyQualifiedName~B36'
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b19-build --settings artifacts/b19-tests.runsettings
```

Log/TRX:

- `artifacts/b19-baseline-restore.log`, `b19-baseline-build.log`, `b19-baseline-tests.log`, `b19-baseline-tests/b19-baseline.trx`.
- `artifacts/b19-final-build.log`, `b19-focused.log`, `b19-focused-tests/b19-focused.trx`.
- `artifacts/b19-full-tests.log`, `b19-full-tests/b19-full.trx`.
- `artifacts/b19-final-b19-tests.log` (kiểm lại 42 test riêng sau build cuối).
- `artifacts/b19-verification.json` đối chiếu số lượng/tên test và manifest.

Focused skip duy nhất: `B34StageUiTests.Demo_twenty_PDFs_drive_UI_decisions_through_real_A35_HTTP_endpoints`, test demo opt-in hiện có. Các SQL/demo/retention/external opt-in khác tiếp tục skip như baseline.

So với kết quả B41 ở repository gốc trước đây (2239 pass/107 skip), worktree mới chưa có tessdata ignored nên **4 test OCR** chuyển sang skip ngay trong baseline B19; không phải hồi quy B19. Cả bốn có lý do `Chưa có tessdata (chạy scripts/tai-tessdata.ps1)`:

1. `CvTextExtractorTests.Clear_png_is_read_with_ocr`.
2. `CvTextExtractorTests.Scanned_pdf_is_read_with_ocr`.
3. `CvTextExtractorTests.Photo_like_jpg_is_read_with_ocr`.
4. `CvExtractEndpointTests.Upload_image_then_worker_completes_with_ocr`.

Không cài/copy tessdata hoặc chỉnh test để che khác biệt môi trường. So sánh chính thức là baseline B19 2235/0/111 → 2277/0/111.

Trong quá trình triển khai đã sửa lỗi fixture/query thiếu khóa, dữ liệu ReviewFlag DTO, InlineData nullable double và custom renderer cần RendererInfo; sửa analyzer bằng assertion tương đương. Browser phát hiện số KPI bị tách dòng khi count lớn và khoảng trắng thẻ thời gian, đã sửa rồi chạy lại. Các lỗi tạm thời không được tính là kết quả pass cuối.

## 6. Danh sách toàn bộ file A/M/D

**16 file: 11 A mới, 5 M sửa, 0 D xóa.** A ở bảng là file mới còn untracked, chưa stage.

| Trạng thái | File |
|---|---|
| A | `docs/B19-implementation-report.md` |
| A | `src/SmartCv.Web/Components/Dashboard/RejectReasonChart.razor` |
| A | `src/SmartCv.Web/Components/Dashboard/RejectReasonChart.razor.css` |
| A | `src/SmartCv.Web/Components/Dashboard/StageFunnel.razor` |
| A | `src/SmartCv.Web/Components/Dashboard/StageFunnel.razor.css` |
| M | `src/SmartCv.Web/Components/Pages/Home.razor` |
| A | `src/SmartCv.Web/Components/Pages/Home.razor.css` |
| M | `src/SmartCv.Web/Components/Pages/Results.razor` |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor` |
| M | `src/SmartCv.Web/Components/Results/RankingTable.razor.cs` |
| A | `src/SmartCv.Web/Helpers/DashboardFilter.cs` |
| A | `src/SmartCv.Web/Helpers/DashboardText.cs` |
| A | `src/SmartCv.Web/Helpers/DashboardWorkspace.cs` |
| A | `src/SmartCv.Web/Services/DashboardApiClient.cs` |
| M | `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` |
| A | `tests/SmartCv.Tests/B19DashboardTests.cs` |

Manifest `artifacts/b19-commit-files.txt`: đúng 16 đường dẫn POSIX, một file mỗi dòng, không duplicate, khớp toàn bộ `git status --porcelain=v1 --untracked-files=all`. Bao gồm báo cáo này, không có artifacts/screenshot/secret. Manifest và `artifacts/b19-base-sha.txt` là UTF-8 không BOM và bị ignore.

Không có thay đổi Core/API/Infrastructure, migration, appsettings, package, solution, NavMenu, scoring, PromptPlayground hoặc source nhiệm vụ khác.

## 7. Giới hạn và checkpoint cuối

- `/jobs` (B20), `/jobs/new` (B21), `/runs`, `/runs/{id}` (B26) là URL hợp đồng chờ các trang tương ứng; có thể 404 trên main hiện tại. Không tạo route giả hoặc triển khai các nhiệm vụ này.
- Chưa kiểm browser Firefox/WebKit, thiết bị vật lý hoặc screen reader. Có kiểm bàn phím, focus, reduced motion, semantic labels và nội dung chữ; chưa thực hiện chứng nhận WCAG đầy đủ.
- Browser dùng A19 DTO/API giả an toàn; không tuyên bố đã kiểm Dashboard với SQL Server thật. Các test API A19 không cần SQL trong full suite vẫn chạy. SQL/demo/external opt-in giữ nguyên trạng thái skip.
- Không chạy migration, reset/seed database, worker/retention hoặc gọi Gemini thật; không dùng dữ liệu CV cá nhân.
- Kiểm tra cuối: `git diff --check` sạch; staged index rỗng; HEAD bằng base; không unmerged/merge/rebase/cherry-pick dở dang; worktree B19 chỉ có 16 file nêu trên.
- Repository chính/B41 không bị sửa. Các host browser test tự sở hữu đã dừng.

**Dừng tại bàn giao báo cáo để người dùng duyệt code, ảnh, test và manifest. Chưa thực hiện bất kỳ thao tác stage/commit/push/PR nào.**
