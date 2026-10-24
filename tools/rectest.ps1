param([string]$Format = "mp4", [string]$Encoder = "auto")
$exe = Join-Path $PSScriptRoot "..\src\VCast.App\bin\Debug\net8.0-windows\VCastStudio.exe"
$start = Get-Date
$p = Start-Process $exe -ArgumentList "--rectest", $Format, $Encoder -PassThru
if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force; "TIMEOUT" } else { "exit=$($p.ExitCode)" }
$log = Get-ChildItem "$env:APPDATA\VCastStudio-selftest\logs\*.log" | Where-Object { $_.LastWriteTime -ge $start } | Sort-Object LastWriteTime | Select-Object -First 1
Get-Content $log.FullName -Encoding UTF8 | Select-String "\[rectest\]|\[output\]|shutdown ok" | ForEach-Object { $_.Line }
(Get-Content $log.FullName -Encoding UTF8 | Select-String "TS_SMOOTHING").Count
