# Pure helpers of Install-BackupWorker.ps1, kept apart so they can be tested without administrator rights.
# ASCII only: Windows PowerShell 5.1 reads a file without BOM as ANSI.

function Get-WorkerBinPath([string]$Exe, [string]$Config) { "`"$Exe`" run --config `"$Config`"" }

# The raw argument string for "sc.exe create|config". It must not go through PowerShell's own native-argument
# quoting: PowerShell 5.1 does not escape inner quotes, which registers "C:\Program" as the service binary.
function Get-ServiceArguments([ValidateSet('create', 'config')][string]$Verb, [string]$Name, [string]$BinPath, [string]$Account) {
    $escaped = $BinPath.Replace('"', '\"')
    # An existing service that already runs as the right account keeps its logon untouched.
    $logon = if ($Account) { " obj= `"$Account`"" } else { '' }
    "$Verb $Name binPath= `"$escaped`"$logon start= delayed-auto DisplayName= `"$Name`""
}

# A value of the installed settings file, or $null. Re-installing keeps what was chosen the first time.
function Get-InstalledValue($Config, [string]$Section, [string]$Name) {
    if ($null -eq $Config) { return $null }
    $part = $Config.PSObject.Properties[$Section]
    if (!$part) { return $null }
    $value = $part.Value.PSObject.Properties[$Name]
    if ($value) { return $value.Value }
    return $null
}

function Invoke-Sc([string]$Arguments, [string]$ScPath = (Join-Path $env:WINDIR 'System32\sc.exe')) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $ScPath
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($info)
    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        $words = $Arguments.Split(' ')
        throw "sc.exe $($words[0]) $($words[1]) failed with exit code $($process.ExitCode): $($output.Trim())"
    }
}

# Windows PowerShell 5.1 returns a JSON array from ConvertFrom-Json as ONE object; wrapping it in @() nests it and
# ConvertTo-Json then writes {"value":[...],"Count":n}, which the worker cannot read. Enumerate explicitly.
function ConvertTo-FlatList($Value) {
    $list = @()
    foreach ($item in $Value) {
        if ($null -eq $item) { continue }
        # Repairs a settings file written by the earlier installer.
        if ($item -is [psobject] -and $item.PSObject.Properties['value'] -and $item.PSObject.Properties['Count']) { $list += ConvertTo-FlatList $item.value }
        else { $list += $item }
    }
    return , $list
}

function Read-KnownMissingList([string]$Path) {
    $parsed = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    return , (ConvertTo-FlatList $parsed)
}

# Re-installing must not silently drop acknowledgements recorded earlier.
function Get-ExistingKnownMissing([string]$ConfigPath) {
    if (!(Test-Path -LiteralPath $ConfigPath)) { return , @() }
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $worker = $config.PSObject.Properties['BackupWorker']
    if (!$worker -or !$worker.Value.PSObject.Properties['KnownMissingCvs']) { return , @() }
    return , (ConvertTo-FlatList $worker.Value.KnownMissingCvs)
}

# icacls does not understand the ".\user" form SQL Server reports for local service accounts.
function ConvertTo-GrantAccount([string]$ServiceAccount) {
    if ($ServiceAccount -eq 'LocalSystem') { return '*S-1-5-18' }
    if ($ServiceAccount.StartsWith('.\')) { return "$env:COMPUTERNAME\$($ServiceAccount.Substring(2))" }
    return $ServiceAccount
}

# Splits the worker's "sql-grants" output on lines that contain only GO.
function Split-SqlBatches([string[]]$Lines) {
    $batches = @(); $current = New-Object System.Text.StringBuilder
    foreach ($line in $Lines) {
        if ($line -match '^GO\s*$') {
            if ($current.ToString().Trim()) { $batches += $current.ToString() }
            [void]$current.Clear()
        }
        else { [void]$current.AppendLine($line) }
    }
    if ($current.ToString().Trim()) { $batches += $current.ToString() }
    return , $batches
}
