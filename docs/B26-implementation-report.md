# B26 — Lịch sử lượt chấm và chi tiết lượt chấm

Ngày bàn giao: 18/11/2026 (Asia/Saigon). Trạng thái: **chưa git add/commit/push**, chờ duyệt.

## 1. Repository, nhánh và kiểm tra Git đầu phiên

- Repository duy nhất được dùng: `C:\TTCN\project\AI-CV-Screening`. **Không tạo worktree, clone hoặc thư mục `AI-CV-Screening-B26`.**
- Remote `origin`: `https://github.com/TuyetAnh0101/AI-CV-Screening.git`.
- Nhánh: `feature/b26-run-history`, không có upstream. Nhánh **đã tồn tại sẵn ở local** (reflog: `branch: Created from origin/main`), không có ở remote, nên không tạo lại; chỉ kiểm tra và tiếp tục đúng nhánh.
- SHA nền đầy đủ = HEAD = `origin/main` sau `git fetch origin --prune`: **`22a3e14700528f394968b5998164d132981033be`** (`feat(B21): implement job create and edit form (#74)`); ahead/behind so với `origin/main` = 0/0.
- Đầu phiên: working tree/index sạch, không staged/unstaged/untracked/unmerged, không có merge/rebase/cherry-pick/revert dở dang.
- B21 đã vào `main` bằng squash (#74). Nhánh local `feature/b21-job-form` (upstream `[gone]`) không là ancestor của main, nhưng `JobEdit.razor`, `JobApiClient.cs`, `ApiClientBase.cs` giống hệt main → không lấy code B21 chưa merge. Không làm B22.
- `git worktree list` có 6 đăng ký cũ (repo hiện tại, B19, B39, gemini-fix, main-latest, scratchpad wt-a11): chỉ đọc, không sửa/dọn.
- Không chạy git add, commit, push, merge, rebase, reset, stash, restore, clean hoặc tạo PR.

Metadata: [SHA nền](../artifacts/b26-base-sha.txt) (ASCII, 40 ký tự), [manifest](../artifacts/b26-commit-files.txt). Cả hai nằm trong `artifacts/` đã bị `.gitignore`.

## 2. Tài liệu và hợp đồng A25 đã dùng

Đọc trực tiếp `C:\TTCN\A19_B26_SmartCv.zip` (giải nén vào scratchpad riêng): mục A25, B26, B19 (5 lượt gần nhất), B22 (tiêu chí B26), bảng lỗi thường gặp; toàn bộ mẫu `Runs.razor(.css)`, `RunDetail.razor(.css)`, `RunApiClient.cs`, `NavMenu.razor`, và `B19/Helpers/RunText.cs`. ZIP không có ảnh mockup B26.

Đối chiếu source thật: `RunApiClient`, `ApiClientBase`, `ApiResult`, `RunContracts`, `RunHistoryContracts`, `ResultContracts`, `RunHistoryQuery`, `RunEndpoints` (`ListAsync`, `GetAsync`, `FailuresAsync`, `SafeRunFailure`), `VietnamTime`, `AnonymityState`, `AnonymousToggle`, `Display`, `StatusText`, `StatusBadge`, `RankingView`, `RunFlow`, `Results.razor`, `EvaluationDetail.razor`, `Prompts.razor` + `PromptTestPanel`/`PromptTestFlow`, `Home.razor`, `NavMenu.razor`, `MainLayout.razor.css`, `app.css`, CSS B19/B20.

**`RunText.cs` không có trên `main`** (đã kiểm tra lại) → B26 tự triển khai `Helpers/RunText.cs`.

| Hợp đồng A25 | Cách B26 dùng |
|---|---|
| `GET /api/runs?page=&pageSize=20&type=&status=&from=&to=` → `RunPageDto {items,page,pageSize,totalCount,totalPages}` | `RunApiClient.ListPageAsync(RunHistoryFilter)` **luôn gửi `page` + `pageSize=20`** → luôn nhận `RunPageDto`. Phân trang hoàn toàn phía server. |
| `GET /api/runs` **không** `page` → `List<RunSummaryDto>` (B7) | **Giữ nguyên** `ListScreeningAsync` (`?type=Screening&take=…`), không đổi một dòng. Test xác nhận vẫn không gửi `page` và đọc được mảng từ A25 thật. |
| `from`/`to` = ngày Việt Nam `yyyy-MM-dd`, tính cả hai đầu | Client kiểm tra bằng **chính `RunHistoryQuery.Create`** của Core trước khi gọi API (định dạng, ngày tồn tại, `from > to`, ngày ngoài phạm vi UTC, `(page−1)×pageSize` tràn Int32). |
| `GET /api/runs/{id}` → `RunProgressDto` + `SystemPrompt`, `PromptVersionId`, `Temperature`, `EvaluationSetId/Name`, `AccuracyPercent`, `Items` | Cấu hình snapshot của lượt; job + số hồ sơ từ `Items`. Không gọi API prompt/cấu hình hiện tại. |
| `GET /api/runs/{id}/failures` → `RunFailuresDto {failedCount, keywordOnlyCount, items[kind, errorMessage, cvExtractionStatus…]}` | `RunApiClient.GetFailuresAsync`, tải và thử lại **riêng** với phần tóm tắt. |
| 400 ProblemDetails, 404 `Results.NotFound()` | 400/404/429/500/503/offline/timeout đổi thành câu tiếng Việt cố định; không hiện `detail` thô của API. |

## 3. Trang `/runs` — Lịch sử lượt chấm

- **Bộ lọc**: Loại lượt (Tất cả / Sàng lọc (Screening) / Đánh giá prompt (PromptTest)), Trạng thái (Tất cả / Đang chờ / Đang phân tích / Hoàn thành / Thất bại), Từ ngày, Đến ngày (`<input type=date>`, `yyyy-MM-dd`, ghi chú UTC+7 tính cả hai đầu), nút **Bỏ lọc**.
- **Địa chỉ trang là bộ lọc đang áp dụng** (`RunHistoryFilter`): `/runs?type=PromptTest&status=Completed&from=…&to=…&page=2`, thứ tự cố định, bỏ `page` khi = 1, nhận hoa/thường (`status=running` → `Running`). F5, Back/Forward, gửi link đều giữ bộ lọc (đồng bộ qua `LocationChanged` + chống tải trùng như B19). Đổi bộ lọc luôn về trang 1 và thêm một mục lịch sử trình duyệt.
- **Bộ lọc sai không gọi API**: giá trị lạ, ngày không tồn tại, `from > to`, `page=0/-1/abc`, page tràn Int32, tham số lặp. Hiện lỗi inline (`role=alert`, `aria-invalid` đúng ô) + khung "Bộ lọc chưa hợp lệ" + Bỏ lọc; địa chỉ giữ bộ lọc hợp lệ trước đó.
- **Bảng 9 cột, đủ 10 dữ liệu nghiệp vụ** (Temperature gộp vào ô Prompt · Model): Lượt chấm (`#id` là liên kết + nhãn loại) · Bắt đầu (giờ VN) + thời gian chạy · Vị trí / Bộ CV đánh giá · Prompt · Model · Temperature · Hồ sơ (+ "N đã xử lý" khi đang chạy) · Lỗi (đỏ nhẹ khi > 0) · Tỷ lệ đúng · Trạng thái (`StatusBadge` + `StatusText.ForRun`).
  - Thời gian chạy: `null` → "Chưa ghi nhận"/"—", `0` → "Dưới 1 giây", lượt đang chờ/đang chạy luôn "Chưa bắt đầu chấm"/"Đang chạy", giá trị lớn → "24.855 ngày 3 giờ".
  - `AccuracyPercent` null → "—" + "chưa có" (không thành 0%); lượt Screening → "—" + chữ ẩn "Không áp dụng cho lượt sàng lọc".
  - Tên prompt/job/model dài: tối đa 2 dòng; toàn văn vẫn trong DOM (trình đọc màn hình), trong `title` và ở trang chi tiết.
  - `<caption>`, `th scope=col/row`; điều hướng bằng liên kết `#id` (bàn phím/Enter), không dùng click trên `<tr>`. Liên kết chi tiết mang theo bộ lọc/trang hiện tại.
- **Phân trang thật**: "Trang 2/5 · 87 lượt" + "Hiển thị 21–40"; Trước/Sau disabled đúng (trang đầu/cuối hoặc đang tải). Tối đa 20 dòng.
- **Trạng thái đặc biệt**: skeleton lần đầu; khi đổi bộ lọc giữ bảng cũ mờ + "Đang cập nhật…" (không nhấp nháy); chưa có lượt nào → hướng dẫn + nút **Tải hồ sơ lên** (`/upload`) và link System Prompt; lọc không khớp → **Bỏ lọc**; trang vượt quá → "Trang 9 không có lượt nào" + "Về trang 5"; lỗi API/timeout → khung lỗi + **Thử lại** (+ Bỏ lọc khi có bộ lọc); có lượt đang chạy → ghi chú "trang không tự tải lại, bấm Làm mới" (không polling).
- **Phản hồi cũ**: `RunHistoryWorkspace` hủy request trước (CancellationToken) + generation guard; kiểm tra `page/pageSize/type/status` của phản hồi khớp bộ lọc; `Dispose` hủy mọi request khi rời trang.

## 4. Trang `/runs/{id}` — Chi tiết lượt chấm

- Liên kết "← Lịch sử lượt chấm" trở về đúng bộ lọc/trang trước (đọc lại từ query, kiểm tra hợp lệ; sai → `/runs`). Tiêu đề "Chi tiết lượt chấm #ID" + `StatusBadge`, dòng phụ loại · giờ bắt đầu · model; nút **Làm mới**.
- **Dãy số liệu cao bằng nhau** (5 thẻ Screening, 6 thẻ PromptTest): Tổng hồ sơ · Đã xử lý (x/y, thanh tiến độ khi đang chạy) · Hồ sơ lỗi (đỏ khi > 0, kèm số chỉ có điểm từ khóa) · Bắt đầu · Hoàn tất + thời gian chạy · Tỷ lệ đúng (PromptTest). Lượt đang chạy: "Chưa hoàn tất" + thông báo xanh; lượt Thất bại có thông báo đỏ.
- **Cấu hình đã dùng (snapshot, chỉ xem)**: phiên bản System Prompt + mã phiên bản, Model, Temperature, "30% từ khóa · 70% AI" tính từ DTO của lượt + thanh tỷ lệ + chú thích Trọng số Keyword / Trọng số AI, Ngưỡng Đạt, Bộ CV đánh giá (PromptTest). Nút **Xem nội dung Prompt** (`aria-expanded/aria-controls`, số ký tự/dòng) mở khung đọc `<pre>` dạng text an toàn (Blazor mã hóa; `<script>` hiện nguyên chữ), không có ô sửa, không log. Ghi chú hạn chế A25: ngưỡng 85/70/55 và trần 69 không lưu theo lượt.
- **Kết quả và liên kết**: Screening → từng job, số hồ sơ/lỗi, **Xem bảng xếp hạng** → `/results?runId={id}&jobId={job}` (đúng lượt đang xem). PromptTest → bộ đánh giá, tỷ lệ đúng, hồ sơ đã đánh giá, **Xem đánh giá Prompt** → `/prompts?testRunId={id}`; đã đọc `PromptTestFlow.RestoreAsync`: token số nguyên chỉ `GET` lượt có sẵn, **không tạo lượt mới**.
- **Hồ sơ lỗi**: chip tổng hợp FailedCount (đỏ nhẹ, ✕) và KeywordOnlyCount (vàng nhạt, !) — phân biệt bằng màu + biểu tượng + chữ; bảng/thẻ: hồ sơ (tên/bí danh + tệp), vị trí, loại lỗi, lý do; CV trích xuất thất bại có ô **Cách khắc phục** + link Tải lại CV; **Xem kết quả** → `/results/evaluations/{evaluationId}` (Screening kèm `runId/jobId` để quay về đúng bảng). Không có lỗi → một dòng xanh gọn (ẩn chip 0/0). Lỗi `/failures` → giữ nguyên tóm tắt/cấu hình, báo lỗi trong khung + **Thử lại** chỉ tải lại danh sách.
- **ID sai**: `/runs/99999` → "Không có lượt chấm #99999." + **Thử lại** + Về lịch sử; `/runs/0`, `/runs/-5` báo mã không hợp lệ, không gọi API; `/runs/abc` không khớp route → trang NotFound sẵn có. Khi đổi route id, dữ liệu lượt cũ bị xóa ngay (và chỉ render lượt có `RunId == Id`).

## 5. Ẩn danh và hiển thị lỗi an toàn

- Inject `AnonymityState`, đăng ký `Changed` (cập nhật trực tiếp khi bật/tắt), gọi `EnsureLoadedAsync` khi tương tác; đặt `AnonymousToggle` trong khung Hồ sơ lỗi.
- Bật Ẩn danh: tên → `Anonymity.Name(null, applicationId)` ("Ứng viên #501 · HS501"), tệp → `Anonymity.FileName` ("CV đã ẩn tên"); aria-label dùng cùng bí danh; không đặt tên/tệp vào `title`, DOM ẩn hay log. Danh sách `Items` (có tên) của lượt không bao giờ được render.
- Lý do lỗi: `RunText.SafeReason` chỉ cho qua **11 câu cố định** mà A25 `SafeRunFailure` tạo; mọi chuỗi khác (URL provider, API key, đường dẫn máy, nội dung CV) bị thay bằng câu chung cùng loại — kể cả khi tắt Ẩn danh. `CvExtractionError` không bao giờ render; gợi ý suy từ `CvExtractionStatus`.
- Lỗi API: chỉ câu cố định theo mã HTTP; offline/timeout/JSON sai dùng câu cố định của `ApiClientBase`.
- Lưu ý hành vi B36 có sẵn (không sửa): khi chưa đọc được lựa chọn → che (fail-closed); sau khi đọc, nếu trình duyệt **chưa từng lưu** lựa chọn thì Ẩn danh = tắt.

## 6. Khác biệt so với ZIP và lý do

| ZIP | B26 thực tế |
|---|---|
| Chép đè `RunApiClient` (bản B38, `IEnumerable`, `take=20`) | Giữ nguyên client hiện tại (B6/B7/B38/A42), chỉ **thêm** `ListPageAsync(RunHistoryFilter)` và `GetFailuresAsync`. |
| Chép đè `NavMenu` (Lucide stroke, nhãn khác) | Giữ nguyên menu hiện tại, chỉ thêm 1 mục "Lịch sử lượt chấm" (icon fill cùng kiểu) vào nhóm Quản lý. |
| `RunText` có sẵn từ B19 | Không có trên main → tạo mới; trọng số qua `Display.Weight` (tỷ lệ 0–1) thay heuristic `≤1`; thời gian chạy phân biệt 0/null/ngày; gợi ý CV không chèn `extractionError`; thêm `SafeReason`, giờ VN an toàn với `DateTime.MaxValue`. |
| URL chỉ có `type/status`, `replace: true`, ngày/trang không lên URL | Đủ 5 tham số trên URL, push history, kiểm tra trước khi gọi API, hủy + generation guard. |
| `<tr @onclick>` + link | Chỉ liên kết `#id` (bàn phím), không click trên `<tr>`. |
| `Display.Percent`, `Display.Duration`, `sc-btn-ghost`, `sc-label`, `sc-input` | Không tồn tại trong source hiện tại → dùng `RunText`, token CSS và nút `sc-btn-*` đang có. |
| Chi tiết: link `/prompts` không có RunId, render `ErrorMessage` thô, lỗi failures không thử lại riêng | `/prompts?testRunId={id}`, `SafeReason`, thử lại riêng, giữ tóm tắt, chặn hiển thị lượt cũ khi đổi id. |

## 7. Danh sách file (A/M/D)

| Trạng thái | File |
|---|---|
| A | `docs/B26-implementation-report.md` |
| M | `src/SmartCv.Web/Components/Layout/NavMenu.razor` (+2 dòng) |
| A | `src/SmartCv.Web/Components/Pages/RunDetail.razor`, `RunDetail.razor.css` |
| A | `src/SmartCv.Web/Components/Pages/Runs.razor`, `Runs.razor.css` |
| A | `src/SmartCv.Web/Helpers/RunDetailWorkspace.cs`, `RunHistoryFilter.cs`, `RunHistoryWorkspace.cs`, `RunText.cs` |
| M | `src/SmartCv.Web/Services/RunApiClient.cs` (+12 dòng, chỉ thêm) |
| A | `tests/SmartCv.Tests/B26RunHistoryTests.cs` |
| D | (không có) |

Không sửa API, Core, Infrastructure, database, migration, `Program.cs`, `ServiceCollectionExtensions` (RunApiClient đã đăng ký), `.gitignore`, package hay solution.

## 8. Build

`dotnet --info`: SDK 10.0.403, runtime 10.0.14, Windows 10.0.26300 ([đầy đủ](../artifacts/b26/dotnet-info.txt)). Build baseline và build cuối (restore mới vào `artifacts/b26/final-build`): **0 warning, 0 error**.

## 9. Kiểm thử

Runsettings giống B21 (`DOTNET_ENVIRONMENT/ASPNETCORE_ENVIRONMENT=Testing`), không cấp biến SQL/Gemini; không có biến `SMARTCV*` trong môi trường. Đếm từ TRX (skipped = `NotExecuted`).

| Lượt | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| Baseline full (trước khi sửa) | 2482 | 2375 | 0 | 107 |
| B26 riêng (cuối) | 83 | 83 | 0 | 0 |
| Focused regression (B19/B20/B21/B26, A25 Runs, Results UI, B34/B36/B38/B39/B41, Jobs, WebApiClient, EvaluationDetail, PromptActivation) | 797 | 775 | 0 | 22 |
| Full regression (cuối) | 2565 | 2458 | 0 | 107 |

Đối chiếu baseline → cuối: **thêm đúng 83 test B26, bỏ 0 test cũ, danh sách skipped không đổi** (102 test SQL opt-in + 5 kịch bản demo ZIP: A38 consent, A42 OCR demo, B34 demo, SampleCvMasking, A40 ReviewFlags). B26 chạy lặp 5 lần liên tiếp: 83/83 mỗi lần. Không xóa/skip/giảm assertion test cũ.

83 test B26 (22 Fact + 7 Theory/61 case) gồm: parser URL (24 case hợp lệ/không hợp lệ, tham số lặp, link B19 `?status=Running`), chữ hiển thị (thời gian 0/null/rất lớn, accuracy null ≠ 0%, trọng số, giờ UTC+7, `SafeReason`), hợp đồng client (B7 không `page`, lịch sử luôn `page+pageSize`), **A25 thật trong bộ nhớ** (TestServer + EF InMemory, không worker/Gemini): ngày VN 00:00:00 và 23:59:59 đều được tính, trang lịch sử, chi tiết snapshot (prompt cũ, không phải prompt đang active), Failed/KeywordOnly tách riêng, ẩn danh không lộ tên/tệp/secret; render component thật cho bảng (null/0/rất lớn), 10 deep link sai không gọi API, 3 trạng thái rỗng, 6 kiểu lỗi + Thử lại, phản hồi cũ đến muộn sau khi đổi bộ lọc, URL/Back/Forward/re-render không gọi trùng, khoảng ngày sai, prompt chỉ là text node, link Results/Prompts chỉ GET, Ẩn danh bật/tắt trực tiếp, 404/500/503/timeout, failures lỗi giữ tóm tắt, đổi route id, lượt đang chạy, sidebar + route.

Lệnh tái hiện:

```powershell
dotnet restore SmartCv.slnx -p:ArtifactsPath=C:/TTCN/project/AI-CV-Screening/artifacts/b26/final-build
dotnet build SmartCv.slnx --no-restore --artifacts-path artifacts/b26/final-build
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b26/final-build --settings artifacts/b26/tests.runsettings --filter "FullyQualifiedName~B26RunHistoryTests"
dotnet test SmartCv.slnx --no-build --artifacts-path artifacts/b26/final-build --settings artifacts/b26/tests.runsettings
```

TRX/log: `artifacts/b26/baseline-tests/baseline.trx`, `final-b26-tests/b26.trx`, `final-focused-tests/focused.trx`, `final-full-tests/full.trx`; danh sách tên: `tests-baseline.txt`, `tests-final.txt`, `skipped-baseline.txt`, `skipped-final.txt`.

## 10. Nghiệm thu trình duyệt

Harness `artifacts/b26/browser/` (không thuộc commit): API A25 **giả lập dữ liệu tổng hợp** (47 lượt, tên ứng viên ghi rõ "dữ liệu mẫu", lọc bằng chính `RunHistoryQuery`) + **SmartCv.Web thật** chạy in-process + Chromium headless 156.0.8078.4 qua Playwright .NET 1.55. Không SQL Server, không Gemini, không CV thật. Kết quả: **11/11 nhóm kiểm tra đạt, 0 lỗi console, 91 request đều là GET** ([kết quả + số đo](../artifacts/b26/browser-results.json)).

| Số đo | Giá trị |
|---|---|
| Font | Inter |
| `/runs`: h1 / h2 / ô bảng / tiêu đề cột / label / badge | 28 / 18 / 13 / 12 / 12 / 12 px |
| Control bộ lọc / nút phân trang | 44 px (5 control) / 40 px |
| Cột số | căn phải, `tabular-nums`; 0 badge bị xuống dòng |
| `/runs/{id}`: h1 / h2 / số KPI / dữ liệu cấu hình | 26 / 18 / 26 / 14 px; 5 thẻ cùng cao 131 px |
| Tràn ngang (`/runs` và `/runs/{id}`) ở 1440/1024/768/390/360/320 | `scrollWidth == innerWidth`, 0 phần tử ra ngoài khung |

Đã kiểm trong trình duyệt: phân trang + Trước/Sau disabled; đổi bộ lọc → trang 1; Back/Forward/F5 giữ bộ lọc; `from > to` báo lỗi, không request; ngày 05/11 lấy đúng #116, #115, #114 theo UTC+7; lọc rỗng/Bỏ lọc; URL sai không request; trang vượt quá; hệ thống rỗng → `/upload`; 503 + Thử lại; chọn Screening (chậm 1,5 s) rồi PromptTest ngay → chỉ còn PromptTest; Tab tới `#id` có viền focus, Enter mở chi tiết, link Lịch sử về đúng bộ lọc/trang; chi tiết Screening/PromptTest/Running/Not found/không lỗi; mở prompt (thẻ `<script>`, `<b>` hiện nguyên văn); Ẩn danh bật/tắt trực tiếp; failures 500 giữ tóm tắt rồi Thử lại; đổi route id không hiện lượt cũ khi đang tải.

Ảnh (43 tệp, `artifacts/b26/screenshots/`): `runs-{1440,1024,768,390,360,320}` (+ `-top`, `-list`), `runs-filter-prompttest`, `runs-invalid-range`, `runs-invalid-url`, `runs-filtered-empty`, `runs-system-empty`, `runs-api-error`, `runs-updating`, `runs-loading-stale`, `runs-keyboard-focus`, `detail-screening-{1440,1024,768,390,360,320}`, `detail-prompt-open`, `detail-prompt-open-390`, `detail-failures-anonymous`, `detail-failures-named`, `detail-prompttest`, `detail-running`, `detail-not-found`, `detail-failures-error`, `detail-no-failures`, `detail-loading`.

## 11. Lỗi đã phát hiện và sửa trong quá trình làm

- Rà soát ảnh: nhãn "Đánh giá prompt" bị cắt (…); "đã xử lý 7" xuống 3 dòng; tên dài làm dòng bảng rất cao; ở chế độ thẻ (≤1250px) badge trạng thái rơi xuống cuối thẻ do auto-placement của CSS grid; `.more-jobs` không bị giới hạn 2 dòng do độ ưu tiên CSS; badge "AI lỗi · chỉ có điểm từ khóa" xuống dòng; bộ lọc trên điện thoại chiếm cả màn hình; viền hàng số liệu trong thẻ bị đứt đoạn; thẻ 320px chật; chip "0/0" thừa cạnh thông báo không lỗi; chip đếm cũ còn hiện khi danh sách lỗi đang báo lỗi. Tất cả đã sửa và đo lại (harness kiểm thêm: chip không bị cắt, badge một dòng và nằm trong thẻ, đầu thẻ cùng hàng).
- Câu lỗi dùng chung ghi "lịch sử lượt chấm" cả ở trang chi tiết → thêm tham số chủ ngữ.
- Lỗi của chính test/harness (không phải lỗi sản phẩm): biên trang tràn tính sai (107374183 vẫn hợp lệ, 107374184 mới tràn); assertion HTML chưa tính thuộc tính CSS isolation `b-xxxx`; harness giả định Ẩn danh mặc định bật; dữ liệu mẫu gán tỷ lệ đúng cho lượt Thất bại. Chỉ sửa test/harness, không nới điều kiện sản phẩm.

## 12. Giới hạn chưa kiểm chứng

- Chưa chạy với SQL Server và database thật (`SmartCvDb`) — theo yêu cầu không sửa database; A25 được kiểm qua TestServer + EF InMemory, trình duyệt qua API giả lập.
- Chưa bấm sang trang `/results` và `/prompts` thật trong trình duyệt; chỉ kiểm `href` và đọc code `Results.razor`/`PromptTestFlow` (`testRunId` số → chỉ GET).
- Trang Tổng quan B19 chưa mở trong trình duyệt ở phiên này; link `/runs?status=Running` và `/runs/{id}` được kiểm bằng parser + route attribute + test B19 sẵn có (đều pass).
- Chưa thử với trình đọc màn hình thật; ở chế độ thẻ (≤1250px) bảng chuyển `display:block` nên ngữ nghĩa bảng có thể mất với một số trình đọc (giống B20).
- Ô ngày dùng giao diện gốc của trình duyệt (Chromium headless hiển thị `mm/dd/yyyy`); giá trị gửi đi luôn `yyyy-MM-dd`.
- Ở 320px, nhãn "Tỷ lệ đúng" trong thẻ số liệu xuống 2 dòng (các thẻ vẫn cao bằng nhau).
- Không tự tải lại lượt đang chạy (cố ý, tránh nhấp nháy) — người dùng bấm Làm mới.
- Hồ sơ lỗi của lượt PromptTest mở `/results/evaluations/{id}` không kèm `runId/jobId` (nút quay lại của trang đó về `/results`).
- Ngưỡng 85/70/55 và trần 69 không lưu theo lượt (hạn chế A25, đã ghi chú trên giao diện).

## 13. Checkpoint Git cuối

```text
$ git status --short --branch
## feature/b26-run-history
 M src/SmartCv.Web/Components/Layout/NavMenu.razor
 M src/SmartCv.Web/Services/RunApiClient.cs
?? docs/B26-implementation-report.md
?? src/SmartCv.Web/Components/Pages/RunDetail.razor
?? src/SmartCv.Web/Components/Pages/RunDetail.razor.css
?? src/SmartCv.Web/Components/Pages/Runs.razor
?? src/SmartCv.Web/Components/Pages/Runs.razor.css
?? src/SmartCv.Web/Helpers/RunDetailWorkspace.cs
?? src/SmartCv.Web/Helpers/RunHistoryFilter.cs
?? src/SmartCv.Web/Helpers/RunHistoryWorkspace.cs
?? src/SmartCv.Web/Helpers/RunText.cs
?? tests/SmartCv.Tests/B26RunHistoryTests.cs

$ git diff --check
(không có output)

$ git diff --cached --name-status
(không có output — chưa stage gì)
```

File mới: CRLF thống nhất, không khoảng trắng cuối dòng, không BOM; `core.autocrlf=true` (system) nên sẽ được lưu LF trong index như các file hiện có.

## 14. Đối chiếu manifest

[`artifacts/b26-commit-files.txt`](../artifacts/b26-commit-files.txt): 12 đường dẫn tương đối, mỗi dòng một path, không trùng, khớp đúng 12 file trong `git status` ở mục 13 (2 M + 10 A) — không thiếu, không thừa, không chứa file ngoài B26. Artifacts, log, ảnh, TRX, harness trình duyệt đều nằm trong `artifacts/` (bị ignore) và không thuộc manifest.

Gợi ý commit khi duyệt xong: `git add` đúng 12 path trong manifest; thông điệp ví dụ `feat(B26): implement run history and run detail pages`.
