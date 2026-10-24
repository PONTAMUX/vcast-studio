using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VCast.Core;

public sealed record WindowInfo(IntPtr Hwnd, string Title, string Class, string Exe, ImageSource? Icon, bool Minimized)
{
    public string Note => Minimized ? "最小化中は映りません（元に戻すと映ります）" : "";
    public double Dim => Minimized ? 0.55 : 1.0;
    // OBS win-capture の "window" 設定書式: タイトル:クラス:exe（'#'→"#22"、':'→"#3A"）
    public string ObsWindowId => $"{Enc(Title)}:{Enc(Class)}:{Enc(Exe)}";
    static string Enc(string s) => s.Replace("#", "#22").Replace(":", "#3A");
    public string Tooltip => $"{Title}\n{Exe}";
}

// タスクバーに並ぶのと同じ条件でトップレベルウィンドウを列挙する
public static class WindowList
{
    // アイコン取得はそのウィンドウへのメッセージ送信を伴う（ゲーム中だとカクつきの原因になり得る）ので一度だけ
    static readonly Dictionary<IntPtr, ImageSource?> IconCache = new();
    static readonly Dictionary<uint, string> ExeCache = new();

    public static List<WindowInfo> Enumerate(IntPtr exclude)
    {
        var result = new List<WindowInfo>();
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (h == exclude || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true; // 別デスクトップ・UWPの裏
                var title = Text(h);
                if (title.Length == 0) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;
                if (!ExeCache.TryGetValue(pid, out var exe))
                {
                    try { exe = Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { return true; }
                    ExeCache[pid] = exe;
                }
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                if (!IconCache.TryGetValue(h, out var icon)) { icon = Icon(h); IconCache[h] = icon; }
                result.Add(new WindowInfo(h, title, cls.ToString(), exe, icon, IsIconic(h)));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        // 閉じたウィンドウの分は捨てる
        if (IconCache.Count > 200) { var alive = result.Select(r => r.Hwnd).ToHashSet(); foreach (var k in IconCache.Keys.Where(k => !alive.Contains(k)).ToList()) IconCache.Remove(k); }
        if (ExeCache.Count > 300) ExeCache.Clear();
        return result;
    }

    static string Text(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static ImageSource? Icon(IntPtr h)
    {
        var icon = SendMessageTimeout(h, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out var r) != IntPtr.Zero ? r : IntPtr.Zero;
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, GCLP_HICON);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, GCLP_HICONSM);
        if (icon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    const int GW_OWNER = 4, GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, WM_GETICON = 0x7F, ICON_BIG = 1, GCLP_HICON = -14, GCLP_HICONSM = -34;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const uint SMTO_ABORTIFHUNG = 2;
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32")] static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
