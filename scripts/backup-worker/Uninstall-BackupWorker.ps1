# Stops and removes the backup worker services. DEFAULT = PREVIEW; -Apply requires "Run as administrator".
# Keeps the data folder (the encryption keys!), settings, SQL users and every backup on Google Drive.
[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallerFunctions.ps1')
$services = 'SCAppBackupWorker', 'SCAppRestoreWorker'
Write-Host 'Would stop and delete services: SCAppBackupWorker, SCAppRestoreWorker.'
Write-Host 'Keeps: C:\ProgramData\SCApp\Backup (deleting a key file without an offline copy makes backups unreadable), SQL users, Drive files.'
Write-Host 'The restore ownership claim stays in master until a DBA removes it:'
Write-Host '  DROP PROCEDURE dbo.SCApp_ClaimRestoredDatabase; DROP LOGIN SCAppRestoreClaimSigner; DROP CERTIFICATE SCAppRestoreClaimCert;'
if (!$Apply) { Write-Host 'PREVIEW ONLY.'; exit 0 }
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run as administrator.'; exit 2 }
foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (!$service) { Write-Host "$name is not installed"; continue }
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $name -Force; (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(2)) }
    Invoke-Sc "delete $name"
    Write-Host "removed $name"
}
