using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VCast.Obs;

// libobs のライフサイクル管理。起動は1プロセス1回。
public static unsafe class ObsEngine
{
    // obs.dll のみ依存 or frontend-api を呼ぶだけで Qt ウィジェットを作らないもの（Fable review 01）
    static readonly HashSet<string> AllowedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "image-source", "obs-ffmpeg", "obs-filters", "obs-nvenc", "obs-qsv11", "obs-outputs",
        "obs-text", "obs-transitions", "obs-webrtc", "obs-x264", "rtmp-services", "text-freetype2",
        "win-capture", "win-dshow", "win-wasapi", "nv-filters", "obs-browser",
    };

    public static string RuntimeDir { get; private set; } = "";
    // 自己テストは別フォルダ（ユーザーの設定・保存データ・ブラウザのキャッシュと取り合わない）
    public static bool TestMode { get; } = Environment.GetCommandLineArgs().Any(a => a is "--selftest" or "--rectest" or "--soak");
    public static string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), TestMode ? "VCastStudio-selftest" : "VCastStudio");
    public static string LogPath { get; private set; } = "";
    public static uint BaseWidth { get; private set; } = 1920;
    public static uint BaseHeight { get; private set; } = 1080;
    public static bool Started { get; private set; }
    public static List<string> LoadedModules { get; } = new();

    static StreamWriter? _log;
    static readonly object LogLock = new();

    // ブラウザ部品（CEF）は常に数プロセス・約 240MB 使うので、アバターやブラウザソースを使うときまで読み込まない
    static string? _onlyModule;
    static bool _loadBrowser;
    public static bool BrowserLoaded => LoadedModules.Contains("obs-browser");

    // 起動時に読むか: 保存済みのシーンにブラウザソース（アバター含む）があるとき
    static bool SavedScenesUseBrowser()
    {
        try
        {
            VCast.Core.Profiles.EnsureInit();
            foreach (var f in new[] { VCast.Core.Profiles.ScenesPath, VCast.Core.Profiles.ScenesPath + ".bak" })
                if (File.Exists(f) && File.ReadAllText(f).Contains("\"browser_source\"")) return true;
            return false;
        }
        catch { return true; } // 判断できないときは読み込んでおく（表示が消えるよりまし）
    }

    // 後から読み込む（一度読むと終了まで残る）。失敗したら false
    public static bool EnsureBrowser()
    {
        if (BrowserLoaded) return true;
        if (!Started) return false;
        _loadBrowser = true; _onlyModule = "obs-browser";
        try
        {
            Native.obs_find_modules2(&OnFindModule, IntPtr.Zero);
            if (!BrowserLoaded) return false;
            // 後処理（obs_module_post_load）を持つのは許可モジュールのうち obs-browser だけ（実 DLL のエクスポートで確認済み）。
            // obs_post_load_modules は全モジュールの post_load を呼ぶが、他に無いので二重に走るものは無い。許可モジュールを増やすときは要再確認
            Native.obs_post_load_modules();
            Log("[module] obs-browser loaded on demand");
            return true;
        }
        finally { _onlyModule = null; }
    }

    public static void Start(uint width = 1920, uint height = 1080, uint fps = 60)
    {
        _loadBrowser = SavedScenesUseBrowser() || Environment.GetEnvironmentVariable("VCAST_BROWSER") == "1";
        RuntimeDir = LocateRuntime();
        var bin = Path.Combine(RuntimeDir, "bin", "64bit");
        // 自己テスト・連続運転テストは毎回まっさらな設定から始める（前回のテストが残した設定に左右されないように。ユーザーの設定フォルダには触れない）
        if (TestMode)
        {
            foreach (var f in new[] { "settings.json", "avatar.json", "hotkeys.json", "soundboard.json", "profile.txt" })
                try { File.Delete(Path.Combine(ConfigDir, f)); } catch { }
            foreach (var d in new[] { "profiles", "scenes" })
                try { Directory.Delete(Path.Combine(ConfigDir, d), true); } catch { }
        }
        Directory.CreateDirectory(Path.Combine(ConfigDir, "logs"));
        Directory.CreateDirectory(Path.Combine(ConfigDir, "plugin_config"));
        LogPath = Path.Combine(ConfigDir, "logs", $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _log = new StreamWriter(LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };

        // libobs 内部の LoadLibrary / 相対パス解決のため DLL 検索先と cwd を OBS bin に合わせる
        Native.SetDefaultDllDirectories(Native.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        Native.AddDllDirectory(bin);
        Native.AddDllDirectory(Path.Combine(RuntimeDir, "obs-plugins", "64bit"));
        Directory.SetCurrentDirectory(bin);
        NativeLibrary.Load(Path.Combine(bin, "obs.dll"));
        CopyEncoderTestTools(bin);

        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Native.base_set_log_handler(&OnLog, IntPtr.Zero);
        Native.base_set_crash_handler(&OnCrash, IntPtr.Zero);

        Log($"VCast Studio / libobs {Marshal.PtrToStringUTF8(Native.obs_get_version_string())} / runtime {RuntimeDir}");
        // OBS 本体の main() と同じく RTWQ を起動（win-wasapi が MMCSS "Capture" の作業キューを使う）
        _rtwq = TryRtwq(startup: true);
        if (!Native.obs_startup("ja-JP", Path.Combine(ConfigDir, "plugin_config"), IntPtr.Zero))
            throw new InvalidOperationException("obs_startup に失敗しました");
        _coreStarted = true;
        Native.obs_set_ui_task_handler(&OnUiTask);
        try { StartCore(width, height, fps); }
        catch
        {
            // 途中で失敗しても libobs とログを確実に閉じる
            Shutdown();
            throw;
        }
    }

    static void StartCore(uint width, uint height, uint fps)
    {
        Native.obs_add_data_path(Path.Combine(RuntimeDir, "data", "libobs") + "\\");
        Native.obs_add_module_path(Path.Combine(RuntimeDir, "obs-plugins", "64bit"),
                                   Path.Combine(RuntimeDir, "data", "obs-plugins", "%module%"));

        var priv = Native.obs_data_create();
        Native.obs_data_set_bool(priv, "BrowserHWAccel", true);
        Native.obs_apply_private_data(priv);
        Native.obs_data_release(priv);

        var oai = new Native.obs_audio_info { samples_per_sec = 48000, speakers = Native.SPEAKERS_STEREO };
        if (!Native.obs_reset_audio(ref oai)) throw new InvalidOperationException("音声の初期化に失敗しました");

        var ovi = new Native.obs_video_info
        {
            graphics_module = "libobs-d3d11",
            fps_num = fps, fps_den = 1,
            base_width = width, base_height = height, output_width = width, output_height = height,
            output_format = Native.VIDEO_FORMAT_NV12, adapter = 0, gpu_conversion = true,
            colorspace = Native.VIDEO_CS_709, range = Native.VIDEO_RANGE_PARTIAL, scale_type = Native.OBS_SCALE_BICUBIC,
        };
        var rv = Native.obs_reset_video(ref ovi);
        if (rv != 0) throw new InvalidOperationException($"映像の初期化に失敗しました (code {rv})");
        BaseWidth = width; BaseHeight = height;

        try { VCast.AvatarNative.NativeAvatarSource.Register(); }
        catch (Exception ex) { Log("[avatar-native] register failed: " + ex.Message); }
        Native.obs_find_modules2(&OnFindModule, IntPtr.Zero);
        Native.obs_post_load_modules();
        Native.obs_log_loaded_modules();
        Started = true;
    }

    public static void Shutdown()
    {
        if (!_coreStarted) return;
        _coreStarted = false;
        Started = false;
        _shuttingDown = true;
        Native.obs_set_output_source(0, IntPtr.Zero);
        // ソース破棄は非同期キュー。残ったまま obs_shutdown するとモジュール解放後に destroy が走り落ちる
        while (Native.obs_wait_for_destroy_queue()) { }
        Native.obs_shutdown();
        if (_rtwq) TryRtwq(startup: false);
        Log("shutdown ok");
        lock (LogLock) { _log?.Dispose(); _log = null; }
    }

    static bool _coreStarted, _rtwq;
    static volatile bool _shuttingDown;
    // 終了処理の開始（出力やソースの解放より前に呼ぶ）。以降の UI タスクは UI スレッドを待たずその場で実行する
    public static void BeginTeardown() => _shuttingDown = true;
    static System.Windows.Threading.Dispatcher? _ui;

    // UIタスク: 通常は WPF の UI スレッドで実行。終了処理中は UI スレッドが obs_shutdown で塞がっているので
    // 待ち合わせ付きの依頼はその場で実行してデッドロックを避ける
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnUiTask(IntPtr task, IntPtr param, byte wait)
    {
        try
        {
            var run = (delegate* unmanaged[Cdecl]<IntPtr, void>)task;
            var ui = _ui;
            if (ui == null || ui.CheckAccess() || _shuttingDown) { run(param); return; }
            if (wait != 0) ui.Invoke(() => run(param));
            else ui.BeginInvoke(() => run(param));
        }
        catch (Exception ex) { Log("[ui-task] " + ex.Message); }
    }

    [DllImport("RTWorkQ")] static extern int RtwqStartup();
    [DllImport("RTWorkQ")] static extern int RtwqShutdown();

    static bool TryRtwq(bool startup)
    {
        try
        {
            var hr = startup ? RtwqStartup() : RtwqShutdown();
            if (hr < 0) Log($"RTWQ {(startup ? "startup" : "shutdown")} failed 0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex) { Log("RTWQ unavailable: " + ex.Message); return false; }
    }

    public static IEnumerable<string> InputTypes()
    {
        for (nuint i = 0; Native.obs_enum_input_types(i, out var id); i++)
            yield return Marshal.PtrToStringUTF8(id)!;
    }

    // ログに書く前に伏せ字にする値（ストリームキーなど。サーバーの応答にキーが含まれることがある）
    static readonly HashSet<string> Secrets = new();
    public static void AddSecret(string value) { if (value.Length >= 12) lock (LogLock) Secrets.Add(value); }

    public static void Log(string msg)
    {
        lock (LogLock)
        {
            foreach (var s in Secrets) if (msg.Contains(s)) msg = msg.Replace(s, "****");
            _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnFindModule(IntPtr param, Native.obs_module_info2* info)
    {
        try { LoadModule(info); } catch (Exception ex) { Log("[module] " + ex.Message); }
    }

    static void LoadModule(Native.obs_module_info2* info)
    {
        var name = Marshal.PtrToStringUTF8((IntPtr)info->name) ?? "";
        if (_onlyModule != null && name != _onlyModule) return; // 後読み込み中は対象だけ
        if (LoadedModules.Contains(name)) return; // 既定パスと追加パスで同じフォルダが二重に列挙される
        if (name == "obs-browser" && !_loadBrowser) { Log("[module] obs-browser deferred (loaded when first needed)"); return; }
        if (!AllowedModules.Contains(name)) { Log($"[module] skip (not allowed): {name}"); return; }
        if (Native.obs_open_module(out var module, info->bin_path, info->data_path) != 0)
        { Log($"[module] open failed: {name}"); return; }
        if (!Native.obs_init_module(module)) { Log($"[module] init failed: {name}"); return; }
        LoadedModules.Add(name);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnLog(int level, byte* fmt, IntPtr args, IntPtr param)
    {
        try
        {
            var lv = level switch { 100 => "ERR", 200 => "WRN", 300 => "INF", _ => "DBG" };
            Log($"[{lv}] {Format(fmt, args)}");
        }
        catch { } // ネイティブへ例外を投げるとプロセスごと落ちる
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnCrash(byte* fmt, IntPtr args, IntPtr param)
    {
        string msg;
        try { msg = Format(fmt, args); } catch { msg = "(unknown)"; }
        try { File.WriteAllText(Path.Combine(ConfigDir, "logs", $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), msg); } catch { }
        Log("[CRASH] " + msg);
        Environment.FailFast("libobs crash: " + msg);
    }

    static string Format(byte* fmt, IntPtr args)
    {
        const int Size = 4096;
        var buf = stackalloc byte[Size];
        Native.vsnprintf(buf, Size - 1, fmt, args); // 切り詰め時は -1 が返り終端されない
        buf[Size - 1] = 0;
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? "";
    }

    // obs-nvenc / qsv / amf の検出用 exe と録画用 obs-ffmpeg-mux.exe は「実行ファイルと同じフォルダ」から探す
    static void CopyEncoderTestTools(string bin)
    {
        foreach (var t in new[] { "obs-nvenc-test.exe", "obs-qsv-test.exe", "obs-amf-test.exe", "obs-ffmpeg-mux.exe" })
        {
            var src = Path.Combine(bin, t);
            var dst = Path.Combine(AppContext.BaseDirectory, t);
            try { if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst); } catch { }
        }
    }

    // 配布時: exe 横の runtime\obs。開発時: 親ディレクトリを遡って runtime\obs を探す
    static string LocateRuntime()
    {
        // 開発時の切り替え用（絞ったランタイムの動作確認など）
        var env = Environment.GetEnvironmentVariable("VCAST_RUNTIME");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "bin", "64bit", "obs.dll"))) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "runtime", "obs");
            if (File.Exists(Path.Combine(cand, "bin", "64bit", "obs.dll"))) return cand;
        }
        throw new DirectoryNotFoundException("runtime\\obs（同梱OBSエンジン）が見つかりません");
    }
}
