# Tải dữ liệu ngôn ngữ cho OCR (Tesseract): tiếng Anh và tiếng Việt (A7).
# Chạy ở thư mục gốc repo:   powershell -ExecutionPolicy Bypass -File .\scripts\tai-tessdata.ps1
# File nặng khoảng 30 MB nên KHÔNG đưa lên GitHub (đã chặn trong .gitignore). Mỗi máy tự tải một lần.
# Thêm -Force để tải lại (ví dụ file cũ bị hỏng).

param([switch]$Force)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dest = Join-Path $PSScriptRoot '..\src\SmartCv.Api\tessdata'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

function Test-TrainedData([string]$path) {
    # File thật là dữ liệu nhị phân vài MB. Trang lỗi (mạng chặn, proxy) thường là HTML vài KB.
    if (-not (Test-Path $path)) { return $false }
    if ((Get-Item $path).Length -lt 1MB) { return $false }
    $head = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($path)[0..255])
    return -not ($head -match '<(!DOCTYPE|html)')
}

foreach ($lang in 'eng', 'vie') {
    $file = Join-Path $dest "$lang.traineddata"
    if (-not $Force -and (Test-TrainedData $file)) { Write-Host "Đã có $lang.traineddata, bỏ qua."; continue }

    $url = "https://github.com/tesseract-ocr/tessdata/raw/HEAD/$lang.traineddata"
    $tmp = "$file.download"
    Write-Host "Đang tải $url ..."
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        if (-not (Test-TrainedData $tmp)) { throw "File tải về không phải dữ liệu OCR (quá nhỏ hoặc là trang HTML)." }
        Move-Item -Force $tmp $file
    }
    finally {
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
    }
    Write-Host ("  Xong: {0:N1} MB" -f ((Get-Item $file).Length / 1MB))
}
Write-Host "Hoàn tất. Dữ liệu OCR nằm ở: $((Resolve-Path $dest).Path)"
