using System.Runtime.InteropServices;
using System.Windows;

namespace VCast.Ui;

// モニターの一覧（WPF 座標＝DIP に換算）。自己テストをサブディスプレイに出すのに使う
public static class Monitors
{
    public static Rect? Secondary()
    {
        var rects = new List<(RECT r, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT rc, IntPtr d) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi)) rects.Add((mi.rcWork, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        var sec = rects.FirstOrDefault(x => !x.primary);
        if (rects.Count < 2) return null;
        // 物理ピクセル → DIP（主モニターの DPI で近似）
        var scale = SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth / (double)(rects.First(x => x.primary).r.Right - rects.First(x => x.primary).r.Left) : 1.0;
        var r = sec.r;
        return new Rect(r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
}
