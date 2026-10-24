# 長時間の連続運転テスト。VCast から本物の RTMP でこの PC 内の受信口へ配信し続け、メモリ・コマ落ち・音映像のズレを記録する
#   使い方: powershell -ExecutionPolicy Bypass -File tools\soak.ps1 -Minutes 30
param([int]$Minutes = 5, [string]$Config = "Debug")
$exe = Join-Path $PSScriptRoot "..\src\VCast.App\bin\$Config\net8.0-windows\VCastStudio.exe"
$start = Get-Date
$p = Start-Process $exe -ArgumentList "--soak", $Minutes -PassThru
$p.PriorityClass = "BelowNormal"
if (-not $p.WaitForExit(($Minutes + 5) * 60 * 1000)) { Stop-Process -Id $p.Id -Force; "TIMEOUT" } else { "exit=$($p.ExitCode)" }
$log = Get-ChildItem "$env:APPDATA\VCastStudio-selftest\logs\*.log" | Where-Object { $_.LastWriteTime -ge $start } | Sort-Object LastWriteTime | Select-Object -First 1
Get-Content $log.FullName -Encoding UTF8 | Select-String "\[soak\]|CRASH|UI-ERROR" | ForEach-Object { $_.Line }
