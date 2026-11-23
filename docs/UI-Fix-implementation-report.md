# SCApp – Fix UI profile / ranking density (branch `fix/b56-ui-refinement`)

## Phạm vi

1. `UserMenu.razor`: kết hợp avatar initials, tên và vai trò thành một link `/account` duy nhất. Bỏ nút Tài khoản thừa và nút chìa khóa đổi mật khẩu trên thanh topbar. Việc đổi/thiết lập mật khẩu vẫn có ở `/account` qua `SecurityCard`; nút Đăng xuất giữ `AuthNavigator.SignOutAsync`.
2. `RankingTable.razor`: giữ nguyên API, thứ tự xếp hạng, bộ lọc AI, giai đoạn HR, cờ HR, quyết định, menu và export; chỉ bổ sung tóm tắt số CV đang hiển thị và nút xóa bộ lọc, liên kết cùng `ResetFilters` sẵn có.
3. `RankingTable.razor.css`: bộ lọc nhóm rõ ràng, compact 2 hàng desktop, rail cuộn ngang ở viewport nhỏ; hàng bảng súc tích hơn, giữ đầy đủ kỹ năng bắt buộc, nhãn AI, điểm KW/AI, stage HR và lý do loại. Không can thiệp phép tính/nhãn.
4. `ui-refinement.js`: hỗ trợ cuộn rail đến bộ lọc người dùng chọn và dùng phím mũi tên di chuyển giữa các nút cùng nhóm, không phát request và không giữ dữ liệu; nếu JS lỗi/tắt Blazor vẫn lọc được. Tuân thủ `prefers-reduced-motion`.
5. `RankingFilterDisplay.cs`, `UiRefinementTests.cs`: quy tắc hiển thị số lượng bộ lọc, số CV; không thay đổi business rules.

## Nghiệp vụ không thay đổi

- AI dự đoán **không tương đương** quyết định HR. Ví dụ AI `Không đạt` nhưng HR `Qua vòng CV` là hai nguồn trạng thái khác nhau; UI không sửa ngầm.
- Hai CV có cùng điểm số có thể cùng hạng `02`. Không ép đổi thứ hạng vì muốn giao diện đẹp.
- CV chưa có tên rõ ràng vẫn giữ nhãn `Chưa rõ tên ứng viên`; không suy đoán từ tên file.
- `Bắt buộc 1/3` là mức khớp yêu cầu; không tự chuyển thành điểm phù hợp.
- Không hiển thị ảnh avatar từ Google vì hợp đồng `MeDto` hiện chưa có URL ảnh tin cậy; dùng initials nhất quán.
- Không thay đổi A54/B56 Workspace/Quota, không sửa database, không thay đổi OAuth hoặc token.

## Kiểm thử cần điền sau chạy tại Windows

- [ ] Đúng branch `fix/b56-ui-refinement` và working tree sạch trước apply.
- [ ] Dry run `UI FIX PREFLIGHT OK`, backup được tạo khi apply.
- [ ] `dotnet build src/SmartCv.Web` PASS.
- [ ] `dotnet test tests/SmartCv.Tests --filter FullyQualifiedName~UiRefinement` PASS.
- [ ] `dotnet test tests/SmartCv.Tests --filter FullyQualifiedName~B55|FullyQualifiedName~B56|FullyQualifiedName~B34|FullyQualifiedName~B29` PASS (PowerShell dùng dấu nháy cho filter).
- [ ] Dashboard: bấm avatar/tên đi `/account`, đổi mật khẩu trong SecurityCard, đăng xuất hoạt động.
- [ ] Trang Ranking: AI filter, HR stage, Cần xem kỹ, Cờ HR, tìm kiếm, sắp xếp, reset bộ lọc.
- [ ] Ranking: cùng điểm cùng hạng; AI vs HR độc lập; chọn nhiều, đổi giai đoạn, từ chối có lý do, cờ HR, chi tiết, export.
- [ ] 390×844, 768, 1024, 1440; keyboard Tab/Enter/Space/Left/Right; CSS khi không tải JS.
- [ ] Prefers reduced motion, kiểm tra focus-visible, khi JS tải lỗi/không được phép vẫn thao tác bình thường.
- [ ] `dotnet test SmartCv.slnx` và phân loại test lỗi có sẵn vs test lỗi mới.
- [ ] `git diff --check`, `git diff --cached --check` khi stage.

## Rủi ro và giới hạn

- Mã nền dùng để xác định anchor: GitHub `main` ngày 23/11/2026 (commit `33742d0`). Nếu branch B56/fix đã sửa các file này, **installer phải dừng**. Không bỏ qua anchor check.
- Đây là bản thiết kế/patch đã rà soát tĩnh; chưa chạy .NET trên máy Windows của người dùng. Không tuyên bố build PASS trước khi có log.
- Khi B56 chưa được merge `main`, branch fix là stacked trên B56. Tạo PR fix **vào B56**, hoặc đợi B56 merge rồi chủ động chuyển base; tuyệt đối không auto merge.
