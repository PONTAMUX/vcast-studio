# 自己テストを VCAST_SNAP=<名前> 付きで実行し、ログに "[selftest] SNAP <名前>" が出た瞬間にメインウィンドウを撮影する
param([Parameter(Mandatory)][string]$Snap, [string]$Out, [int]$TimeoutSec = 180, [string]$Config = "Debug", [string]$Title = "")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SnapWin { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
public static IntPtr Find(string part) { IntPtr found = IntPtr.Zero; EnumWindows((h,l)=>{ var sb=new System.Text.StringBuilder(256); GetWindowText(h,sb,256); if(sb.ToString().Contains(part)){found=h;return false;} return true;}, IntPtr.Zero); return found; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public struct RECT { public int L,T,R,B; } }
"@
$exe = Join-Path $PSScriptRoot "..\src\VCast.App\bin\$Config\net8.0-windows\VCastStudio.exe"
if (-not $Out) { $Out = Join-Path $env:TEMP "vcast-$Snap.png" }
$env:VCAST_SNAP = $Snap
$start = Get-Date
$p = Start-Process $exe -ArgumentList "--selftest" -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSec); $shot = $false
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep -Milliseconds 300
    $log = Get-ChildItem "$env:APPDATA\VCastStudio-selftest\logs\*.log" | Where-Object { $_.LastWriteTime -ge $start } | Sort-Object LastWriteTime | Select-Object -First 1
    if ($log -and ((Get-Content $log.FullName -Encoding UTF8 -Raw) -match "SNAP $Snap")) {
        Start-Sleep -Milliseconds 1500
        $p.Refresh(); $h = $p.MainWindowHandle
        if ($Title) { $h = [SnapWin]::Find($Title) }
        $r = New-Object SnapWin+RECT; [SnapWin]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R-$r.L), ($r.B-$r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [SnapWin]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
        $bmp.Save($Out); $g.Dispose(); $bmp.Dispose(); $shot = $true
        break
    }
}
Remove-Item Env:VCAST_SNAP
if (-not $p.WaitForExit([Math]::Max(1000, ($deadline - (Get-Date)).TotalMilliseconds))) { Stop-Process -Id $p.Id -Force; Write-Output "TIMEOUT" } else { Write-Output "exit=$($p.ExitCode)" }
Write-Output "snap=$shot $Out"
if ($log) { Get-Content $log.FullName -Encoding UTF8 | Select-String "\[selftest\] (FAIL|DONE)|\[avatar|UI-ERROR|CRASH|shutdown ok" | ForEach-Object { $_.Line } }
