# Installs or updates the SCApp Google Drive backup worker as Windows services. DEFAULT = PREVIEW (nothing changes).
#   .\Install-BackupWorker.ps1                                       preview and checks
#   .\Install-BackupWorker.ps1 -Apply                                install/update (Run as administrator)
#   .\Install-BackupWorker.ps1 -Apply -GrantRestoreClaim             also allow the restore worker to open its restores
#   .\Install-BackupWorker.ps1 -Apply -Start                         ... and start the services
# Uses virtual service accounts (no passwords, no new Windows users). Never changes BitLocker or execution
# policy, never copies data, never replaces an existing key and never starts a backup itself.
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Start,
    # Adds a certificate-signed procedure in master. Without it restore drills and recoveries cannot work, because
    # a database restored by a least-privilege account stays closed to that account. Review the SQL first.
    [switch]$GrantRestoreClaim,
    [string]$Server = 'localhost\BB',
    [string]$Database = 'SmartCvDb',
    [string]$InstallDir = 'C:\Program Files\SCApp\BackupWorker',
    [string]$DataDir = 'C:\ProgramData\SCApp\Backup',
    [string]$CvBaseDirectory,
    [string]$ApiKeysPath,
    [string]$WebKeysPath = (Join-Path $env:LOCALAPPDATA 'ASP.NET\DataProtection-Keys'),
    # JSON array of acknowledged missing CVs (see docs). Omit to keep the list already installed.
    [string]$KnownMissingFile,
    [switch]$SkipRestoreWorker,
    # Replacement machine: type the saved recovery key instead of creating a new one.
    [switch]$ImportKey,
    [int]$MinimumFreeGb = 10
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallerFunctions.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$backupService = 'SCAppBackupWorker'; $restoreService = 'SCAppRestoreWorker'
$backupAccount = "NT SERVICE\$backupService"; $restoreAccount = "NT SERVICE\$restoreService"
$eventSource = 'SCApp Backup Worker'
$exe = Join-Path $InstallDir 'SmartCv.BackupWorker.exe'
$backupConfig = Join-Path $DataDir 'worker.backup.json'; $restoreConfig = Join-Path $DataDir 'worker.restore.json'
$connectionString = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true"
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$script:blocking = 0

function Check([string]$Name, [bool]$Ok, [string]$Detail, [bool]$Required = $true) {
    $mark = 'OK  '; if (!$Ok) { if ($Required) { $mark = 'FAIL'; $script:blocking++ } else { $mark = 'WARN' } }
    Write-Host ("[{0}] {1} - {2}" -f $mark, $Name, $Detail)
}
function Query([string]$Db, [string]$Sql) {
    Add-Type -AssemblyName System.Data
    $c = [Data.SqlClient.SqlConnection]::new("Server=$Server;Database=$Db;Integrated Security=true;TrustServerCertificate=true"); $c.Open()
    try { $cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $t = [Data.DataTable]::new(); $r = $cmd.ExecuteReader(); $t.Load($r); return , $t } finally { $c.Dispose() }
}

# Values already installed win over rediscovery; user secrets are read only when nothing else says where the key ring is.
$installed = $null
try { if (Test-Path -LiteralPath $backupConfig) { $installed = Get-Content -LiteralPath $backupConfig -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { }
if (!$ApiKeysPath) { $ApiKeysPath = Get-InstalledValue $installed 'DataProtection' 'KeysPath' }
if (!$CvBaseDirectory) { $CvBaseDirectory = Get-InstalledValue $installed 'BackupWorker' 'CvBaseDirectory' }
if (!$CvBaseDirectory) { $CvBaseDirectory = Join-Path $repo 'src\SmartCv.Api' }
if (!$PSBoundParameters.ContainsKey('WebKeysPath') -and (Get-InstalledValue $installed 'BackupWorker' 'WebKeysPath')) { $WebKeysPath = Get-InstalledValue $installed 'BackupWorker' 'WebKeysPath' }
if (!$ApiKeysPath) {
    $secret = & dotnet user-secrets list --project (Join-Path $repo 'src\SmartCv.Api') 2>$null | Where-Object { $_ -like 'DataProtection:KeysPath = *' } | Select-Object -First 1
    if ($secret) { $ApiKeysPath = $secret.Substring('DataProtection:KeysPath = '.Length).Trim() }
}

Write-Host "=== CHECKS ($(if ($Apply) { 'APPLY' } else { 'PREVIEW' })) ==="
Check 'Administrator' $elevated 'Required for -Apply (Run as administrator).' ([bool]$Apply)
$drive = [IO.Path]::GetPathRoot($DataDir)
$free = [Math]::Round(([IO.DriveInfo]::new($drive)).AvailableFreeSpace / 1GB, 1)
Check 'Free space' ($free -ge $MinimumFreeGb) "$free GB free on $drive (temporary staging only; need $MinimumFreeGb GB)."
Check 'CV storage' (Test-Path -LiteralPath (Join-Path $CvBaseDirectory 'Uploads\CV')) (Join-Path $CvBaseDirectory 'Uploads\CV')
Check 'API key ring' ([bool]($ApiKeysPath -and (Test-Path -LiteralPath $ApiKeysPath))) "$ApiKeysPath (DataProtection:KeysPath of the API)"
Check 'Web key ring' (Test-Path -LiteralPath $WebKeysPath) $WebKeysPath
Check '.NET SDK' ([bool](Get-Command dotnet -ErrorAction SilentlyContinue)) 'Needed to publish the worker.'
$sqlServiceAccount = $null
try {
    $row = (Query 'master' "SELECT IS_SRVROLEMEMBER('sysadmin') AS A, OBJECT_ID(N'master.dbo.SCApp_ClaimRestoredDatabase') AS P;").Rows[0]
    Check 'SQL sysadmin' ($row.A -eq 1) "$Server (only to create the service logins and grants)."
    $claimInstalled = !($row.P -is [DBNull])
    $tables = (Query $Database "SELECT COUNT(*) AS N FROM sys.tables WHERE name LIKE 'Backup%';").Rows[0].N
    Check 'Database migration' ($tables -ge 7) "backup tables found: $tables. Apply database\a62_google_drive_backup.sql first (after approval)."
    $missing = @((Query $Database "SELECT StoredPath FROM dbo.Cvs WHERE NOT (AnonymizedAt IS NOT NULL AND StoredPath = N'(da-xoa)');").Rows | Where-Object { !(Test-Path -LiteralPath (Join-Path $CvBaseDirectory $_.StoredPath.Replace('/', '\'))) }).Count
    Check 'CV files present' ($missing -eq 0) "$missing referenced CV file(s) not found. Full backups fail until this is 0 or each is approved (see docs)." $false
    $claimState = if ($SkipRestoreWorker) { 'no restore worker' } elseif ($GrantRestoreClaim) { 'will be installed (see planned change 5b)' }
        elseif ($claimInstalled) { 'already installed' } else { 'Not installed: add -GrantRestoreClaim, or restore drills and recoveries will not work.' }
    Check 'Restore ownership claim' ($claimInstalled -or $GrantRestoreClaim -or $SkipRestoreWorker) $claimState $false
    try {
        $services_ = Query 'master' "SELECT service_account AS A FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%';"
        if ($services_.Rows.Count) { $sqlServiceAccount = [string]$services_.Rows[0].A }
    } catch { }
    Check 'SQL service account' ([bool]$sqlServiceAccount) "$sqlServiceAccount (needs to write the temporary backup files)." $false
}
catch { Check 'SQL' $false "Cannot query $Server with the current account." }
$known = @()
if ($KnownMissingFile) {
    try { $known = Read-KnownMissingList $KnownMissingFile; Check 'Known-missing list' $true "$($known.Count) record(s) from $KnownMissingFile" }
    catch { Check 'Known-missing list' $false 'Not valid JSON.' }
}
else {
    try { $known = Get-ExistingKnownMissing $backupConfig; if ($known.Count) { Check 'Known-missing list' $true "$($known.Count) installed record(s) kept" } }
    catch { Check 'Known-missing list' $false 'The installed settings cannot be read; pass -KnownMissingFile explicitly.' ([bool]$Apply) }
}
$bitlocker = (New-Object -ComObject Shell.Application).NameSpace($drive).Self.ExtendedProperty('System.Volume.BitLockerProtection')
Check 'Source disk encryption (information)' ($bitlocker -eq 1) 'Not required: backups are encrypted before upload. The ORIGINAL database and CVs on this disk are not encrypted at rest.' $false

Write-Host "`n=== PLANNED CHANGES ==="
Write-Host "1. Publish the worker to $InstallDir"
Write-Host "2. Data folders $DataDir\{key,staging,restore} (SYSTEM/Administrators + the service accounts; SQL Server writes staging/restore)"
Write-Host "3. Services $backupService$(if (!$SkipRestoreWorker) { ", $restoreService" }) (virtual accounts, delayed auto start, restart on failure)"
Write-Host "4. Read access: CV storage and Web key ring -> $backupAccount; API key ring -> both services"
Write-Host "5. SQL logins/users with least privilege (backup: db_backupoperator + 5 CV columns + backup tables; restore: CREATE ANY DATABASE)"
if ($GrantRestoreClaim -and !$SkipRestoreWorker) {
    Write-Host "5b. master: procedure SCApp_ClaimRestoredDatabase signed by a certificate whose login has CONTROL SERVER and whose"
    Write-Host "    private key is deleted at once. $restoreAccount may only EXECUTE it, and only for a SmartCv_Restore_ database it restored."
}
Write-Host "6. Event log source '$eventSource' (so the worker's errors are visible)"
Write-Host "7. Encryption key: kept if present; otherwise created interactively (shown ONCE; you store it offline)"
Write-Host $(if ($Start) { '8. Start the services' } else { '8. Services are NOT started (use -Start, or Start-Service later)' })

if (!$Apply) { Write-Host "`nPREVIEW ONLY. Blocking checks: $script:blocking. Nothing was changed."; exit $(if ($script:blocking) { 2 } else { 0 }) }
if ($script:blocking) { Write-Host "`nABORTED: $script:blocking blocking check(s). Nothing was changed."; exit 2 }

Write-Host "`n=== APPLY ==="
$services = @($backupService); if (!$SkipRestoreWorker) { $services += $restoreService }
$wasRunning = @()
foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        $wasRunning += $name
        Stop-Service -Name $name -Force
        (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(2))
    }
}
& dotnet publish (Join-Path $repo 'src\SmartCv.BackupWorker\SmartCv.BackupWorker.csproj') -c Release -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (see the messages above). The services were stopped and not changed.' }
foreach ($d in 'key', 'staging', 'restore') { New-Item -ItemType Directory -Force (Join-Path $DataDir $d) | Out-Null }

function Settings([string]$Role) {
    [ordered]@{
        ConnectionStrings = @{ SmartCvDb = $connectionString }
        DataProtection    = @{ KeysPath = $ApiKeysPath }
        BackupWorker      = [ordered]@{
            Role = $Role; CvBaseDirectory = $CvBaseDirectory; CvStorageRoot = 'Uploads/CV'; ApiKeysPath = $ApiKeysPath; WebKeysPath = $WebKeysPath
            StagingDirectory = (Join-Path $DataDir 'staging'); KeyDirectory = (Join-Path $DataDir 'key'); RestoreFilesRoot = (Join-Path $DataDir 'restore')
            StagingLimitMb = 4096; MinimumFreeSpaceMb = 2048; KnownMissingCvs = $known
        }
        Logging           = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.EntityFrameworkCore' = 'Warning'; 'System.Net.Http' = 'Warning' }
            EventLog = @{ LogLevel = @{ Default = 'Warning'; 'SCApp.Backup' = 'Information' } } }
    } | ConvertTo-Json -Depth 6
}
[IO.File]::WriteAllText($backupConfig, (Settings 'backup'), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($restoreConfig, (Settings 'restore'), [Text.UTF8Encoding]::new($false))

$configs = @{ $backupService = $backupConfig; $restoreService = $restoreConfig }
foreach ($name in $services) {
    $existing = Get-CimInstance Win32_Service -Filter "Name='$name'"
    $verb = if ($existing) { 'config' } else { 'create' }
    $account = if ($existing -and $existing.StartName -eq "NT SERVICE\$name") { '' } else { "NT SERVICE\$name" }
    Invoke-Sc (Get-ServiceArguments $verb $name (Get-WorkerBinPath $exe $configs[$name]) $account)
    Invoke-Sc "failure $name reset= 86400 actions= restart/60000/restart/300000/restart/900000"
}
if (![Diagnostics.EventLog]::SourceExists($eventSource)) { New-EventLog -LogName Application -Source $eventSource }

function Grant([string]$Path, [string[]]$Arguments) {
    $output = & icacls.exe $Path @Arguments
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for ${Path}: $($output -join ' ')" }
}
$restoreRead = @(); if (!$SkipRestoreWorker) { $restoreRead = @("${restoreAccount}:(OI)(CI)RX") }
Grant $DataDir (@('/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "${backupAccount}:(OI)(CI)RX") + $restoreRead)
Grant (Join-Path $DataDir 'staging') @('/grant', "${backupAccount}:(OI)(CI)M")
if (!$SkipRestoreWorker) { Grant (Join-Path $DataDir 'restore') @('/grant', "${restoreAccount}:(OI)(CI)M") }
if ($sqlServiceAccount) {
    $sqlGrant = ConvertTo-GrantAccount $sqlServiceAccount
    Grant (Join-Path $DataDir 'staging') @('/grant', "${sqlGrant}:(OI)(CI)M")
    Grant (Join-Path $DataDir 'restore') @('/grant', "${sqlGrant}:(OI)(CI)M")
}
Grant (Join-Path $CvBaseDirectory 'Uploads\CV') @('/grant', "${backupAccount}:(OI)(CI)RX")
Grant $WebKeysPath @('/grant', "${backupAccount}:(OI)(CI)RX")
Grant $ApiKeysPath @('/grant', "${backupAccount}:(OI)(CI)RX")
if (!$SkipRestoreWorker) { Grant $ApiKeysPath @('/grant', "${restoreAccount}:(OI)(CI)RX") }

$restoreName = if ($SkipRestoreWorker) { $backupAccount } else { $restoreAccount }
$arguments = @('sql-grants', $Database, $backupAccount, $restoreName)
if ($GrantRestoreClaim -and !$SkipRestoreWorker) { $arguments += '--restore-claim' }
$grants = & $exe @arguments
if ($LASTEXITCODE -ne 0) { throw 'sql-grants failed' }
$batches = Split-SqlBatches $grants
if ($SkipRestoreWorker) { $batches = @($batches | Where-Object { $_ -notmatch 'CREATE ANY DATABASE' }) }
$sqlFile = Join-Path $DataDir 'sql-grants.sql'
($grants -replace "PASSWORD = N'[^']*'", "PASSWORD = N'<redacted>'") | Set-Content -LiteralPath $sqlFile -Encoding ascii
$c = [Data.SqlClient.SqlConnection]::new("Server=$Server;Database=master;Integrated Security=true;TrustServerCertificate=true"); $c.Open()
try {
    foreach ($batch in $batches) { $cmd = $c.CreateCommand(); $cmd.CommandText = $batch; [void]$cmd.ExecuteNonQuery() }
}
finally { $c.Dispose() }
Write-Host "SQL grants applied (copy: $sqlFile)"

if (!(Test-Path -LiteralPath (Join-Path $DataDir 'key\backup-key.dat'))) {
    $keyArguments = @('init-key', '--config', $backupConfig)
    if ($ImportKey) { $keyArguments += '--import'; Write-Host "`nImporting the saved recovery key:" }
    else { Write-Host "`nCreating the encryption key (store the shown key OFFLINE):" }
    & $exe @keyArguments
    if ($LASTEXITCODE -ne 0) { throw 'init-key failed' }
}
else { Write-Host "`nExisting encryption key kept." }

if ($Start) { foreach ($name in $services) { Start-Service -Name $name }; Start-Sleep -Seconds 10 }
Get-Service -Name $services | Format-Table Name, Status
if (!$Start -and $wasRunning.Count) { Write-Host "NOTE: these services were running and are now STOPPED: $($wasRunning -join ', '). Start-Service when ready." }
Write-Host "Check readiness (as administrator, after the services have run for a minute): & '$exe' check --config '$backupConfig'"
Write-Host "                                                               restore role: & '$exe' check --config '$restoreConfig'"
Write-Host 'Then open /admin/backups.'
