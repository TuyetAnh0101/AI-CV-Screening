# B25 + làm mới giao diện toàn hệ thống — báo cáo triển khai

Nhánh `feature/b25-unified-ui-refresh`. Ngày thực hiện: 19/11/2026. Trạng thái bàn giao: **chưa `git add`, chưa commit, chưa push, chưa tạo PR, chưa merge.**

---

## 1. Tên nhiệm vụ B25

Theo hướng dẫn tuần 4 trong ZIP (`HUONG_DAN_A23_A24_A43_B23_B24_B25.md`, mục *B25 – Trang quản lý bộ đánh giá*): **B25 = Trang Bộ đánh giá `/evaluation-sets`**. Trang này cho biết số nhãn, độ tin cậy theo C8 và các lượt đánh giá. B25 **không** phải phần quyền dữ liệu ứng viên: xuất/xóa dữ liệu (A43) thuộc B24 và đã merge ở #81.

Lần này làm thêm việc người dùng yêu cầu: làm mới giao diện toàn hệ thống theo hướng "Modern Enterprise ATS — Deep Teal + AI Glow + Mint/Cyan".

## 2. Tài liệu và ảnh đã đọc

| Nguồn | Nội dung đã đọc |
|---|---|
| `C:\TTCN\A23_B25_SmartCv.zip` → `HUONG_DAN_A23_A24_A43_B23_B24_B25.md` | Bảng phân việc (B25 phụ thuộc B41, không cần API mới) và toàn bộ mục B25: giao diện, file, các bước, cách thử, commit, xử lý sự cố |
| `B25/src/SmartCv.Web/Helpers/EvaluationSetHealth.cs` | Quy tắc C8 và các hằng số |
| `B25/src/SmartCv.Web/Components/Pages/EvaluationSets.razor` (+ `.css`) | Bố cục mẫu |
| `B25/src/SmartCv.Web/Components/Layout/NavMenu.razor` | Mục "Bộ đánh giá" |
| `B25/tests/SmartCv.Tests/Web/EvaluationSetHealthTests.cs` | 11 test quy tắc |
| Ảnh mẫu | **Không có ảnh mẫu trong ZIP.** |

## 3. Thông tin Git

- Nhánh: `feature/b25-unified-ui-refresh`, tạo bằng `git switch --no-track -c feature/b25-unified-ui-refresh origin/main`.
- Base SHA (40 ký tự): `9a3aaadd67b7519ab92629b90cb5c4dcb444e5c5`, cũng ghi trong `artifacts/b25-ui-base-sha.txt`.
- Lúc tạo nhánh, `HEAD` = `origin/main` = commit trên.
- Lệnh **không** dùng: worktree, clone, stash, reset, clean, restore, rebase, `git add`, commit, push.

## 4. Xác nhận B24

- `origin/main` có `9a3aaad feat(B24): unify candidate and CV management (#81)`.
- Nhánh B24 cục bộ không còn khác biệt file nào so với `main`.
- Phụ thuộc của B25 (B41: gán nhãn ở trang Prompt) đã có trên `main` từ trước.

## 5. Audit từng trang (trước khi sửa)

| Trang / thành phần | Vấn đề tìm thấy | Đã xử lý |
|---|---|---|
| Khung chung (MainLayout, NavMenu) | Nền nội dung xám phẳng; sidebar `#0A2E2D→#0E4A47` (teal xanh lá) lệch bảng màu mới | Nền `--sc-grad-page` pha teal; sidebar Deep Teal + ánh Mint; thanh trên trắng mờ |
| `app.css` | Token rời rạc; màu cứng trong 25 file CSS scoped (`#0f766e`, `#115e59`, `#14b8a6`, `#6366f1`…); chỉ có 2 kiểu nút | Bộ token mới, ánh xạ tên cũ, 5 kiểu nút + disabled/loading/small, primitive ô nhập |
| Bảng điều khiển | Chấm trang trí ở dòng phạm vi và phễu | Bỏ chấm; giữ biểu đồ 2 trục (chỉ đổi màu theo token) |
| Tải lên | Tiêu đề 28px cứng; chấm "info" | Tiêu đề theo token; chấm thay bằng icon "i" |
| Ứng viên / Hồ sơ CV / Chi tiết | Chấm giai đoạn trang trí; chấm trạng thái đọc chữ luôn hiện | Bỏ chấm giai đoạn; chấm đọc chữ chỉ hiện khi *đang đọc* (nhấp nháy) |
| Kết quả | h1 30px; chấm trạng thái luôn hiện; **ô chọn hàng không bấm được trên điện thoại (ô hạng đè lên)**; nút Loại dùng `btn-danger` Bootstrap | h1 theo token; chấm chỉ khi lượt đang chạy; sửa đè ô (z-index); nút Loại dùng `sc-btn-danger` |
| Chi tiết đánh giá | h1 tới 32px | 24–28px |
| Vị trí tuyển dụng | Chấm trạng thái trang trí | Bỏ chấm; màu theo token |
| System Prompt | Quá dài: chứa cả quản lý bộ + gán nhãn; chấm "Đang dùng" | Tách bộ đánh giá sang `/evaluation-sets`; "Đang dùng" dùng dấu ✓ |
| Lịch sử / Chi tiết lượt | Màu AI là indigo (`#6366f1`) | Đổi sang Cyan (AI Glow) |
| ConfirmDialog | Icon "Ⅱ" khó hiểu; màu đỏ riêng | Icon "!"/"?"; nút `sc-btn-danger`; bóng/bo góc theo token |
| ErrorAlert | `btn-outline-danger` Bootstrap | `sc-btn-danger-outline sc-btn-sm` |
| Tiêu đề tab | `Results` và `PageHeader` ghi "SmartCV", các trang khác ghi "SCApp" | Thống nhất "SCApp" |

## 6. Design tokens — trước / sau

| Nhóm | Trước | Sau (B25) |
|---|---|---|
| Thương hiệu | `--sc-brand-700 #0f766e`, `-800 #0e4a47`, `-900 #0a2e2d`, `-500 #14b8a6` | `--sc-deep-teal #0e3640`, `--sc-teal #164c56` (=brand-700), `--sc-brand-900 #0a2a32`, `--sc-mint #28bfa8` (=brand-500), `--sc-cyan #54b9d2`, `--sc-mist #edf6f5` (=brand-50), `--sc-paper #f5f7f5` (=surface-50) |
| Chữ | ink-900 `#0f2a2a` | `--sc-ink-900 #20343c` (Ink), 700/500/400 chỉnh theo |
| Ngữ nghĩa | `--sc-error/info/neutral` | Thêm `--sc-success/-warning/-danger/-info` (+ `-bg`, `-line`); `--sc-error` trỏ về danger |
| Gradient | `--sc-grad-sidebar`, `--sc-grad-ai` (có indigo) | `--sc-grad-brand` (+hover), `--sc-grad-mint`, `--sc-grad-ai` (Mint→Cyan), `--sc-grad-sidebar`, `--sc-grad-page`, `--sc-grad-head` |
| Bóng | 1 bóng thẻ | `--sc-shadow-xs`, `--sc-shadow-card`, `--sc-shadow-pop` |
| Bo góc | card 16, section 20 | Thêm `--sc-radius-sm 8`, `-md 10`, `-lg 12`, `-pill` |
| Khoảng cách | (không có) | `--sc-space-1…8` (4–32px) |
| Chữ | (cỡ cứng từng trang) | `--sc-text-badge 12`, `-label 12`, `-body 13`, `-base 14`, `-section 17`, `-page clamp(24px, 2.3vw, 28px)` |
| Điều khiển | (không có) | `--sc-control-h 40`, `-h-sm 32`, `--sc-focus #1f8f87`, `--sc-focus-ring`, `--sc-disabled-bg/-fg/-line` |
| Tên cũ | `--brand`, `--border`, `--text-*`, `--fit-*` | Giữ nguyên tên, trỏ sang token mới. CSS scoped của các trang không phải đổi tên. |

Mức đánh giá `--fit-*` giữ nguyên **nghĩa và ngưỡng**, chỉ chỉnh sắc độ. Không đổi logic chấm điểm.

## 7. Thành phần dùng chung

- **Nút:** `.btn.sc-btn-primary | -secondary | -tertiary | -danger | -danger-outline | -success`, `.sc-btn-sm`, `.is-loading` (vòng quay, giữ kích thước), `.sc-more-btn` (nút ⋯). B24 `c-btn-danger(-outline)` dùng chung quy tắc.
- **Ô nhập:** `.sc-field`, `.sc-label`, `.sc-input` (B24 `.c-input` dùng chung), `.sc-help`, `.sc-field-error` (chừa sẵn chiều cao để báo lỗi không làm nhảy bố cục).
- **Thẻ:** `.sc-card` / `.card-panel`, `.page-header` (nền `--sc-grad-head`).
- **Huy hiệu:** `StatusBadge` và `.fit-badge` đọc token ngữ nghĩa.
- **Hộp thoại:** `ConfirmDialog` và `ModalShell` dùng `--sc-shadow-pop`, nền mờ Deep Teal.
- Không thêm selector rộng kiểu `input, select` toàn cục; CSS scoped của từng trang vẫn tự quyết bố cục.

## 8. Nút, ô nhập, form và trạng thái

- **Chiều cao:** mọi nút dùng chung `min-height: 40px` (nhỏ: 32px), bo 10px, chữ 14px/600.
- **Primary:** gradient Deep Teal. **Secondary:** nền trắng, viền teal nhạt. **Tertiary:** không viền.
- **Danger:** đỏ `#b42318`. Xóa / loại / bỏ khỏi bộ **không bao giờ dùng màu xanh lá**. Đã đổi các nút sau sang danger: StageDialog "Loại", ConfirmDialog, "Bỏ khỏi bộ", "Bỏ thay đổi và rời trang".
- **Disabled:** nền xám nhạt, chữ xám, `cursor: not-allowed`. Không làm mờ cả khối nữa, nên chữ vẫn đọc được.
- **Focus:** viền 2px `--sc-focus`, đủ tương phản trên nền trắng, cách 2px; ô nhập thêm vòng sáng Mint.
- **Validation:** viền đỏ + `aria-invalid`; vùng lỗi chừa sẵn chiều cao.
- **Loading:** `.is-loading` có sẵn cho nút mới. Các nút cũ vẫn đổi chữ ("Đang lưu…") như trước, chưa chuyển hết (xem mục 19).

## 9. Gradient — dùng ở đâu

| Gradient | Chỉ dùng cho |
|---|---|
| `--sc-grad-brand` | Nút Primary, icon nhấn (thẻ lối sang Bộ đánh giá trên trang Prompt) |
| `--sc-grad-mint` | Thanh tỷ lệ Đạt / Không đạt (B25) |
| `--sc-grad-ai` | Thứ do AI tạo (giữ quy ước cũ, đổi indigo → Mint/Cyan) |
| `--sc-grad-sidebar` | Menu trái |
| `--sc-grad-page` | Nền vùng nội dung (pha teal ở trên, tránh trắng tinh) |
| `--sc-grad-head` | Thẻ đầu trang: Tải lên, Kết quả, `.page-header` |

## 10. Chấm trạng thái — giữ / bỏ

**Giữ** (trạng thái sống, luôn kèm chữ):
- API online/offline (`ApiStatusBadge`)
- Tab "Phân tích hồ sơ" khi lượt đang chạy (Upload)
- `RunProgressPanel` live
- "Đang cập nhật…" (Runs)
- `.fit-badge.is-pending` (Đang chấm)
- Chấm đọc chữ CV khi `Pending`
- Chấm lượt ở Kết quả, chỉ khi lượt Queued/Running

**Giữ** (dấu dữ liệu / chú giải biểu đồ, không phải trang trí):
- Điểm trên biểu đồ xu hướng
- Điểm CI ở So sánh tỷ lệ đúng
- Chú giải ●○ của ScoreRing
- Chú giải Bỏ sót / Chọn nhầm (MismatchTable)
- Chú giải lý do loại (Dashboard)

**Bỏ** (trang trí):
- Chấm giai đoạn: CvApplication, CandidateDetail, RankingTable, EvaluationSetManager
- `.stage-dot` toàn cục
- Chấm phạm vi (Dashboard)
- Chấm phễu và chân phễu (StageFunnel)
- Chấm "info" (Upload, thay bằng icon "i")
- Chấm hạn mức (Playground)
- Chấm mức chất lượng chữ (ExtractedTextDialog)
- Chấm trạng thái vị trí (Jobs)
- Chấm "Đang dùng" (Prompts, thay bằng ✓)
- `::before` của mọi `.fit-badge` (trừ is-pending)

## 11. Trang đã làm / chưa làm

| Trang | Kết quả |
|---|---|
| Bảng điều khiển | Làm mới màu, bỏ chấm trang trí, giữ biểu đồ 2 trục (Deep Teal / Mint) |
| Tải lên | Thẻ đầu trang gradient, tiêu đề theo token, icon info |
| Ứng viên, Hồ sơ CV, Chi tiết ứng viên | Token mới, bỏ chấm, nút xóa dùng danger-outline chung |
| Kết quả, Chi tiết đánh giá | Tiêu đề, gradient đầu trang, nút Loại màu đỏ chuẩn, sửa ô chọn trên điện thoại |
| Vị trí tuyển dụng, Sửa vị trí | Token mới, bỏ chấm trạng thái |
| Lịch sử lượt chấm, Chi tiết lượt | Token mới, màu AI → Cyan, tiêu đề mobile 24px |
| System Prompt | Ngắn lại (bỏ khối bộ CV + gán nhãn), thêm thẻ lối sang Bộ đánh giá, khóa bản nháp chỉ còn cho prompt |
| **Bộ dữ liệu đánh giá (mới)** | Xem mục 12–13 |
| Chưa làm | Không đổi bố cục bên trong từng trang (chỉ đổi token và thành phần). Chưa thay mọi hàng thao tác phụ bằng menu ⋯ (mục 19). |

## 12. Cấu trúc Bộ đánh giá ↔ System Prompt

```
/evaluation-sets  (menu: Đánh giá AI › Bộ dữ liệu đánh giá)
 ├─ Đầu trang + nút "System Prompt · Chạy Prompt Test →"   (kèm ?set=<id>)
 ├─ Hướng dẫn C8 (<details>, mở sẵn khi chưa có bộ)
 └─ EvaluationSetManager — DUY NHẤT một instance (đã chuyển khỏi /prompts)
     ├─ Bước 1 Tạo bộ · thẻ từng bộ (viền + chữ kết luận C8, Đạt/Không đạt, thanh tỷ lệ, sai số, phiên bản bộ, số lượt)
     ├─ Bước 4 Kiểm tra bộ (số liệu API) + Độ tin cậy B25 (mức, sai số, tỷ lệ, cảnh báo)
     ├─ [slot SetInsights] Lượt Prompt Test trên bộ này (5 lượt gần nhất, cảnh báo "bộ đã đổi sau lượt")
     ├─ Bước 3 Gán nhãn HR · Bước 2 Thêm CV đã quét   (logic thành viên/nhãn UX v2 giữ nguyên)
     └─ Link Bước 5 → /prompts?set=<id>#prompt-test-panel
/prompts
 ├─ Soạn prompt · Lịch sử phiên bản · Thử với hồ sơ (B39)
 ├─ Thẻ "Bộ dữ liệu đánh giá → Quản lý bộ đánh giá"
 ├─ PromptTestPanel — NƠI DUY NHẤT chạy Prompt Test (nhận ?set= để chọn sẵn bộ)
 └─ So sánh tỷ lệ đúng · CV AI đánh giá khác HR
```

- Đổi bộ trên `/evaluation-sets` ghi `?set=<id>` bằng `replace`, không thêm bước Back. Tham số này chỉ là mã bộ, không chứa PII.
- `NavigationLock` trên `/evaluation-sets`:
  - Khi còn nhãn hoặc ghi chú chưa lưu, rời trang sẽ bị chặn và hiện thông báo "Ở lại và lưu / Bỏ thay đổi và rời trang".
  - Đổi bộ ngay trong trang vẫn do bộ quản lý tự xử lý như trước.
- Thanh "Đang dùng" ở `/prompts`: khóa bản nháp prompt vẫn giữ nguyên.
- Không có API sửa hoặc sao chép bộ, nên **không thêm nút sửa/sao chép** (đã có test kiểm tra).

## 13. Bảng đối chiếu yêu cầu B25

| Yêu cầu trong hướng dẫn B25 | Cách làm | Trạng thái |
|---|---|---|
| Trang `/evaluation-sets` | `Pages/EvaluationSets.razor` (+ `.css`) | ✅ |
| Hướng dẫn C8, mở sẵn khi chưa có bộ | `<details class="guide" open="@(setCount == 0)">`, 6 quy tắc | ✅ |
| Mỗi bộ một thẻ, viền trên xanh / vàng / đỏ theo kết luận | `set-card verdict-good/caution/poor/empty` + chip chữ kết luận | ✅ |
| Ngưỡng: Đủ tin cậy / Dùng được, cần thận trọng / Chưa đủ tin cậy | `Helpers/EvaluationSetHealth.cs` (MinSize 30, GoodSize 50, lệch <20% nặng, <30% hơi lệch, mỗi nhãn ≥10, CV đọc lỗi) | ✅ |
| Số trên thẻ: CV có nhãn · Đạt · Không đạt + thanh tỷ lệ · số vị trí · số lượt · sai số | Thẻ có: CV có nhãn, Đạt, Không đạt, thanh tỷ lệ, sai số ±, số lượt, phiên bản bộ. **Số vị trí** nằm ở khối độ tin cậy của bộ đang mở, đếm từ thành viên có nhãn | ✅ (số vị trí: xem mục 15) |
| Sai số "±10,9 điểm %" (Wilson A41, p≈80%) | `MarginOfError(n)` dùng `PromptAccuracyCalculator.Wilson` | ✅ (30→13,9 · 50→10,9 · 100→7,8 có test) |
| Danh sách cảnh báo cụ thể | `health-issues`, nhãn "Nghiêm trọng / Cần lưu ý / Thông tin" | ✅ |
| 5 lượt gần nhất (→ `/runs/{id}`), tỷ lệ đúng; link xem thêm | Bảng "Lượt Prompt Test trên bộ này" + "Xem tất cả lượt" → `/runs?type=PromptTest` | ✅ (thêm cột phiên bản bộ và cảnh báo bộ đã đổi) |
| Nút "Gán nhãn ở trang Prompt" | **Thay đổi theo yêu cầu mới:** gán nhãn chuyển hẳn sang trang này, nên đổi thành nút "System Prompt · Chạy Prompt Test" | ✅ (khác ZIP, xem mục 15) |
| Menu Đánh giá AI › Bộ đánh giá | "Bộ dữ liệu đánh giá" ngay dưới "System prompt" | ✅ |
| Chưa có lượt: thông báo | "Bộ này chưa có lượt Prompt Test nào…" + nút sang trang chạy | ✅ |
| Tắt API: khung lỗi + Thử lại | Lượt: thông báo cố định + "Thử lại" (không lộ lỗi gốc). Danh sách bộ: lỗi của bộ quản lý + "Tải lại" | ✅ |
| Test 11 quy tắc | Đã port và thêm test (mục 17) | ✅ |
| Không làm lại gán nhãn | Dùng lại `EvaluationSetManager` của UX v2, chỉ chuyển chỗ | ✅ |

## 14. API đã dùng (không thêm API mới)

- `GET /api/evaluation-sets`: danh sách bộ và số liệu thẻ (LabelCount, PassCount, UnreadyCount, Revision, RunCount…).
- `GET /api/evaluation-sets/{id}`: thành viên, readiness, phiên bản bộ (UX v2).
- `GET /api/evaluation-sets/{id}/candidates`, `POST …/members`, `DELETE …/members/{appId}`, `PUT …/labels`, `GET …/label-suggestions`: giữ nguyên trong `EvaluationSetManager`. Chỉ ghi khi HR bấm nút; không tự ghi.
- `GET /api/prompt-tests?evaluationSetId={id}` (`PromptApiClient.GetAccuracyRunsAsync`): lượt Prompt Test của bộ đang mở.
- `GET /api/jobs`: bộ lọc vị trí (như cũ).
- Trang `/prompts`: giữ nguyên các API cũ.

Mỗi lần mở trang chỉ gọi thêm **một** API lượt cho bộ đang mở, thay vì 2 lời gọi cho **mỗi** bộ như mẫu ZIP. Có bộ đếm thế hệ để bỏ phản hồi cũ.

## 15. Thay đổi so với hướng dẫn / ngoài SmartCv.Web

**Không có file nào ngoài `src/SmartCv.Web` và `tests/` bị sửa.** Không đổi chấm điểm, Gemini, OCR, worker, lưu CV, bảo mật, migration, nhãn HR, quy trình tuyển dụng.

Những điểm khác mẫu ZIP:
1. **Gán nhãn chuyển sang `/evaluation-sets`.** ZIP để gán nhãn ở trang Prompt và chỉ đặt link. Yêu cầu mới bảo tách quản lý bộ khỏi `/prompts`, nên `EvaluationSetManager` (UX v2) chuyển hẳn sang trang mới, giữ một instance.
2. **Số vị trí** chỉ tính cho bộ đang mở, đếm từ thành viên có nhãn đã lưu. Không gọi `/applications` cho từng bộ (tránh N+1). Thẻ bộ truyền `jobCount = 0` nên bỏ qua quy tắc "chỉ 1 vị trí".
3. **CV đọc lỗi** lấy từ `UnreadyCount` (thẻ) và `Readiness.LabeledNotReadyCount` (bộ đang mở) do API trả về, không tự đếm lại.
4. Tên menu: "**Bộ dữ liệu đánh giá**" (theo yêu cầu mới) thay vì "Bộ đánh giá".
5. Thêm cột **phiên bản bộ** và cảnh báo "Bộ đã đổi (hiện tại N)" theo `SetChangedSinceRun` (UX v2).
6. Lỗi tải lượt hiện thông báo cố định, **không** đưa nguyên văn lỗi API ra giao diện.
7. Sửa lỗi có sẵn (không do B25 gây ra): ở Kết quả trên màn ≤767px, ô hạng đè lên ô chọn hàng nên **không chọn được hồ sơ trên điện thoại**. Đã sửa bằng `position: relative; z-index: 1` cho `.c-select`.

## 16. File thêm / sửa / xóa

Danh sách đầy đủ (A/M, phân cách bằng TAB) ở `artifacts/b25-ui-commit-manifest.tsv`. Tóm tắt:

- **A (5):**
  - `src/SmartCv.Web/Components/Pages/EvaluationSets.razor`
  - `src/SmartCv.Web/Components/Pages/EvaluationSets.razor.css`
  - `src/SmartCv.Web/Helpers/EvaluationSetHealth.cs`
  - `tests/SmartCv.Tests/B25EvaluationSetsTests.cs`
  - `docs/B25-ui-refresh-implementation-report.md`
- **M (61):** `wwwroot/app.css`, `Layout/NavMenu.razor`, `Layout/MainLayout.razor.css`, `Pages/Prompts.razor` (+css), `Prompts/EvaluationSetManager.razor` (+css), `Prompts/PromptTestPanel.razor`, `Prompts/PromptPlayground.razor`, `Prompts/AccuracyCompare.razor`, `Shared/ConfirmDialog.razor` (+css), `Shared/ErrorAlert.razor`, `Shared/PageHeader.razor`, `Shared/StatusBadge.razor.css`, `Results/StageDialog.razor`, `Results/RankingTable.razor` (+css), `Pages/Jobs.razor` (+css), `Pages/Home.razor` (+css), `Pages/Upload.razor` (+css), `Pages/Results.razor` (+css), `Dashboard/StageFunnel.razor` (+css), và các file `.razor.css` chỉ đổi màu cứng sang token.
- **D:** không có.

## 17. Build và kiểm thử

| Bước | Lệnh | Kết quả |
|---|---|---|
| Baseline (trước khi sửa) | `dotnet build SmartCv.slnx -c Release` + `dotnet test tests/SmartCv.Tests -c Release --no-build` | Build 0 cảnh báo, 0 lỗi · **2801 đạt, 0 lỗi, 122 bỏ qua** |
| Test B25 | `--filter FullyQualifiedName~B25Ui` | **18 đạt, 0 lỗi (cùng `EvaluationSetWorkspaceTests`: 22/22). Các nhóm test trang Prompt (B39, PromptVersioningUi, PromptActivationUi) đạt** |
| Build cuối | `dotnet build SmartCv.slnx -c Release` | **0 cảnh báo, 0 lỗi** |
| Regression đầy đủ | `dotnet test tests/SmartCv.Tests -c Release --no-build` | ****2819 đạt, 0 lỗi, 122 bỏ qua** (= 2801 baseline + 18 test B25). Một lần chạy trung gian có 1 lỗi ở `RunScoringWorkerTests.Idle_worker_polls_on_schedule_not_in_a_tight_loop`: test nhạy thời gian, đã biết; chạy riêng 15/15 đạt; hai lần chạy đầy đủ sau đó 0 lỗi** |

Test mới (`SmartCv.Tests.B25Ui.B25EvaluationSetsTests`, 16 phương thức, 18 ca):
- 12 ca quy tắc độ tin cậy (port từ ZIP, thêm "không biết số vị trí" và "số liệu không nhất quán").
- Trang `/evaluation-sets`:
  - đúng 1 `EvaluationSetManager`, không có PromptTestPanel;
  - hướng dẫn, mức tin cậy, viền thẻ;
  - cảnh báo phiên bản bộ;
  - link `/prompts?set=7#prompt-test-panel`;
  - không có nút sửa/sao chép;
  - không gửi lệnh ghi.
- Đổi bộ: URL `?set=8` dùng `replace` và tải lượt của bộ 8.
- Lỗi tải lượt: thông báo cố định, không lộ lỗi gốc; "Thử lại" hoạt động.
- Bản nháp nhãn chặn rời trang; "Ở lại" giữ nguyên bản nháp; "Bỏ thay đổi" rời trang mà không ghi gì.
- `/prompts`:
  - không còn bộ quản lý;
  - chọn sẵn bộ từ `?set=`;
  - chỉ một `#prompt-test-panel`;
  - không tải thành viên bộ.

Test cũ: không phải sửa test nào. Không test cũ nào kiểm tra `EvaluationSetManager` nằm trong trang Prompt; `EvaluationSetWorkspaceTests` render component trực tiếp nên vẫn đạt.

## 18. QA trình duyệt theo viewport

- **Công cụ:** Chrome cài sẵn qua Playwright .NET 1.55, headless.
- **API thật** (`SmartCv.Api` qua `WebApplicationFactory` + Kestrel) với DB **InMemory**, dữ liệu giả, AI giả, worker tắt.
- **Bảng điều khiển** cần SQL Server (truy vấn SQL thuần). Dùng lại catalog thử nghiệm có sẵn `SmartCv_A19_Test_ui3c41f96dda73` ở chế độ **reuse, không seed lại, chỉ đọc**. Không tạo DB mới, không đụng SmartCvDb.

| Viewport | Trang đã chụp | Tràn ngang | Lỗi console |
|---|---|---|---|
| 1440 / 1280 / 1024 / 820 / 768 / 390 / 360 / 320 | Bảng điều khiển, Tải lên, Ứng viên, Hồ sơ CV, Chi tiết ứng viên, Kết quả, Chi tiết đánh giá, Vị trí, Sửa vị trí, System Prompt, Bộ đánh giá (3 trạng thái bộ), Lịch sử lượt, Chi tiết lượt | **Không** ở cả 128 ảnh trang | **0** |

Kiểm tra thao tác (1440 và 390):
- `/evaluation-sets?set=1`: 1 bộ quản lý, 0 PromptTestPanel, 2 lượt, 1 lượt "bộ đã đổi".
- Gán nhãn nháp rồi bấm sang System Prompt: URL giữ nguyên, hiện thông báo. "Bỏ thay đổi" chuyển tới `/prompts?set=1#prompt-test-panel` và đã chọn sẵn bộ "Backend junior".
- Sau khi sửa: thứ tự trên trang là Độ tin cậy → Lượt Prompt Test → Tab gán nhãn (đã đo ở 320/360/820).
- Kết quả 390: chọn hàng được, mở hộp thoại Loại (nút đỏ).

Lỗi bố cục tìm thấy khi QA và đã sửa:
1. Chip trạng thái ép tên bộ thành nhiều dòng → chip xuống dòng riêng.
2. Bảng lượt nằm cuối trang, sau 40 hồ sơ → đưa lên ngay sau bước 4 (slot `SetInsights`).
3. "Tạo bộ CV" / "Xem tất cả lượt" xuống dòng trong nút ở 320px → `nowrap` + padding gọn.
4. Ô chọn hàng ở Kết quả trên điện thoại bị che (lỗi có sẵn) → sửa z-index.
5. Chấm trạng thái trang trí ở Vị trí, Phễu, Hồ sơ CV → bỏ / chỉ hiện khi đang chạy.

Ảnh `evaluation-sets-*-<viewport>.png` toàn trang được chụp **trước** sửa lỗi 1–3. Ảnh `crop-sets-*` (320/360/820) chụp **sau** khi sửa.

## 19. Giới hạn và việc chưa làm

- **Menu ⋯:** đã có kiểu `.sc-more-btn`, nhưng chưa chuyển các hàng thao tác phụ hiện có sang menu ⋯. Mỗi trang cần một menu có điều khiển bàn phím đầy đủ, làm vội dễ hỏng truy cập.
- **`.is-loading`:** có sẵn, nhưng các nút cũ vẫn đổi chữ "Đang …" như trước, nên độ rộng có thể đổi nhẹ.
- **Số vị trí trên thẻ:** chỉ có ở bộ đang mở (mục 15.2).
- **Thông báo chặn ở PromptTestPanel:** "Bộ CV phải có nhãn của cả hai loại…" vẫn hiện cùng lỗi "2 hồ sơ chưa sẵn sàng" khi bộ có CV đọc lỗi. Đây là logic có sẵn của PromptTestPanel, B25 không đổi.
- Ảnh toàn trang của `/evaluation-sets` có trước sửa bố cục (mục 18); đã chụp bổ sung ảnh cắt sau sửa.
- **Chưa kiểm tra trên dữ liệu thật SmartCvDb** (cố ý: không chạm DB thật).
- **Catalog thử nghiệm giữ lại**, chờ duyệt dọn: `SmartCv_A19_Test_ui3c41f96dda73`, `SmartCv_A19_Test_ui6ddbc3f319cf`, `SmartCv_A19_Test_faa9c453…` (từ các nhiệm vụ trước). B25 chỉ đọc catalog đầu tiên, không tạo catalog mới.

## 20. Đường dẫn ảnh chụp

Thư mục `artifacts/b25-ui-screenshots/` (bị `.gitignore`, không đưa vào commit):

- **Trang × viewport:** `<trang>-<1440|1280|1024|820|768|390|360|320>.png`, với trang là:
  - `dashboard`, `dashboard-inmemory`, `upload`
  - `candidates`, `cvs`, `candidate-detail`
  - `results`, `evaluation-detail`
  - `jobs`, `job-edit`
  - `prompts`
  - `evaluation-sets`, `evaluation-sets-good`, `evaluation-sets-empty-set`
  - `runs`, `run-detail`
- **B25:**
  - `evaluation-sets-guide-open-{1440,390}`
  - `evaluation-sets-runs-{1440,390}`
  - `evaluation-sets-discard-{1440,390}`
  - `evaluation-sets-create-{1440,390}`
  - `prompts-from-set-{1440,390}`
  - `crop-sets-{top,cards,health,runs}-{320,360,820}`
- **Hộp thoại:**
  - `modal-extracted-text-{1440,390}`
  - `modal-stage-reject-{1440,390}`
  - `modal-candidate-editor-{1440,390}`
  - `modal-confirm-{1440,390}`
  - `crop-results-select-390`
  - `focus-ring-1440`

## 21. `git status` cuối

Nhánh `feature/b25-unified-ui-refresh`, HEAD vẫn là base `9a3aaad`. Chưa `git add`, chưa commit.

```
 M src/SmartCv.Web/Components/Candidates/CandidatePeopleView.razor.css
 M src/SmartCv.Web/Components/Candidates/ModalShell.razor.css
 M src/SmartCv.Web/Components/CvLibrary/AttachCandidateDialog.razor.css
 M src/SmartCv.Web/Components/CvLibrary/CvApplication.razor
 M src/SmartCv.Web/Components/Dashboard/StageFunnel.razor
 M src/SmartCv.Web/Components/Dashboard/StageFunnel.razor.css
 M src/SmartCv.Web/Components/Dashboard/TrendChart.razor.css
 M src/SmartCv.Web/Components/Layout/MainLayout.razor.css
 M src/SmartCv.Web/Components/Layout/NavMenu.razor
 M src/SmartCv.Web/Components/Pages/CandidateDetail.razor
 M src/SmartCv.Web/Components/Pages/CandidateDetail.razor.css
 M src/SmartCv.Web/Components/Pages/Candidates.razor.css
 M src/SmartCv.Web/Components/Pages/CvLibrary.razor.css
 M src/SmartCv.Web/Components/Pages/EvaluationDetail.razor.css
 M src/SmartCv.Web/Components/Pages/Home.razor
 M src/SmartCv.Web/Components/Pages/Home.razor.css
 M src/SmartCv.Web/Components/Pages/Jobs.razor
 M src/SmartCv.Web/Components/Pages/Jobs.razor.css
 M src/SmartCv.Web/Components/Pages/Prompts.razor
 M src/SmartCv.Web/Components/Pages/Prompts.razor.css
 M src/SmartCv.Web/Components/Pages/Results.razor
 M src/SmartCv.Web/Components/Pages/Results.razor.css
 M src/SmartCv.Web/Components/Pages/RunDetail.razor.css
 M src/SmartCv.Web/Components/Pages/Runs.razor.css
 M src/SmartCv.Web/Components/Pages/Upload.razor
 M src/SmartCv.Web/Components/Pages/Upload.razor.css
 M src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor
 M src/SmartCv.Web/Components/Prompts/AccuracyCompare.razor.css
 M src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor
 M src/SmartCv.Web/Components/Prompts/EvaluationSetManager.razor.css
 M src/SmartCv.Web/Components/Prompts/PromptEditor.razor.css
 M src/SmartCv.Web/Components/Prompts/PromptHistory.razor.css
 M src/SmartCv.Web/Components/Prompts/PromptPlayground.razor
 M src/SmartCv.Web/Components/Prompts/PromptPlayground.razor.css
 M src/SmartCv.Web/Components/Prompts/PromptTestPanel.razor
 M src/SmartCv.Web/Components/Prompts/PromptTestPanel.razor.css
 M src/SmartCv.Web/Components/Results/CandidateDrawer.razor.css
 M src/SmartCv.Web/Components/Results/FitDistribution.razor.css
 M src/SmartCv.Web/Components/Results/HrDecisionPanel.razor.css
 M src/SmartCv.Web/Components/Results/ManualFlagDialog.razor.css
 M src/SmartCv.Web/Components/Results/RankingTable.razor
 M src/SmartCv.Web/Components/Results/RankingTable.razor.css
 M src/SmartCv.Web/Components/Results/RunJobFilter.razor.css
 M src/SmartCv.Web/Components/Results/StageDialog.razor
 M src/SmartCv.Web/Components/Results/StageDialog.razor.css
 M src/SmartCv.Web/Components/Results/StageTimeline.razor.css
 M src/SmartCv.Web/Components/Shared/AnonymousToggle.razor.css
 M src/SmartCv.Web/Components/Shared/ConfirmDialog.razor
 M src/SmartCv.Web/Components/Shared/ConfirmDialog.razor.css
 M src/SmartCv.Web/Components/Shared/ErrorAlert.razor
 M src/SmartCv.Web/Components/Shared/PageHeader.razor
 M src/SmartCv.Web/Components/Shared/ReviewFlagsBadge.razor.css
 M src/SmartCv.Web/Components/Shared/ScoreBar.razor.css
 M src/SmartCv.Web/Components/Shared/ScoreRing.razor.css
 M src/SmartCv.Web/Components/Shared/StatusBadge.razor.css
 M src/SmartCv.Web/Components/Upload/CvDropzone.razor.css
 M src/SmartCv.Web/Components/Upload/CvFileTable.razor.css
 M src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor
 M src/SmartCv.Web/Components/Upload/ExtractedTextDialog.razor.css
 M src/SmartCv.Web/Components/Upload/RunProgressPanel.razor.css
 M src/SmartCv.Web/wwwroot/app.css
?? docs/B25-ui-refresh-implementation-report.md
?? src/SmartCv.Web/Components/Pages/EvaluationSets.razor
?? src/SmartCv.Web/Components/Pages/EvaluationSets.razor.css
?? src/SmartCv.Web/Helpers/EvaluationSetHealth.cs
?? tests/SmartCv.Tests/B25EvaluationSetsTests.cs
```

`artifacts/` (ảnh, SHA, manifest) bị `.gitignore` nên không xuất hiện ở đây.

## 22. `git diff --check`

`git diff --check`: **không có lỗi khoảng trắng.** Git chỉ cảnh báo về chuyển đổi LF→CRLF của `app.css` và `CandidateDetail.razor.css` (do `core.autocrlf`, không phải lỗi).

## 23. Đối chiếu manifest ↔ `git status`

`artifacts/b25-ui-commit-manifest.tsv`: 66 dòng `A`/`M` + TAB + đường dẫn (5 A, 61 M, 0 D), gồm cả báo cáo này.

Đối chiếu hai chiều: `diff <(cut -f2 manifest | sort) <(git status --porcelain --untracked-files=all | cut -c4- | grep -v ^artifacts/ | sort)` → **không khác**. Mọi file trong manifest đều có trong status và ngược lại.

Manifest không chứa `artifacts/`, `bin/`, `obj/`, `.env`, secret, upload, log, ảnh chụp hay bản sao lưu.

**Bàn giao:** Chưa `git add`. Chưa commit. Chưa push. Chưa tạo PR. Chưa merge.
