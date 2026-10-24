using System.Windows;
using System.Windows.Threading;
using VCast.Obs;

namespace VCast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => ObsEngine.Log("[FATAL] " + ev.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ev) => { ObsEngine.Log("[task-error] " + ev.Exception.GetBaseException()); ev.SetObserved(); };
        // VCast は 1 つだけ（2 つ目はブラウザ部品のデータを取り合って表示が壊れる）。既に開いていればそれを前に出す
        if (!ObsEngine.TestMode)
        {
            _single = new System.Threading.Mutex(true, "VCastStudio.SingleInstance", out var first);
            if (!first)
            {
                var h = FindWindow(null, "VCast Studio");
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                Shutdown(0);
                return;
            }
        }
        try
        {
            // fps は保存済み設定から（ObsEngine 起動前なので設定ファイルを直接読む）
            var fps = 60u;
            try
            {
                VCast.Core.Profiles.EnsureInit();
                var path = VCast.Core.Profiles.SettingsPath;
                if (File.Exists(path) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("Fps", out var f) && f.GetInt32() == 30) fps = 30;
            }
            catch { }
            ObsEngine.Start(fps: fps);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配信エンジンを起動できませんでした。\n\n{ex.Message}\n\nログ: {ObsEngine.LogPath}", "VCast Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // 起動中の印（正常終了で消す）。残っていれば前回は異常終了 → メイン画面のステータスで知らせる。シーンは 1 分ごとに自動保存されている
        try
        {
            var marker = Path.Combine(ObsEngine.ConfigDir, "running.marker");
            UncleanLastExit = File.Exists(marker);
            File.WriteAllText(marker, DateTime.Now.ToString("o"));
            if (UncleanLastExit) ObsEngine.Log("[startup] previous session did not exit cleanly");
        }
        catch { }
        // 自己テストはバックグラウンドで作業中のユーザーからフォーカスを奪わない
        var selfTest = e.Args.Contains("--selftest") || e.Args.Contains("--rectest") || e.Args.Contains("--soak");
        var main = new MainWindow { ShowActivated = !selfTest };
        if (selfTest)
        {
            // ゲームなどの邪魔をしないよう、優先度を下げてサブディスプレイに出す
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            if (VCast.Ui.Monitors.Secondary() is { } r)
            {
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Left = r.Left + 40; main.Top = r.Top + 40;
                main.Width = Math.Min(main.Width, r.Width - 80); main.Height = Math.Min(main.Height, r.Height - 80);
            }
        }
        main.Show();
    }

    System.Threading.Mutex? _single;
    [System.Runtime.InteropServices.DllImport("user32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);

    public static bool UncleanLastExit { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        try { File.Delete(Path.Combine(ObsEngine.ConfigDir, "running.marker")); } catch { }
        ObsEngine.Shutdown();
        base.OnExit(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ObsEngine.Log("[UI-ERROR] " + e.Exception);
        MessageBox.Show(e.Exception.Message, "VCast Studio エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
