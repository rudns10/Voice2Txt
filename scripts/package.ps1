<#
.SYNOPSIS
  Voice2Txt GUI를 self-contained로 게시하고 배포용 zip을 만든다.
  개발 환경(.NET SDK 등)이 없는 PC에서도 압축만 풀면 Voice2Txt.Gui.exe 더블클릭으로 실행 가능.

.PARAMETER Models
  zip에 동봉할 모델 키 목록. 예: small / medium / "small","medium" / none
  동봉하면 인터넷 없이 즉시 변환 가능(폐쇄망용, zip 커짐).
  기본값 none = 미포함(앱에서 '텍스트 변환' 누를 때 1회 다운로드).
  동봉할 경우 모델 파일은 %LOCALAPPDATA%\Voice2Txt\models 에서 가져오므로,
  없으면 먼저 앱에서 해당 모델로 1회 변환해 받아두세요.

.NOTES
  - .NET 런타임 + WindowsAppSDK + Whisper.net(Vulkan/CPU) 네이티브 포함.
  - 대상: Windows 10/11 x64.

.EXAMPLE
  pwsh scripts/package.ps1                       # 모델 미포함(첫 변환 시 다운로드) — 기본
  pwsh scripts/package.ps1 -Models small         # small 동봉(폐쇄망용)
  pwsh scripts/package.ps1 -Models small,medium  # 둘 다 동봉
#>
param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [string]$Version = "0.0.1",
    [string[]]$Models = @("none")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Voice2Txt.Gui\Voice2Txt.Gui.csproj"

$modelSrcDir = Join-Path $env:LOCALAPPDATA "Voice2Txt\models"

function Get-ModelFileName([string]$key) {
    switch ($key.Trim().ToLower()) {
        "small"  { "ggml-small-q5_1.bin" }
        "medium" { "ggml-medium-q5_0.bin" }
        default  { $null }
    }
}

Write-Host "[1/4] 게시(self-contained, $Rid, $Configuration)..." -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r $Rid --self-contained true -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

$pub = Join-Path $root "src\Voice2Txt.Gui\bin\$Configuration\net9.0-windows10.0.19041.0\$Rid\publish"
if (-not (Test-Path (Join-Path $pub "Voice2Txt.Gui.exe"))) { throw "게시 결과를 찾을 수 없습니다: $pub" }

# 이전 동봉 모델 정리
$destModels = Join-Path $pub "models"
if (Test-Path $destModels) { Remove-Item $destModels -Recurse -Force }

$bundled = @()
if ($Models -and ($Models -join "") -ne "none") {
    Write-Host "[2/4] 모델 동봉..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $destModels | Out-Null
    foreach ($m in $Models) {
        $fn = Get-ModelFileName $m
        if (-not $fn) { Write-Warning "  알 수 없는 모델: $m (small/medium)"; continue }
        $src = Join-Path $modelSrcDir $fn
        if (Test-Path $src) {
            Copy-Item $src $destModels -Force
            $bundled += $m.Trim().ToLower()
            $mb = [math]::Round((Get-Item $src).Length / 1MB, 0)
            Write-Host "  + $fn ($mb MB)" -ForegroundColor Green
        }
        else {
            Write-Warning "  모델 없음: $src — 앱에서 '$m' 모델로 1회 변환해 받은 뒤 다시 실행하세요."
        }
    }
}
else {
    Write-Host "[2/4] 모델 미포함 (첫 실행 시 자동 다운로드)" -ForegroundColor Cyan
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$suffix = if ($bundled.Count -gt 0) { "-with-" + ($bundled -join "-") } else { "" }
$zip = Join-Path $dist "Voice2Txt-$Version-$Rid$suffix.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "[3/4] 압축 -> $zip" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal

$sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "[4/4] 완료: $zip ($sizeMB MB)" -ForegroundColor Green
Write-Host ""
Write-Host "배포: zip 전달 -> 압축 해제 -> Voice2Txt.Gui.exe 실행" -ForegroundColor Yellow
if ($bundled.Count -gt 0) {
    Write-Host "모델 동봉됨($($bundled -join ', ')) → 인터넷 없이 즉시 변환 가능." -ForegroundColor Yellow
}
else {
    Write-Host "첫 변환 시 모델(약 190MB)이 1회 다운로드됩니다(인터넷 필요)." -ForegroundColor Yellow
}
