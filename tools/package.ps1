# 配布用フォルダ（dist\VCastStudio）と zip を作る。.NET も同梱するので相手の PC に何も入れなくてよい
#   使い方: powershell -ExecutionPolicy Bypass -File tools\package.ps1
param([string]$Version = "0.1.0", [switch]$SkipRuntime, [switch]$Draft)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$dist = Join-Path $root "dist\VCastStudio"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if (-not $SkipRuntime) {
    python (Join-Path $root "tools\make_runtime.py") "C:\Program Files\obs-studio" (Join-Path $root "runtime\obs-min")
    if ($LASTEXITCODE -ne 0) { throw "make_runtime.py が失敗しました" }
}

dotnet publish (Join-Path $root "src\VCast.App") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:SatelliteResourceLanguages=ja -p:Version=$Version -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish が失敗しました" }

# OBS エンジン（絞ったもの）
$rt = Join-Path $dist "runtime"
New-Item -ItemType Directory -Force $rt | Out-Null
Copy-Item (Join-Path $root "runtime\obs-min") (Join-Path $rt "obs") -Recurse
$obsDll = Join-Path $rt "obs\bin\64bit\obs.dll"
if (-not (Test-Path $obsDll)) { throw "OBS エンジンがコピーされていません" }

# 説明・ライセンス
Copy-Item (Join-Path $root "dist-files\*") $dist -Recurse -Force

# 公開して配る前の確認（GPL のため、ソースの入手先が未記入のまま配ってはいけない）。試作の zip は -Draft で作れる
$srcNote = Get-Content (Join-Path $dist "SOURCE.txt") -Raw -Encoding UTF8
$licGpl = Join-Path $rt "obs\licenses\OBS-gplv2.txt"
if (-not (Test-Path $licGpl)) { throw "GPL の本文が同梱されていません（tools\make_runtime.py を -SkipRuntime なしで実行してください）" }
if ($srcNote -match "ここへ") {
    if ($Draft) { Write-Warning "SOURCE.txt にソースの入手先が未記入です（試作版として作成。このまま公開・配布しないでください）" }
    else { throw "SOURCE.txt にソースの入手先が未記入です。記入するか、試作なら -Draft を付けてください" }
}

# 不要物（デバッグ情報）
Get-ChildItem $dist -Recurse -Include *.pdb | Remove-Item -Force

$zip = Join-Path $root "dist\VCastStudio-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $dist -DestinationPath $zip -CompressionLevel Optimal
$mb = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"folder: $dist ({0:N0} MB)" -f $mb
"zip: $zip ({0:N0} MB)" -f ((Get-Item $zip).Length / 1MB)
