# B39 – Thử prompt trên một CV và xem nội dung gửi Gemini

Báo cáo triển khai ngày 17/11/2026. Trạng thái: **đã code, build, test xong; chưa stage/commit/push** — chờ duyệt.

## 1. Git và nền

| Mục | Giá trị |
|---|---|
| Nhánh | `feature/b39-prompt-playground` (mới, `--no-track`) |
| Worktree | `C:\TTCN\project\AI-CV-Screening-B39` (worktree riêng; working tree chính không bị đụng tới) |
| SHA nền | `238c6d7cb0dd7171aa791664af74f63d9d7620ea` = `origin/main` lúc tạo (sau `git fetch origin --prune`) |
| Phụ thuộc | A41 `7cd42ab` (#61), A42 (#62, #63), B38 (#64), A25 (#69) đều đã có trên nền |
| Nhánh B39 trước khi làm | Không có local, không có remote (`git ls-remote --heads origin`) |
| Working tree chính | `C:\TTCN\project\AI-CV-Screening` ở `feature/a25-run-history-api`, sạch, giữ nguyên; không stash/reset/checkout |
| Lúc bắt đầu | Worktree B39 sạch (`git status` rỗng) |

## 2. Ma trận yêu cầu → code → bằng chứng

| Yêu cầu B39 | Đã làm | Bằng chứng |
|---|---|---|
| Nút **Thử với hồ sơ…** cạnh **Hoàn tác**, mở khung ngay dưới ô soạn | Nút trong `PromptEditor` (thứ tự: Thử với hồ sơ… · Hoàn tác · Lưu thành phiên bản mới), `aria-expanded`/`aria-controls`; khung `PromptPlayground` nằm cuối thẻ ô soạn; đóng thì trả tiêu điểm về nút | `Try_button_sits_next_to_undo…`; ảnh `after-01`, `after-02`; báo cáo Playwright “closed; focus on: Thử với hồ sơ…” |
| Không làm mất chữ đang gõ / tên / ghi chú / `PromptDraft` / `NavigationLock` | Khung chỉ **đọc** `PromptDraft`; không gọi `Load/Reset`, không đổi `IsDirty` | `Edited_prompt_runs_as_draft_keeps_editor_state…`, `Closing_while_running…` |
| Nháp → `{"systemPrompt"}`, giống bản nguồn → `{"promptVersionId"}`, không gửi cả hai | `PlaygroundRules.Plan/SingleSource`; client gửi body chỉ có **một** trường; thiếu/thừa nguồn bị chặn trước khi gửi | `Unchanged_prompt_runs_by_version_id…`, `Draft_preview_posts_only_system_prompt…`, `Invalid_source_is_rejected_without_any_http_call` |
| Quy tắc CRLF/LF/khoảng trắng | Đổi CRLF/CR → LF rồi bỏ khoảng trắng đầu/cuối (đúng như A41 `Trim()` bản nháp); khoảng trắng giữa nội dung vẫn là thay đổi | `Draft_detection_normalizes_line_endings_and_outer_whitespace_only` (6 trường hợp) |
| Chưa có phiên bản nguồn | Luôn chấm dạng nháp; nội dung trống/quá 20.000 ký tự → khóa nút kèm lý do, không null reference | `Without_any_version_the_draft_is_tested…`, `Empty_or_oversized_drafts_are_blocked…` |
| Không bao giờ tạo/kích hoạt phiên bản khi chấm thử | Không có lời gọi `POST /api/prompts`, `/activate`, PUT/PATCH/DELETE trong luồng | `NoPromptMutations` trong mọi test UI; `Real_api_…never_create_or_activate_prompts` (đếm bảng trước/sau trên API thật InMemory) |
| Kết quả cũ khi prompt đổi | Mỗi kết quả mang “chữ ký” nguồn (`version:{id}` hoặc toàn bộ nội dung nháp đã chuẩn hóa — **không** dùng `GetHashCode`); khác chữ ký → khung vàng “Kết quả của nội dung trước”, thẻ chấm thử mờ + viền đứt, nhãn “chưa cập nhật” trên thẻ xem | `Results_of_previous_content_are_labelled…`, `Double_click_sends_one_request_and_edit_during_request_marks_result_stale`; ảnh `after-06` |
| Đổi CV/Job/bộ/phiên bản → xóa dữ liệu nhạy cảm, bỏ phản hồi muộn | `ResetOutputsAsync` hủy mọi request đang chờ (CTS riêng từng loại), xóa kết quả + nội dung gửi đi; phản hồi danh sách của bộ cũ bị bỏ qua | `Changing_job_resets…`, `Selecting_another_version_in_history_clears…`, `Page_set_change_reloads_labels_and_late_responses…`, `Page_set_change_while_running_cancels…` |
| Dùng **bộ CV đang chọn của trang** | Tham số `EvaluationSetId` từ `Prompts.razor`; đổi bộ trong khung → báo lại trang (đồng bộ Chạy đánh giá / So sánh). Trang chưa chọn → bộ đầu tiên theo thứ tự API (cùng quy tắc `AccuracyCompare`) và báo lại trang | `Without_page_set_the_first_set_is_chosen_and_reported…`; test đầu tiên kiểm tra mọi request danh sách đi tới bộ 7 |
| Nhóm Job → hồ sơ theo **ID**, nhãn `· HR: Đạt/Không đạt` | `PlaygroundRules.Jobs` nhóm theo `JobId`; option “Tên · HS{id} · HR: Đạt”; Job hiển thị “2/5 sẵn sàng” | `Jobs_are_grouped_by_id_not_title`, `Option_text_shows_hr_label…` |
| Pending/Failed/không chữ/đã ẩn danh: khóa + lý do; CV tombstone không lộ tên/file | Option `disabled` + “— đọc CV lỗi / CV đang được đọc / CV không có chữ để chấm / đã ẩn danh hóa theo hạn lưu”; tombstone hiện “Hồ sơ đã ẩn danh · HS5” | `Unready_and_anonymized_applications_are_disabled…`; ảnh `after-03` |
| Trạng thái đổi giữa lúc chọn và lúc bấm (409/404) | Báo lỗi tiếng Việt, tự tải lại danh sách, nút khóa kèm lý do mới | `Application_becoming_unready_between_pick_and_run_reports_409…` |
| Hai cột: chấm thử vs **Đã lưu · phiên bản** của đúng hồ sơ | Thẻ trái (viền AI Glow) từ `AiPreviewDto`; thẻ phải từ `CurrentEvaluation`; hai cột cạnh nhau khi khung ≥ 560 px, xếp dọc khi hẹp hơn | ảnh `after-05`, `after-07`, `after-10-*` |
| Điểm, mức, Đạt/Không đạt, từ khóa / AI, kỹ năng đạt/thiếu, nhận xét, cảnh báo KeywordOnly / AI lỗi / AI giả lập | `TrialView`/`SavedView`; kỹ năng thiếu bắt buộc in đậm “(bắt buộc)”; cảnh báo AI giả lập không nói là tiêu tiền Gemini | `Trial_view_warns_about_simulated_or_keyword_only_scores` |
| Chênh điểm có dấu, chỉ khi hai điểm hợp lệ; null ≠ 0 | “+6,5 / −22 điểm so với bản đã lưu”; không so khi thiếu điểm đã lưu hoặc hai bên khác nguồn điểm (một bên chỉ từ khóa) | `Signed_delta…`, `Missing_scores_are_never_treated_as_zero`, `Compare_explains_why…` |
| Chưa có kết quả đã lưu | “Hồ sơ chưa có kết quả đã lưu để so” | `Application_without_saved_evaluation…` |
| Thẻ **Nội dung gửi Gemini** từ `POST /api/prompts/assembled-preview`, không tốn lượt | Gọi khi mở thẻ / đổi hồ sơ; khi đang xem mà sửa prompt thì **chờ ngừng gõ 600 ms** rồi gọi 1 lần; không gọi lại khi chỉ render lại; lỗi không tự gọi lại | `Payload_tab_shows_server_masked_content_without_spending_quota_and_refreshes_after_edit` |
| “Đã che thông tin cá nhân” + số liệu từ API | Dòng trạng thái + bảng 6 ô (Họ tên, Email, Điện thoại, Liên kết, Ngày sinh, Giới tính), CV gốc/đã gửi, đã cắt, model, temperature, AI giả lập, mã kỹ năng cho phép; nhãn `[EMAIL]`… tô nhẹ (vẫn là text) | ảnh `after-08`, `after-11` |
| Không tự ghép CV ở Web, không MarkupString | Chỉ hiển thị chuỗi API trả; `<pre>` text Razor tự escape | `Payload_is_rendered_as_text_not_markup` (thử `<img onerror>`) |
| 409 masking tắt → không hiện payload | Nhận `code = PersonalDataMaskingDisabled`: xóa payload cũ, hiện hướng dẫn bật `Gemini:MaskPersonalData = true`; API trả 200 nhưng `personalDataMasked = false` cũng **không** hiển thị (fail closed) | `Masking_disabled_409_hides_payload…`, `Unmasked_success_response_is_never_displayed`, `Real_api_masking_disabled_returns_409…`; ảnh `after-13` |
| Badge lượt thử từ API, vàng khi ≤ 3, hết thì khóa | “Còn X/Y lượt thử trong Z phút” (Y, Z lấy từ API); tone ok/low/empty; hết lượt: nút khóa + “Lượt sớm nhất được trả lại lúc HH:mm (UTC+7)” + “Kiểm tra lại lượt” (chỉ GET) | `Quota_text_uses_api_numbers_and_tones`, `Quota_429_locks_the_button…`; ảnh `after-12` |
| 429 tiếng Việt, không tự retry POST, đọc lại quota | Dùng `detail` tiếng Việt của API; `PreviewQuotaExhausted` không có nút thử lại; `PreviewBusy`/rate limiter cho thử lại **thủ công** kèm Retry-After; mọi lỗi đều GET lại quota (không tự trừ) | `Server_side_429_shows_vietnamese_message…`, `Problem_code_and_retry_after_are_exposed_for_429` |
| 503/timeout/mất mạng/null JSON/lệch schema | Thông báo an toàn, nói rõ lượt **có thể** đã được tính, chỉ thử lại thủ công | `Busy_or_unavailable_server_allows_manual_retry_only`, `Null_json_schema_mismatch_html_error_and_network_failure_are_safe_messages` |
| Double-click, request chồng, đóng khung khi đang chạy | Cờ `running` + nút khóa; nút **Dừng chờ** hủy request rồi đọc lại quota; đóng khung → Dispose hủy mọi task, không cập nhật trạng thái sau đó | `Double_click_sends_one_request…`, `Stop_waiting_cancels_request_and_rereads_quota`, `Closing_while_running_cancels_without_errors…` |
| Bàn phím, a11y | Thẻ xem là **nút thường** `aria-pressed` + `aria-controls` (không dùng `role=tablist` vì không làm điều hướng mũi tên); `aria-live` cho quota/kết quả; `focus-visible` 3 px màu `--sc-brand-700`; tiêu điểm vào tiêu đề khung khi mở; `prefers-reduced-motion` | ảnh `after-09`; Playwright: Shift+Tab tới “Kết quả” |
| Ẩn danh (B36) | Khi bật Ẩn danh: option hiện “Ứng viên #id · HS id”, nhận xét AI tự do bị ẩn như trang Chi tiết | `Anonymous_mode_hides_candidate_names…` |

### Khác ZIP và lý do

- **Không chép đè** `Prompts.razor`, `PromptEditor.razor`, `PromptApiClient.cs` của ZIP (thế hệ cũ, sẽ xóa NavigationLock, activation sync, EvaluationSetManager, PromptTestPanel, AccuracyCompare, MismatchTable). Chỉ chèn: 1 dòng tham số trong `Prompts.razor`, nút + khung trong `PromptEditor.razor`.
- **Không tạo** `IPromptApi`/`FakePromptApi`; không đổi DI sang fake. Dùng `PromptApiClient` thật; fake chỉ có trong test (HTTP handler giả).
- DTO: dùng đúng `AiPreviewDto`, `CurrentEvaluationDto`, `PreviewQuotaDto`, `AssembledPromptDto`, `AiPreviewRequest`, `AssembledPreviewRequest` của `SmartCv.Core.Contracts` (ZIP dùng `PromptPreviewResultDto` không có trên main).
- ZIP lấy bộ đầu tiên vô điều kiện → B39 dùng bộ đang chọn của trang.
- ZIP cache nội dung gửi đi bằng `Draft.GetHashCode()` → B39 so **toàn bộ** nội dung đã chuẩn hóa, có hủy request cũ, debounce.
- ZIP giả quota “giảm 1” sau 429 → B39 luôn đọc lại quota từ API.
- ZIP coi `null` là `0` ở thẻ đã lưu (`cur.FinalScore ?? 0`) → B39 hiển thị “—” và không tính chênh.
- `AiPreviewApiClient` (mới, 1 dòng DI): chỉ để có **thời gian chờ 5 phút** cho `POST ai-preview`. Client prompt hiện có giới hạn 30 giây, trong khi một lần chấm thử có thể chờ Gemini thử lại (`Gemini:TimeoutSeconds` 90 giây/lần). Hai API không tốn lượt (quota, assembled-preview) được thêm vào `PromptApiClient` như yêu cầu.
- `ApiResult`/`ApiClientBase`: **thêm** `ErrorCode` (trường `code` của ProblemDetails) và `RetryAfter` (header). Hành vi cũ giữ nguyên (thông báo, status, log); cần để phân biệt hết lượt / đang bận / tắt che dữ liệu.

## 3. API thật được dùng

| API (A41/A15) | Dùng khi | Ghi DB | Tốn lượt |
|---|---|---|---|
| `GET /api/evaluation-sets` | Mở khung (tên bộ, chọn bộ) | Không | Không |
| `GET /api/evaluation-sets/{id}/applications` | Danh sách Job/hồ sơ + nhãn HR của bộ | Không | Không |
| `GET /api/applications/ai-preview/quota` → `PreviewQuotaDto` | Mở khung, sau mọi lần chấm lỗi/hủy, “Kiểm tra lại lượt” | Không | Không |
| `POST /api/applications/{id}/ai-preview` body `{"systemPrompt"}` **hoặc** `{"promptVersionId"}` → `AiPreviewDto` | Bấm Chấm thử | Không (A41 kiểm chứng, test B39 đếm bảng) | Có (1 lượt, server quyết định) |
| `POST /api/prompts/assembled-preview` body `{"applicationId", "systemPrompt"|"promptVersionId"}` → `AssembledPromptDto` | Thẻ Nội dung gửi Gemini | Không | Không |

Quyền riêng tư: payload chỉ nằm trong bộ nhớ component (Blazor Server), bị xóa khi đổi lựa chọn/đóng khung; không ghi `localStorage`/`sessionStorage`/URL (Playwright kiểm tra: không có); không log nội dung prompt/CV (log hiện có của `ApiClientBase` chỉ ghi URL + mã lỗi + `detail` ProblemDetails của API). API đã trả `Cache-Control: no-store` cho tuyến này.

## 4. File (khớp `git status --porcelain=v1 --untracked-files=all` và `artifacts/b39-commit-files.txt`)

| Trạng thái | File | Lý do |
|---|---|---|
| M (+2 −1) | `src/SmartCv.Web/Components/Pages/Prompts.razor` | Truyền `evaluationSetId`, `setsRefresh`, `SelectEvaluationSet` cho ô soạn |
| M (+26) | `src/SmartCv.Web/Components/Prompts/PromptEditor.razor` | Nút “Thử với hồ sơ…”, hiển thị `PromptPlayground`, trả tiêu điểm khi đóng |
| M (+10) | `src/SmartCv.Web/Components/Prompts/PromptEditor.razor.css` | Kiểu nút thử (nhấn AI nhẹ), mobile ≥ 44 px |
| M (+22 −9) | `src/SmartCv.Web/Services/ApiClientBase.cs` | Đọc `code` của ProblemDetails và `Retry-After` |
| M (+9) | `src/SmartCv.Web/Services/ApiResult.cs` | Thêm `ErrorCode`, `RetryAfter` |
| M (+26) | `src/SmartCv.Web/Services/PromptApiClient.cs` | `GetPreviewQuotaAsync`, `GetAssembledPromptAsync` |
| M (+1) | `src/SmartCv.Web/Services/ServiceCollectionExtensions.cs` | Đăng ký `AiPreviewApiClient` (5 phút) |
| A | `src/SmartCv.Web/Components/Prompts/PromptPlayground.razor` | Khung thử |
| A | `src/SmartCv.Web/Components/Prompts/PromptPlayground.razor.css` | Giao diện khung (Deep Teal + AI Glow, container query) |
| A | `src/SmartCv.Web/Helpers/PlaygroundRules.cs` | Quy tắc thuần + `PlaygroundSelection` |
| A | `src/SmartCv.Web/Services/AiPreviewApiClient.cs` | `POST ai-preview` |
| A | `tests/SmartCv.Tests/B39PlaygroundRulesTests.cs` | 44 test quy tắc (kể cả theory) |
| A | `tests/SmartCv.Tests/B39PromptApiClientTests.cs` | 13 test client + hợp đồng API thật |
| A | `tests/SmartCv.Tests/B39PromptPlaygroundUiTests.cs` | 25 test trang/khung |
| A | `docs/B39-implementation-report.md` | Báo cáo này |

Không có D. Không sửa Backend (A41/A39/A42), database, Upload B38, B41, Figma. Line ending: lưu CRLF ở working tree như các file khác (`core.autocrlf=true` → index LF), không dòng trống thừa cuối file.

## 5. Kiểm thử

| Bước | Kết quả |
|---|---|
| Baseline trước khi sửa (`dotnet build` + `dotnet test SmartCv.slnx`) | 2231 test: 2120 pass, 0 fail, 111 skip |
| `dotnet build SmartCv.slnx` (cuối) | 0 lỗi, 0 cảnh báo |
| Focused `FullyQualifiedName~B39` | **82/82 pass** |
| Full regression `dotnet test SmartCv.slnx` (cuối) | **2313 test: 2202 pass, 0 fail, 111 skip** |
| Nhóm hồi quy liên quan (trong full run) | B12–B16 prompt UI 126 pass · A14–A17 143 pass + 20 skip · A41/A10 preview 74 pass + 1 skip · Gemini 88 pass · B34–B36 42 pass + 1 skip · B38/Upload 95 pass · WebApiClient 17 pass |
| 111 skip | Test SQL Server/OCR **opt-in** (biến `SMARTCV_TEST_SQL_CONNECTION`, `SMARTCV_A11/A15/A16/A17_TEST_SQL_CONNECTION`, `SMARTCV_B34_DEMO_ZIP`, tessdata). Không chạy vì không được phép đụng SQL/DB; B39 không đổi Backend/SQL |

Test B39 dùng HTTP giả theo hợp đồng A41 hoặc API thật chạy InMemory với `Gemini:UseFake = true` — **không gọi Gemini thật, không dùng SQL Server, không đụng `SmartCvDb`**.

### Giao diện trình duyệt (Chrome thật, Playwright .NET, headless)

Môi trường xem: API thật (pipeline `SmartCv.Api` của nhánh) chạy InMemory + AI giả trên cổng tạm, dữ liệu **tổng hợp** (tên mẫu “Nguyễn Văn A…”, email `@example.com`, số `0900 000 00x`); Web Release của nhánh; ảnh “trước” chạy Web của `origin/main` (`git archive`, không đổi nhánh). Ảnh nằm ở `artifacts/b39-screenshots/` (thư mục bị `.gitignore`, không commit):

| Ảnh | Nội dung |
|---|---|
| `before-editor-1440.png`, `before-editor-390.png` | `main`: chưa có nút thử |
| `after-01-editor-closed-1440.png` | Nút “Thử với hồ sơ…” cạnh Hoàn tác |
| `after-02-open-empty-1440.png`, `after-03-picker-options-1440.png` | Khung trống, badge lượt, danh sách hồ sơ có lý do khóa |
| `after-04-loading-1440.png` | Đang chấm (skeleton, Dừng chờ) |
| `after-05-version-result-1440.png`, `after-07-draft-result-1440.png` | Hai cột: phiên bản / bản nháp vs Đã lưu |
| `after-06-stale-after-edit-1440.png` | Kết quả của nội dung trước |
| `after-08-payload-1440.png`, `after-11-payload-360.png` | Nội dung gửi Gemini đã che |
| `after-09-keyboard-focus-1440.png` | Tiêu điểm bàn phím |
| `after-10-result-{1024,768,390,360}.png` | Responsive |
| `after-12-quota-exhausted-1440.png` | Hết lượt (badge đỏ, nút khóa, giờ mở lại) |
| `after-13-masking-off-1440.png` | Máy chủ tắt che dữ liệu → không hiện payload |

Kết quả tự động ở mọi khổ 1440/1024/768/390/360: 0 lỗi console/page, không cuộn ngang trang, 0 nút/ô chọn < 32 px trong khung (mobile ≥ 44 px), payload có `[EMAIL]`/`[PHONE]`/`[CANDIDATE]` và không có email/số gốc, storage/URL không chứa payload, ô soạn giữ bản nháp, lịch sử vẫn 2 phiên bản. Ảnh cắt theo phần tử được chụp với thanh trên cùng tạm đặt `position: static` **chỉ trong lúc chụp** để không đè ảnh (sản phẩm không đổi).

### Còn lại / rủi ro

- **Chưa chạy với Gemini thật và SQL Server thật** (không được phép). Cần nghiệm thu tay theo mục 7. AI giả cho cùng điểm cho nháp và phiên bản nên ảnh chụp chưa thể hiện “sửa một câu → điểm đổi” với dữ liệu thật; test UI kiểm tra điều này bằng HTTP giả.
- Hạn mức là của **cả ứng dụng** (A41): người khác dùng thì số lượt giảm; B39 luôn đọc lại từ API nhưng badge chỉ cập nhật khi mở khung/sau thao tác hoặc bấm “Kiểm tra lại lượt”.
- Hủy chờ / đóng khung khi đang chấm: server giữ lượt nếu đã gửi tới AI (thiết kế A41); giao diện báo rõ “có thể đã được tính”.
- Thông báo lỗi chi tiết là `detail` tiếng Việt từ API; API trả HTML (lỗi 500 Development) thì client chỉ hiện câu mặc định, không lộ stack trace.
- Thẻ Kết quả hiển thị nhận xét AI; khi bật Ẩn danh, nhận xét bị ẩn như trang Chi tiết (B36).
- Khung nằm trong cột ô soạn (2/3 bề rộng): hai cột so sánh cạnh nhau khi khung ≥ 560 px, ở 1100–1440 px mỗi cột ~310 px.

## 6. Trạng thái Git khi dừng

- `git diff --check`: sạch.
- `git diff --cached --name-status`: rỗng (không stage).
- `git status --short --branch`: `## feature/b39-prompt-playground`, 7 M + 8 A như mục 4 (kể cả file báo cáo này).
- Không có merge/rebase/cherry-pick đang dở, không có `index.lock`.
- `artifacts/` bị ignore: `b39-base-sha.txt`, `b39-commit-files.txt`, log build/test, TRX, ảnh.
- Không add/commit/push/merge/rebase/reset/stash/clean/restore, không tạo PR.

## 7. Ảnh hưởng và cách thử lại thủ công

- **B38**: không sửa file Upload; `ApiClientBase` chỉ thêm trường → 95 test B38/Upload pass.
- **B41**: sẽ đụng `PromptApiClient.cs`, `Prompts.razor`, `ApiClientBase.cs` (B41 ZIP có `PutJsonAsync`, main đã có sẵn). B39 chỉ thêm cuối file/1 dòng tham số → xung đột nếu có là nhỏ; **không** dùng `IPromptApi`/`FakePromptApi` của ZIP B41.
- **B42**: có thể dùng `PlaygroundRules` (API khác ZIP: `ScoreDelta` trả `decimal?`, chữ “Còn …/… lượt thử trong … phút” giữ nguyên).
- **Demo Development**: không đổi cấu hình API/Web; không bật worker; không đổi rate limiter.

Thử tay (API thật, `Gemini:UseFake = true` trước; Gemini thật chỉ khi được duyệt):

1. Chạy API + Web như thường. Trang **Prompt & độ chính xác** → **Thử với hồ sơ…**: thấy “Còn X/30 lượt thử trong 60 phút”, bộ CV trùng bộ đang chọn ở “So sánh tỷ lệ đúng”.
2. Chọn *Backend Developer (.NET)* → một hồ sơ “· HR: Đạt” → **Chấm thử v2-…**: hai cột, chênh điểm; lượt giảm 1.
3. Thêm một câu vào ô soạn → nút thành **Chấm thử bản nháp**, kết quả cũ ghi “Kết quả của nội dung trước” → bấm: cột Đã lưu giữ nguyên.
4. **Nội dung gửi Gemini**: thấy câu vừa thêm trong System prompt, `[EMAIL]`, `[PHONE]`, `[CANDIDATE]` trong CV; số lượt không đổi.
5. Lịch sử phiên bản không có bản mới; `database/check_a41_khong_tao_dong.sql` giữ nguyên 4 con số; chuyển trang thì vẫn hỏi giữ bản nháp.
6. Hết lượt: chạy API với `$env:AiPreview__LimitPerWindow="2"` → sau 2 lần nút khóa, badge đỏ, có giờ mở lại; xóa biến sau khi thử.
