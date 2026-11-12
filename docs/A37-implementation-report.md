# Báo cáo triển khai A37 – Che thông tin cá nhân trước khi gửi Gemini

Ngày thực hiện: 12/11/2026 · Nhánh `feature/a37-mask-personal-data` · Commit nền `6cae92f`

> **Tóm tắt.** Nội dung CV gửi Gemini được che email, số điện thoại, LinkedIn/Facebook/GitHub, họ tên, ngày/năm sinh và giới tính bằng `[EMAIL]`, `[PHONE]`, `[LINK]`, `[CANDIDATE]`, `[DOB]`, `[GENDER]`.
> - Việc che diễn ra ở **một điểm duy nhất** (`GeminiEvaluationService`), **trước** khi cắt CV dài và trước khi tạo payload, nên **AI Preview, chấm chính thức và Prompt Test** dùng chung (đã kiểm thử qua API cho cả ba luồng).
> - Keyword scoring và database vẫn dùng nội dung gốc. Không đổi schema hay migration. Không sửa System Prompt lưu trong database.
> - `Gemini:MaskPersonalData` mặc định `true`. Đặt `false` thì payload giống hệt trước A37.
>
> **Vòng 2** hoàn thiện nhận diện họ tên:
> - tên nằm dưới chức danh, hoặc cùng dòng với chức danh;
> - tên viết khác thứ tự (`Khoa Trần Minh`, `Khoa Tran`, `Trần Khoa`);
> - lưới an toàn khi không xác định được tên;
> - sửa lỗi coi tiêu đề mục (`KINH NGHIỆM`) là họ tên.
>
> Build 0 warning / 0 error. **1516 Passed, 0 Failed, 42 Skipped / 1558**, trong đó **159 test A37**.
>
> **Chưa** so điểm AI bằng Gemini thật (chưa được phép dùng API key thật). Không có số liệu nào được tạo thay.

## 1. Nhánh Git và commit nền

| Mục | Kết quả |
|---|---|
| Trạng thái ban đầu | Nhánh trước đó sạch (không staged, modified, untracked hay conflict). Stash cũ không đụng tới |
| `git fetch origin --prune` | Đã chạy |
| `origin/main` | `6cae92f` (#53). A35 #49 `6554f3b`, B34 #50 `7566dce`, Runtime Fix #51 `c7d0c8a`, #52 `8f30584` đều là ancestor |
| Nhánh A37 | Tạo đúng một nhánh `feature/a37-mask-personal-data` từ `origin/main`, không tracking. Vòng 2 làm tiếp trên cùng nhánh, không tạo nhánh mới |
| HEAD | `6cae92f98286e9099270f7e41036bd10c4596b75`. Không commit, không stage |
| Kiểm tra lại cuối vòng 2 | Fetch lại: `origin/main` vẫn `6cae92f` |

## 2. Các yêu cầu đã triển khai

| Yêu cầu | Cách làm | Bằng chứng (test) |
|---|---|---|
| Email → `[EMAIL]` | Regex email | Bộ che + payload |
| Điện thoại VN và quốc tế → `[PHONE]` | Di động VN, cố định VN, quốc tế có `+` (8–15 chữ số) | 14 + 7 dạng số, 1 dòng có hai số |
| LinkedIn, Facebook, GitHub → `[LINK]` | Regex đường dẫn, gồm `*.github.io`; giữ dấu câu cuối câu | 10 tình huống |
| Họ tên → `[CANDIDATE]` | Xem mục 2.1 | 74 tình huống tên |
| Ngày/năm sinh → `[DOB]`, giới tính → `[GENDER]` | Che giá trị sau nhãn, giữ nhãn | 8 tình huống |
| Giữ dữ liệu chuyên môn | Che theo mẫu và nhãn cụ thể; từ điển loại trừ chức danh, mục CV, tổ chức, địa danh | Mục 6; 20 CV mẫu giữ nguyên 246 dòng |
| `PersonalDataMasker` kiểm thử độc lập | Lớp tĩnh thuần trong `SmartCv.Core/Privacy`, không phụ thuộc DI hay log | 140 unit test |
| Che trước khi cắt CV, trước payload | `GeminiEvaluationService.PrepareCvText` chạy trước `AiPromptBuilder` (nơi cắt) | `Masking_happens_before_truncation_so_no_fragment_leaks` |
| Dùng chung cho mọi luồng AI | AI Preview và tiến trình chấm (lượt Screening và PromptTest) đều gọi `IAiEvaluator` → `GeminiEvaluationService` | `AiFlowMaskingTests`: đủ ba luồng qua API |
| Truyền đúng tên ứng viên | `CandidateName = Cv.Candidate.FullName` ở AI Preview và tiến trình chấm | Test Preview, worker, ba luồng |
| Keyword dùng CV gốc | Không đổi dòng chấm keyword | Test worker: keyword trên bản gốc khác bản che, giá trị lưu là của bản gốc |
| Database giữ nguyên; không sửa schema/migration | Không ghi lại `ExtractedText`; không có migration nào thay đổi | Mọi test luồng so sánh `ExtractedText` sau khi chấm |
| `Gemini:MaskPersonalData` mặc định `true`; tắt thì tương thích ngược | `GeminiOptions` + `appsettings.json`; tắt thì gửi nguyên văn như cũ | `Disabled_masking_sends_exactly_the_pre_A37_prompt` |
| Hướng dẫn placeholder trong request; không sửa System Prompt trong database | `AiPromptBuilder.MaskedDataRule` trong mục "QUY TẮC ĐÁNH GIÁ" của request; `systemInstruction` gửi nguyên văn `SystemPrompt` của phiên bản | Ba luồng: so sánh `systemInstruction` với `SystemPrompt` |
| Log chỉ ghi số lượng | Debug: số lượng từng loại, nguồn họ tên. Information khi không xác định được tên. Không ghi nội dung | `Logs_only_counts_never_personal_data_or_key`, `Error_logs_do_not_contain_personal_data` |

### 2.1. Nhận diện họ tên (vòng 2)

Thứ tự ưu tiên; nguồn được ghi trong `MaskResult.NameSource`:

1. **Provided** – họ tên trong bảng `Candidates`.
2. **Label** – sau nhãn `Họ và tên:` / `Full name:`.
3. **Header** – phần đầu CV (tối đa 8 dòng):
   - Bỏ qua tiêu đề chung (`SƠ YẾU LÝ LỊCH`, `CURRICULUM VITAE`). Dừng ở mục nội dung (`KINH NGHIỆM`, `HỌC VẤN`, `KỸ NĂNG`…).
   - Tách dòng theo `-`, `–`, `|`, `•`, `,` để lấy tên trong `NGUYỄN VĂN AN - Backend Developer`.
   - Dòng đầu tiên được đoán như trước. Các dòng sau (ví dụ dưới chức danh) phải **bắt đầu hoặc kết thúc bằng họ Việt phổ biến**, để không coi `Hà Nội`, `Công Ty ABC` là tên.
4. **Fallback (lưới an toàn)** – chỉ khi 1–3 không ra tên. Che chuỗi 3–5 chữ viết hoa bắt đầu hoặc kết thúc bằng họ Việt (`Tôi là Phạm Quang Huy`). Bỏ qua:
   - chuỗi đứng sau tên trường, tổ chức, đường phố (`Đại học Tôn Đức Thắng`, `THPT Lê Quý Đôn`, `đường Nguyễn Huệ`, `Bệnh viện Bạch Mai`);
   - địa danh (`Hà Nội`, `Hồ Chí Minh`, `Thái Lan`);
   - cụm từ thường bắt đầu bằng chữ trùng họ (`Đào Tạo`, `Lương Mong Muốn`, `Lý Thuyết Đồ Thị`, `Đoàn Thanh Niên`).
5. **None** – không xác định được tên. Vẫn che email, số điện thoại, link, ngày sinh, giới tính; log mức Information (không có nội dung).

Mỗi tên được che dưới **ba dạng**:
- **Đúng thứ tự**: không phân biệt hoa thường, có dấu hay mất dấu, NFC hay NFD, xuống dòng giữa tên.
- **Mọi thứ tự khác của đủ các chữ**: `Khoa Trần Minh`, `KHOA MINH TRAN`.
- **Cặp họ + tên theo hai chiều** (tên từ 3 chữ): `Trần Khoa`, `Khoa Tran`.

Hai dạng sau **chỉ che khi các chữ viết hoa chữ cái đầu**, nên không che cụm từ thường: với ứng viên "Nguyễn Thành Công", câu "triển khai thành công" giữ nguyên.

Họ có dấu so sánh **đúng dấu** ("Làm", "Tổng", "Đồng" không phải họ); dạng không dấu chỉ dùng cho chữ không dấu. Chữ đơn lẻ bỏ dấu không dùng để loại trừ, vì trùng tên người ("thử" → Thu, "chỉ" → Chi, "năm" → Nam).

**Trước/sau vòng 2** (dữ liệu giả, công cụ đối chiếu chạy cục bộ):

| Tình huống | Trước vòng 2 | Sau vòng 2 |
|---|---|---|
| `Lập trình viên Backend .NET` ⏎ `Nguyễn Văn An` | Tên lộ | `[CANDIDATE]` |
| `Backend Developer` ⏎ `TRẦN MINH KHOA` | Tên lộ | `[CANDIDATE]` |
| `NGUYỄN VĂN AN - Backend Developer` | Tên lộ | `[CANDIDATE] - Backend Developer` |
| `Trần Minh Khoa \| Lập trình viên .NET` | Tên lộ | `[CANDIDATE] \| Lập trình viên .NET` |
| `Backend Developer` ⏎ `Khoa Minh Trần` | Tên lộ | `[CANDIDATE]` |
| Biết tên "Trần Minh Khoa", CV ghi `Khoa Trần Minh` / `Khoa Tran` / `Trần Khoa` | Tên lộ | `[CANDIDATE]` |
| Chưa biết tên, `Tôi là Phạm Quang Huy` trong câu | Tên lộ | `[CANDIDATE]` (Fallback) |
| Dòng đầu `KINH NGHIỆM` | **Che nhầm** "kinh nghiệm" thành `[CANDIDATE]` (làm hỏng nội dung) | Giữ nguyên; tên ở dưới được che |
| `Backend Developer` ⏎ `Hà Nội` / `Công Ty ABC` | Giữ nguyên | Giữ nguyên |
| `Đại học Tôn Đức Thắng, THPT Lê Quý Đôn` | Giữ nguyên | Giữ nguyên |
| Biết tên "Nguyễn Thành Công", `Dự án Thành Công… thành công` | Giữ nguyên | Giữ nguyên |

**Xử lý an toàn khi không xác định được tên.** Đã làm: lưới an toàn (Fallback) và log Information khi `NameSource = None`. Đề xuất, **chưa làm, cần anh/chị quyết định**:
1. **Chế độ nghiêm ngặt** (tùy chọn mới, mặc định tắt): khi `NameSource = None` thì không gọi Gemini, chấm `KeywordOnly` (nhánh có sẵn). An toàn tuyệt đối với họ tên, nhưng CV không có tên rõ ràng sẽ không có điểm AI.
2. **Lưu họ tên vào bảng `Candidates` khi tải CV**, có HR xác nhận, để mọi CV có nguồn Provided. Đây là nhiệm vụ riêng.
3. Theo dõi tỉ lệ log "Không xác định được họ tên…" trên dữ liệu thật trước khi chọn (1).

## 3. File mới / sửa

| File | Loại | Mục đích |
|---|---|---|
| `src/SmartCv.Core/Privacy/PersonalDataMasker.cs` | Mới | Bộ che (thuần): email, SĐT, link, ngày sinh, giới tính, họ tên (4 nguồn + 3 dạng); `MaskResult` có số lượng và `NameSource` |
| `src/SmartCv.Core/Privacy/PersonalDataMasker.Names.cs` | Mới (vòng 2) | Từ điển nhận diện họ tên: họ Việt, tiêu đề/mục CV, chức danh, tổ chức, địa danh, cụm từ không phải tên |
| `src/SmartCv.Core/Ai/AiContracts.cs` | Sửa | `AiEvaluationRequest` thêm tham số cuối `string? CandidateName = null` (tương thích ngược) |
| `src/SmartCv.Core/Ai/AiPromptBuilder.cs` | Sửa | `MaskedDataRule` + tham số tùy chọn `personalDataMasked`; `false` giữ nguyên prompt cũ |
| `src/SmartCv.Infrastructure/Gemini/GeminiOptions.cs` | Sửa | `MaskPersonalData = true` |
| `src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs` | Sửa | `PrepareCvText`: che một lần trước payload; mọi lần thử lại gửi cùng bản đã che; `CvTruncated` theo bản thật sự gửi; log số lượng và nguồn họ tên |
| `src/SmartCv.Infrastructure/Scoring/RunScoringProcessor.cs` | Sửa | Lấy `Candidate.FullName`, truyền vào yêu cầu AI (lượt Screening và PromptTest) |
| `src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs` | Sửa | Như trên cho AI Preview |
| `src/SmartCv.Api/appsettings.json` | Sửa | `"MaskPersonalData": true` trong mục `Gemini` |
| `tests/SmartCv.Tests/Privacy/PersonalDataMaskerTests.cs` | Mới | 39 tình huống của gói hướng dẫn; **thay 1 đầu vào** (mục 7.1) |
| `tests/SmartCv.Tests/Privacy/PersonalDataMaskerEdgeCaseTests.cs` | Mới | 57 tình huống bổ sung (Unicode, định dạng, chuyên môn, CV dài/rỗng) |
| `tests/SmartCv.Tests/Privacy/PersonalDataMaskerNameDetectionTests.cs` | Mới (vòng 2) | 44 tình huống nhận diện họ tên và chặn che nhầm |
| `tests/SmartCv.Tests/Privacy/GeminiPayloadCapture.cs` | Mới | `HttpMessageHandler` giả ghi lại payload và logger ghi lại log (dùng chung) |
| `tests/SmartCv.Tests/Privacy/GeminiPayloadMaskingTests.cs` | Mới | 10 test payload HTTP thật sự gửi Gemini |
| `tests/SmartCv.Tests/Privacy/AiPreviewMaskingTests.cs` | Mới | 3 test qua `POST /ai-preview` |
| `tests/SmartCv.Tests/Privacy/AiFlowMaskingTests.cs` | Mới (vòng 2) | 3 test qua API cho đủ ba luồng: AI Preview, `POST /api/runs` + tiến trình chấm, Prompt Test |
| `tests/SmartCv.Tests/Privacy/RunScoringProcessorMaskingTests.cs` | Mới | 2 test worker (partial của `RunScoringProcessorTests`, không sửa file cũ) |
| `tests/SmartCv.Tests/Privacy/SampleCvMaskingTests.cs` | Mới | 20 CV PDF mẫu (opt-in qua `SMARTCV_B34_DEMO_ZIP`) |
| `docs/A37-implementation-report.md` | Mới | Báo cáo này |

Tổng: **7 file sửa, 12 file mới** (2 mã nguồn, 9 test, 1 báo cáo).

Không chép đè file nào từ gói hướng dẫn: `AiPromptBuilder` và `GeminiEvaluationService` hiện tại có thêm chống prompt injection, ghi chú cắt CV, phân loại lỗi…, nên chỉ sửa cục bộ. `GeminiOptions` của dự án nằm ở `SmartCv.Infrastructure/Gemini`. **Không** thêm 20 PDF vào repository.

## 4. Luồng dữ liệu từ CV tới Gemini

**Trước A37:**

```text
Cvs.ExtractedText ─┬─► KeywordScoringService (bản gốc)
                   └─► AiEvaluationRequest(CvText = bản gốc)
                        └─► GeminiEvaluationService ─► AiPromptBuilder: cắt ở MaxCvCharacters ─► payload (có email, SĐT, họ tên…) ─► Gemini
```

**Sau A37 (`MaskPersonalData = true`), giống nhau cho AI Preview, lượt Screening và lượt Prompt Test:**

```text
Cvs.ExtractedText ──┬─► KeywordScoringService (bản GỐC, không đổi)
Candidates.FullName ┘   AiEvaluationRequest(CvText = bản gốc, CandidateName)
                         └─► GeminiEvaluationService.PrepareCvText
                              PersonalDataMasker.Mask: NFC → email → link → ngày sinh → giới tính → SĐT
                                → họ tên: Provided | Label | Header | Fallback | None (mỗi tên: đúng thứ tự, thứ tự khác, cặp họ + tên)
                              log Debug: số lượng, nguồn họ tên; Information khi None
                              └─► AiPromptBuilder(bản ĐÃ CHE) + MaskedDataRule → cắt ở MaxCvCharacters ─► payload ─► Gemini
                                   systemInstruction = SystemPrompt của phiên bản (nguyên văn)
                                   mọi lần thử lại dùng cùng payload; CvTruncated tính theo bản đã che
Database: không ghi bản đã che. Cvs.ExtractedText giữ nguyên.
```

`MaskPersonalData = false`: nhánh "Trước A37", từng ký tự giống cũ. Nếu bộ che ném lỗi, request thất bại, **không bao giờ** gửi bản gốc thay thế.

## 5. Thông tin cá nhân đã che (đều có test, dữ liệu giả)

| Loại | Ví dụ |
|---|---|
| Email | `an.nguyen@example.com`, địa chỉ có dấu `+`, email dài vắt qua giới hạn cắt CV |
| Điện thoại VN | 10 số có/không `+84`, `(+84)`, `84-`, cách bằng dấu cách/chấm/gạch, liền nhau (`0912 345 678`, `0912.345.678`, `+84 912 345 678`, `SĐT:0912345678`); cố định có mã vùng trong ngoặc (`(028) 3xxx xxxx`); hai số trên một dòng |
| Điện thoại quốc tế | `+1 (415) 555-…`, `+65 …`, `+44 20 …` |
| Đường dẫn | `linkedin.com/in/…` (có/không `https://www.`, giữ dấu chấm cuối câu), `facebook.com/…`, `fb.com/…`, `github.com/…`, `https://<tên>.github.io/…` |
| Họ tên – biết tên | Có dấu, in hoa, mất dấu do OCR, `Đ`, NFD, hoa thường lẫn lộn, nhiều khoảng trắng, xuống dòng giữa tên; **thứ tự khác** (`Khoa Trần Minh`, `KHOA MINH TRAN`); **cặp họ + tên** (`Trần Khoa`, `Khoa Tran`); tên 2 chữ đảo (`Nguyễn An` / `An Nguyễn`) |
| Họ tên – chưa biết tên | Dòng đầu; sau tiêu đề `SƠ YẾU LÝ LỊCH` / `CURRICULUM VITAE`; sau nhãn `Họ và tên:` / `Full name:`; **dưới chức danh** (Việt, Anh, có dấu chấm như `.NET`); **cùng dòng với chức danh** (`-`, `–`, `\|`, `•`, `,`); họ ở cuối (`Khoa Minh Trần`, `Khoa Tran`); **trong câu** (`Tôi là Phạm Quang Huy`, tên 4 chữ) qua lưới an toàn. Tên trùng từ thường khi bỏ dấu vẫn đúng: `LÊ THU HÀ`, `LÂM QUỲNH CHI`, `PHAN ĐÌNH NAM`, `NGUYỄN VĂN LẬP` |
| Ngày/năm sinh | `Ngày sinh: 12/03/2002`, `Năm sinh 2002`, `Sinh năm: 2001`, `Ngày sinh: 05 tháng 3 năm 2001`, `Date of birth: 2001-03-05`, `DOB: …`, `Born in 2001` |
| Giới tính | `Giới tính: Nữ/Nam` (không che "miền Nam, Việt Nam"), `Gender: Female` |
| Nhiều loại cùng lúc | CV đủ 6 loại: đếm đúng từng loại, nội dung chuyên môn giữ nguyên từng ký tự; che lại lần hai không đổi gì |

**20 CV PDF mẫu** của gói (thông tin giả, đọc chữ như ứng dụng, chưa gắn ứng viên): mỗi CV che đúng 1 email, 1 SĐT, 1 link và họ tên (tổng 20/20/20/20). Không còn `@`, `example.com` hay `linkedin.com`.

## 6. Dữ liệu chuyên môn được bảo toàn (đều có test)

- Năm học, thời gian làm việc: `2019 - 2023`, `06/2025 - nay`, `03.2021 đến 05.2022`, `3+ năm (06/2021 - 08/2024)`.
- GPA, điểm, chứng chỉ: `GPA 3.2/4`, `8.0/10`, `27.5/30`, `IELTS 6.5/7.0`, `TOEIC 850/990`, `AWS SAA-C03`, `ISO 27001:2022`.
- Công nghệ, phiên bản: `.NET 8`, `C# 12`, `SQL Server 2022`, `Docker 24.0.7`, `Node.js 20.11.1`, `Angular 17`, `GitHub Actions`, `GitHub`, `LinkedIn Learning`, `Facebook Graph API`, `ASP.NET Core`, `EF Core`.
- Số liệu: `1.500.000 VND`, `15.000.000 - 20.000.000 VND`, `+2.000.000 VND`, `40%`, `top 5%`, `30+ API`, `192.168.100.200`, `cổng 8080`, mã sinh viên.
- **Chức danh** ở dòng đầu hoặc cùng dòng với tên (`Backend Developer`, `Lập Trình Viên`, `Kỹ Sư Phần Mềm`, `Senior Software Engineer`, `Lập trình viên Backend .NET`): không bị che ở bất kỳ chỗ nào.
- **Mục CV** không bị coi là tên: `KINH NGHIỆM`, `KỸ NĂNG`, `HỌC VẤN`, `THÔNG TIN`, `ĐÀO TẠO`.
- **Tổ chức, địa danh, cụm từ viết hoa**: `Đại học Tôn Đức Thắng`, `THPT Lê Quý Đôn`, `đường Nguyễn Huệ`, `TP. Hồ Chí Minh`, `Bệnh viện Bạch Mai`, `Lý Thuyết Đồ Thị`, `Cơ Sở Dữ Liệu`, `Đoàn Thanh Niên…`, `Lương Mong Muốn`, `Ứng Dụng Quản Lý Kho`, `Hàn Quốc`, `Thái Lan`, `Hà Nội`, `Công Ty ABC`.
- Biết tên "Nguyễn Thành Công": `thành công` (chữ thường) và `Thành Công` (chỉ tên đệm + tên) giữ nguyên. Biết tên "Trần Minh Khoa": `Khoa Công nghệ thông tin`, `trưởng khoa Trần…` giữ nguyên.
- 20 CV mẫu: **246 dòng** không phải tên hoặc liên hệ giữ nguyên từng ký tự.

## 7. Build và kiểm thử

Lệnh (với .NET SDK 10.0.403 theo `global.json`; output riêng để không đụng ứng dụng đang chạy):

```powershell
dotnet build SmartCv.slnx --artifacts-path artifacts/a37-build
dotnet test SmartCv.slnx --artifacts-path artifacts/a37-build --no-build
```

- Bật `SMARTCV_B34_DEMO_ZIP=<đường dẫn ZIP hướng dẫn>` (chỉ đọc).
- **Không** bật các test SQL opt-in: A37 không đổi schema/SQL, và các fixture đó tạo thêm database thử nghiệm.
- Không gọi Gemini thật: mọi test dùng `HttpMessageHandler` giả và khóa giả.

| Hạng mục | Kết quả |
|---|---|
| Build | **0 warning, 0 error** |
| Test A37 (tập trung) | **159 Passed, 0 Failed, 0 Skipped**: 39 (gói) + 57 (bổ sung) + 44 (nhận diện tên) + 10 (payload) + 3 (Preview) + 3 (ba luồng) + 2 (worker) + 1 (20 CV PDF) |
| Toàn bộ | **1516 Passed, 0 Failed, 42 Skipped / 1558** (khoảng 71 giây). Trước A37 là 1399; thêm 159 |

| Nhóm (theo tên lớp test) | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit/khác | 926 | 0 | 1 |
| API (TestServer) | 305 | 0 | 0 |
| UI (bUnit/Web) | 270 | 0 | 0 |
| SQL integration | 15 | 0 | 41 |

**Ba luồng AI (`AiFlowMaskingTests`, qua API thật với GeminiEvaluationService thật, HTTP giả):**

| Luồng | Cách chạy | Kiểm tra |
|---|---|---|
| AI Preview | `POST /api/applications/{id}/ai-preview` cho ứng viên có tên trong `Candidates` (CV ghi tên khác thứ tự) và ứng viên chưa gắn tên (tên dưới chức danh) | Không còn họ tên, email, SĐT, link; `systemInstruction` = System Prompt đang dùng; database giữ bản gốc |
| Chấm chính thức (Screening) | `POST /api/runs` → `RunScoringProcessor` lấy từ DI | Như trên; mọi hồ sơ `Completed` |
| Prompt Test | Tạo bộ CV, gán nhãn HR, `POST /api/prompt-tests` → `RunScoringProcessor` | Như trên; `systemInstruction` = System Prompt **của phiên bản đang thử**; lượt `Completed`, có độ chính xác |

**Regression các phần liên quan (đều đạt, không sửa test cũ):**

| Bộ test | Passed |
|---|---:|
| GeminiEvaluationServiceTests | 61 |
| AiPreviewEndpointTests | 24 |
| RunScoringProcessorTests (không kể 2 test A37) | 36 |
| KeywordScoringTests | 126 |
| ScoreCombinerTests | 35 |
| ScoringConsistencyEndpointTests | 8 |
| PromptTestEndpointTests | 15 |
| PromptEvaluationFlowIntegrationTests | 4 |
| B34StageUiTests | 16 |

**42 Skipped = chưa kiểm thử trong phiên này** (không tính là pass):
- 38 fixture SQL tự tạo rồi xóa database: A11 = 6, A15 = 7, A16 = 10, A17 = 3, Prompt = 5, A35 = 7.
- 4 test SQL runtime cần `SMARTCV_RUNTIME_TEST_SQL_CONNECTION`.

Không có test nào trong số này liên quan tới A37.

**Hiệu năng:** CV hơn 400.000 ký tự che xong trong khoảng 0,3 giây; 20 CV PDF trong khoảng 1,5 giây (gồm đọc PDF).

**Chưa kiểm thử: so điểm AI bằng Gemini thật trước/sau khi che** (cần API key thật và chi phí, chưa được phép). Khi được phép, chạy theo mục "Thử" của gói hướng dẫn, nhưng tắt worker bằng tham số dòng lệnh vì launch profile bật worker:

```powershell
dotnet run --project src/SmartCv.Api --launch-profile https -- --Scoring:WorkerEnabled=false --Gemini:MaskPersonalData=false   # lượt TẮT che
dotnet run --project src/SmartCv.Api --launch-profile https -- --Scoring:WorkerEnabled=false                                  # lượt BẬT che
```

### 7.1. Thay đổi đối với test mẫu của gói (minh bạch)

Trong 39 tình huống của gói, **một đầu vào** của `ChuaBietTen_KhongDoanBay` là `"Lập trình viên Backend .NET\nNguyễn Văn An"` với kỳ vọng `Names == 0`. Kỳ vọng đó **chính là lỗ hổng** mà yêu cầu vòng 2 đề nghị sửa: tên ở dòng dưới chức danh bị gửi nguyên cho Gemini. Sau khi sửa mã, đây là test duy nhất thất bại (111/112 đạt khi chạy lại test cũ).

Cách xử lý, không xóa và không nới điều kiện:
- Đầu vào đó chuyển sang test mới `PersonalDataMaskerNameDetectionTests.Ten_duoi_chuc_danh_duoc_che`, kỳ vọng `"Lập trình viên Backend .NET\n[CANDIDATE]"`: che tên, giữ chức danh.
- Tại vị trí cũ dùng `"Lập trình viên Backend .NET\nKinh nghiệm 2 năm C#"`, giữ đúng ý định gốc "dòng đầu có dấu chấm không bị coi là họ tên" (vẫn `Names == 0`). Có ghi chú ngay trong file test.

38 tình huống còn lại giữ nguyên.

Dữ liệu test mới dùng email `@example.com` và số điện thoại giả. Riêng file test của gói vẫn giữ nguyên các địa chỉ mẫu `@gmail.com` / `@fpt.edu.vn` (mục 8).

## 8. Rủi ro còn tồn tại

1. **Tên không phát hiện được khi chưa biết tên:**
   - tên 2 chữ chỉ xuất hiện trong câu (lưới an toàn cần từ 3 chữ, để không che nhầm `Đào Tạo`, `Cao Học`…);
   - tên nước ngoài không đứng ở dòng đầu;
   - họ không dấu ngắn (`LE`, `DO`, `HO`…) trong tên 2 chữ dưới chức danh;
   - chỉ có tên gọi (`Khoa`) hoặc tên đệm + tên (`Minh Khoa`). Không che các dạng này để tránh che nhầm từ thường ("khoa" của trường đại học, "thành công").
2. **Khi đã có tên trong `Candidates`, không đoán thêm từ CV** (giữ hành vi của gói). Tên trên CV khác tên trong database thì không bị che.
3. **Lưới an toàn có thể:**
   - che cả tên người khác (người tham chiếu) thành `[CANDIDATE]`, có lợi cho quyền riêng tư;
   - hiếm khi che nhầm một cụm 3–5 chữ viết hoa bắt đầu bằng chữ trùng họ chưa có trong từ điển loại trừ (ví dụ tên một dự án).

   Từ điển nằm riêng trong `PersonalDataMasker.Names.cs`, dễ bổ sung kèm test.
4. Hiện ứng dụng **không tạo bản ghi `Candidates`** khi tải CV, nên phần lớn CV dựa vào Label/Header/Fallback. Xem đề xuất ở mục 2.1.
5. **Số điện thoại:**
   - Có thể che nhầm số 10 chữ số bắt đầu bằng 03/05/07/08/09 (mã số thuế) hoặc số tiền có `+` từ 8 chữ số.
   - Không che số quốc tế không có `+` hoặc số viết bằng chữ.
6. **Ngoài phạm vi, chưa che:** địa chỉ, quê quán, tuổi (`24 tuổi`), số CCCD, ảnh, liên kết khác (GitLab, website cá nhân, Zalo, Telegram, X).
7. **Chưa so điểm Gemini thật** trước/sau khi che (mục 7).
8. Database vẫn lưu CV gốc (việc xóa thuộc A39). AI giả (`UseFake`) chạy cục bộ, dùng bản gốc.
9. File test của gói hướng dẫn có địa chỉ email mẫu ở tên miền thật (`@gmail.com`, `@fpt.edu.vn`). Đây là dữ liệu tự đặt của gói, nhưng có thể trùng người thật khi đưa lên GitHub công khai. Đề xuất đổi sang `@example.com` (chỉ đổi giá trị, không đổi ý nghĩa test); chờ anh/chị quyết định.

## 9. Git status, diff và kiểm tra conflict

```text
## feature/a37-mask-personal-data
 M src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs
 M src/SmartCv.Api/appsettings.json
 M src/SmartCv.Core/Ai/AiContracts.cs
 M src/SmartCv.Core/Ai/AiPromptBuilder.cs
 M src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs
 M src/SmartCv.Infrastructure/Gemini/GeminiOptions.cs
 M src/SmartCv.Infrastructure/Scoring/RunScoringProcessor.cs
?? docs/A37-implementation-report.md
?? src/SmartCv.Core/Privacy/
?? tests/SmartCv.Tests/Privacy/
```

`git diff --numstat` (file đã theo dõi): tổng **75 dòng thêm, 16 dòng xóa** trên 7 file.

| File | Thêm | Xóa |
|---|---:|---:|
| `GeminiEvaluationService.cs` | 35 | 9 |
| `AiPromptBuilder.cs` | 12 | 1 |
| `AiPreviewEndpoints.cs` | 8 | 2 |
| `RunScoringProcessor.cs` | 8 | 2 |
| `GeminiOptions.cs` | 6 | 0 |
| `AiContracts.cs` | 4 | 1 |
| `appsettings.json` | 2 | 1 |

File mới:
- `PersonalDataMasker.cs` (369 dòng), `PersonalDataMasker.Names.cs` (133 dòng);
- 9 file test trong `tests/SmartCv.Tests/Privacy/` (1326 dòng);
- báo cáo này.

**Kiểm tra:**
- `git diff --check`: sạch; không khoảng trắng cuối dòng, không dấu conflict.
- `git diff --cached`: rỗng.
- `origin/main` không đổi so với commit nền, nên hiện không có xung đột.

**Kiểm tra trước khi đưa lên GitHub công khai** (báo cáo, mã nguồn và test A37):
- Không có đường dẫn máy cục bộ, tên máy, tên người dùng hệ điều hành, API key hay khóa có dạng thật (test chỉ dùng khóa giả như `test-…`).
- Không có dữ liệu CV thật. Mọi họ tên, email (`@example.com`) và số điện thoại trong test và báo cáo là dữ liệu giả, một phần lấy từ bộ 20 CV tự tạo của gói. Ngoại lệ đã nêu ở mục 8.9.
- Không đưa lên Git: 20 PDF mẫu, nội dung CV trích xuất, log, TRX, bản build. Tất cả nằm trong `artifacts/` (đã ignore) hoặc thư mục tạm.

Không chạy `git add`, `commit`, `push`, `merge`, `rebase`, `reset`, `stash` hay `clean`; không tạo PR. Không đổi connection string, User Secrets, API key hay database. Không gọi Gemini thật.

## 10. Kết luận

**SẴN SÀNG** để anh/chị duyệt và commit phần code A37:
- Đủ các yêu cầu ở mục 2, gồm hoàn thiện nhận diện họ tên ở vòng 2.
- Ba luồng AI đã kiểm thử qua API.
- Build 0 warning / 0 error; **1516 Passed, 0 Failed**; 42 Skipped đã nêu rõ.

Cần anh/chị quyết định:
- Một test mẫu đã được thay đầu vào (mục 7.1).
- Có đổi địa chỉ email mẫu trong file test của gói không (mục 8.9).
- Có bật chế độ nghiêm ngặt khi không xác định được tên không (mục 2.1).
- Có chạy so sánh với Gemini thật không (mục 7).

Lệnh đề xuất (anh/chị tự chạy, liệt kê đúng thư mục và file, không dùng `git add .`):

```powershell
git add src/SmartCv.Core/Privacy src/SmartCv.Core/Ai/AiContracts.cs src/SmartCv.Core/Ai/AiPromptBuilder.cs src/SmartCv.Infrastructure/Gemini/GeminiOptions.cs src/SmartCv.Infrastructure/Ai/GeminiEvaluationService.cs src/SmartCv.Infrastructure/Scoring/RunScoringProcessor.cs src/SmartCv.Api/Endpoints/AiPreviewEndpoints.cs src/SmartCv.Api/appsettings.json tests/SmartCv.Tests/Privacy docs/A37-implementation-report.md
git diff --cached --stat
git commit -m "A37: Che email, so dien thoai, link, ho ten truoc khi gui Gemini"
git push -u origin feature/a37-mask-personal-data
```
