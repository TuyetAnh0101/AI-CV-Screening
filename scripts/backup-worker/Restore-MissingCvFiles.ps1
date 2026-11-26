# Finds CV files the database refers to but CV storage does not have, and copies exact (SHA-256 equal) originals
# back from another folder, for example the Uploads of another working copy of the project.
#   .\Restore-MissingCvFiles.ps1 -SourceBase C:\path\to\other\src\SmartCv.Api          check only (default)
#   .\Restore-MissingCvFiles.ps1 -SourceBase C:\path\to\other\src\SmartCv.Api -Apply   copy
# Only SELECTs from the database (id, path, hash, size; no names). Never overwrites, moves or deletes a file and
# never changes a database row. ASCII only: Windows PowerShell 5.1 reads a file without BOM as ANSI.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$SourceBase,
    [string]$TargetBase,
    [string]$Server = 'localhost\BB',
    [string]$Database = 'SmartCvDb',
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$TargetBase) { $TargetBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\SmartCv.Api')) }
$TargetBase = [IO.Path]::GetFullPath($TargetBase)
$targetRoot = [IO.Path]::GetFullPath((Join-Path $TargetBase 'Uploads\CV')) + '\'

Add-Type -AssemblyName System.Data
$connection = [Data.SqlClient.SqlConnection]::new("Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly")
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT CvId,StoredPath,LOWER(FileHash) AS FileHash,FileSizeBytes FROM dbo.Cvs WHERE NOT (AnonymizedAt IS NOT NULL AND StoredPath = N'(da-xoa)')"
    $rows = [Data.DataTable]::new(); $rows.Load($command.ExecuteReader())
}
finally { $connection.Dispose() }

function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$present = 0; $copyable = 0; $copied = 0; $notFound = 0; $unsafe = 0; $differs = 0
foreach ($row in $rows.Rows) {
    $relative = ([string]$row.StoredPath).Replace('/', '\')
    $target = [IO.Path]::GetFullPath((Join-Path $TargetBase $relative))
    if (!$target.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) { $unsafe++; Write-Host "UNSAFE PATH  CvId=$($row.CvId)"; continue }
    if (Test-Path -LiteralPath $target) {
        if ((Get-Item -LiteralPath $target).Length -eq $row.FileSizeBytes -and (Get-Sha $target) -eq $row.FileHash) { $present++ }
        else { $differs++; Write-Host "DIFFERS      CvId=$($row.CvId) (left untouched: review it by hand)" }
        continue
    }
    $source = $null
    foreach ($base in $SourceBase) {
        $candidate = Join-Path ([IO.Path]::GetFullPath($base)) $relative
        if ((Test-Path -LiteralPath $candidate) -and (Get-Item -LiteralPath $candidate).Length -eq $row.FileSizeBytes -and (Get-Sha $candidate) -eq $row.FileHash) { $source = $candidate; break }
    }
    if (!$source) { $notFound++; Write-Host "NOT FOUND    CvId=$($row.CvId)"; continue }
    $copyable++
    if (!$Apply) { Write-Host "WOULD COPY   CvId=$($row.CvId)"; continue }
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    [IO.File]::Copy($source, $target, $false)
    if ((Get-Sha $target) -ne $row.FileHash) { throw "Copy of CvId=$($row.CvId) does not match its hash; stop and review $target" }
    $copied++; Write-Host "COPIED       CvId=$($row.CvId)"
}
Write-Host "`nrows=$($rows.Rows.Count) intact=$present copyable=$copyable copied=$copied notFound=$notFound differs=$differs unsafe=$unsafe"
if (!$Apply -and $copyable) { Write-Host 'CHECK ONLY. Re-run with -Apply to copy. Source files are never moved or deleted.' }
exit $(if ($notFound + $differs + $unsafe + ($copyable - $copied) -gt 0) { 2 } else { 0 })
