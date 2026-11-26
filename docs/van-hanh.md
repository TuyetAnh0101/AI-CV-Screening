# SCApp — Vận hành sao lưu & khôi phục (Google Drive)

Google Drive là **nơi lưu bản sao lưu chính thức duy nhất**. Máy chủ chỉ giữ dữ liệu gốc (SQL Server, tệp CV) và thư mục tạm (staging) trong lúc chạy. Kết nối Google OAuth: [google-drive-backup.md](google-drive-backup.md).

## Kiến trúc

```text
SQL Server + kho CV + key ring
        │  (SCApp Backup Worker — dịch vụ Windows, tài khoản ảo NT SERVICE\SCAppBackupWorker)
        ├─ BACKUP DATABASE (COPY_ONLY, CHECKSUM) / BACKUP LOG (CHECKSUM)
        ├─ đối chiếu SHA-256 mọi tệp CV, chụp tệp mới (giữ khóa SmartCv:A39:privacy ở chế độ chia sẻ)
        ├─ AES-256-GCM trên máy chủ  →  Google Drive (upload resumable, đối chiếu MD5)
        └─ manifest mã hóa tải lên CUỐI CÙNG  →  danh mục BackupPoints trong database
Admin /admin/backups  →  API (chỉ System Admin, ghi Audit)  →  bảng BackupJobs  →  worker thực hiện

Google Drive → tải → kiểm SHA-256 → giải mã → RESTORE vào database MỚI → nhận quyền sở hữu database đó
             → DBCC CHECKDB → dựng lại tệp CV, đối chiếu hash → key ring
        (SCApp Restore Worker — NT SERVICE\SCAppRestoreWorker)
```

- API không có quyền SQL BACKUP/RESTORE và không đọc/ghi tệp sao lưu. Admin chỉ chọn trong các loại công việc cố định.
- Hàng đợi là bảng `BackupJobs`: mỗi loại tối đa một công việc chờ/chạy, không mất khi khởi động lại.
- Mỗi vai trò chỉ một worker chạy tại một thời điểm (`sp_getapplock`).
- Restore **không bao giờ** ghi đè database đang chạy: luôn tạo database tên `SmartCv_Restore_<32 ký tự>`.

## Sáu trạng thái trên trang `/admin/backups` — không thay thế cho nhau

| Trạng thái | Nghĩa là | Không có nghĩa là |
|---|---|---|
| Google Drive đã kết nối | Google còn chấp nhận quyền truy cập ở lần kiểm tra gần nhất | Đã có bản sao |
| Worker đang chạy | Hai dịch vụ vừa báo tín hiệu (dưới 3 phút) | Chạy được công việc |
| Worker sẵn sàng | Có khóa, đủ chỗ, đủ quyền SQL, đã kết nối Drive | Dữ liệu CV đầy đủ (xem cảnh báo riêng) |
| Sao lưu thành công | Mọi tệp đã lên Drive và khớp checksum | Đã tải về giải mã được |
| Đã xác minh | Bản mới nhất đã tải về, giải mã, đối chiếu hash | Đã khôi phục được |
| Khôi phục thử | Đã RESTORE + CHECKDB + dựng lại CV vào database cách ly | Ứng dụng đã được chuyển sang bản đó |

## Lịch tự động

Mặc định: bản đầy đủ 01:30 hằng ngày, transaction log mỗi 4 giờ (database FULL recovery), **kiểm tra toàn vẹn hằng tuần**, khôi phục thử hằng tuần, giữ 14 bản đầy đủ. Đổi trên web. Không cần Admin đăng nhập.

- Máy tắt đúng giờ: mỗi đêm bị lỡ ghi `Bị lỡ`, worker chạy bù **một** lần khi hoạt động lại.
- Lỗi tạm thời (Drive, mạng, khóa, SQL) của bản đầy đủ tự động: thử lại tối đa 2 lần, cách 30 phút. Lỗi cần người xử lý không thử lại.
- Transaction log chỉ được lên lịch khi đã có bản đầy đủ thành công. Drive không truy cập được thì worker **không** chạy `BACKUP LOG` (log vẫn nằm trong SQL Server).
- Log đã tạo mà chưa tải lên được nằm mã hóa trong `staging\logs-pending` và được tải ở lần sau. Không xóa thư mục này bằng tay.
- Công việc đang chạy khi worker chết: ghi `A62_INTERRUPTED`; lần chạy kế tiếp dọn tệp tạm chưa mã hóa.

Mức mất dữ liệu tối đa xấp xỉ chu kỳ log (4 giờ) nếu chuỗi log liên tục; nếu không, tính từ bản đầy đủ gần nhất.

## Phạm vi được sao lưu

- Toàn bộ database (mọi bảng, tài khoản, workspace, audit, outbox…).
- Tệp CV mà database đang tham chiếu; mỗi nội dung chỉ tải lên một lần; tệp đã mất trên Drive được tải lên lại ở bản đầy đủ kế tiếp.
- Key ring Data Protection của API và Web.
- Manifest: LSN, phiên bản schema, danh sách tệp kèm SHA-256.

**Không** sao lưu mã nguồn, `bin/obj`, gói NuGet, log tạm, secrets cấu hình (`Jwt:Key`, SMTP, Gemini, OAuth Client Secret) — **cất riêng** các secrets này; thiếu chúng ứng dụng khôi phục không chạy đầy đủ. Tệp trong kho CV mà database không tham chiếu chỉ được đếm.

## Mã hóa và khóa khôi phục

Mỗi tệp trên Drive được mã hóa AES-256-GCM bằng khóa con dẫn xuất từ **khóa sao lưu 256-bit**; tên tệp CV là HMAC. Khóa lưu tại `C:\ProgramData\SCApp\Backup\key\backup-key.dat` (DPAPI máy; ACL chỉ worker + Administrators). Khóa được hiện **một lần** khi tạo.

Bản cất khóa phải ở **ngoài máy này** và **ngoài Google Drive chứa backup** (trình quản lý mật khẩu hoặc bản in). Không chụp màn hình, không dán vào chat/e-mail. Mất cả máy lẫn bản cất → không giải mã được.

Chạy trong PowerShell mở bằng **Run as administrator** (không chạy trong terminal của VS Code, vì terminal đó giữ lịch sử cuộn):

```powershell
$w = 'C:\Program Files\SCApp\BackupWorker\SmartCv.BackupWorker.exe'
$c = 'C:\ProgramData\SCApp\Backup\worker.backup.json'
& $w verify-key --config $c      # gõ khóa từ bản cất: MATCH / RETIRED / NO MATCH, không lưu gì
& $w init-key --rotate --config $c   # khóa mới; khóa cũ giữ lại để đọc bản cũ
Restart-Service SCAppBackupWorker, SCAppRestoreWorker
```

**Khi khóa bị lộ:** chạy `init-key --rotate`, cất khóa mới, `verify-key` với khóa mới (phải `MATCH`) và với khóa lộ (phải `RETIRED` hoặc `NO MATCH`). Chạy một bản đầy đủ mới (mọi tệp CV được tải lại bằng khóa mới). Các bản cũ vẫn mã hóa bằng khóa đã lộ: nếu chúng đã tồn tại trên Drive, quyết định xóa sau khi bản mới đã khôi phục thử thành công. Giữ bản cất của khóa cũ chừng nào còn muốn khôi phục bản cũ.

## Quyền SQL của hai dịch vụ

| Tài khoản | Quyền |
|---|---|
| `NT SERVICE\SCAppBackupWorker` | `db_backupoperator`; SELECT 5 cột của `Cvs` (không có tên tệp, không có nội dung); bảng `Backup*`; ghi Audit; đọc lịch sử backup trong `msdb` |
| `NT SERVICE\SCAppRestoreWorker` | `CREATE ANY DATABASE`; bảng `Backup*`; ghi Audit; EXECUTE `master.dbo.SCApp_ClaimRestoredDatabase` |

**Vì sao cần thủ tục nhận quyền sở hữu:** database được RESTORE giữ nguyên chủ sở hữu của database nguồn bên trong tệp, nên tài khoản chỉ có `CREATE ANY DATABASE` không mở, không CHECKDB và không xóa được chính database nó vừa khôi phục. Thủ tục này chỉ nhận database tên `SmartCv_Restore_<hex>` do **chính tài khoản gọi** khôi phục. Nó được ký bằng certificate (login của certificate có `CONTROL SERVER`), private key bị xóa ngay sau khi ký. Chỉ cài khi chạy installer với `-GrantRestoreClaim`; xem SQL trước bằng:

```powershell
& $w sql-grants SmartCvDb 'NT SERVICE\SCAppBackupWorker' 'NT SERVICE\SCAppRestoreWorker' --restore-claim
```

**Kiểm tra quyền đúng cách:** lệnh `check` chạy dưới tài khoản của bạn (thường là sysadmin) nên không đại diện cho dịch vụ. Dịch vụ tự kiểm tra quyền của chính nó và báo lên; `check` in lại dòng `service self-check`.

## Cài đặt / cập nhật worker

PowerShell **Run as administrator**, tại thư mục dự án:

```powershell
.\scripts\backup-worker\Install-BackupWorker.ps1 -GrantRestoreClaim          # xem trước, không đổi gì
.\scripts\backup-worker\Install-BackupWorker.ps1 -Apply -GrantRestoreClaim   # cài/cập nhật
Start-Service SCAppBackupWorker, SCAppRestoreWorker
```

Chạy lại an toàn: không tạo lại khóa đã có, giữ danh sách `KnownMissingCvs` đã cài. Khi cập nhật, dịch vụ bị dừng và **không** tự khởi động lại nếu không có `-Start`. Nếu cần chạy script bị chặn bởi execution policy: `powershell -ExecutionPolicy Bypass -File <script> …`.

Gỡ: `.\scripts\backup-worker\Uninstall-BackupWorker.ps1 -Apply` (giữ khóa, cấu hình, bản trên Drive).

## Lệnh worker

```powershell
& $w check      --config $c   # sẵn sàng: cấu hình, khóa, CV, SQL, tự kiểm tra của dịch vụ, Drive
& $w cv-report  --config $c   # CV thiếu/sai: chỉ CvId và SHA-256
& $w once       --config $c   # một chu kỳ ngay (khi dịch vụ đang dừng)
& $w check      --config 'C:\ProgramData\SCApp\Backup\worker.restore.json'
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='SCApp Backup Worker'} -MaxEvents 50
```

Log chỉ chứa mã lỗi và số đếm; không có đường dẫn CV, token hay nội dung.

## Khi bản sao lưu báo thiếu tệp CV (`A62_FILES`)

Database tham chiếu tệp theo đường dẫn tương đối `Uploads/CV/...` dưới `CvUpload:BaseDirectory` (để trống = thư mục của API đang chạy). **Chạy API từ một bản sao mã nguồn khác (git worktree) sẽ ghi CV vào thư mục của bản sao đó** — đây là cách tệp "biến mất". Nên đặt `CvUpload:BaseDirectory` cố định và trỏ worker (`-CvBaseDirectory`) vào cùng nơi.

1. `cv-report` để biết CvId nào thiếu/sai.
2. Tìm bản gốc và sao chép lại (so SHA-256, không ghi đè, không xóa nguồn, không đổi database):

   ```powershell
   .\scripts\backup-worker\Restore-MissingCvFiles.ps1 -SourceBase 'C:\TTCN\project\AI-CV-Screening-B28\src\SmartCv.Api'          # chỉ kiểm tra
   .\scripts\backup-worker\Restore-MissingCvFiles.ps1 -SourceBase 'C:\TTCN\project\AI-CV-Screening-B28\src\SmartCv.Api' -Apply   # sao chép
   ```

3. Chỉ khi tệp **thật sự không còn ở đâu**, người chịu trách nhiệm dữ liệu quyết định từng hồ sơ. Tạo tệp JSON (UTF-8), mỗi phần tử một hồ sơ, rồi cài bằng `-KnownMissingFile`:

   ```json
   [ { "CvId": "<guid từ cv-report>", "Sha256": "<sha từ cv-report>",
       "Reason": "demo-seed-never-had-file", "ApprovedBy": "<họ tên người duyệt>", "ApprovedOn": "2026-11-26" } ]
   ```

   - `demo-seed-never-had-file`: chỉ được chấp nhận cho đúng 12 dòng của `database/seed_demo.sql` (cv-report đánh dấu).
   - `loss-accepted-by-data-owner`: dữ liệu thật đã mất, được chấp nhận.
   - Cả hai đều bắt buộc người duyệt và ngày. Mỗi bản sao lưu dựa vào danh sách này ghi sự kiện Audit `backup.known_missing_applied`.
   - Tệp xuất hiện lại thì được kiểm hash và sao lưu bình thường. Nên gỡ mục đó khỏi danh sách.

Đừng tạo tệp giả, đừng sửa `StoredPath` hay xóa hồ sơ chỉ để bản sao lưu chạy được.

## Khôi phục thử và khôi phục sự cố

Trên web: **Khôi phục thử** khôi phục bản mới nhất vào database mới, kiểm tra rồi xóa database đó. **Khôi phục…** (tab Bản sao lưu) tạo database mới và giữ lại; có thể chọn thời điểm (giờ máy chủ SQL), phải nhập `KHÔI PHỤC`. Kết quả: `C:\ProgramData\SCApp\Backup\restore\<database>\` gồm `files\Uploads\CV\…`, `keys\api|web`, `restore-result.json` (số dòng bảng chính, CHECKDB, số tệp CV). Thư mục này chứa dữ liệu thật **không mã hóa**: xóa sau khi dùng xong.

Chuyển sang bản khôi phục (có phê duyệt, trong thời gian bảo trì):

1. Dừng API, Web, worker. Giữ nguyên database và tệp cũ.
2. Kiểm `restore-result.json`, đối chiếu tài khoản, workspace, hồ sơ.
3. Database khôi phục đang thuộc sở hữu của tài khoản restore: DBA chạy `ALTER AUTHORIZATION ON DATABASE::[<tên>] TO [<tài khoản quản trị>];` và kiểm user của ứng dụng.
4. Trỏ `ConnectionStrings:SmartCvDb` sang database mới, `CvUpload:BaseDirectory` sang `…\<database>\files`; đặt key ring đã khôi phục (Web dùng DPAPI theo tài khoản Windows — chỉ giải mã được bằng đúng tài khoản cũ, chưa kiểm chứng trên máy khác).
5. **Áp dụng lại mọi yêu cầu xóa/ẩn danh dữ liệu cá nhân phát sinh sau thời điểm khôi phục.**
6. Xem trước hàng đợi email/chấm điểm cũ trước khi bật worker ứng dụng.
7. Rollback: trỏ cấu hình về database/thư mục cũ.

## Mất cả máy chủ (mất ổ C)

Cần có **trước đó, ngoài máy**: khóa khôi phục (cả khóa cũ nếu đã đổi), secrets cấu hình, tài khoản Google chứa backup, và thông tin OAuth Client/dự án Google Cloud.

1. Máy mới: SQL Server tương thích (cùng hoặc mới hơn), .NET, mã nguồn đúng phiên bản.
2. Tạo database trống **cùng tên** (`SmartCvDb`), đặt FULL recovery, chạy migration.
3. Cài worker với `-Apply -ImportKey -GrantRestoreClaim` và gõ khóa đã cất (**không tạo khóa mới**). Khóa cũ hơn (trước các lần đổi khóa): `init-key --import --retired --config $c`.
4. Trên web: kết nối **đúng tài khoản Google** bằng **đúng OAuth Client** (quyền `drive.file` chỉ thấy tệp do chính client đó tạo).
5. Tab Lịch: **Tạm dừng** lịch tự động trước, rồi tab Google Drive: **Đọc lại danh mục từ Drive**. Điểm nhập lại từ Drive không bao giờ bị tự động xóa bởi lưu giữ.
6. **Khôi phục…** bản đầy đủ muốn lấy (kèm thời điểm nếu cần), chuyển đổi như trên, rồi mới bật lại lịch.

## Lưu giữ

Giữ N bản đầy đủ mới nhất (7–90), mọi log mà bản cũ nhất còn cần, và mọi tệp CV mà một điểm còn lại tham chiếu. Không dọn khi đang khôi phục. Tệp đã bị xóa khỏi hệ thống theo chính sách vẫn còn trên Drive cho tới khi mọi điểm chứa nó hết hạn.

## Mã lỗi

| Mã | Ý nghĩa | Việc cần làm |
|---|---|---|
| `A62_FILES` | CV thiếu hoặc sai hash/kích thước | mục "thiếu tệp CV" ở trên |
| `A62_NO_BACKUP` | Chưa có bản đầy đủ thành công | chạy Sao lưu ngay |
| `A62_LOG_CHAIN` | Chuỗi log đứt (thường do công cụ khác chạy `BACKUP LOG`) | chạy bản đầy đủ mới |
| `A62_DRIVE_AUTH` | Token hết hạn/bị thu hồi | Kết nối lại Drive ([google-drive-backup.md](google-drive-backup.md)) |
| `A62_DRIVE_QUOTA` | Drive đầy | giải phóng/nâng dung lượng |
| `A62_DRIVE_UNAVAILABLE` | Mạng/Google lỗi tạm thời | tự thử lại |
| `A62_DRIVE_MISSING`, `A62_DRIVE_VERIFY`, `A62_CHECKSUM` | Tệp trên Drive mất/hỏng | khôi phục từ thùng rác Drive hoặc tạo bản đầy đủ mới |
| `A62_KEY`, `A62_KEY_MISMATCH` | Thiếu khóa / bản dùng khóa khác | `init-key --import` (`--retired`) |
| `A62_PRIVILEGE` | Dịch vụ thiếu quyền SQL | chạy lại installer, xem `check` |
| `A62_DATABASE` | Database không ONLINE, hoặc bản trên Drive thuộc database tên khác | dùng đúng tên database |
| `A62_SPACE` | Ổ đĩa/staging thiếu chỗ | giải phóng dung lượng |
| `A62_RECOVERY_MODEL` | Database ở SIMPLE | DBA quyết định |
| `A62_LOCKED`, `A62_INTERRUPTED`, `A62_TIMEOUT`, `A62_CONSISTENCY` | Tạm thời | tự thử lại hoặc bấm Thử lại |

## Checklist nghiệm thu (chỉ khi đủ mới coi là đang vận hành)

- [ ] `cv-report` không còn hồ sơ chưa xử lý.
- [ ] `check` cho cả hai vai trò in `READY` (gồm `service self-check`).
- [ ] `verify-key` với bản cất ngoài máy: `MATCH`.
- [ ] OAuth ở trạng thái **In production** và đã kết nối lại sau khi chuyển.
- [ ] Sao lưu ngay: thành công; trong Drive chỉ thấy tệp `.enc`.
- [ ] Kiểm tra toàn vẹn: thành công.
- [ ] Sao lưu transaction log: thành công.
- [ ] Khôi phục thử: thành công.
- [ ] **Khôi phục…** vào database mới, kiểm `restore-result.json`, mở thử vài tệp CV; `SmartCvDb` không đổi.
- [ ] Sau 01:30: có công việc **Theo lịch** thành công trong Báo cáo.
- [ ] `Restart-Service` hai dịch vụ: tự báo sẵn sàng lại, không tạo công việc trùng.

## Việc định kỳ

- Hằng tuần: xem trang `/admin/backups` (cảnh báo, lỗi, lần xác minh, lần khôi phục thử).
- Hằng quý: `verify-key` với bản cất; một lần **Khôi phục…** có kiểm tra dữ liệu.

## Bản sao lưu local cũ

Hệ thống không tự xóa hay tải lên các bản `.bak` cũ trên máy. Quản trị viên quyết định riêng; nếu muốn giữ, tạo bản đầy đủ mới trên Drive và khôi phục thử thành công trước khi dọn.
