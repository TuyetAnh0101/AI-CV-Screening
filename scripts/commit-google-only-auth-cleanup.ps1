[CmdletBinding()]
param([switch]$ReviewedAcceptance)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Chỉ người dùng tự chạy sau khi đọc báo cáo và xử lý/chấp nhận các mục chưa xác minh.
if (-not $ReviewedAcceptance) {
    throw 'Bàn giao PARTIAL. Đọc docs/google-only-auth-cleanup-report.md và hoàn tất nghiệm thu trước khi dùng -ReviewedAcceptance.'
}

$expectedRoot = 'C:/TTCN/project/AI-CV-Screening'
$expectedBranch = 'chore/google-only-auth-cleanup'
$expectedBase = '2b2c514b66b34c2cd378fc7e17f3b83e34fb7add'
$expectedFiles = @(
    'docs/google-only-auth-cleanup-report.md'
    'scripts/commit-google-only-auth-cleanup.ps1'
    'src/SmartCv.Api/Auth/ExternalLoginService.cs'
    'src/SmartCv.Core/Constants/SignupSources.cs'
    'src/SmartCv.Web/Components/Account/AccountPage.razor'
    'src/SmartCv.Web/Components/Account/LoginMethodsCard.razor'
    'src/SmartCv.Web/Components/Pages/Users.razor'
    'src/SmartCv.Web/Components/Shared/ExternalLoginButtons.razor'
    'src/SmartCv.Web/Helpers/ExternalAuthUiRules.cs'
    'src/SmartCv.Web/Helpers/UserText.cs'
    'src/SmartCv.Web/wwwroot/css/b55-external.css'
    'tests/SmartCv.Tests/ExternalAuth/A58EndpointTests.cs'
    'tests/SmartCv.Tests/ExternalAuth/A58LinkTests.cs'
    'tests/SmartCv.Tests/E2E/B57BrowserTests.cs'
    'tests/SmartCv.Tests/GoogleOnlyAuthUiTests.cs'
)

function Invoke-CheckedGit {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = @(& git -c "safe.directory=$expectedRoot" @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Git thất bại (exit $LASTEXITCODE). Dừng script." }
    return $result
}

Push-Location -LiteralPath $expectedRoot
try {
    $root = (Invoke-CheckedGit -Arguments @('rev-parse', '--show-toplevel')) -join ''
    if ($root.Replace('\', '/').TrimEnd('/') -ine $expectedRoot) { throw 'Sai repository.' }
    $origin = (Invoke-CheckedGit -Arguments @('remote', 'get-url', 'origin')) -join ''
    $allowedOrigins = @('https://github.com/TuyetAnh0101/AI-CV-Screening.git',
        'https://github.com/TuyetAnh0101/AI-CV-Screening', 'git@github.com:TuyetAnh0101/AI-CV-Screening.git')
    if ($origin -cnotin $allowedOrigins) { throw 'Origin không khớp repository đã nghiệm thu. Không in URL có thể chứa credentials.' }
    $pushOrigins = @(Invoke-CheckedGit -Arguments @('remote', 'get-url', '--push', '--all', 'origin'))
    if ($pushOrigins.Count -ne 1) { throw 'Origin phải có một push URL duy nhất đã nghiệm thu.' }
    $pushOrigin = $pushOrigins[0]
    if ($pushOrigin -cnotin $allowedOrigins) { throw 'Push URL không khớp repository đã nghiệm thu.' }
    if (((Invoke-CheckedGit -Arguments @('branch', '--show-current')) -join '') -cne $expectedBranch) { throw 'Sai nhánh.' }
    if (((Invoke-CheckedGit -Arguments @('rev-parse', 'HEAD')) -join '') -cne $expectedBase) { throw 'HEAD đã thay đổi; phải rà soát lại base và diff.' }
    foreach ($marker in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-merge', 'rebase-apply', 'sequencer')) {
        $markerPath = (Invoke-CheckedGit -Arguments @('rev-parse', '--git-path', $marker)) -join ''
        if (Test-Path -LiteralPath $markerPath) { throw 'Có thao tác Git dở dang.' }
    }
    $stagedBefore = @(Invoke-CheckedGit -Arguments @('diff', '--cached', '--name-only'))
    if ($stagedBefore.Count -gt 0) { throw 'Index đã có staged changes. Script không sửa hoặc bỏ stage thay đổi có sẵn.' }
    $tracked = @(Invoke-CheckedGit -Arguments @('diff', '--name-only', $expectedBase, '--'))
    $untracked = @(Invoke-CheckedGit -Arguments @('ls-files', '--others', '--exclude-standard'))
    $actualFiles = @(@($tracked + $untracked) | Sort-Object -Unique)
    if ($actualFiles.Count -eq 0) { throw 'Không có thay đổi. Không tạo commit rỗng.' }
    $scopeDifference = @(Compare-Object -ReferenceObject ($expectedFiles | Sort-Object) -DifferenceObject $actualFiles)
    if ($scopeDifference.Count -gt 0) {
        $scopeDifference | Format-Table | Out-Host
        throw 'Danh sách thay đổi không khớp báo cáo. Dừng trước khi stage.'
    }
    $sensitivePath = '(?i)(^|/)(\.env($|\.)|secrets?($|\.)|appsettings\.(Production|Local|BackupWorker)\.json|key-[^/]+\.xml)|\.(bak|mdf|ldf|trn|pfx|pem|key|dll|zip|pdf|docx)$|(^|/)(Uploads|mail-pickup|\.mail-dev|\.mail-keys)/'
    foreach ($file in $actualFiles) {
        if ($file -match $sensitivePath) { throw "Phát hiện đường dẫn nhạy cảm: $file" }
    }
    Invoke-CheckedGit -Arguments @('diff', '--check') | Out-Host
    foreach ($file in $expectedFiles) {
        Invoke-CheckedGit -Arguments @('add', '--', $file) | Out-Host
    }
    $stagedFiles = @(Invoke-CheckedGit -Arguments @('diff', '--cached', '--name-only'))
    if (@(Compare-Object ($expectedFiles | Sort-Object) ($stagedFiles | Sort-Object)).Count -gt 0) { throw 'Staged changes ngoài phạm vi.' }
    Invoke-CheckedGit -Arguments @('diff', '--cached', '--check') | Out-Host
    $stagedDiff = @(Invoke-CheckedGit -Arguments @('diff', '--cached', '--no-ext-diff', '--no-color'))
    $addedLines = @($stagedDiff | Where-Object { $_.StartsWith('+') -and -not $_.StartsWith('+++') })
    $credentialPattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|AIza[0-9A-Za-z_-]{35}|ya29\.[0-9A-Za-z_-]{20,}|1//[0-9A-Za-z_-]{30,}|gh[pousr]_[0-9A-Za-z]{30,}|github_pat_[0-9A-Za-z_]{30,}|eyJ[0-9A-Za-z_-]{15,}\.[0-9A-Za-z_-]{15,}\.[0-9A-Za-z_-]{15,}'
    if ($addedLines | Where-Object { $_ -match $credentialPattern }) {
        throw 'Có nội dung giống credential trong staged diff. Không in giá trị. Kiểm tra cục bộ trước khi tiếp tục.'
    }
    Invoke-CheckedGit -Arguments @('status', '--short', '--branch') | Out-Host
    Invoke-CheckedGit -Arguments @('diff', '--cached', '--name-status') | Out-Host
    Invoke-CheckedGit -Arguments @('diff', '--cached', '--stat') | Out-Host
    $stagedDiff | Out-Host
    Write-Host 'Bộ dò tự động chỉ hỗ trợ kiểm tra. Tự xem toàn bộ staged diff, secrets và hạn chế trong báo cáo.'
    $answer = Read-Host 'Chỉ nhập COMMIT-AND-PUSH khi đã nghiệm thu và đồng ý commit/push các file trên'
    if ($answer -cne 'COMMIT-AND-PUSH') { throw 'Chưa xác nhận. Không commit/push; giữ staged changes để bạn xem.' }
    $currentDiff = @(Invoke-CheckedGit -Arguments @('diff', '--cached', '--no-ext-diff', '--no-color'))
    if (($currentDiff -join "`n") -cne ($stagedDiff -join "`n")) { throw 'Staged diff đã thay đổi sau khi xem; cần rà soát lại.' }
    $unstagedAfterReview = @(Invoke-CheckedGit -Arguments @('diff', '--name-only'))
    $untrackedAfterReview = @(Invoke-CheckedGit -Arguments @('ls-files', '--others', '--exclude-standard'))
    if ($unstagedAfterReview.Count -gt 0 -or $untrackedAfterReview.Count -gt 0) { throw 'Working tree thay đổi sau khi stage; cần rà soát lại.' }
    if (((Invoke-CheckedGit -Arguments @('branch', '--show-current')) -join '') -cne $expectedBranch) { throw 'Nhánh đã thay đổi.' }
    if (((Invoke-CheckedGit -Arguments @('rev-parse', 'HEAD')) -join '') -cne $expectedBase) { throw 'HEAD đã thay đổi.' }
    Invoke-CheckedGit -Arguments @('diff', '--cached', '--check') | Out-Host
    Invoke-CheckedGit -Arguments @('commit', '-m', 'chore(auth): retain Google-only external authentication and remove unused provider code') | Out-Host
    # Invoke-CheckedGit dừng ngay nếu commit thất bại; không có force push hoặc merge.
    if (((Invoke-CheckedGit -Arguments @('remote', 'get-url', '--push', '--all', 'origin')) -join '') -cne $pushOrigin) { throw 'Push URL đã thay đổi. Không push.' }
    Invoke-CheckedGit -Arguments @('push', '-u', 'origin', 'HEAD') | Out-Host
    Invoke-CheckedGit -Arguments @('status', '--short', '--branch') | Out-Host
}
finally { Pop-Location }
