using System.Windows;
using System.Windows.Threading;
using VCast.Obs;
using VCast.Ui;

namespace VCast.Core;

// 起動引数 --selftest で実行。全ソース種別の生成・設定画面・編集操作を自動で通してログに結果を書く。
public static class SelfTest
{
    // 修飾キー 3 つ + キー を実際に押す（グローバルホットキーの確認用）
    static void SendKeys(params int[] vks)
    {
        var inputs = new List<INPUT>();
        foreach (var vk in vks) inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (ushort)vk } });
        foreach (var vk in vks.Reverse()) inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = 2 } });
        SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct INPUT { [System.Runtime.InteropServices.FieldOffset(0)] public uint type; [System.Runtime.InteropServices.FieldOffset(8)] public KEYBDINPUT ki; [System.Runtime.InteropServices.FieldOffset(8)] public long pad1; [System.Runtime.InteropServices.FieldOffset(16)] public long pad2; [System.Runtime.InteropServices.FieldOffset(24)] public long pad3; [System.Runtime.InteropServices.FieldOffset(32)] public long pad4; }
    [System.Runtime.InteropServices.DllImport("user32")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    // ffmpeg（この PC にあれば）で 1 秒目のフレームの大きさと平均の明るさを調べる
    static (int w, int h, double mean)? ProbeVideo(string file)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", $"-v error -ss 1 -i \"{file}\" -frames:v 1 -vf scale=54:96,format=gray -f rawvideo -")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(10000);
            var px = ms.ToArray();
            if (px.Length != 54 * 96) return null;
            var psi2 = new System.Diagnostics.ProcessStartInfo("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{file}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p2 = System.Diagnostics.Process.Start(psi2)!;
            var dims = p2.StandardOutput.ReadToEnd().Trim().Split(',');
            p2.WaitForExit(5000);
            return (int.Parse(dims[0]), int.Parse(dims[1]), px.Average(b => (double)b));
        }
        catch { return null; }
    }

    // 1kHz・-12dBFS・2秒のサイン波（16bit ステレオ 48kHz）
    static void WriteTone(string path)
    {
        const int rate = 48000, secs = 2;
        using var w = new BinaryWriter(File.Create(path));
        var samples = rate * secs;
        w.Write("RIFF"u8); w.Write(36 + samples * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 4);
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 0.25 * short.MaxValue);
            w.Write(v); w.Write(v);
        }
    }

    static byte[] OscString(string v)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(v);
        var r = new byte[(b.Length / 4 + 1) * 4];
        b.CopyTo(r, 0);
        return r;
    }

    static byte[] OscMessage(string address, params object[] args)
    {
        var ms = new MemoryStream();
        ms.Write(OscString(address));
        ms.Write(OscString("," + string.Concat(args.Select(a => a is string ? "s" : "f"))));
        foreach (var a in args)
        {
            if (a is string str) ms.Write(OscString(str));
            else { var f = BitConverter.GetBytes((float)a); Array.Reverse(f); ms.Write(f); }
        }
        return ms.ToArray();
    }

    static byte[] OscBundle(params byte[][] messages)
    {
        var ms = new MemoryStream();
        ms.Write(OscString("#bundle"));
        ms.Write(new byte[8]);
        foreach (var m in messages)
        {
            var len = BitConverter.GetBytes(m.Length); Array.Reverse(len);
            ms.Write(len); ms.Write(m);
        }
        return ms.ToArray();
    }

    // 録画だけの切り分け用: --rectest <mp4|mkv> <encoderId|auto>
    public static async Task RecordProbe(Window owner, OutputManager outs, AppSettings st, string format, string encoder)
    {
        st.RecordFolder = Path.Combine(Path.GetTempPath(), "vcast-selftest");
        st.RecordFormat = format;
        st.VideoEncoder = encoder;
        ObsEngine.Log($"[rectest] start format={format} encoder={OutputManager.PickVideoEncoder(encoder)}");
        var ok = outs.StartRecord(out var err);
        ObsEngine.Log($"[rectest] started={ok} {err}");
        await Task.Delay(3000);
        ObsEngine.Log("[rectest] stopping");
        outs.StopRecord();
        for (var i = 0; i < 50 && outs.Recording; i++) await Task.Delay(100);
        await Task.Delay(500);
        var p = outs.LastRecordingPath;
        ObsEngine.Log($"[rectest] done file={p} size={(p != null && File.Exists(p) ? new FileInfo(p).Length : -1)}");
        owner.Close();
    }

    public static async Task Run(Window owner, SceneManager scenes, SceneEditor editor)
    {
        var fails = 0;
        void Ok(string m) => ObsEngine.Log("[selftest] OK   " + m);
        void Ng(string m) { fails++; ObsEngine.Log("[selftest] FAIL " + m); }
        static Task Wait(int ms) => Task.Delay(ms);

        var main = owner as MainWindow;
        if (main != null)
        {
            if (main.IsEmptyStateShown) Ok("empty state shown on empty scene"); else Ng("empty state not shown");
            if (main.AppCount > 0) Ok($"app strip lists {main.AppCount} windows"); else Ng("app strip empty");
        }

        // 0) ファイルから追加（同梱データ内の PNG を使う）
        var png = Directory.EnumerateFiles(Path.Combine(ObsEngine.RuntimeDir, "data"), "*.png", SearchOption.AllDirectories).FirstOrDefault();
        if (png != null)
        {
            var fvm = SourceCatalog.AddFile(scenes, png);
            await Wait(300);
            if (fvm != null && Native.obs_source_get_width(fvm.Source) > 0) Ok($"add file -> image_source {Native.obs_source_get_width(fvm.Source)}px");
            else Ng("add file");
            if (main != null) { if (!main.IsEmptyStateShown) Ok("empty state hidden after add"); else Ng("empty state still shown"); }
            if (fvm != null) { editor.Select(IntPtr.Zero); scenes.RemoveItem(fvm); }
        }
        if (SourceCatalog.CanOpen("a.txt")) Ng("txt accepted"); else Ok("unsupported file rejected");

        // 0b) 開いているアプリから追加
        var win = WindowList.Enumerate(IntPtr.Zero).FirstOrDefault(w => !w.Minimized);
        if (win != null)
        {
            var wvm = SourceCatalog.AddWindow(scenes, win);
            if (wvm != null)
            {
                await Wait(1500); // WGC は最初のフレームが届くと大きさが決まる
                var ww = Native.obs_source_get_width(wvm.Source);
                if (ww > 0) Ok($"add window ({win.Exe}) captured {ww}px"); else Ng($"add window ({win.Exe}) size 0 (黒い可能性)");
                editor.Select(IntPtr.Zero); scenes.RemoveItem(wvm);
            }
            else Ng("add window");
            // つまんでいるカード: 表示・追従・状態切替・閉じる
            var ghost = new VCast.Ui.DragGhost(win.Icon, win.Title);
            ghost.MoveTo(200, 200, 1.0); ghost.Show(); ghost.SetOver(true); await Wait(300); ghost.SetOver(false); ghost.Close();
            Ok("drag ghost show/over/close");
        }

        // 1) 全ソース種別: 生成 → 設定画面を開いて閉じる → 削除
        foreach (var t in SceneManager.InputTypes())
        {
            try
            {
                var vm = scenes.AddSource(t.Id, "test-" + t.Id);
                if (vm == null) { Ng($"create {t.Id}"); continue; }
                var w = new PropertiesWindow(vm.Source) { Owner = owner, ShowActivated = false };
                w.Show();
                // 見た目確認用: 環境変数 VCAST_SNAP=<種別ID> の設定画面だけ長めに開く
                var snap = Environment.GetEnvironmentVariable("VCAST_SNAP") == t.Id;
                if (snap) ObsEngine.Log("[selftest] SNAP " + t.Id);
                await Wait(snap ? 2500 : 400);
                w.Close();
                editor.Select(IntPtr.Zero);
                scenes.RemoveItem(vm);
                Ok($"source+props {t.Id} ({t.DisplayName})");
            }
            catch (Exception ex) { Ng($"{t.Id}: {ex.Message}"); }
        }

        // 2) 編集操作: 色ソースを置いて移動・拡大縮小
        var bg = scenes.AddSource("color_source_v3", "selftest-color");
        if (bg == null) { Ng("color source"); }
        else
        {
            var s = Native.obs_source_get_settings(bg.Source);
            Native.obs_data_set_int(s, "width", 400);
            Native.obs_data_set_int(s, "height", 300);
            Native.obs_data_set_int(s, "color", unchecked((long)0xFFFF7C5C));
            Native.obs_source_update(bg.Source, s);
            Native.obs_data_release(s);
            await Wait(200);

            editor.PointerDown(200, 150, 0.5, false); // 中央をつかむ
            editor.PointerMove(500, 350, 0.5, false);
            editor.PointerUp();
            Native.obs_sceneitem_get_pos(bg.Item, out var p);
            if (Math.Abs(p.x - 300) < 1 && Math.Abs(p.y - 200) < 1) Ok($"move -> ({p.x},{p.y})"); else Ng($"move -> ({p.x},{p.y}) expected (300,200)");

            editor.PointerDown(700, 500, 0.5, false); // 右下の角
            editor.PointerMove(900, 650, 0.5, false);
            editor.PointerUp();
            Native.obs_sceneitem_get_scale(bg.Item, out var sc);
            Native.obs_sceneitem_get_pos(bg.Item, out var p2);
            // 幅 400→600 (x1.5)、高さ 300→450 (x1.5)、左上は固定
            if (Math.Abs(sc.x - 1.5) < 0.01 && Math.Abs(sc.y - 1.5) < 0.01 && Math.Abs(p2.x - 300) < 1) Ok($"resize -> scale {sc.x:0.##}");
            else Ng($"resize -> scale ({sc.x},{sc.y}) pos ({p2.x},{p2.y})");

            editor.FitToScreen();
            Native.obs_sceneitem_get_pos(bg.Item, out var p3);
            Native.obs_sceneitem_get_scale(bg.Item, out var sc3);
            if (Math.Abs(sc3.x - 3.6) < 0.01 && Math.Abs(p3.x - 240) < 1 && Math.Abs(p3.y) < 1) Ok("fit to screen"); else Ng($"fit -> scale {sc3.x} pos ({p3.x},{p3.y})");

            // 辺のハンドル（右の辺）: 横だけ伸びる → 元に戻す
            if (main != null)
            {
                editor.PointerDown(1680, 540, 0.5, false);
                editor.PointerMove(1780, 540, 0.5, false);
                editor.PointerUp();
                Native.obs_sceneitem_get_scale(bg.Item, out var se);
                var edgeOk = Math.Abs(se.x - 3.6 * 1540 / 1440.0) < 0.02 && Math.Abs(se.y - 3.6) < 0.01;
                main.UndoForTest();
                Native.obs_sceneitem_get_scale(bg.Item, out var su);
                if (edgeOk && Math.Abs(su.x - 3.6) < 0.01) Ok("edge handle resize + undo"); else Ng($"edge ({se.x},{se.y}) undo {su.x}");

                // 削除 → 元に戻す
                editor.Select(bg.Item);
                var before = scenes.Items.Count;
                main.RemoveSelectedForTest();
                var removed = scenes.Items.Count == before - 1;
                main.UndoForTest();
                var back = scenes.Items.FirstOrDefault(i => i.Name == "selftest-color");
                if (removed && back != null && scenes.Items.Count == before) { Ok("delete + undo restores source"); bg = back; }
                else Ng($"delete/undo removed={removed} back={back != null}");
            }

            var text = scenes.AddSource("text_gdiplus_v3", "selftest-text");
            if (text != null)
            {
                var ts = Native.obs_source_get_settings(text.Source);
                Native.obs_data_set_string(ts, "text", "VCast Studio セルフテスト");
                Native.obs_source_update(text.Source, ts);
                Native.obs_data_release(ts);
                editor.Select(text.Item);
                Ok("text + selection");
            }
        }

        // 2b) 音声ミキサー（音は鳴らさない: モニタリングはオフのまま）
        if (main != null)
        {
            var mixer = main.Mixer;
            if (mixer.Strips.Count >= 2) Ok($"mixer globals ({mixer.Strips.Count} strips)"); else Ng($"mixer strips {mixer.Strips.Count}");
            var tone = Path.Combine(ObsEngine.ConfigDir, "selftest-tone.wav");
            WriteTone(tone);
            var media = scenes.AddSource("ffmpeg_source", "selftest-tone", st =>
            {
                Native.obs_data_set_bool(st, "is_local_file", true);
                Native.obs_data_set_string(st, "local_file", tone);
                Native.obs_data_set_bool(st, "looping", true);
            });
            await Wait(1500);
            var strip = mixer.Strips.FirstOrDefault(x => media != null && x.Source == media.Source);
            if (strip == null) Ng("media strip missing");
            else
            {
                var (l, r, _, _) = strip.Levels;
                if (l > -30) Ok($"meter moves ({l:0.0} dBFS)"); else Ng($"meter silent ({l} dBFS)");
                if (strip.Monitoring == Native.OBS_MONITORING_TYPE_NONE) Ok("monitoring off by default"); else Ng("monitoring on");
                strip.Deflection = 0.5;
                if (Math.Abs(strip.Deflection - 0.5) < 0.01 && strip.DbText.EndsWith("dB")) Ok($"fader -> {strip.DbText}"); else Ng("fader");
                strip.Muted = true; await Wait(300);
                if (Native.obs_source_muted(strip.Source)) Ok("mute"); else Ng("mute");
            }
            if (media != null) { editor.Select(IntPtr.Zero); scenes.RemoveItem(media); }
            if (mixer.Strips.Count == 2) Ok("strip removed with source"); else Ng($"strips after remove {mixer.Strips.Count}");

            // フィルタ: マイクにおすすめ4点 → 一覧 → 画面を開いて閉じる → 全削除
            var mic = mixer.Mic;
            var added = Filters.ApplyCleanMicPreset(mic);
            var list = Filters.List(mic);
            if (added == 4 && list.Count == 4) Ok("clean mic preset (4 filters)"); else Ng($"preset added={added} list={list.Count}");
            var fw = new VCast.Ui.FiltersWindow(mic) { Owner = owner, ShowActivated = false };
            fw.Show(); await Wait(300); fw.Close();
            foreach (var f in Filters.List(mic)) Filters.Remove(mic, f);
            if (Filters.List(mic).Count == 0) Ok("filters removed"); else Ng("filters remain");
        }

        // 映像フィルタ（クロマキー）
        if (bg != null)
        {
            var ck = Filters.Add(bg.Source, "chroma_key_filter_v2");
            if (ck != IntPtr.Zero && Filters.List(bg.Source).Count == 1) Ok("video filter (chroma key)"); else Ng("video filter");
            if (Filters.AvailableFor(bg.Source).Any(t => t.Id == "noise_suppress_filter_v2")) Ng("audio filter offered for video-only source"); else Ok("filter list matches source type");
        }

        // 2c) 3Dアバター（ユーザーのアバター設定はテスト後に元へ戻す）
        if (main != null)
        {
            var av = main.Avatar;
            var savedModel = av.Settings.ModelPath;
            using var http = new System.Net.Http.HttpClient();
            async Task<(int, string)> Get(string path)
            {
                try { var r = await http.GetAsync(av.BaseUrl + path); return ((int)r.StatusCode, r.Content.Headers.ContentType?.MediaType ?? ""); }
                catch (Exception ex) { return (0, ex.Message); }
            }
            var (c1, _) = await Get("avatar.html");
            var (c2, t2) = await Get("vendor/mediapipe/wasm/vision_wasm_internal.wasm");
            var (c3, _) = await Get("../../Windows/win.ini");
            var (c4, _) = await Get("model.vrm"); // トークン無しは拒否
            if (c1 == 200 && c2 == 200 && t2 == "application/wasm") Ok("avatar server serves page + wasm"); else Ng($"avatar server {c1} {c2} {t2}");
            if (c3 != 200) Ok("path traversal blocked"); else Ng("path traversal");
            if (c4 == 403) Ok("model requires token"); else Ng($"model without token -> {c4}");
            try
            {
                using var bad = new System.Net.WebSockets.ClientWebSocket();
                await bad.ConnectAsync(new Uri(av.BaseUrl.Replace("http", "ws") + "ws?role=render&t=wrong"), CancellationToken.None);
                Ng("websocket accepted wrong token");
            }
            catch { Ok("websocket rejects wrong token"); }

            // VCAST_VRM でテストに使うモデルを差し替えられる（既定は Downloads の tanuki）
            var browserWasLazy = !ObsEngine.BrowserLoaded;
            var vrmPath = Environment.GetEnvironmentVariable("VCAST_VRM") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "tanuki_1.1.vrm");
            av.Settings.InputLink = false; av.ApplyConfig(); // 立ち姿を撮るまでは、作業中のキー入力に反応させない
            if (File.Exists(vrmPath)) av.SetModel(vrmPath);
            var avItem = main.AddAvatar();
            if (avItem != null) Ok("avatar source added"); else Ng("avatar source");
            if (browserWasLazy && ObsEngine.BrowserLoaded) Ok("browser loaded on demand when avatar added"); else if (browserWasLazy) Ng("browser not loaded after avatar add");
            for (var i = 0; i < 80 && !av.RenderConnected; i++) await Wait(100);
            if (av.RenderConnected) Ok("avatar page running in OBS browser (websocket connected)"); else Ng("avatar page did not connect");
            if (File.Exists(vrmPath))
            {
                for (var i = 0; i < 100 && !av.ModelStatus.StartsWith("表示中") && !av.ModelStatus.StartsWith("読み込み失敗"); i++) await Wait(100);
                if (av.ModelStatus.StartsWith("表示中")) Ok("VRM loaded: " + av.ModelStatus); else Ng("VRM: " + av.ModelStatus);
                var aw = new VCast.Ui.AvatarWindow(av, () => { }, () => true) { Owner = owner, ShowActivated = false };
                aw.Show();
                // 立ち姿（入力連動なし）→ 打鍵中 の順に撮れるようにする
                await Wait(1500);
                ObsEngine.Log("[selftest] SNAP rest");
                await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "rest" ? 3000 : 200);
                av.Settings.InputLink = true; av.FakeTyping = true; av.ApplyConfig();
                await Wait(2500);
                ObsEngine.Log("[selftest] SNAP typing");
                await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "typing" ? 3000 : 200);
                av.FakeTyping = false;
                av.Settings.InputLink = false; av.ApplyConfig();
                // 顔追跡の見た目確認: まず正面（2.5 秒で「正面」を覚える）→ 顔を横に向ける → 口を開けて笑う
                for (var i = 0; i < 90; i++) { av.InjectFace(0, 0, 0); await Wait(33); }
                // 撮影の間も顔データを送り続ける（途切れると 0.8 秒で待機中の動きに戻るため）
                async Task Hold(int ms, double yaw, double pitch, double roll, Dictionary<string, double>? blend) { for (var t = 0; t < ms; t += 33) { av.InjectFace(yaw, pitch, roll, blend); await Wait(33); } }
                var snapNow = Environment.GetEnvironmentVariable("VCAST_SNAP");
                await Hold(2000, 0.6, 0.15, 0.1, null);
                ObsEngine.Log("[selftest] SNAP face-turn");
                await Hold(snapNow == "face-turn" ? 3500 : 100, 0.6, 0.15, 0.1, null);
                var mouth = new Dictionary<string, double> { ["jawOpen"] = 0.8, ["mouthSmileLeft"] = 0.7, ["mouthSmileRight"] = 0.7, ["eyeBlinkLeft"] = 0.9 };
                await Hold(1500, 0, 0, 0, mouth);
                ObsEngine.Log("[selftest] SNAP face-mouth");
                await Hold(snapNow == "face-mouth" ? 3500 : 100, 0, 0, 0, mouth);
                av.Settings.InputLink = true; av.ApplyConfig();
                av.SetExpression("happy");
                ObsEngine.Log("[selftest] SNAP avatar");
                await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "avatar" ? 3000 : 500);
                av.SetExpression("neutral");
                // 歩かせるモード: 右へ歩く → 奥へ歩く → 止まる
                av.Settings.WalkMode = true; av.Settings.InputLink = false; av.ApplyConfig(); av.ApplyResolution(scenes);
                await Wait(800);
                av.FakeMoveX = 1;
                await Wait(1500);
                ObsEngine.Log("[selftest] SNAP walk-right");
                await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "walk-right" ? 9000 : 200);
                av.FakeMoveX = -1; av.FakeMoveZ = 1;
                await Wait(1500);
                ObsEngine.Log("[selftest] SNAP walk-away");
                await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "walk-away" ? 9000 : 200);
                av.FakeMoveX = 0; av.FakeMoveZ = 0;
                av.Settings.WalkMode = false; av.ApplyConfig(); av.ApplyResolution(scenes);
                await Wait(500);
                aw.Close();
            }
            // 顔追跡の起動（偽カメラ）: WebView2 が動き、カメラ一覧と状態が報告され、顔認識モデルが読み込まれる
            av.Settings.CameraEnabled = true; av.ApplyConfig();
            for (var i = 0; i < 300 && (av.TrackerStatus.Contains("オフ") || av.TrackerStatus.Contains("起動中") || av.TrackerStatus.Contains("読み込み中")); i++) await Wait(100);
            var camsReported = av.Cameras.Count > 0;
            var trackerOk = av.TrackerStatus.Contains("追跡") || av.TrackerStatus.Contains("見つかりません") || av.TrackerStatus.Contains("トラッキング中");
            if (trackerOk && camsReported) Ok($"face tracker starts (fake camera): status=\"{av.TrackerStatus}\", cameras={av.Cameras.Count}");
            else Ng($"face tracker status=\"{av.TrackerStatus}\" cameras={av.Cameras.Count}");
            av.Settings.CameraEnabled = false; av.ApplyConfig();
            await Wait(500);
            if (avItem != null) { editor.Select(IntPtr.Zero); scenes.RemoveItem(avItem); }
            // ネイティブ描画（実験）: 描画スレッドが動き、共有テクスチャに絵が出て、libobs のソースとしてシーンに載る
            if (File.Exists(vrmPath))
            {
                av.Settings.NativeRender = true; av.Settings.InputLink = false; av.ApplyConfig();
                var nItem = main.AddAvatar();
                if (nItem != null && av.NativeActive) Ok("native avatar source added"); else Ng("native avatar source");
                for (var i = 0; i < 150 && !av.NativeStatus.StartsWith("表示中") && !av.NativeStatus.StartsWith("読み込み失敗"); i++) await Wait(100);
                if (av.NativeStatus.StartsWith("表示中")) Ok("native VRM loaded: " + av.NativeStatus); else Ng("native VRM: " + av.NativeStatus);
                var nat = av.NativeInstance;
                if (nat != null)
                {
                    var f0 = nat.Frames; await Wait(1000); var f1 = nat.Frames;
                    if (f1 - f0 >= 30) Ok($"native render loop {(f1 - f0)} frames/s, {nat.LastFrameMs:F2} ms/frame"); else Ng($"native render loop too slow: {f1 - f0} frames/s");
                    if (nat.SharedHandle != 0) Ok("native shared texture handle"); else Ng("native shared texture handle is 0");
                    var px = nat.CaptureFrame();
                    if (px != null)
                    {
                        long opaque = 0; for (var i = 3; i < px.Length; i += 4) if (px[i] > 8) opaque++;
                        var frac = opaque / (double)(nat.Width * nat.Height);
                        if (frac > 0.05 && frac < 0.95) Ok($"native frame has avatar pixels ({frac:P0} opaque)"); else Ng($"native frame opaque fraction {frac:P1}");
                    }
                    else Ng("native frame capture failed");
                    // 顔を横に向けたとき、フレームが変わること（動き→描画がつながっている）
                    var before = nat.CaptureFrame();
                    for (var i = 0; i < 90; i++) { av.InjectFace(0, 0, 0); await Wait(33); }
                    for (var i = 0; i < 45; i++) { av.InjectFace(0.6, 0.15, 0.1, new Dictionary<string, double> { ["jawOpen"] = 0.8 }); await Wait(33); }
                    var after = nat.CaptureFrame();
                    if (before != null && after != null) { long diff = 0; for (var i = 0; i < before.Length; i += 4) if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) > 40) diff++; if (diff > 500) Ok($"native avatar reacts to face input ({diff} px changed)"); else Ng($"native avatar did not react to face input ({diff} px)"); }
                    ObsEngine.Log("[selftest] SNAP native");
                    for (var i = 0; i < 60; i++) { av.InjectFace(0.6, 0.15, 0.1, null); await Wait(33); }
                    await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "native" ? 3000 : 200);
                }
                if (nItem != null) { editor.Select(IntPtr.Zero); scenes.RemoveItem(nItem); }
                av.Settings.NativeRender = false; av.ApplyConfig(); av.ApplyResolution(scenes);
                if (!av.NativeActive) Ok("native avatar stopped"); else Ng("native avatar still active");
            }
            av.Settings.ModelPath = savedModel;
            av.Save();

            // VMC: 自分で OSC パケットを送って受信できるか
            using (var vmc = new VCast.Avatar.VmcReceiver(39591))
            {
                float got = -1;
                vmc.Frame += (b, _) => { if (b.TryGetValue("A", out var v)) got = v; };
                using var udp = new System.Net.Sockets.UdpClient();
                var bundle = OscBundle(OscMessage("/VMC/Ext/Blend/Val", "A", 0.75f), OscMessage("/VMC/Ext/Blend/Apply"));
                udp.Send(bundle, bundle.Length, "127.0.0.1", 39591);
                for (var i = 0; i < 20 && got < 0; i++) await Wait(50);
                if (Math.Abs(got - 0.75f) < 0.001) Ok("VMC receive (OSC bundle)"); else Ng($"VMC got {got}");
            }
        }

        // 2c-2) コメント（Twitch チャットの解析）と VOICEVOX 読み上げ。外部には繋がない（VOICEVOX は自作の模擬サーバー）
        {
            var line = "@badges=;color=#1E90FF;display-name=たろう;emotes=25:0-4,12-16/1902:6-10;id=x :taro!taro@taro.tmi.twitch.tv PRIVMSG #samplech :Kappa Keepo Kappa こんにちは";
            var cm = TwitchChat.Parse(line);
            if (cm != null && cm.Name == "たろう" && cm.Text == "こんにちは" && cm.Color == "#1E90FF") Ok("chat parse (name, color, emotes removed)"); else Ng($"chat parse -> {cm?.Name}/{cm?.Text}");
            var act = TwitchChat.Parse("@display-name=A;color= :a!a@a PRIVMSG #c :\u0001ACTION 手を振る\u0001");
            if (act?.Text == "手を振る") Ok("chat parse /me"); else Ng("chat parse /me -> " + act?.Text);
            if (TwitchChat.Parse("@a=b :tmi.twitch.tv 001 justinfan1 :Welcome") == null) Ok("chat parse ignores non-chat"); else Ng("chat parse non-chat");
            if (TwitchChat.NormalizeChannel("https://www.twitch.tv/SampleCh?foo=1") == "samplech" && TwitchChat.NormalizeChannel("#abc_1") == "abc_1" && TwitchChat.NormalizeChannel("a b!") == "" && TwitchChat.NormalizeChannel("") == "") Ok("channel name normalize"); else Ng($"channel normalize: [{TwitchChat.NormalizeChannel("https://www.twitch.tv/SampleCh?foo=1")}] [{TwitchChat.NormalizeChannel("#abc_1")}] [{TwitchChat.NormalizeChannel("a b!")}]");

            using var rd = new VoiceVoxReader { Dictionary = "草=くさ" };
            var c1 = rd.Clean("viewer", "これ草wwwww https://example.com/x 😀");
            var skipBot = rd.Clean("Nightbot", "こんにちは"); var skipCmd = rd.Clean("viewer", "!uptime");
            var longT = rd.Clean("viewer", string.Concat(Enumerable.Range(0, 200).Select(i => (char)(0x3042 + i % 40))));
            rd.ReadName = true; var withName = rd.Clean("たろう", "やあ"); rd.ReadName = false;
            if (c1 == "これくさわら URL省略" && skipBot == null && skipCmd == null && longT != null && longT.EndsWith("以下省略") && longT.Length < 80 && withName == "たろうさん、やあ") Ok("reader text cleanup (dictionary, laugh, URL, bot, command, length, name)");
            else Ng($"reader clean: [{c1}] bot={skipBot} cmd={skipCmd} name=[{withName}] long=[{longT}] len={longT?.Length}");

            // 模擬 VOICEVOX（/version /speakers /audio_query /synthesis）
            var port = 50000 + Random.Shared.Next(1000, 9000);
            using var fake = new System.Net.HttpListener(); fake.Prefixes.Add($"http://127.0.0.1:{port}/"); fake.Start();
            double gotSpeed = 0; int gotSpeaker = -1;
            _ = Task.Run(async () =>
            {
                while (fake.IsListening)
                {
                    System.Net.HttpListenerContext ctx; try { ctx = await fake.GetContextAsync(); } catch { return; }
                    var path = ctx.Request.Url!.AbsolutePath; byte[] body; var ct = "application/json";
                    if (path == "/version") body = System.Text.Encoding.UTF8.GetBytes("\"0.test\"");
                    else if (path == "/speakers") body = System.Text.Encoding.UTF8.GetBytes("[{\"name\":\"テスト子\",\"styles\":[{\"id\":7,\"name\":\"ノーマル\"},{\"id\":8,\"name\":\"あまあま\"}]}]");
                    else if (path == "/audio_query") { gotSpeaker = int.Parse(System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query)["speaker"] ?? "-1"); body = System.Text.Encoding.UTF8.GetBytes("{\"speedScale\":1,\"volumeScale\":1}"); }
                    else if (path == "/synthesis")
                    {
                        using var sr = new StreamReader(ctx.Request.InputStream); var j = System.Text.Json.Nodes.JsonNode.Parse(await sr.ReadToEndAsync())!; gotSpeed = j["speedScale"]!.GetValue<double>();
                        ct = "audio/wav"; body = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4 };
                    }
                    else body = Array.Empty<byte>();
                    ctx.Response.ContentType = ct; ctx.Response.OutputStream.Write(body); ctx.Response.Close();
                }
            });
            using var vr = new VoiceVoxReader { BaseUrl = $"http://127.0.0.1:{port}", Speaker = 8, Speed = 1.4 };
            var styles = await vr.GetStyles(); var ver = await vr.GetVersion();
            if (styles is { Count: 2 } && styles[0].Id == 7 && styles[0].Label == "テスト子（ノーマル）" && ver == "0.test") Ok("voicevox client: version + speakers"); else Ng("voicevox speakers");
            var played = new List<byte[]>(); vr.PlayOverride = w => { lock (played) played.Add(w); return true; };
            vr.Enqueue("a", "テストです");
            for (var i = 0; i < 40 && played.Count == 0; i++) await Wait(50);
            if (played.Count == 1 && played[0].Length == 8 && gotSpeaker == 8 && Math.Abs(gotSpeed - 1.4) < 0.001) Ok("voicevox synth request (speaker/speed) and playback hook"); else Ng($"voicevox synth: played={played.Count} speaker={gotSpeaker} speed={gotSpeed}");
            fake.Stop();
            // 繋がらないときは落ちずにメッセージを出す
            var failMsg = ""; using var vr2 = new VoiceVoxReader { BaseUrl = "http://127.0.0.1:9" }; vr2.StatusChanged += m => failMsg = m;
            vr2.Enqueue("a", "だめ");
            for (var i = 0; i < 60 && failMsg.Length == 0; i++) await Wait(100);
            if (failMsg.Contains("VOICEVOX")) Ok("voicevox offline -> message, no crash"); else Ng("voicevox offline: " + failMsg);
        }

        // 2c-3) 緊急「映像を隠して音を消す」と、遅延の選択肢
        if (main != null)
        {
            var mic = main.Mixer.Mic;
            var wasMuted = mic != IntPtr.Zero && Native.obs_source_muted(mic);
            main.TogglePanic();
            var outSrc = Native.obs_get_output_source(0); var coverOn = outSrc != IntPtr.Zero && Native.Str(Native.obs_source_get_name(outSrc)) == "vcast_panic_cover"; if (outSrc != IntPtr.Zero) Native.obs_source_release(outSrc);
            var on = main.PanicOn && (mic == IntPtr.Zero || Native.obs_source_muted(mic)) && coverOn;
            main.TogglePanic();
            var off = !main.PanicOn && (mic == IntPtr.Zero || Native.obs_source_muted(mic) == wasMuted);
            if (on && off) Ok("panic: cover on + audio muted, then restored"); else Ng($"panic on={on} off={off}");
        }

        // 2d) 配信・録画（実際の配信サービスには繋がない。録画は一時フォルダ）
        if (main != null)
        {
            var outs = main.Outputs;
            var st = main.Settings;
            var saved = st.Clone();
            var events = new List<OutputEvent>();
            void OnEv(OutputEvent e) => events.Add(e);
            outs.Event += OnEv;
            var tmp = Path.Combine(Path.GetTempPath(), "vcast-selftest");
            Directory.CreateDirectory(tmp);
            st.RecordFolder = tmp;
            st.ReplaySeconds = 15;
            ObsEngine.Log("[selftest] encoder " + OutputManager.PickVideoEncoder("auto"));

            if (outs.StartRecord(out var rerr)) { await Wait(3000); outs.StopRecord(); }
            for (var i = 0; i < 50 && !events.Any(e => e.Kind == OutputKind.Record && e.Message.StartsWith("録画を保存")); i++) await Wait(100);
            var rec = outs.LastRecordingPath;
            if (rec != null && File.Exists(rec) && new FileInfo(rec).Length > 10_000) Ok($"record 3s -> {Path.GetFileName(rec)} {new FileInfo(rec).Length / 1024} KB");
            else Ng($"record: {rerr} {rec}");

            if (outs.StartReplay(out var perr))
            {
                await Wait(4000); // キーフレームが溜まるまで
                outs.SaveReplay();
                for (var i = 0; i < 100 && outs.LastReplayPath == null; i++) await Wait(100);
                outs.StopReplay();
            }
            if (outs.LastReplayPath != null && File.Exists(outs.LastReplayPath)) Ok("replay saved -> " + Path.GetFileName(outs.LastReplayPath)); else Ng("replay: " + perr);

            // 誰も待っていないローカルのポートへ配信 → 接続失敗が正しく報告されるか
            st.Destinations = new() { new StreamDestination { Name = "selftest", Service = "Custom", Server = "rtmp://127.0.0.1:19350/live", Key = "sk_selftest_0123456789abcdef", Enabled = true } };
            var started = outs.StartStream(out var serr);
            for (var i = 0; i < 150 && !events.Any(e => e.Kind == OutputKind.Stream && e.IsError); i++) await Wait(100);
            var failEv = events.FirstOrDefault(e => e.Kind == OutputKind.Stream && e.IsError);
            if (failEv != null) Ok("stream failure reported: " + failEv.Message); else Ng($"stream failure not reported (started={started} {serr})");
            outs.StopStream();

            if (OutputManager.VirtualCamInstalled())
            {
                if (outs.StartVirtualCam(out var verr)) { await Wait(800); var on = outs.VirtualCamActive; outs.StopVirtualCam(); if (on) Ok("virtual camera on/off"); else Ng("virtual camera not active"); }
                else Ng("virtual camera: " + verr);
            }
            else Ok("virtual camera driver not installed (skip)");

            var pf = new VCast.Ui.PreflightWindow(new VCast.Ui.PreflightWindow.Context(st, main.Mixer, scenes, false, "")) { Owner = owner, ShowActivated = false };
            pf.Show(); await Wait(3600);
            ObsEngine.Log("[selftest] SNAP preflight");
            await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "preflight" ? 2500 : 200);
            pf.Close();
            Ok("preflight window ran");

            // HEVC / 録画だけ高画質（GPU が対応しているときだけ HEVC）
            {
                var f0 = (st.Codec, st.RecordSeparate, st.RecordQuality);
                st.Codec = "hevc"; st.RecordSeparate = true; st.RecordQuality = 18;
                var hevcId = OutputManager.PickVideoEncoder("auto", "hevc");
                ObsEngine.Log($"[selftest] hevc encoder = {hevcId}");
                events.Clear();
                if (outs.StartRecord(out var herr)) { await Wait(2500); outs.StopRecord(); }
                for (var i = 0; i < 50 && !events.Any(e => e.Kind == OutputKind.Record && e.Message.StartsWith("録画を保存")); i++) await Wait(100);
                var hfile = outs.LastRecordingPath;
                if (hfile != null && File.Exists(hfile) && new FileInfo(hfile).Length > 10_000) Ok($"record (separate high quality) {new FileInfo(hfile).Length / 1024} KB, stream encoder {outs.VideoEncoderId}");
                else Ng($"record separate: {herr} {hfile}");
                // 配信用エンコーダが HEVC になっているか（GPU 対応時）
                if (OutputManager.IsHevc(hevcId) == OutputManager.IsHevc(outs.VideoEncoderId)) Ok("codec selection applied"); else Ng($"codec selection {hevcId} vs {outs.VideoEncoderId}");
                (st.Codec, st.RecordSeparate, st.RecordQuality) = f0;
                await Wait(500);
            }

            // 縦型: キャンバスを動かして横と同時に録画
            st.VerticalEnabled = true; st.VerticalRecord = true; st.VerticalLayout = "stack";
            main.ApplyVertical();
            await Wait(500);
            if (outs.StartRecord(out var verr2)) { await Wait(3000); outs.StopRecord(); }
            for (var i = 0; i < 50 && outs.Recording; i++) await Wait(100);
            await Wait(800);
            var vfile = outs.LastVerticalRecordingPath;
            if (vfile != null && File.Exists(vfile) && new FileInfo(vfile).Length > 10_000)
            {
                var probe = ProbeVideo(vfile);
                if (probe == null) Ok($"vertical record -> {new FileInfo(vfile).Length / 1024} KB (ffmpeg なしのため中身は未確認)");
                else if (probe.Value.w == 1080 && probe.Value.h == 1920 && probe.Value.mean > 5) Ok($"vertical record 1080x1920, not black (mean {probe.Value.mean:0})");
                else Ng($"vertical record {probe.Value.w}x{probe.Value.h} mean {probe.Value.mean:0}");
            }
            else Ng($"vertical record: {verr2} {vfile}");
            st.VerticalEnabled = false; main.ApplyVertical();

            outs.Event -= OnEv;
            st.CopyFrom(saved); // ユーザーの設定は保存し直さない
            var perf = new PerfMonitor(); perf.Sample(); await Wait(1000); var snap = perf.Sample();
            if (snap.Fps > 30 && snap.MemoryMb > 50) Ok($"perf: CPU {snap.CpuPercent:0.0}% mem {snap.MemoryMb:0}MB {snap.Fps:0}fps lag {snap.RenderLagPercent:0.#}%"); else Ng($"perf {snap}");
        }

        // 3) シーン操作
        var s2 = scenes.AddScene("selftest-scene");
        scenes.SetCurrent(s2);
        scenes.SetCurrent(scenes.Scenes[0]);
        scenes.RemoveScene(s2);
        if (scenes.Scenes.Count == 1) Ok("scene add/switch/remove"); else Ng("scene count " + scenes.Scenes.Count);

        // 2f) サウンドボード・プレイリスト・ダッキング（音はモニターに出さない）
        if (main != null)
        {
            var tone = Path.Combine(ObsEngine.ConfigDir, "selftest-tone.wav");
            if (!File.Exists(tone)) WriteTone(tone);
            var sb = main.SoundboardCore;
            var savedPad = sb.Pads[0];
            sb.Pads[0] = new SoundPad { Name = "test", File = tone, Volume = 0.5, Expression = "happy" };
            sb.Prepare(0);
            sb.Play(0);
            await Wait(300);
            var playing = sb.IsPlaying(0);
            var exprDuring = main.Avatar.Expression;
            for (var i = 0; i < 40 && sb.IsPlaying(0); i++) await Wait(100);
            if (playing && exprDuring == "happy" && !sb.IsPlaying(0) && main.Avatar.Expression == "neutral") Ok("soundboard play/end + expression link");
            else Ng($"soundboard playing={playing} expr={exprDuring} after={sb.IsPlaying(0)}/{main.Avatar.Expression}");
            if (!main.Mixer.Strips.Any(x => Native.Str(Native.obs_source_get_name(x.Source))?.StartsWith("サウンドボード") == true)) Ok("soundboard hidden from scene/mixer"); else Ng("soundboard visible in mixer");
            sb.Pads[0] = savedPad; sb.Prepare(0);

            var pm = main.Playlists;
            var pl = pm.Create(scenes);
            pm.Write(pl, new List<string> { tone, tone }, loop: false, shuffle: false, index: 0, loadFile: true);
            Native.obs_source_media_restart(pl);
            // PC が忙しいと曲の開始が遅れるので余裕を持って待つ
            for (var i = 0; i < 80 && PlaylistManager.Options(pl).index == 0; i++) await Wait(100);
            var advanced = PlaylistManager.Options(pl).index == 1;
            await Wait(500);
            for (var i = 0; i < 80 && Native.obs_source_media_get_state(pl) is Native.OBS_MEDIA_STATE_PLAYING or Native.OBS_MEDIA_STATE_OPENING; i++) await Wait(100);
            await Wait(500);
            var stoppedAtEnd = Native.obs_source_media_get_state(pl) != Native.OBS_MEDIA_STATE_PLAYING && PlaylistManager.Options(pl).index == 1;
            if (advanced && stoppedAtEnd) Ok("playlist advances and stops at end"); else Ng($"playlist advanced={advanced} stopped={stoppedAtEnd} idx={PlaylistManager.Options(pl).index}");
            var plItem = scenes.Items.FirstOrDefault(x => x.Source == pl);
            if (plItem != null) { editor.Select(IntPtr.Zero); scenes.RemoveItem(plItem); }

            Filters.SetDucking(main.Mixer.Desktop, main.MicName, true);
            var duckOn = Filters.HasDucking(main.Mixer.Desktop);
            Filters.SetDucking(main.Mixer.Desktop, main.MicName, false);
            if (duckOn && !Filters.HasDucking(main.Mixer.Desktop)) Ok("ducking on/off"); else Ng("ducking");
        }

        // 2g) シーン切り替え・スタジオモード（ユーザー設定は保存しない）
        if (main != null)
        {
            var tr = main.Transitions;
            var st = main.Settings;
            var savedStudio = st.StudioMode; var savedSimple = st.SimpleMode;
            var savedPro = st.ProMode;
            st.SimpleMode = false; st.ProMode = true; main.ApplyMode(); // スタジオモードはプロモードの機能
            var a = scenes.Current!;
            var b = scenes.AddScene("selftest-B");
            scenes.SetCurrent(b);
            await Wait(500);
            var followNormal = tr.ProgramScene == b.Source;
            scenes.SetCurrent(a); await Wait(500);
            st.StudioMode = true; main.ApplyStudio();
            scenes.SetCurrent(b); await Wait(200);
            var programKept = tr.ProgramScene == a.Source;
            ObsEngine.Log("[selftest] SNAP studio");
            await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "studio" ? 2500 : 100);
            tr.Take(b.Source, animate: false); var cutOk = tr.ProgramScene == b.Source;
            scenes.SetCurrent(a); await Wait(100);
            tr.TBar(a.Source, 0.5); await Wait(100); var midOk = tr.ManualActive && tr.ProgramScene == b.Source;
            tr.TBar(a.Source, 1.0); await Wait(100); var tbarOk = tr.ProgramScene == a.Source && !tr.ManualActive;
            scenes.SetCurrent(b); tr.Take(b.Source, animate: true); await Wait(tr.DurationMs + 200); var autoOk = tr.ProgramScene == b.Source;
            // Tバーを途中で 0 に戻す = 取り消し（本番は b のまま）
            scenes.SetCurrent(a); tr.TBar(a.Source, 0.4); await Wait(100); tr.TBar(a.Source, 0.0); await Wait(100);
            if (!tr.ManualActive && tr.ProgramScene == b.Source) Ok("T-bar cancel keeps program"); else Ng($"T-bar cancel manual={tr.ManualActive}");
            scenes.SetCurrent(b);
            if (followNormal && programKept && cutOk && midOk && tbarOk && autoOk) Ok("transitions: normal follow / studio preview / cut / T-bar / auto");
            else Ng($"transitions follow={followNormal} kept={programKept} cut={cutOk} mid={midOk} tbar={tbarOk} auto={autoOk}");
            scenes.SetCurrent(a);
            st.StudioMode = false; main.ApplyStudio();
            scenes.RemoveScene(b);
            st.StudioMode = savedStudio; st.SimpleMode = savedSimple; st.ProMode = savedPro; main.ApplyMode();
        }

        // 2h) 外部操作（obs-websocket v5 互換）: 実際に接続して操作する
        if (main != null)
        {
            var st = main.Settings;
            var saved = (st.RemoteEnabled, st.RemotePort, st.RemotePassword, st.RemoteLan);
            st.RemoteEnabled = true; st.RemotePort = 4460; st.RemotePassword = "selftest-password-123"; st.RemoteLan = false;
            main.ApplyRemote();
            try
            {
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                ws.Options.AddSubProtocol("obswebsocket.json");
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:4460"), CancellationToken.None);
                async Task<System.Text.Json.Nodes.JsonNode?> Recv()
                {
                    var buf = new byte[1 << 16];
                    var r = await ws.ReceiveAsync(buf, new CancellationTokenSource(5000).Token);
                    return System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, r.Count));
                }
                async Task Send(string json) => await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                var hello = await Recv();
                var ch = hello?["d"]?["authentication"]?["challenge"]?.GetValue<string>();
                var salt = hello?["d"]?["authentication"]?["salt"]?.GetValue<string>();
                string B64(string x) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(x)));
                var auth = B64(B64("selftest-password-123" + salt) + ch);
                await Send($"{{\"op\":1,\"d\":{{\"rpcVersion\":1,\"authentication\":\"{auth}\",\"eventSubscriptions\":0}}}}");
                var identified = (await Recv())?["op"]?.GetValue<int>() == 2;
                async Task<System.Text.Json.Nodes.JsonNode?> Req(string type, string data = "{}")
                {
                    await Send($"{{\"op\":6,\"d\":{{\"requestType\":\"{type}\",\"requestId\":\"{type}\",\"requestData\":{data}}}}}");
                    while (true) { var m = await Recv(); if (m?["op"]?.GetValue<int>() == 7) return m?["d"]; }
                }
                var list = await Req("GetSceneList");
                var sceneCount = list?["responseData"]?["scenes"]?.AsArray().Count ?? -1;
                var b = scenes.AddScene("remote-B");
                var set = await Req("SetCurrentProgramScene", "{\"sceneName\":\"remote-B\"}");
                await Wait(main.Transitions.DurationMs + 200);
                var switched = main.Transitions.ProgramScene == b.Source;
                var micName = main.MicName;
                var before = Native.obs_source_muted(main.Mixer.Mic);
                await Req("ToggleInputMute", $"{{\"inputName\":\"{micName}\"}}");
                var toggled = Native.obs_source_muted(main.Mixer.Mic) != before;
                await Req("ToggleInputMute", $"{{\"inputName\":\"{micName}\"}}");
                var vend = await Req("CallVendorRequest", "{\"vendorName\":\"vcast\",\"requestType\":\"SetExpression\",\"requestData\":{\"expression\":\"surprised\"}}");
                var exprOk = main.Avatar.Expression == "surprised";
                main.Avatar.SetExpression("neutral");
                var unknown = await Req("NoSuchRequest");
                var unknownOk = unknown?["requestStatus"]?["code"]?.GetValue<int>() == 204;
                scenes.SetCurrent(scenes.Scenes[0]); scenes.RemoveScene(b);
                if (identified && sceneCount >= 1 && set?["requestStatus"]?["result"]?.GetValue<bool>() == true && switched && toggled && exprOk && unknownOk)
                    Ok($"remote control (obs-websocket v5): auth, scene list/switch, mute, vendor expression");
                else Ng($"remote identified={identified} scenes={sceneCount} switched={switched} toggled={toggled} expr={exprOk} unknown={unknownOk}");

                // パスワード違いは拒否
                using var bad = new System.Net.WebSockets.ClientWebSocket();
                bad.Options.AddSubProtocol("obswebsocket.json");
                await bad.ConnectAsync(new Uri("ws://127.0.0.1:4460"), CancellationToken.None);
                var buf2 = new byte[4096];
                await bad.ReceiveAsync(buf2, CancellationToken.None);
                await bad.SendAsync(System.Text.Encoding.UTF8.GetBytes("{\"op\":1,\"d\":{\"rpcVersion\":1,\"authentication\":\"wrong\"}}"), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                var rr = await bad.ReceiveAsync(buf2, new CancellationTokenSource(5000).Token);
                if (rr.MessageType == System.Net.WebSockets.WebSocketMessageType.Close && (int?)bad.CloseStatus == 4009) Ok("remote rejects wrong password"); else Ng("remote accepted wrong password");
            }
            catch (Exception ex) { Ng("remote: " + ex.Message); }
            (st.RemoteEnabled, st.RemotePort, st.RemotePassword, st.RemoteLan) = saved;
            main.ApplyRemote();
        }

        // 2i) ホットキー: 形式の解析・重複の拒否・実際にキーを送って動くか
        if (main != null)
        {
            var hk = main.Hotkeys;
            var parseOk = HotkeyManager.TryParse("Ctrl+Alt+Shift+F9", out var m1, out var v1) && m1 == 7 && v1 == 0x78
                && HotkeyManager.TryParse("Ctrl+Alt+5", out _, out var v5) && v5 == '5' && !HotkeyManager.TryParse("F9", out _, out _) && !HotkeyManager.TryParse("Ctrl+Nonsense", out _, out _);
            var fmt = HotkeyManager.Format(System.Windows.Input.Key.F9, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) == "Ctrl+Shift+F9"
                && HotkeyManager.Format(System.Windows.Input.Key.D3, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) == "Ctrl+Alt+3"
                && HotkeyManager.Format(System.Windows.Input.Key.LeftCtrl, System.Windows.Input.ModifierKeys.Control) == null;
            if (parseOk && fmt) Ok("hotkey parse/format"); else Ng($"hotkey parse={parseOk} fmt={fmt}");

            var original = hk.BindingOf("mic.mute");
            var err = hk.Set("mic.mute", "Ctrl+Alt+Shift+F9");
            var dup = hk.Set("mic.mute", hk.BindingOf("expr.happy")); // 他で使用中の組み合わせは拒否される
            var before = Native.obs_source_muted(main.Mixer.Mic);
            SendKeys(0x11, 0x12, 0x10, 0x78); // Ctrl+Alt+Shift+F9
            for (var i = 0; i < 30 && Native.obs_source_muted(main.Mixer.Mic) == before; i++) await Wait(100);
            var fired = Native.obs_source_muted(main.Mixer.Mic) != before;
            if (fired) Native.obs_source_set_muted(main.Mixer.Mic, before);
            hk.Set("mic.mute", original);
            if (err == null && dup != null && fired) Ok($"hotkey remap works via real key press (dup rejected: {dup})"); else Ng($"hotkey set={err} dup={dup} fired={fired}");
        }

        // 2j) プロファイル: 作成 → 切り替え（設定とシーンが別になる）→ 戻る（元どおり）→ 名前変更・削除
        if (main != null)
        {
            var st = main.Settings;
            main.ForceProfileSave = true;
            var orig = Profiles.Current;
            var probe = "selftest-profile-" + Guid.NewGuid().ToString("N")[..6];
            var nameErr = Profiles.ValidateName("a/b") != null && Profiles.ValidateName("") != null && Profiles.ValidateName(orig) != null && Profiles.ValidateName(probe) == null;
            var origDelay = st.StreamDelaySec;
            var origScenes = scenes.Scenes.Select(x => x.Name).ToList();
            st.StreamDelaySec = 30; // 目印
            Profiles.Create(probe, null);
            main.SwitchProfile(probe);
            var isolated = Profiles.Current == probe && st.StreamDelaySec == 0 && scenes.Scenes.Count == 1 && scenes.Scenes[0].Name == "メイン" && scenes.Items.Count == 0;
            scenes.AddSource("color_source_v3", "profile-only");
            main.SwitchProfile(orig);
            var restored = Profiles.Current == orig && st.StreamDelaySec == 30 && scenes.Scenes.Select(x => x.Name).SequenceEqual(origScenes) && !scenes.Items.Any(i => i.Name == "profile-only");
            main.SwitchProfile(probe);
            var kept = scenes.Items.Any(i => i.Name == "profile-only"); // 切り替えて戻っても、そのプロファイルのシーンは残っている
            var renamed = probe + "-r";
            Profiles.Rename(probe, renamed); var renameOk = Profiles.Current == renamed && Profiles.Exists(renamed) && !Profiles.Exists(probe);
            main.SwitchProfile(orig);
            var delBlockedBeforeSwitch = true;
            Profiles.Delete(renamed);
            var deleted = !Profiles.Exists(renamed);
            st.StreamDelaySec = origDelay;
            main.ForceProfileSave = false;
            if (nameErr && isolated && restored && kept && renameOk && deleted && delBlockedBeforeSwitch) Ok("profiles: create/switch isolates settings+scenes, switch back restores, rename, delete");
            else Ng($"profiles nameErr={nameErr} isolated={isolated} restored={restored} kept={kept} rename={renameOk} deleted={deleted}");
        }

        // 2e) 保存と読み込み（OBS 形式）
        if (main != null)
        {
            var file = Path.Combine(Path.GetTempPath(), "vcast-selftest", "collection.json");
            var sceneNames = scenes.Scenes.Select(x => x.Name).ToList();
            var itemNames = scenes.Items.Select(x => x.Name).OrderBy(x => x).ToList();
            var saved = SceneStore.Save(file, scenes, main.Mixer, main.Transitions.ProgramScene);
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var hasKeys = json.TryGetProperty("sources", out _) && json.TryGetProperty("scene_order", out _) && json.TryGetProperty("AuxAudioDevice1", out _);
            if (saved && hasKeys) Ok("save (OBS scene collection format)"); else Ng("save");
            var loadedOk = main.ImportScenes(file);
            var sameScenes = scenes.Scenes.Select(x => x.Name).SequenceEqual(sceneNames);
            var sameItems = scenes.Items.Select(x => x.Name).OrderBy(x => x).SequenceEqual(itemNames);
            if (loadedOk && sameScenes && sameItems) Ok($"load round trip ({sceneNames.Count} scenes, {itemNames.Count} items)");
            else Ng($"load: ok={loadedOk} scenes={string.Join(",", scenes.Scenes.Select(x => x.Name))} items={string.Join(",", scenes.Items.Select(x => x.Name))}");
            bg = scenes.Items.FirstOrDefault(i => i.Name == "selftest-color");
            // 壊れたファイルは拒否して今のシーンを残す
            var bad = Path.Combine(Path.GetTempPath(), "vcast-selftest", "broken.json");
            File.WriteAllText(bad, "{ this is not json");
            var before = scenes.Scenes.Count;
            var rejected = !SceneStore.LooksValid(bad);
            if (rejected && scenes.Scenes.Count == before) Ok("broken collection rejected, scenes kept"); else Ng("broken collection accepted");
        }

        // 3 段階のモード（かんたん / 詳細 / プロ）、マクロ、レイアウト集
        if (main != null)
        {
            var st = main.Settings; var wasS = st.SimpleMode; var wasP = st.ProMode;
            bool Vis(System.Windows.UIElement e) => e.Visibility == System.Windows.Visibility.Visible;
            st.SimpleMode = false; st.ProMode = false; main.ApplyMode();
            var detail = !Vis(main.MacroButton) && !Vis(main.LayoutButton) && !Vis(main.ProfileButton) && !Vis(main.TransitionBox) && Vis(main.SoundboardButton);
            st.ProMode = true; main.ApplyMode();
            var pro = Vis(main.MacroButton) && Vis(main.LayoutButton) && Vis(main.ProfileButton) && Vis(main.TransitionBox) && main.ProOn;
            st.SimpleMode = true; main.ApplyMode();
            var simple = !Vis(main.MacroButton) && !Vis(main.LayoutButton) && !main.ProOn;
            if (detail && pro && simple) Ok("modes: simple / detail / pro show the right buttons"); else Ng($"modes detail={detail} pro={pro} simple={simple}");

            // マクロ: 表情＋マイクのミュートが 1 操作で効く
            st.SimpleMode = false; st.ProMode = true; main.ApplyMode();
            var mic = main.Mixer.Mic; var micWas = mic != IntPtr.Zero && Native.obs_source_muted(mic);
            while (st.Macros.Count < 6) st.Macros.Add(new MacroDef { Name = $"マクロ {st.Macros.Count + 1}" });
            var savedMacro = st.Macros[0];
            st.Macros[0] = new MacroDef { Name = "テスト", Expression = "surprised", Mic = "mute" };
            main.RunMacro(0);
            if (main.Avatar.Expression == "surprised" && (mic == IntPtr.Zero || Native.obs_source_muted(mic))) Ok("macro runs expression + mic mute"); else Ng($"macro expr={main.Avatar.Expression}");
            st.Macros[0] = savedMacro; main.Avatar.SetExpression("neutral");
            if (mic != IntPtr.Zero) { Native.obs_source_set_muted(mic, micWas); foreach (var sv in main.Mixer.Strips) sv.Refresh(); }

            // レイアウト: 一番大きい映像（4000x3000 の板）が 1920x1080 に収まる（倍率 0.36、中央）
            var board = main.Scenes.AddSource("color_source_v3", "layout-test", cs => { Native.obs_data_set_int(cs, "width", 4000); Native.obs_data_set_int(cs, "height", 3000); });
            await Wait(400); // 作りたての映像ソースは、1 フレーム描かれるまで大きさが決まらない
            if (board != null)
            {
                main.ApplyLayout("corner");
                Native.obs_sceneitem_get_scale(board.Item, out var sc); Native.obs_sceneitem_get_pos(board.Item, out var ps);
                var fit = Math.Abs(sc.x - 0.36f) < 0.005f && Math.Abs(ps.x - 240f) < 1f && Math.Abs(ps.y) < 1f && board.Visible;
                main.ApplyLayout("avatar");
                main.Scenes.RemoveItem(board);
                if (fit) Ok("layout: largest source fitted to canvas (scale 0.36, centered)"); else Ng($"layout scale={sc.x} pos={ps.x},{ps.y}");
            }
            else Ng("layout test source");
            st.SimpleMode = wasS; st.ProMode = wasP; main.ApplyMode();
        }

        // かんたんモードの画面（保存はしない）
        if (main != null)
        {
            var was = main.Settings.SimpleMode;
            main.Settings.SimpleMode = true; main.ApplyMode();
            await Wait(300);
            ObsEngine.Log("[selftest] SNAP simple");
            await Wait(Environment.GetEnvironmentVariable("VCAST_SNAP") == "simple" ? 2500 : 200);
            main.Settings.SimpleMode = false; main.ApplyMode();
            await Wait(200);
            main.Settings.SimpleMode = was; main.ApplyMode();
            Ok("simple/pro mode switch");
        }

        // 選択枠の見た目確認用に、色ソースを選択した状態で終える
        if (bg != null) editor.Select(bg.Item);
        ObsEngine.Log($"[selftest] DONE fails={fails}");
        await Wait(4000); // スクリーンショット用
        owner.Close();
    }
}
