# 自己テストを実行して結果を表示する。時間内に終わらなければ強制終了して TIMEOUT と表示
param([int]$TimeoutSec = 180, [string]$Config = "Debug")
$exe = Join-Path $PSScriptRoot "..\src\VCast.App\bin\$Config\net8.0-windows\VCastStudio.exe"
$start = Get-Date
$p = Start-Process $exe -ArgumentList "--selftest" -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Stop-Process -Id $p.Id -Force
    Write-Output "TIMEOUT (killed after $TimeoutSec s)"
} else {
    Write-Output "exit=$($p.ExitCode)"
}
$log = Get-ChildItem "$env:APPDATA\VCastStudio-selftest\logs\*.log" | Where-Object { $_.LastWriteTime -ge $start } | Sort-Object LastWriteTime | Select-Object -First 1
if (-not $log) { Write-Output "no log"; exit 1 }
Write-Output "log: $($log.FullName)"
Get-Content $log.FullName -Encoding UTF8 | Select-String "\[selftest\]|UI-ERROR|CRASH|ui-task|shutdown ok" | ForEach-Object { $_.Line }
