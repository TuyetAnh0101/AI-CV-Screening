# Báo cáo cải thiện Landing Page SCApp

## 1. Thông tin chung

| Mục | Nội dung |
|---|---|
| Repository | `TuyetAnh0101/AI-CV-Screening` |
| Nhánh | `feature/landing-page-refinement` (tạo từ `origin/main`, chưa commit, chưa push) |
| BASE_SHA | `66b11039013908f2fb5ef654abef210b36496506` |
| Mục tiêu | Thiết kế lại Landing Page `/`: gọn, đúng nghiệp vụ, đồng bộ màu với ứng dụng, tương tác nhẹ, responsive |
| Phạm vi | Giao diện Landing và thay đổi tối thiểu ở `PublicLayout`, `PublicFooter`, một chuỗi CTA |
| Kết luận | **PASS cho các hạng mục đã kiểm**, còn hạn chế ở mục 8 |

## 2. Kết quả rà soát trước khi sửa

- Landing gồm 7 khối: Hero, Tính năng, Quy trình, Lợi ích, Bảo vệ dữ liệu, FAQ, CTA cuối trang.
- Logic CTA: `AuthenticationStateProvider` → `AccessRules` → `PublicSettingsClient` (GET công khai `/api/auth/registration-settings`, không Bearer, cache theo circuit, lỗi thì đóng đăng ký) → `PublicCta.Resolve`.
- Vấn đề đã thấy:
  - Hero hiển thị ghi chú của lập trình viên ("Thay minh họa bằng ảnh chụp… trước khi nghiệm thu B52").
  - Nút đăng ký ghi "Bắt đầu miễn phí" trong khi chưa công bố chính sách giá.
  - Quy trình đặt "Đăng ký" làm bước 1 và thiếu bước nguồn/đồng ý, kiểm tra chữ, chủ động bắt đầu phân tích.
  - Các thẻ giống hệt nhau, không có phân cấp; không có điều hướng đến mục bảo vệ dữ liệu.
  - `Landing.razor.css` chứa style không còn phần tử nào dùng.
- Nghiệp vụ đã đối chiếu từ source: luồng tải CV (`Upload.razor`), định dạng và giới hạn tệp (`CvUpload`), OCR và cảnh báo đọc sai, kết quả dự phòng `KeywordOnly`, giai đoạn hồ sơ, hạn mức workspace cá nhân, đăng ký và đăng nhập Google, quyền ẩn danh hóa, xuất báo cáo.
- Giữ nguyên: logic CTA, chuyển hướng URL cũ `?jobId/from/to`, `AccessRules`, `RoleBasedNavigation`, API, dữ liệu.

## 3. Thiết kế trước và sau

| Khu vực | Trước | Sau |
|---|---|---|
| Navigation | 3 liên kết tĩnh | 4 liên kết, đánh dấu mục đang xem, bóng khi cuộn, Escape đóng menu; mobile đặt CTA cùng hàng logo khi chỉ có một nút |
| Hero | Khung xám giữ chỗ, có ghi chú nội bộ | Thông điệp "Sàng lọc thông minh. Tuyển dụng có căn cứ."; minh họa bảng xếp hạng bằng HTML/CSS, ghi rõ dữ liệu mẫu |
| Tính năng | 5 thẻ giống nhau | Bento Grid 7 thẻ, một thẻ chính có sơ đồ đối chiếu |
| Product Showcase | Không có | 5 tab (CV, AI, Xếp hạng, Theo dõi, Báo cáo), điều khiển bằng chuột và bàn phím |
| Quy trình | 5 thẻ | Timeline 6 bước đúng luồng thật, tô dần khi cuộn, chuyển dọc dưới 1024px |
| Vai trò AI | Không có mục riêng | Khối nền deep teal: AI hỗ trợ, con người quyết định, điểm không phải xác suất trúng tuyển |
| Đối tượng | Không có | HR nội bộ, Personal Owner, Quản trị hệ thống |
| Lợi ích | 3 ý | 5 ý, bố cục biên tập, không nêu số liệu |
| Bảo vệ dữ liệu | 4 thẻ | 7 cơ chế có trong hệ thống, nêu rõ không phải cam kết tuyệt đối |
| FAQ | 8 câu | 12 câu; giới hạn dung lượng lấy từ cấu hình, không viết cứng |
| CTA cuối | Nền nhạt | Khối gradient thương hiệu |
| Footer | 4 liên kết | Thêm "Cách hoạt động", "Câu hỏi" (đều là neo có thật) |

**Màu sắc.** Dùng token `--sc-*` của ứng dụng (Deep Teal, mint) làm chủ đạo, qua hai vòng phản hồi:

1. Bản dựng theo bảng xanh dương/tím trong yêu cầu ban đầu bị đánh giá là lệch các trang còn lại và rối mắt.
2. Bản chỉ dùng teal bị đánh giá là nhạt nhòa, thiếu điểm nhấn.

Bản hiện tại gán nghĩa cố định cho hai màu tương phản: **hổ phách `#f6a62d`** cho hành động (nút chính, tab và mục đang mở, bước quyết định của con người) và **san hô `#c8432f`** cho điều cần chú ý. Nhịp nền: Hero deep teal → trắng → mint → trắng → khối AI deep teal → trắng → ấm → trắng → CTA hổ phách. Màu mức phù hợp trong hình minh họa giống màu trong ứng dụng.

Tương phản đã tính cho các cặp chính: chữ trắng trên san hô 4,9:1; deep teal trên hổ phách 6,4:1; chữ phụ trên nền Hero 9,2:1; chữ phụ trên nền ấm 4,8:1.

**Typography.** Inter có sẵn. H1 46px ở desktop, 30–33px ở mobile; H2 26–33px; nội dung 13–16px.

**JavaScript** (`wwwroot/js/landing-refinement.js`, ES module nạp từ `Landing.razor`): xuất hiện khi cuộn, trạng thái header, mục đang xem, tiến trình timeline, nút về đầu trang, cuộn đến neo khi mở thẳng `/#faq`, chặn cuộn trang bằng phím mũi tên trên tab. Khởi tạo một lần, gỡ listener và observer khi rời trang, không gọi API, không dùng storage hay `innerHTML`. Không tải được module thì nội dung vẫn hiển thị đầy đủ. Đổi tab do Blazor xử lý.

**Accessibility.** Một H1, thứ bậc tiêu đề không nhảy cấp, tab theo mẫu WAI-ARIA, FAQ dùng `details/summary`, focus-visible, giữ skip link, tắt chuyển động khi `prefers-reduced-motion`, vùng chạm từ 40px.

**Hiệu năng.** Không thêm thư viện, font, ảnh hay CDN. Chưa đo Lighthouse hoặc thời gian tải.

## 4. Danh sách file thay đổi

Tiền tố chung: `src/SmartCv.Web/`.

| File | Loại | Nội dung và lý do |
|---|---|---|
| `Components/Pages/Landing.razor` | Sửa | Thứ tự section mới, nạp/gỡ module JS; logic CTA giữ nguyên |
| `Components/Pages/Landing.razor.css` | Sửa | Token màu và style dùng chung của Landing, thay style cũ không dùng |
| `Components/Public/LandingHero.razor(.css)` | Sửa | Hero mới, bỏ tham số `HasScreenshot` và ghi chú giữ chỗ |
| `Components/Public/FeatureGrid.razor(.css)` | Sửa | Bento Grid |
| `Components/Public/HowItWorks.razor(.css)` | Sửa | Timeline 6 bước |
| `Components/Public/BenefitList.razor(.css)` | Sửa | Lợi ích |
| `Components/Public/DataProtectionSection.razor(.css)` | Sửa | Bảo vệ dữ liệu |
| `Components/Public/FaqSection.razor(.css)` | Sửa | FAQ theo nghiệp vụ thật |
| `Components/Public/CtaBand.razor(.css)` | Sửa | CTA cuối trang |
| `Components/Public/LandingProductShowcase.razor(.css)` | Thêm | Showcase theo tab |
| `Components/Public/LandingAiSection.razor(.css)` | Thêm | Vai trò AI |
| `Components/Public/LandingAudienceSection.razor(.css)` | Thêm | Đối tượng sử dụng |
| `Components/Public/LandingIcon.razor` | Thêm | Icon SVG |
| `wwwroot/js/landing-refinement.js` | Thêm | Tương tác giao diện |
| `Components/Public/PublicFooter.razor` | Sửa | Thêm 2 liên kết neo (hiện cả trên Privacy/Terms) |
| `Components/Layout/PublicLayout.razor` | Sửa | Liên kết "Bảo vệ dữ liệu" (hiện cả trên Privacy/Terms), Escape đóng menu |
| `Components/Layout/PublicLayout.razor.css` | Sửa | Chỉ thêm quy tắc có điều kiện `:has(.lp-page)` |
| `Services/PublicSettingsClient.cs` | Sửa | Một chuỗi: "Bắt đầu miễn phí" → "Đăng ký tài khoản" |
| `docs/landing-page-refinement-report.md` | Thêm | Báo cáo này |

Không xóa file nào có trên `main`.

## 5. Screenshot

Lưu tại `artifacts/landing-refinement/` (thư mục được gitignore, chỉ có trên máy thực hiện):

- `before/`: trang cũ ở 1920, 1366, 390px.
- `after/`: toàn trang 7 kích thước, từng section ở 1366 và 390px, menu mobile, CTA khi đăng ký bật/tắt, smoke Login/Register/Forgot password/Privacy/Terms.
- `signed-in/`: Landing và trang đích CTA của Admin, HR, Owner, tài khoản bắt buộc đổi mật khẩu.

Tất cả là ảnh chụp trình duyệt thật với dữ liệu tổng hợp. Hình trong Hero và Showcase là **hình minh họa**, không phải ảnh chụp Dashboard.

## 6. Kết quả kiểm thử

| Hạng mục | Baseline | Sau chỉnh sửa | Kết luận |
|---|---|---|---|
| Debug Build (bản sao sạch) | 0 lỗi, 12 warning | 0 lỗi, 12 warning | PASS |
| Release Build | 0 lỗi, 12 warning | 0 lỗi, 12 warning | PASS |
| Focused test (B52, B51, B28, B53, B27LoginUi, RoleNavigation, GoogleOnly) | 193/193 | 193/193 | PASS |
| Browser, khách (script tạm, Chrome) | Không chạy | 101/101 | PASS |
| Browser, đã đăng nhập (test tạm, API InMemory) | Không chạy | 1/1 | PASS |
| Full regression (Release) | Không chạy | FULL_RESULT | FULL_VERDICT |

Debug build trong repository bị chặn vì `SmartCv.Api` và `SmartCv.Web` đang chạy từ thư mục Debug, nên được build trên bản sao mã nguồn. Script và test tạm đã được xóa khỏi repository sau khi chạy.

### Nghiệp vụ

| Mã | Cách kiểm | Kết quả |
|---|---|---|
| LAND-01…03 | Trình duyệt, API stub bật/tắt đăng ký | PASS |
| LAND-04 | Test `Settings_is_public_fail_closed_and_cached` | PASS |
| LAND-05…08 | Trình duyệt đăng nhập thật trên API InMemory | PASS |
| LAND-09 | Chỉ test đơn vị/render (B28), chưa dựng trên trình duyệt | PASS (test) |
| LAND-10 | Trình duyệt và test B51 | PASS |
| LAND-11 | Bấm liên kết Privacy/Terms | PASS |
| LAND-12 | Log API stub chỉ có `registration-settings`; test B51 | PASS |

### Giao diện

| Mã | Kết quả |
|---|---|
| UI-01…04 (1920, 1440, 1366, 1024, 768, 390, 360) | PASS, không cuộn ngang |
| UI-05…08 (menu, neo, showcase, FAQ) | PASS |
| UI-09, UI-10 (reduced motion, bàn phím) | PASS cho các thao tác đã tự động hóa |
| UI-11 | Không dùng ảnh trong Hero |
| UI-12, UI-15 (console, 404) | PASS trên Landing, Privacy, Terms; các trang đăng nhập/đăng ký bỏ qua 404 do API stub không có endpoint |
| UI-13, UI-14 (tải lại, rời và quay lại 3 lần) | PASS |

Smoke Dashboard HR, Personal Owner, Admin: mở được từ CTA, không có lỗi console.

## 7. Xác nhận phạm vi

| Câu hỏi | Trả lời |
|---|---|
| Sửa Dashboard HR / Personal Owner / Admin? | Không |
| Sửa API? | Không |
| Sửa database hoặc migration? | Không |
| Thay đổi Google OAuth, JWT, SMTP? | Không |
| Ảnh hưởng A62? | Không sửa file nào liên quan |
| Sửa thuật toán chấm điểm? | Không |
| Thay đổi phân quyền? | Không |
| Ảnh hưởng trang ngoài phạm vi? | Privacy và Terms có thêm 1 liên kết trên header và 2 liên kết ở footer |
| Dùng dữ liệu ứng viên thật? | Không |

## 8. Hạn chế và ghi chú

- Hero dùng hình minh họa, chưa có ảnh chụp sản phẩm thật.
- Tương phản chỉ tính cho một số cặp màu chính, chưa quét toàn trang bằng công cụ; chưa thử trình đọc màn hình, chưa thử Firefox/Safari và thiết bị thật. Không tuyên bố đạt WCAG.
- Chưa đo hiệu năng.
- Trạng thái đăng ký được kiểm bằng API stub, chưa kiểm với cấu hình triển khai thật.
- Trên mobile trang dài (khoảng 9.400px ở 390px, trước đây 6.500px) vì có thêm các mục mới. Nếu muốn ngắn hơn, ứng viên để bỏ là "Đối tượng sử dụng" hoặc "Lợi ích".
- Style dựa vào CSS `:has()`, cần trình duyệt đời mới.
- Ngoài phạm vi, chưa sửa: header của Privacy/Terms vẫn ghi "Đăng ký công khai sắp mở"; `PublicCta.SignIn.Note` còn chữ "sắp mở" (test B52 đang kiểm chuỗi này, Landing không hiển thị); hai trang pháp lý vẫn là bản dự thảo; warning CS9113 ở `Helpers/VerificationFlow.cs`.

## 9. Kiểm thử thủ công

1. Dừng API/Web đang chạy để lấy bản build mới, rồi mở hai terminal:
   `dotnet run --project src/SmartCv.Api --launch-profile https` và
   `dotnet run --project src/SmartCv.Web --launch-profile https`.
2. Mở `https://localhost:7258/` khi chưa đăng nhập.
3. Bấm lần lượt 4 mục menu, xem mục đang xem được đánh dấu.
4. Đổi 5 tab showcase bằng chuột và phím mũi tên, Home, End.
5. Cuộn xem hiệu ứng xuất hiện, timeline và nút về đầu trang.
6. Mở/đóng FAQ bằng Enter.
7. Đổi `Registration:Enabled` trong cấu hình API (chỉ trên máy thử), tải lại và so nút CTA.
8. Đăng nhập Admin, HR, Owner rồi mở `/`.
9. Thu cửa sổ về 390px, thử menu (cả phím Escape).
10. Mở Privacy, Terms, Login, Register và ba Dashboard; xem console không có lỗi mới.
