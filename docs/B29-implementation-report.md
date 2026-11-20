# B29 — Quản lý tài khoản và đổi mật khẩu (SCApp)

Ngày thực hiện: 20/11/2026 · Kết luận: **CONDITIONAL** (xem mục J)

---

## A. Tổng quan

**Đã hoàn thành**
- Trang **/change-password** với hai luồng: **bắt buộc** (tài khoản dùng mật khẩu tạm: không có menu, không gọi API nghiệp vụ, không bỏ qua được, chỉ có Đăng xuất) và **tự nguyện** (nút chìa khóa trên thanh trên cùng / khối tài khoản trên di động, có "Hủy và quay lại").
- Trang **/users** (chỉ Admin): tìm kiếm có debounce, lọc vai trò / trạng thái, phân trang ở máy chủ (đọc `X-Total-Count`), bảng trên máy tính, thẻ trên màn hình hẹp; tạo, sửa, khóa, mở khóa tài khoản và đặt lại mật khẩu cho HR. Mật khẩu tạm chỉ hiện một lần, có nút sao chép.
- Mục menu **"Tài khoản"** chỉ Admin thấy. HR gõ `/users` thì nhận trang 403 (B28) và không có request nào được gửi.
- 74 test B29 mới (phần lớn chạy với **API A30 thật** trong TestServer). Kiểm thử bằng Chrome thật: **52/52 PASS**.
- Sửa **2 lỗi của B28** phát hiện khi kiểm thử B29 (mục D).

**Chưa hoàn thành / chưa kiểm chứng:** chưa chạy với SQL Server và tài khoản thật (theo yêu cầu, không dùng tài khoản thật cho thao tác ghi). Xem mục G.

---

## B. Git

| Mục | Giá trị |
|---|---|
| Repository | `C:\TTCN\project\AI-CV-Screening` (repository chính, **không** tạo worktree, clone hay thư mục project mới) |
| Nhánh | `feature/B29-account-management-ui` |
| SHA nền | `0c91135b03aba943891c1e8488a418e5161eeb03` (`origin/main`, gồm A27–A30, B27, B28 #88) |
| Trạng thái trước khi tạo nhánh | `main` sạch, không có merge / rebase / cherry-pick dở dang |
| Upstream | đã bỏ (`git push` không thể trỏ nhầm `main`) |
| add / commit / push / merge / rebase / stash / reset | **Không thực hiện** |

**Về nhánh B29 đã tồn tại.** `feature/B29-account-management-ui` đã có sẵn (tạo lúc 09:41 trong worktree B28, không do nhiệm vụ này tạo) và trỏ vào `2110250`, tức nền cũ chưa có B28. Trước khi dùng, đã kiểm tra: nhánh có **0 commit riêng**, là tổ tiên của `origin/main`, và không được checkout ở worktree nào. Vì vậy đã đặt lại nhánh về `origin/main` bằng `git switch -C feature/B29-account-management-ui origin/main`. Thao tác này không mất gì, và B29 có đủ nền B28 + A30 cần thiết.

**File thay đổi:** 21 sửa, 21 mới (gồm báo cáo này), chỉ nằm trong `src/SmartCv.Web/`, `tests/SmartCv.Tests/` và `docs/B29-implementation-report.md`. Không xóa, không đổi tên file. Danh sách chính xác ở `C:\TTCN\project\SCApp-B29-Handoff\commit-manifest.tsv`.

---

## C. Tài liệu

- **ZIP `A27_B43_SmartCv.zip`:** đã đọc phần hướng dẫn chung, A30 (API, quy tắc, mật khẩu tạm), B27, B28, **B29** (giao diện, file, các bước, tiêu chí thử) và "Lỗi thường gặp". Đã đọc mã mẫu B29: `Users.razor`, `ChangePassword.razor`, `UserApiClient.cs`, `UserText.cs`, `AuthGate.razor`, `NavMenu.razor`, `UserMenu.razor`, `Login.razor`, `AuthApiClient.cs`, `ServiceCollectionExtensions.cs`, `UserTextTests.cs`.
- **Ảnh / mockup:** ZIP **không có** file ảnh, SVG hay PDF nào. Thiết kế bám theo giao diện SCApp hiện có (Deep Teal, B27/B28).
- **Đối chiếu với API thật:** A30 trong `main` đầy đủ hơn mô tả của ZIP, nên **lấy mã nguồn API làm chuẩn**: `ETag`/`If-Match` (`Version`), header `X-Total-Count`/`X-Page`/`X-Page-Size`, các mã lỗi `email_conflict`, `account_conflict`, `admin_safety`, `admin_password_reset_forbidden`, `credential_rate_limited`, `account_unavailable`, và giới hạn 10 thao tác/phút cho mỗi tài khoản.

**Những điểm làm khác mẫu ZIP (có chủ đích)**

| Mẫu ZIP | B29 thực tế | Lý do |
|---|---|---|
| Tải toàn bộ danh sách, lọc ở trình duyệt | Lọc, tìm và phân trang ở máy chủ, tổng số lấy từ `X-Total-Count`, không đoán | Yêu cầu nhiệm vụ; API đã hỗ trợ |
| Đếm "Admin cuối cùng" trên danh sách đang xem | Gọi riêng `GET /api/users?role=Admin&isActive=true&pageSize=1` để có số Admin đang hoạt động **trên toàn hệ thống**. Chưa có số này thì khóa nút (fail-closed) | Danh sách đã phân trang / lọc thì đếm sai |
| Không gửi `If-Match` | Mọi thao tác ghi gửi `If-Match` theo Version đã xem. Gặp 409 thì **không ghi đè**, chỉ cho "Tải thông tin mới nhất" rồi làm lại | Hai Admin sửa cùng lúc |
| `ChangePassword` dùng `AuthLayout`, nằm trong `Pages` | Nằm ở `Components/Account`, dùng `MainLayout` | Nếu để trong `Pages`, `[Authorize]` mặc định (kết hợp **AND** với chính sách riêng) sẽ chặn chính tài khoản mật khẩu tạm và tạo vòng lặp chuyển trang. `MainLayout` + `AuthGate` tự hiện khung trống ở luồng bắt buộc và khung đầy đủ ở luồng tự nguyện |
| `AuthGate` tự chuyển sang trang đổi mật khẩu | Nhánh `PasswordChangeRequired` trong `NotAuthorizedContent` (B28) chuyển tới `/change-password?returnUrl=…` | Một nơi quyết định duy nhất (AuthorizeRouteView), không trùng trách nhiệm với AuthGate |
| Lỗi mạng khi đổi mật khẩu: báo lỗi chung | Khóa form, chỉ **đọc** `GET /api/auth/me` bằng token cũ để biết đã đổi hay chưa, **không gửi lại** | Thao tác thay đổi thông tin đăng nhập không được tự gửi lại |
| Tạo Admin không cần xác nhận | Bắt buộc đánh dấu xác nhận khi tạo hoặc nâng quyền Admin | Quyền cao |

---

## D. Nghiệp vụ

**Phân quyền**
- `/users`: `[Authorize(Policy = SmartCv.Admin)]`. HR nhận 403, chưa đăng nhập về `/login`, tài khoản mật khẩu tạm về `/change-password`. Trang không được tạo khi bị chặn nên không có request nào.
- `/change-password`: chính sách `SignedIn` (đã xác minh, Admin hoặc HR, **kể cả** đang dùng mật khẩu tạm). Chưa đăng nhập về `/login`; chưa xác minh được phiên thì hiện "Thử lại" (B28).
- Quyền mới `AppPermission.ManageUsers` (chỉ Admin), mục menu `AdminOnly`.

**Luồng bắt buộc đổi mật khẩu (MustChangePassword)**
1. Sau khi đăng nhập, `Login` đưa thẳng tới `/change-password?returnUrl=<trang muốn mở>`.
2. Mọi trang nghiệp vụ (kể cả `/users`) đều chuyển về `/change-password`; trang nghiệp vụ không được tạo nên không gọi API CV/Job/Ranking. Không có vòng lặp vì trang đổi mật khẩu dùng chính sách `SignedIn`.
3. Đổi thành công: token mới được nạp vào `AuthSession` **trước** mọi request khác. Request cũ trả 401 về sau **không** xóa phiên mới (B27 so sánh token; có test).
4. Khung trang chuyển từ trống sang đầy đủ, trang hiện "Đã đổi mật khẩu" với nút "Tiếp tục" về trang muốn mở ban đầu. Thông báo một lần (`AccountNotice`) giữ được trạng thái này khi trang được tạo lại.

**Đổi mật khẩu**
- Kiểm tra trước khi gửi bằng `PasswordRules` của Core (8–128 ký tự, có chữ và số, không có khoảng trắng đầu/cuối, không trùng email), mật khẩu mới phải khác mật khẩu hiện tại, ô nhập lại phải khớp. Danh sách 5 luật tự đánh dấu khi gõ. **Không trim** mật khẩu.
- Xử lý lỗi:

| Trường hợp | Cách xử lý |
|---|---|
| 400 `currentPassword` | Lỗi dưới ô, xóa ô, đưa con trỏ về ô đó |
| 400 `newPassword` | Lỗi dưới ô mật khẩu mới |
| 401 | Kết thúc phiên, về `/login?expired=1` |
| 403 | Báo không có quyền |
| 409 | Chưa ghi, cho thử lại |
| 429 | Đếm ngược theo `Retry-After` |
| 503 / 5xx / mất mạng / quá giờ / phản hồi hỏng | Không biết đã đổi chưa: khóa form, kiểm tra bằng `/api/auth/me`. Token cũ còn dùng được thì báo "CHƯA được thay đổi" và mở lại form; bị 401 thì báo "Có thể đã đổi, hãy đăng nhập lại"; vẫn mất mạng thì hiện "Kiểm tra lại" và "Đăng nhập lại" |

- Không gửi trùng, không tự gửi lại, xóa mật khẩu khỏi bộ nhớ sau mỗi lần API trả lời và khi rời trang. Không lưu vào localStorage hay sessionStorage.

**Quản lý tài khoản**
- **Tạo:** họ tên, email, vai trò (thẻ chọn HR / Quản trị viên). Mặc định để hệ thống tự tạo mật khẩu tạm; nếu tắt thì nhập mật khẩu ban đầu theo đúng luật. Kiểm tra bằng `UserRules.ValidateCreate` của Core. Email trùng báo lỗi dưới ô. Kết quả không rõ thì tải lại danh sách và nhắc tìm theo email trước khi tạo lại; nếu tài khoản đã được tạo thì dùng "Đặt lại mật khẩu".
- **Mật khẩu tạm:** hiện một lần, chữ đơn cách, có nút "Sao chép" (dùng clipboard; trình duyệt chặn thì hướng dẫn sao chép tay), có lời nhắc bảo mật. Đóng hộp thoại là xóa khỏi bộ nhớ.
- **Sửa:** chỉ họ tên và vai trò; email không đổi được. Không có thay đổi thì không gửi. Không cho tự đổi vai trò của mình hoặc hạ quyền Admin cuối cùng: thẻ vai trò bị khóa và ghi lý do. Đổi vai trò thì báo người đó phải đăng nhập lại.
- **Khóa / mở khóa:** hộp thoại nêu tài khoản và hậu quả. Mục "Khóa" bị làm mờ và ghi lý do với chính mình và Admin cuối cùng. Mở khóa **không** đổi mật khẩu hay cờ đổi mật khẩu (đã kiểm chứng với API thật).
- **Đặt lại mật khẩu:** chỉ hiện ở dòng **HR**. Admin không có mục này; nếu vẫn gọi API thì A30 trả 409 `admin_password_reset_forbidden`, giao diện báo rõ. Hộp thoại xác nhận nêu rằng phiên của HR sẽ kết thúc và mật khẩu tạm chỉ hiện một lần.
- Một lần bấm chỉ gửi một request (khóa nút khi đang gửi; đang gửi thì không đóng được hộp thoại). Không tự gửi lại.

**Lỗi B28 đã sửa (phát hiện khi kiểm thử trình duyệt B29)**
1. **Không đăng xuất được sau khi đăng nhập từ link sâu.** Mở trang nội bộ khi chưa đăng nhập, rồi đăng nhập trong cùng tab: `AuthNavigator.IsLeaving` bị giữ `true` mãi, nên nút Đăng xuất và Đổi mật khẩu bị vô hiệu. Đã sửa: cờ chỉ bật khi tải lại trang; lần chuyển trong tab thì chống trùng bằng cách kiểm tra đã ở `/login` chưa. Có test hồi quy.
2. Thêm nút chìa khóa làm thanh trên cùng tràn ngang ở 768–991px. Đã sửa: ở khoảng này nút Đăng xuất chỉ còn biểu tượng (vẫn có `aria-label`).

---

## E. Giao diện

- **Trang Tài khoản:** đầu trang gồm nhãn nhỏ "Quản trị hệ thống", tiêu đề 24–28px (token `--sc-text-page`), mô tả 13,5px và nút "Tạo tài khoản" cao 40px. Thanh lọc gồm ô tìm 40px, chọn vai trò, nhóm nút trạng thái và nút tải lại. Không dùng thẻ thống kê lớn.
- **Bảng:** hàng khoảng 60px, chữ 13,5px. Cột: avatar chữ viết tắt + họ tên + email (cắt bằng `…` và có tooltip; tài khoản của mình có nhãn "Bạn"), vai trò (huy hiệu teal đậm cho Admin, xanh nhạt cho HR), trạng thái (chấm xanh mint / xám), mật khẩu ("Chờ đổi mật khẩu" màu vàng), đăng nhập gần nhất (giờ Việt Nam), nút "Sửa" và menu "⋯". Cột "Đăng nhập gần nhất" ẩn dưới 1100px. Dưới 992px mỗi dòng thành một thẻ; không có cuộn ngang ở mọi độ rộng đã thử.
- **Hộp thoại:** dùng lại `ModalShell` (thẻ `<dialog>` gốc: giữ focus bên trong, Escape đóng, trả focus về nút đã mở). Rộng 560px, đầu và chân hộp thoại cố định, cuộn được trên màn hình nhỏ. Hành động nguy hiểm dùng nút đỏ, cảnh báo nền vàng.
- **Đổi mật khẩu:** thẻ 460px giống thẻ đăng nhập (ô 44px, chữ 15px), có danh sách luật mật khẩu và lỗi dưới từng ô.
- **Accessibility:** `aria-invalid`/`aria-describedby` cho từng ô, `role=radiogroup` cho chọn vai trò, `role=menu` cho menu dòng, `role=status`/`alert` cho thông báo, nút nhãn rõ ràng (`aria-label="Sửa {tên}"`), `autocomplete="current-password"` / `"new-password"` và ô `username` ẩn cho trình quản lý mật khẩu.
- **Ảnh kiểm thử:** `C:\TTCN\project\SCApp-B29-Handoff\screenshots\` (19 ảnh ở 360, 390, 768, 1024, 1440 và 1920px).

---

## F. API

| Endpoint (A30) | Dùng ở | Ghi chú |
|---|---|---|
| `POST /api/auth/change-password` | `AuthApiClient.ChangePasswordAsync` | Gửi Bearer là token của phiên, không bao giờ tự gửi lại |
| `GET /api/auth/me` | Kiểm tra kết quả không rõ | Chỉ đọc |
| `GET /api/users?search&role&isActive&page&pageSize` | `UserApiClient.ListAsync` | Đọc `X-Total-Count`, `X-Page`, `X-Page-Size`; log chỉ ghi nhãn đường dẫn, không ghi email tìm kiếm |
| `POST /api/users` | Tạo tài khoản | 201 kèm `temporaryPassword` (nếu hệ thống tự tạo) |
| `PUT /api/users/{id}` | Sửa | `If-Match` + `expectedVersion` |
| `POST /api/users/{id}/lock`, `/unlock` | Khóa / mở khóa | `If-Match` |
| `POST /api/users/{id}/reset-password` | Đặt lại mật khẩu (HR) | `If-Match` |

Mọi câu lỗi hiển thị đều do giao diện tự viết theo **mã trạng thái và mã lỗi**, không dùng chữ của exception từ máy chủ. Riêng thông báo kiểm tra dữ liệu theo từng ô (400) dùng câu đã được A30 viết sẵn cho người dùng.

`ApiClientBase` có thêm `SendRequestAsync` và `JsonRequest` (protected) để service con thêm header (`If-Match`, Bearer chỉ định) mà vẫn dùng chung cách đọc lỗi và xử lý 401/403 của B27.

**Phụ thuộc:** A30 và B28 đều đã có trong `main`. **Không sửa API.** Không phát hiện lỗi backend nào ảnh hưởng B29.

---

## G. Kiểm thử

| Bước | Kết quả |
|---|---|
| .NET SDK | 10.0.403 |
| Baseline toàn bộ | 3237 PASS · 0 FAIL · 131 SKIP (3368). Lưu ý: lần chạy này trùng lúc đã bắt đầu sửa code. Nền sạch tương ứng là cây mã B28 giống hệt từng byte, đã chạy trước đó: 3233 PASS · 0 FAIL · 135 SKIP |
| Build Release | 0 lỗi, 0 cảnh báo |
| Test B29 mới (74) | **74 PASS** |
| B27 / B28 / test giao diện liên quan | 238 PASS |
| **Toàn bộ sau B29** | xem dòng ghi kết quả ở cuối mục này |

**Test B29:** `B29ChangePasswordTests`, `B29UsersTests`, `B29RouteUiTests`. Chúng gồm:
- Luật mật khẩu, không trim, chống gửi trùng.
- Từng mã lỗi của A30 (401/403/409/429/503/500, mất mạng, quá giờ, dữ liệu hỏng).
- Kết quả không rõ chỉ được giải quyết bằng `/me`; token cũ trả 401 muộn không xóa phiên mới.
- Đổi mật khẩu thật với API A28/A30: mật khẩu tạm hết tác dụng, token cũ bị từ chối, API nghiệp vụ mở bằng token mới; API nghiệp vụ trả 403 `password_change_required` trước khi đổi.
- Danh sách thật với API A30: tổng số, phân trang, lọc, tìm; kết quả cũ không ghi đè kết quả mới; debounce.
- Tạo HR với mật khẩu tạm một lần; email trùng; xác nhận Admin; kết quả không rõ không gửi lại; 429.
- Sửa với `If-Match` và 409 (không ghi đè, tải lại rồi làm lại); không tự đổi vai trò; quy tắc Admin cuối cùng.
- Khóa / mở khóa (mở khóa giữ nguyên mật khẩu và cờ); không tự khóa.
- Đặt lại chỉ cho HR và kiểm chứng được mật khẩu tạm; bấm hai lần chỉ một request.
- Mọi thao tác ghi gửi `If-Match`; chuỗi truy vấn an toàn.
- Vẽ thật `Routes.razor`: Admin vào `/users`; HR nhận 403 và không có request; chưa đăng nhập về `/login`; mật khẩu tạm mở `/change-password` không vòng lặp, không menu, không request; luồng tự nguyện có menu.

**Test B27/B28 được sửa theo hành vi mới có chủ đích:** tài khoản mật khẩu tạm nay chuyển tới `/change-password` thay vì thẻ hướng dẫn "liên hệ Admin"; ma trận quyền trang thêm `/users` (Admin) và `/change-password` (SignedIn); `B27FakeAuthApi` có thêm `ChangePasswordAsync`.

**Kiểm thử trình duyệt:** Chrome thật (Playwright .NET, headless), API A28/A29/A30 thật trên Kestrel, database InMemory, 25 tài khoản tổng hợp, môi trường Staging (không nạp User Secrets), không SQL, không tài khoản thật. Kết quả **52/52 PASS, 0 lỗi console**, gồm:
- Menu "Tài khoản" chỉ Admin thấy; tổng 25, phân trang 20/5; tìm, lọc vai trò, lọc "Đã khóa".
- Không tự khóa (mục bị làm mờ, có lý do); không có mục đặt lại mật khẩu cho Admin.
- Tạo HR → mật khẩu tạm 12 ký tự, nút Sao chép đúng nội dung, đóng là mất; email trùng; xác nhận Admin.
- Sửa họ tên; khóa / mở khóa (giữ "Chờ đổi mật khẩu"); đặt lại mật khẩu.
- HR đăng nhập bằng mật khẩu tạm → `/change-password?returnUrl=%2Fcandidates`, không menu; gõ `/jobs` bị đưa về; nhập lại không khớp báo lỗi; đổi thành công → "Tiếp tục" về `/jobs`; menu HR không có "Tài khoản"; `/users` trả 403.
- Đăng nhập bằng mật khẩu mới vào thẳng ứng dụng; tự đổi mật khẩu khi sai mật khẩu hiện tại báo lỗi dưới ô; "Hủy" quay lại trang trước.
- Không cuộn ngang ở 360, 390, 768, 1024, 1440, 1920px; hộp thoại vừa màn hình 360px.

Các lỗi đã phát hiện và sửa trong quá trình kiểm thử trình duyệt:
- Hộp thoại mật khẩu tạm không hiện sau khi tạo tài khoản: trang cha không vẽ lại.
- Lỗi đăng xuất của B28 (mục D).
- Tràn ngang ở 768px: thanh trên cùng, cộng với bảng chưa chuyển sang dạng thẻ.
- Đường kẻ hàng bị lệch: `display:flex` đặt trên `<td>`.
- Nút thao tác đè lên avatar trong dạng thẻ: lỗi độ ưu tiên CSS.

**NOT VERIFIED:**
- API thật trên SQL Server với `SmartCvDb` và tài khoản thật (theo yêu cầu, không thử thao tác ghi với tài khoản thật).
- Sao chép clipboard trên trình duyệt không phải localhost / HTTPS (đã có hướng dẫn sao chép tay khi bị chặn).
- Trình đọc màn hình thật; Firefox và Safari.

Không tuyên bố LIVE TEST PASS.

---

## H. An toàn

- **Không** tạo, sửa, khóa, reset hay xóa tài khoản Admin thật; không chạm `SmartCvDb` (guard của bộ test vẫn bật, không đặt biến môi trường SQL). Mọi thao tác ghi chỉ chạy trên TestServer / harness InMemory với tài khoản tổng hợp.
- **Không** reset database, không chạy migration, không sửa connection string, User Secrets, JWT hay AdminSeeder.
- **Không** sửa `SmartCv.Api`, `SmartCv.Core`, `SmartCv.Infrastructure`.
- Không tắt phân quyền, không thêm `AllowAnonymous`. Trang mới đều có `[Authorize]` (test phản chiếu kiểm tra tất cả trang).
- Mật khẩu tạm và mật khẩu người dùng không ghi log, không lưu vào trình duyệt, xóa khỏi bộ nhớ khi đóng hộp thoại hoặc rời trang. `UserApiClient` tắt logger URI của HttpClientFactory vì từ khóa tìm kiếm có thể là email.
- Không ảnh hưởng nhiệm vụ khác: chỉ sửa trong `SmartCv.Web` và test. Hai thay đổi trên file của B28 (`AuthNavigator`, `NotAuthorizedContent`) là sửa lỗi và tích hợp đã ghi rõ ở trên.
- Các tiến trình thử nghiệm (cổng 5791/5792) do nhiệm vụ này mở đều đã dừng; không động tới API của người dùng ở cổng 7269.

---

## I. Danh sách file

**Mới**

| File | Mục đích |
|---|---|
| `Components/Account/ChangePassword.razor`, `.css` | Trang đổi mật khẩu (hai luồng) |
| `Components/Pages/Users.razor`, `.css` | Trang quản lý tài khoản |
| `Components/Users/UserFormDialog.razor`, `.css` | Hộp thoại tạo / sửa |
| `Components/Users/UserActionDialog.razor`, `.css` | Xác nhận khóa / mở khóa / đặt lại |
| `Components/Users/TemporaryPasswordDialog.razor`, `.css` | Mật khẩu tạm một lần + sao chép |
| `Components/Shared/PasswordInput.razor`, `.css` | Ô mật khẩu có Hiện / Ẩn |
| `Components/Shared/RedirectToChangePassword.razor` | Chuyển tài khoản mật khẩu tạm tới trang đổi |
| `Helpers/ChangePasswordForm.cs` | Logic form đổi mật khẩu |
| `Helpers/UsersWorkspace.cs`, `Helpers/UserText.cs` | Logic trang tài khoản, chữ hiển thị, điều kiện nút |
| `Services/UserApiClient.cs` | Client A30 `/api/users` |
| `Services/AccountNotice.cs` | Thông báo một lần "đã đổi mật khẩu" |
| `tests/…/B29ChangePasswordTests.cs`, `B29UsersTests.cs`, `B29RouteUiTests.cs` | Test B29 |
| `docs/B29-implementation-report.md` | Báo cáo này |

**Sửa**

| File | Mục đích |
|---|---|
| `Services/ApiClientBase.cs` | `SendRequestAsync` / `JsonRequest` (protected), hành vi cũ giữ nguyên |
| `Services/AuthApiClient.cs` | `ChangePasswordAsync` |
| `Services/ServiceCollectionExtensions.cs` | Đăng ký `UserApiClient` (không log URI) |
| `Services/WebAuthSetup.cs` | Đăng ký `AccountNotice` |
| `Services/AuthNavigator.cs` | Sửa lỗi B28 `IsLeaving` |
| `Helpers/AccessRules.cs` | `ManageUsers`, `UserId(...)` |
| `Helpers/AppNavigation.cs` | Mục menu "Tài khoản" (AdminOnly) |
| `Helpers/AuthText.cs` | `ChangePasswordUrl`, `AfterPasswordChange` |
| `Components/Shared/NotAuthorizedContent.razor` | Nhánh mật khẩu tạm → `/change-password` |
| `Components/Pages/Login.razor` | Mật khẩu tạm vào thẳng trang đổi |
| `Components/Pages/NotFound.razor` | Nút "Đổi mật khẩu" cho tài khoản mật khẩu tạm |
| `Components/Layout/UserMenu.razor`, `.css` | Nút đổi mật khẩu; Đăng xuất chỉ biểu tượng ở 768–991px |
| `Components/Layout/NavMenu.razor`, `.css` | Nút đổi mật khẩu trong khối tài khoản di động |
| `Components/_Imports.razor` | `using SmartCv.Web.Components.Users` |
| `tests/…/B27AuthTestDoubles.cs`, `B27LoginUiTests.cs`, `B28AccessRulesTests.cs`, `B28AuthTestSupport.cs`, `B28RoleUiTests.cs` | Cập nhật theo hành vi B29 |

**File ngoài phạm vi:** không có.

---

## J. Hướng dẫn Git

**Commit message đề xuất**

```
feat(B29): implement account management and password change UI

- /users (Admin): server-side search/filter/paging, create/edit/lock/unlock,
  HR password reset, one-time temporary password, If-Match conflict handling
- /change-password: forced flow for temporary passwords and voluntary flow,
  token renewal, no auto-retry on unknown outcomes
- Fix B28 AuthNavigator leaving flag after in-circuit login redirect
```

**Danh sách file được phép commit:** đúng các dòng trong `C:\TTCN\project\SCApp-B29-Handoff\commit-manifest.tsv` (stage từng file, **không** dùng `git add .`).

**Trạng thái:** đủ điều kiện commit / push lên nhánh `feature/B29-account-management-ui` sau khi người dùng xem lại diff. Khi push lần đầu dùng `git push -u origin feature/B29-account-management-ui`, vì nhánh đã bỏ upstream.

**Trước khi tạo PR:**
1. Thử tay với API + SQL Server bằng tài khoản HR **thử nghiệm** do nhóm tạo (không dùng Admin thật): tạo HR → đăng nhập bằng mật khẩu tạm → đổi mật khẩu; khóa, mở khóa, đặt lại; HR mở `/users` → 403.
2. Xem lại hai thay đổi trên file B28 (`AuthNavigator`, `NotAuthorizedContent`) trong PR.

**Kết luận: CONDITIONAL.** Chức năng, test (0 FAIL) và kiểm thử trình duyệt trên môi trường cô lập đã hoàn tất; còn thiếu thử tay trên môi trường thật.
