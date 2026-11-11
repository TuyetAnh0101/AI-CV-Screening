# Bàn giao SCApp — UI shell refinement

Ngày kiểm tra: 11/11/2026 (Asia/Saigon).

## A. Trạng thái tiếp quản

Đã chạy đủ 11 lệnh Git được yêu cầu trước khi sửa code, đồng thời kiểm tra reflog, unmerged index và các marker merge/cherry-pick/revert/rebase/sequencer. Git trong sandbox ban đầu báo khác chủ sở hữu; dùng `git -c safe.directory=C:/TTCN/project/AI-CV-Screening ...` cho từng lệnh đọc, không sửa cấu hình Git global.

| Câu hỏi | Kết quả lúc tiếp quản |
|---|---|
| Đã có nhánh UI chưa? | Có. Reflog ghi `branch: Created from origin/main`, lúc 13:47:46 +0700 ngày 11/11/2026. Git không xác định công cụ đã tạo nhánh; phù hợp với trạng thái Claude bàn giao. |
| Nhánh hiện tại / nhánh UI | `feature/ui-shell-refinement`; tiếp tục chính nhánh này. |
| Commit nền và HEAD | `5d09a3123b455d8bd37f5d2752f2ea0e1d5db0c8` — `fix(scoring): correct experience handling and preserve scoring history (#40)`. |
| Nền main | `origin/main` cũng ở `5d09a31`; local `main` còn ở `aad69f2`, chậm 7 commit. Không checkout hay cập nhật main. |
| Thay đổi đang ở đâu? | 4 file unstaged trong working tree đang checkout nhánh UI; chưa thuộc commit nào. |
| Modified / untracked / staged | 4 / 0 / 0. Diff ban đầu: 159 dòng thêm, 76 dòng bỏ. |
| Merge/cherry-pick/conflict | Không có; unmerged index rỗng, không có marker thao tác dang dở. |
| B10/B11 hoặc việc khác | Không có thay đổi riêng ngoài UI trong working tree. B10 đã có trong lịch sử nền (PR #38); các chức năng B10 hiện hữu được giữ nguyên. |
| Rủi ro chuyển nhánh | Có 4 thay đổi chưa commit cần bảo toàn. Không cần chuyển nhánh, không thực hiện thao tác chuyển hay di chuyển thay đổi. |

Bản sao nguyên trạng 4 file tiếp quản nằm trong `artifacts/ui-shell/inherited/`; kết quả lệnh Git ban đầu được lưu tại `artifacts/ui-shell/git-initial-audit.json` (đều được Git ignore).

### Đánh giá công việc trước đó

- **A — Giữ lại:** sidebar desktop 256px, tablet 240px; header và vùng logo desktop 72px; Deep Teal; tokens màu, shadow, radius; menu 44px, icon 18px; hover/active/focus, reduced motion; tất cả nhóm và đích điều hướng. Các selector `::deep` của NavMenu đúng và `--sc-ink-900` có khai báo.
- **B — Hoàn thiện:** tokens mới chưa áp dụng đồng nhất lên các surface/bảng; font display còn ưu tiên Be Vietnam Pro và body còn hưởng 16px/24px từ Bootstrap.
- **C — Sửa lỗi:** bảng có thể tràn/cắt nội dung khi vùng chính hẹp; menu thao tác bị khung cuộn cắt; nút Hủy bị đẩy ra ngoài card ở 320/390px; preload font khác URL font thực dùng.
- **D — Bổ sung:** logo chính thức, font Inter cục bộ, thuộc tính trạng thái/quan hệ cho menu mobile, favicon chính thức.
- **E — Ngoài phạm vi:** không có thay đổi chưa commit của nhiệm vụ khác để xử lý. Trang Prompts hiện là placeholder B12–B16 từ commit nền, không triển khai nghiệp vụ trong nhiệm vụ này.

## B. Công việc Codex tiếp tục

Giữ phần triển khai tốt của Claude, không viết lại shell. Hoàn thiện trên 4 file đang sửa và 6 file UI hiện hữu:

- Thay ô chữ SC tạm bằng PNG chính thức từ Figma, giữ tỷ lệ ảnh gốc, nền sáng giúp logo đọc rõ trên sidebar tối. Dùng cùng logo cho favicon.
- Inter trở thành font chính và font display. Font variable đầy đủ dấu tiếng Việt được phục vụ cục bộ, có OFL; body 14px/22px. Font kỹ thuật giữ fallback monospace.
- Đồng nhất surface trắng, border nhẹ, radius card 16px, shadow nhỏ; bỏ các banner gradient cũ. Giữ AI glow tại thành phần AI hiện hữu.
- Giữ nguyên padding/grid/thứ tự section, bảng, form và nút của từng trang. Chỉ sửa khả năng co nút ở mobile và cuộn nội bộ bảng khi cần.
- Thêm `aria-expanded`, `aria-controls`, nhãn điều hướng và focus phù hợp cho menu mobile; header hẹp có thể xuống dòng.
- Bảng CV luôn cuộn ngang nội bộ khi thiếu chỗ. Script UI đặt menu thao tác hiện hữu theo viewport, cập nhật khi cuộn/resize và đóng bằng Escape; giữ nguyên handler Blazor/API.
- Bảng xếp hạng có khung cuộn ngang để giữ đủ cột.
- Sửa đường dẫn icon select tương đối với stylesheet được phục vụ tại gốc ứng dụng; đồng nhất URL preload với `@font-face` để tránh tải font trùng.

## C. Đối chiếu Figma và ZIP

- Đã gọi `get_design_context` node **170:917**, nhận code/metadata và screenshot logo. Asset tải nguyên bản từ URL Figma trả về, lưu `wwwroot/images/brand/scapp-logo.png`; không vẽ lại, chỉnh sửa ảnh hay dùng screenshot thiết kế làm nền.
- PNG gốc 1407×1118, 1.005.194 byte; SHA-256 `1A31241A35D79BD5033207909403AD8DF97938EC4DB03122EE6A3BB69B764280`. Hiển thị rộng 68px, cao tự động theo tỷ lệ gốc; kiểm tra ảnh tải thành công trong trình duyệt.
- Đã thử cả `get_design_context` và `get_screenshot` node **407:2313**. Cả hai bị giới hạn lượt gọi của Figma Starter. `whoami` xác nhận gói Starter. **Chưa xác minh được toàn bộ màn hình với node này; không tuyên bố pixel-perfect hoặc hoàn toàn khớp Figma.**
- Đã mở ZIP `C:\TTCN\B7_B16_SmartCv.zip`, đọc `B7B16/HUONG_DAN_B7_B16.md`, snippets font/CSS v2 và NavMenu mẫu.
- Đã xem đủ 4 ảnh: `1-ket-qua-xep-hang.png`, `2-ket-qua-chi-keyword.png`, `3-chi-tiet-cv.png`, `4-prompt-do-chinh-xac.png`.
- Preview xác nhận sidebar 256px, vùng logo/header 72px, nền `#f5f8f8`, menu 14px/nhãn nhóm 11px và palette đang kế thừa. Giữ điều hướng và bố cục thực tế của repository, không chuyển bố cục trang theo ảnh mẫu.
- ZIP dùng Be Vietnam Pro cho display và logo minh họa; áp dụng ưu tiên Inter và logo Figma chính thức theo yêu cầu mới của người dùng.
- Inter tải từ Google Fonts: `https://github.com/google/fonts/tree/main/ofl/inter`, trục `wght` 100–900, `opsz` 14–32. Đã kiểm tra glyph tiếng Việt; font kèm SIL Open Font License 1.1.

## D. Giao diện trước và sau

| Thành phần | Trạng thái Claude để lại | Sau khi Codex sửa |
|---|---|---|
| Sidebar | Đã thu 256px, Deep Teal, menu/active hợp lý | Giữ cấu trúc; nhãn nhóm rõ hơn, mobile có aria và logo thật |
| Header | Trắng, 72px; title có thể co trên màn hình nhỏ | Giữ nội dung/trạng thái API; bổ sung co title và xử lý 320px |
| Logo | Ô chữ SC placeholder | PNG chính thức node 170:917, đúng tỷ lệ; favicon cùng asset |
| Font | Inter tải ngoài, display Be Vietnam Pro; body 16px/24px | Inter cục bộ, tiếng Việt đầy đủ, body 14px/22px |
| Nền/surface | Nền mới đúng tông, banner còn gradient cũ | Nền trung tính, surface trắng, border/radius/shadow đồng nhất |
| Bảng/menu | Viền cyan, nhãn nặng; nguy cơ tràn/cắt popup | Nhãn gọn, border nhẹ, cuộn nội bộ, menu thoát khung cuộn |
| Nút mobile | Hủy có thể bị đẩy khỏi card/viewport | Co trong card, giữ nguyên thứ tự nút |

Ảnh trước: `artifacts/ui-shell/before/home-1440x900.png`, `upload-1440x900.png`, `results-1440x900.png`.

Ảnh và số đo sau: `artifacts/ui-shell/after-actual/`, `after-fixture/`, `after/`, `final/`. Ảnh trong `after-fixture`, `final` và ảnh gắn `fixture` dùng dữ liệu tổng hợp; không nhầm với dữ liệu backend thật.

## E. Bảo toàn chức năng

- Không sửa API, backend, database, service gọi API, model, helper nghiệp vụ hoặc test hiện hữu.
- Không đổi thuật toán chấm, trạng thái xử lý, dữ liệu nghiệp vụ hay văn bản nghiệp vụ.
- Không đổi route, danh sách menu, component hiện hữu, bảng/cột, form, thứ tự section hoặc nút.
- Không sửa markup trang nghiệp vụ; các thay đổi trang chỉ ở CSS.
- Route thực tế: `/`, `/upload`, `/results`, `/results/evaluations/{Id:int}`, `/prompts`, Error/NotFound. Job nằm trong trang chủ/chọn việc; CV nằm trong upload và chi tiết kết quả. Không tạo trang Job/CV/Prompt mới ngoài phạm vi.
- Fixture kiểm tra chỉ chạy loopback, GET với dữ liệu tổng hợp; mọi yêu cầu ghi bị từ chối 405. Không kết nối database thật, không gọi Gemini và không ghi hồ sơ thật.

## F. Danh sách file Git

### 4 file Claude đã sửa, Codex kế thừa và hoàn thiện — unstaged

1. `src/SmartCv.Web/Components/Layout/MainLayout.razor.css`
2. `src/SmartCv.Web/Components/Layout/NavMenu.razor`
3. `src/SmartCv.Web/Components/Layout/NavMenu.razor.css`
4. `src/SmartCv.Web/wwwroot/app.css`

Không có file nào trong 4 file này giữ nguyên toàn bộ byte sau Codex; các phần tốt nêu ở A vẫn được kế thừa.

### 6 file hiện hữu được Codex sửa thêm — unstaged

5. `src/SmartCv.Web/Components/App.razor`
6. `src/SmartCv.Web/Components/Pages/Results.razor.css`
7. `src/SmartCv.Web/Components/Pages/Upload.razor.css`
8. `src/SmartCv.Web/Components/Results/RankingTable.razor.css`
9. `src/SmartCv.Web/Components/Upload/CvDropzone.razor.css`
10. `src/SmartCv.Web/Components/Upload/CvFileTable.razor.css`

### 5 file mới của Codex — untracked

11. `src/SmartCv.Web/wwwroot/fonts/inter/Inter-Variable.ttf`
12. `src/SmartCv.Web/wwwroot/fonts/inter/OFL.txt`
13. `src/SmartCv.Web/wwwroot/images/brand/scapp-logo.png`
14. `src/SmartCv.Web/wwwroot/js/row-menus.js`
15. `docs/ui-shell-refinement-handover.md`

Staged: **0**. File thuộc nhiệm vụ khác bị sửa/xóa: **0**. Các script, log, TRX, ảnh, fixture và bản sao tiếp quản nằm trong `artifacts/` được Git ignore.

Không add, commit, push, PR, merge, đổi remote hay xóa nhánh. Không stash/reset/restore/clean/rebase/cherry-pick.

## G. Kiểm thử

| Kiểm tra | Lệnh / bằng chứng | Kết quả |
|---|---|---|
| Build nền và build sau sửa | `dotnet build SmartCv.slnx --no-restore` | Exit 0; 0 warning, 0 error |
| Build cuối | `artifacts/ui-shell/build-final.log` | Exit 0; 0 warning, 0 error |
| Full test trong sandbox | `dotnet test SmartCv.slnx --no-build --no-restore --logger "trx;LogFileName=ui-shell-regression.trx" --results-directory artifacts/ui-shell/test-results` | Exit 1; 990 passed, 109 failed, 31 skipped. Lỗi môi trường quyền Windows Event Log/Data Protection; không tính là thành công. |
| Full test chạy lại dưới tài khoản dự án | Cùng lệnh, tên TRX `ui-shell-regression-user.trx`; log `test-user.log` | Exit 0; **1.099 passed, 0 failed, 31 skipped**, tổng 1.130 |
| SQL Server integration | 31 test có điều kiện môi trường | Không chạy vì chưa cấu hình các database kiểm thử riêng; không báo đã pass |
| Hồi quy UI sau vòng hoàn thiện | `dotnet test SmartCv.slnx --no-build --no-restore --filter "FullyQualifiedName~UiTests"` | Exit 0; **161 passed, 0 failed, 0 skipped**; `test-final-ui.log` và `ui-shell-final-ui.trx` |
| Git whitespace | `git diff --check` | Exit 0; chỉ cảnh báo LF/CRLF theo cấu hình repo nếu có |

Đã chạy Chrome headless/CDP trên giao diện Blazor thật, chụp ảnh và lấy DOM/computed style:

- 5 route chính × 7 viewport: **1920×1080, 1440×900, 1366×768, 1280×720, 768×1024, 390×844, 320×720**.
- Matrix 35 lượt cấu hình API thật và 35 lượt fixture: không tràn ngang document, không lỗi JavaScript chưa xử lý, không lỗi tải asset phía trình duyệt. Một số lượt fixture ban đầu chỉ hiện trạng thái lỗi API, vì vậy matrix này không tự chứng minh bảng dữ liệu đã được kiểm tra.
- Đo riêng phát hiện nút Hủy bị cắt dù document không báo overflow; sau sửa cả hai nút nằm trong card/viewport ở 320/390/768px. Số đo và ảnh cuối nằm trong `artifacts/ui-shell/final/`.
- Menu 1/2/3 file ở 1440/1280/390: nằm trong viewport và `elementFromPoint` xác nhận không bị cắt; Escape đóng, xóa file giảm số dòng đúng. Chỉ dùng `.txt` sai định dạng để kiểm tra local, không gửi hồ sơ lên backend.
- Menu mobile mở/đóng và aria; điều hướng active; hai tab upload; chọn job: đã kiểm tra tương tác.
- Dialog reconnect được mở riêng ở 320/390px: nằm trong viewport, không thay đổi logic reconnect.
- Sau các chỉnh sửa chính đã thử lại menu 1/2/3 file ở 390px: hiển thị đủ, Escape/xóa local đúng; logo/favicon tải thành công, Inter chỉ tải một lần và weight 650 có dấu tiếng Việt hoạt động.
- Bảng có dữ liệu tổng hợp được kiểm tra riêng qua `/results?runId=3&jobId=1`: 5 dòng đầy đủ; đổi job còn 2 dòng, tìm Linh còn 1 dòng; trang chi tiết/JSON/kỹ năng đọc được. Ảnh gồm đầu trang, cuối trang và toàn trang ở 1440/1280/768/390/320px.
- Sửa thêm thanh cuộn dọc dư 1px của wrapper bảng; lần kiểm tra cuối ở 1440/1280/768/390px xác nhận đủ 5 dòng và 7 cột, cuộn ngang đến được cột cuối ở tablet, không còn thanh cuộn dọc nội bộ. Bằng chứng: `final/table-geometry.json`, `final/ranking-*-scrolled-right.png`.
- Đã dừng các Web/fixture và Chrome CDP tạm do phiên kiểm tra tạo; giữ nguyên ứng dụng/trình duyệt không thuộc phiên kiểm tra và bảo toàn artifacts.

### Giới hạn

API thật `https://localhost:7269` có lúc trả 200 và có lúc từ chối kết nối. Không thay đổi cấu hình/source/backend để che lỗi, không tuyên bố kiểm thử upload–chấm điểm–database–Gemini end-to-end thành công. Kiểm tra dữ liệu tổng hợp được tách biệt và đánh dấu rõ.

Trang Prompts vẫn là placeholder có sẵn; không thể kiểm tra editor/so sánh prompt chưa được triển khai. Node thiết kế Figma 407:2313 chưa truy cập được do giới hạn MCP.

## H. Kết luận bàn giao

**BLOCKED — UI SHELL REFINEMENT**

Phần UI đã được kế thừa, hoàn thiện và build thành công. Chưa xác nhận hoàn tất toàn bộ tiêu chí bàn giao vì node Figma chính bị giới hạn truy cập, API thật không ổn định và 31 test SQL Server tích hợp chưa chạy. Cần kiểm tra lại các phần này khi nguồn tham khảo và môi trường phụ thuộc sẵn sàng. Không cần phục hồi hay di chuyển thay đổi Git; toàn bộ thay đổi vẫn ở working tree của nhánh UI để người dùng review.
