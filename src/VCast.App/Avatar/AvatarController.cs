using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using VCast.AvatarNative;
using VCast.Core;
using VCast.Obs;

namespace VCast.Avatar;

public sealed class AvatarSettings
{
    public string? ModelPath { get; set; }
    public bool CameraEnabled { get; set; }
    public string CameraId { get; set; } = "";
    public bool LipsyncMic { get; set; } = true;
    public bool InputLink { get; set; } = true;
    public bool VmcEnabled { get; set; }
    public int VmcPort { get; set; } = 39539;
    public string Framing { get; set; } = "bust";
    public double HeadSensitivity { get; set; } = 1.2;
    public bool Mirror { get; set; } = true;
    public double Zoom { get; set; } = 1.0;
    public double CameraHeight { get; set; } = 0;
    public double CameraYaw { get; set; } = 0;
    public double Brightness { get; set; } = 1.0;
    public double ExpressionStrength { get; set; } = 1.0;
    public bool VmcFromLan { get; set; } // スマホなど別の機器から VMC を受け取る
    public bool HighRes { get; set; }    // 描画解像度を 2 倍にする（拡大しても綺麗。GPU を少し使う）
    public bool NativeRender { get; set; } // ブラウザ部品を使わず直接描く（実験）
    public bool WalkMouse { get; set; } = true; // 歩かせるモード中、左ボタンを押している間カーソルの位置へ歩く
    public bool WalkEnabled { get; set; } // 右パネルに WASD ON/OFF ボタンを出す
    [System.Text.Json.Serialization.JsonIgnore] // 起動のたびにオフ（オンのまま保存すると、枠が画面いっぱいのまま戻せなくなる）
    public bool WalkMode { get; set; }   // WASD でアバターを歩かせる（ブラウザ描画のみ）
}

public record CameraInfo(string Id, string Label);

// アバター機能のまとめ役: ローカルサーバー・トラッカー・入力・表情ホットキー・マイク口パク・VMC
// 状態の変更と StatusChanged の通知は必ず UI スレッドで行う
public sealed class AvatarController : IDisposable
{
    public static readonly (string Id, string Label)[] Expressions =
    {
        ("neutral", "ふつう"), ("happy", "笑顔"), ("angry", "怒り"), ("sad", "かなしい"), ("relaxed", "おだやか"), ("surprised", "おどろき"),
    };
    public const string SourceName = "3Dアバター";
    const int AvatarWidth = 810, AvatarHeight = 1080;
    int ResW => Settings.HighRes ? AvatarWidth * 2 : AvatarWidth;
    int ResH => Settings.HighRes ? AvatarHeight * 2 : AvatarHeight;

    readonly string _settingsPath = Path.Combine(ObsEngine.ConfigDir, "avatar.json");
    readonly Func<float> _micDb;
    readonly System.Threading.Timer _pump;
    readonly AvatarServer _server;
    VmcReceiver? _vmc;
    TrackerWindow? _tracker;
    string? _trackerUrl;
    int _pumping;
    string _modelUrl = "";
    NativeAvatar? _native;
    public string NativeStatus => _native?.Status ?? "";
    public bool NativeActive => _native != null;
    public NativeAvatar? NativeInstance => _native;

    public AvatarSettings Settings { get; private set; } = new();
    public List<CameraInfo> Cameras { get; private set; } = new();
    public string TrackerStatus { get; private set; } = "カメラはオフ";
    public string ModelStatus { get; private set; } = "モデル未選択";
    public string Expression { get; private set; } = "neutral";
    public bool RenderConnected => _server.Count("render") > 0 || (_native?.Frames ?? 0) > 0;
    public long VmcPackets => _vmc?.Packets ?? 0;
    public string BaseUrl => _server.BaseUrl;
    public string RenderUrl => _server.PageUrl("avatar.html");
    public event Action? StatusChanged;
    public volatile bool FakeTyping;
    public volatile int FakeMoveX, FakeMoveZ; // 自己テストで歩く姿を確認するとき

    // 自己テスト用: 顔追跡の結果を人工的に描画ページへ送る（yaw/pitch/roll はラジアン、blend は 0〜1 の表情）
    public void InjectFace(double yaw, double pitch, double roll, Dictionary<string, double>? blend = null)
    {
        var b = new Dictionary<string, double>
        {
            ["jawOpen"] = 0, ["mouthFunnel"] = 0, ["mouthPucker"] = 0, ["mouthStretchLeft"] = 0, ["mouthStretchRight"] = 0, ["mouthSmileLeft"] = 0, ["mouthSmileRight"] = 0,
            ["eyeBlinkLeft"] = 0.05, ["eyeBlinkRight"] = 0.05, ["browInnerUp"] = 0, ["eyeWideLeft"] = 0, ["eyeWideRight"] = 0,
            ["eyeLookInLeft"] = 0, ["eyeLookOutLeft"] = 0, ["eyeLookInRight"] = 0, ["eyeLookOutRight"] = 0,
            ["eyeLookUpLeft"] = 0, ["eyeLookUpRight"] = 0, ["eyeLookDownLeft"] = 0, ["eyeLookDownRight"] = 0,
        };
        if (blend != null) foreach (var (k, v) in blend) b[k] = v;
        _server.Broadcast("render", "face", Msg(new { type = "face", blend = b, head = new { yaw, pitch, roll, tx = 0.0, ty = 0.0 } }));
    }

    static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public AvatarController(Func<float> micDb)
    {
        _micDb = micDb;
        Load();
        _server = new AvatarServer { ModelPath = Settings.ModelPath };
        if (Settings.ModelPath != null) _modelUrl = NewModelUrl();
        _server.ClientConnected += OnClientConnected;
        _server.Message += (role, type, root) => OnUi(() => OnMessage(type, root));
        if (Settings.VmcEnabled) StartVmc();
        _server.Tap += OnTap;
        if (Settings.NativeRender) StartNative();
        _pump = new System.Threading.Timer(_ => Pump(), null, 33, 33);
        ObsEngine.Log($"[avatar] server {_server.BaseUrl}");
    }

    static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // --- 設定 ---
    void Load()
    {
        try { if (File.Exists(_settingsPath)) Settings = JsonSerializer.Deserialize<AvatarSettings>(File.ReadAllText(_settingsPath)) ?? new(); }
        catch (Exception ex) { ObsEngine.Log("[avatar] settings load: " + ex.Message); }
        if (Settings.ModelPath != null && !File.Exists(Settings.ModelPath)) Settings.ModelPath = null;
    }

    public void Save()
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { ObsEngine.Log("[avatar] settings save: " + ex.Message); }
    }

    // 同じファイルを選び直しても読み直すよう、URL に通し番号を付ける
    string NewModelUrl() => $"/model.vrm?t={_server.Token}&v={DateTime.Now.Ticks}";

    // ---- ネイティブ描画（実験） ----
    void OnTap(string type, string json) => _native?.Feed(type, json);

    void StartNative()
    {
        if (_native != null) return;
        if (!NativeAvatarSource.Registered) { ObsEngine.Log("[avatar-native] source not registered"); Settings.NativeRender = false; return; }
        _native = new NativeAvatar(ResW, ResH);
        _native.StatusChanged += s => { ModelStatus = s; OnUi(() => StatusChanged?.Invoke()); };
        _native.Feed("config", ConfigJson());
        _native.Feed("expression", Msg(new { type = "expression", name = Expression }));
        _native.SetModel(Settings.ModelPath);
    }

    void StopNative() { _native?.Dispose(); _native = null; }

    public void SetModel(string path)
    {
        Settings.ModelPath = path;
        _native?.SetModel(path);
        _server.ModelPath = path;
        _modelUrl = NewModelUrl();
        ModelStatus = RenderConnected ? "読み込み中…" : "配信画面に置くと表示されます";
        Save();
        _server.Broadcast("render", "model", Msg(new { type = "model", url = _modelUrl }), reliable: true);
        StatusChanged?.Invoke();
    }

    // 歩かせるモードの切替（ボタン・ホットキー共通）。オンにしたらボタン表示も有効にする
    public void SetWalk(bool on, SceneManager scenes)
    {
        Settings.WalkMode = on; if (on) Settings.WalkEnabled = true;
        ApplyConfig(); ApplyResolution(scenes);
    }

    // 設定を保存して描画ページ・VMC・トラッカーへ反映（トラッカーはカメラ設定が変わったときだけ作り直す）
    public void ApplyConfig()
    {
        Save();
        _server.Broadcast("render", "config", ConfigJson(), reliable: true);
        if (_vmc != null && (!Settings.VmcEnabled || _vmc.FromLan != Settings.VmcFromLan)) { _vmc.Dispose(); _vmc = null; }
        if (Settings.VmcEnabled && _vmc == null) StartVmc();
        UpdateTracker();
        StatusChanged?.Invoke();
    }

    string ConfigJson() => Msg(new
    {
        type = "config",
        config = new
        {
            lipsyncMic = Settings.LipsyncMic, inputLink = Settings.InputLink, framing = Settings.Framing,
            headSensitivity = Settings.HeadSensitivity, mirror = Settings.Mirror, zoom = Settings.Zoom,
            cameraHeight = Settings.CameraHeight, cameraYaw = Settings.CameraYaw, brightness = Settings.Brightness,
            expressionStrength = Settings.ExpressionStrength, walkMode = Settings.WalkMode, walkMouse = Settings.WalkMouse,
        },
    });

    // 描画ページだけに設定を送る（スライダー操作中など。保存・トラッカー更新はしない）
    public void PreviewConfig() => _server.Broadcast("render", "config", ConfigJson());

    // いまの顔の向き・目の開き具合を「正面」として覚え直す
    public void Calibrate() => _server.Broadcast("render", "calibrate", Msg(new { type = "calibrate" }), reliable: true);
    static string Msg(object o) => JsonSerializer.Serialize(o, Json);

    // 新しく繋がった描画ページには、その接続にだけ現在の状態を送る
    void OnClientConnected(AvatarServer.Client c)
    {
        if (c.Role != "render") return;
        c.Enqueue("config", ConfigJson(), true);
        if (_modelUrl.Length > 0) c.Enqueue("model", Msg(new { type = "model", url = _modelUrl }), true);
        c.Enqueue("expression", Msg(new { type = "expression", name = Expression }), true);
        OnUi(() => StatusChanged?.Invoke());
    }

    public void SetExpression(string name)
    {
        Expression = name;
        _server.Broadcast("render", "expression", Msg(new { type = "expression", name }), reliable: true);
        StatusChanged?.Invoke();
    }

    // --- 受信（UI スレッド） ---
    void OnMessage(string type, JsonElement root)
    {
        try
        {
            switch (type)
            {
                case "cameras":
                    Cameras = root.GetProperty("list").EnumerateArray().Take(32)
                        .Select(e => new CameraInfo(e.GetProperty("id").GetString() ?? "", Trim(e.GetProperty("label").GetString()))).ToList();
                    break;
                case "tracker-status": TrackerStatus = Trim(root.GetProperty("text").GetString()); break;
                case "model-status": ModelStatus = Trim(root.GetProperty("text").GetString()); ObsEngine.Log("[avatar] model: " + ModelStatus); break;
                case "log": ObsEngine.Log("[avatar-js] " + (root.GetProperty("text").GetString() ?? "")); return;
                default: return;
            }
            StatusChanged?.Invoke();
        }
        catch (Exception ex) { ObsEngine.Log("[avatar] message: " + ex.Message); }
    }

    static string Trim(string? s) => s == null ? "" : s.Length > 120 ? s[..120] : s;

    // --- VMC ---
    void StartVmc()
    {
        try
        {
            _vmc = new VmcReceiver(Settings.VmcPort, Settings.VmcFromLan);
            _vmc.Frame += (blend, bones) => _server.Broadcast("render", "vmc", Msg(new { type = "vmc", blend, bones }));
        }
        catch (Exception ex) { ObsEngine.Log("[avatar] vmc: " + ex.Message); _vmc = null; }
    }

    // --- トラッカー（隠し WebView2） ---
    void UpdateTracker()
    {
        if (Settings.CameraEnabled)
        {
            var url = _server.PageUrl("tracker.html", "&cam=" + Uri.EscapeDataString(Settings.CameraId));
            if (_tracker == null) { _tracker = new TrackerWindow(_server.Origin); _tracker.Show(); _trackerUrl = null; }
            if (url != _trackerUrl) { _tracker.NavigateTo(url); _trackerUrl = url; }
        }
        else if (_tracker != null)
        {
            _tracker.Shutdown();
            _tracker = null;
            _trackerUrl = null;
            TrackerStatus = "カメラはオフ";
        }
    }

    public void ShowCameraCheck(bool show) => _tracker?.SetVisibleOnScreen(show);

    // --- 30Hz: マイク音量と入力状態を描画ページへ（タイマースレッド、多重実行しない） ---
    long _lastKeyL, _lastKeyR, _lastMouse;
    POINT _lastCursor;

    void Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) == 1) return;
        try
        {
            if (_server.Count("render") == 0 && _native == null) return;
            var db = _micDb();
            if (float.IsNaN(db) || float.IsInfinity(db)) db = -100;
            _server.Broadcast("render", "mic", Msg(new { type = "mic", db = Math.Round(Math.Max(-100, db), 1) }));
            // 歩かせるモード中だけ W/A/S/D の押下を送る（ほかのキーは見ない）
            if (Settings.WalkMode)
            {
                int Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0 ? 1 : 0;
                var fk = FakeMoveX != 0 || FakeMoveZ != 0;
                GetCursorPos(out var wc);
                double ww = Math.Max(1, GetSystemMetrics(0)), wh = Math.Max(1, GetSystemMetrics(1));
                _server.Broadcast("render", "move", Msg(new
                {
                    type = "move",
                    x = fk ? FakeMoveX : Down(0x44) - Down(0x41), z = fk ? FakeMoveZ : Down(0x57) - Down(0x53),
                    turn = Down(0x45) - Down(0x51), // E=右回り Q=左回り
                    run = Down(0x10) == 1, // Shift を押している間は走る
                    mouse = Settings.WalkMouse && Down(0x01) == 1, // 左ボタンが押されている間だけカーソル位置を渡す
                    mx = Math.Round(Math.Clamp(wc.X / ww, 0, 1), 3), my = Math.Round(Math.Clamp(wc.Y / wh, 0, 1), 3),
                }));
            }
            if (!Settings.InputLink) return;
            var now = Environment.TickCount64;
            // どのキーかは記録・送信しない。左右どちら側のキーが押されているかだけ
            for (var vk = 0x08; vk <= 0xDE; vk++)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                if (LeftHand(vk)) _lastKeyL = now; else _lastKeyR = now;
            }
            GetCursorPos(out var cp);
            if (cp.X != _lastCursor.X || cp.Y != _lastCursor.Y || (GetAsyncKeyState(0x01) & 0x8000) != 0) _lastMouse = now;
            _lastCursor = cp;
            double sw = Math.Max(1, GetSystemMetrics(0)), sh = Math.Max(1, GetSystemMetrics(1));
            var fake = FakeTyping; // 自己テストで打鍵中の姿勢を確認するとき
            _server.Broadcast("render", "input", Msg(new
            {
                type = "input",
                typingL = fake || now - _lastKeyL < 350 ? 1 : 0,
                typingR = fake || now - _lastKeyR < 350 ? 1 : 0,
                mouseActive = now - _lastMouse < 600,
                mouseX = Math.Round(Math.Clamp(cp.X / sw, 0, 1), 3),
                mouseY = Math.Round(Math.Clamp(cp.Y / sh, 0, 1), 3),
            }));
        }
        catch { }
        finally { Volatile.Write(ref _pumping, 0); }
    }

    // キーボードの左半分（左手で打つキー）
    static bool LeftHand(int vk) => vk is 0x09 or 0x10 or 0x11 or 0x12 or 0x14 or 0x1B or 0x20 or 0xA0 or 0xA2 or 0xA4
        || "12345QWERTASDFGZXCVB".Contains((char)vk);

    // 表情ホットキーの割り当ては HotkeyManager（設定画面「ホットキー」）が持つ
    public static string HotkeyLabel(int index) => HotkeyManager.LabelFor("expr." + Expressions[index].Id);

    // --- OBS のソース ---
    public SceneItemVm? AddToScene(SceneManager scenes)
    {
        var existing = scenes.Items.FirstOrDefault(i => i.Name == SourceName);
        if (existing != null) return existing;
        if (Settings.NativeRender) StartNative();
        var vm = _native != null ? scenes.AddSource(NativeAvatarSource.Id, SceneManager.UniqueName(SourceName), _ => { }) : scenes.AddSource("browser_source", SceneManager.UniqueName(SourceName), s =>
        {
            Native.obs_data_set_string(s, "url", RenderUrl);
            Native.obs_data_set_bool(s, "is_local_file", false);
            Native.obs_data_set_int(s, "width", ResW);
            Native.obs_data_set_int(s, "height", ResH);
            Native.obs_data_set_bool(s, "fps_custom", true);
            Native.obs_data_set_int(s, "fps", 60);
            Native.obs_data_set_bool(s, "shutdown", false);
        });
        if (vm != null)
        {
            // 右下に置く（配信画面の右側に立っている構図）
            var p = new Native.vec2 { x = ObsEngine.BaseWidth - AvatarWidth, y = ObsEngine.BaseHeight - AvatarHeight };
            Native.obs_sceneitem_set_pos(vm.Item, ref p);
            KeepOnScreenSize(vm.Item);
        }
        return vm;
    }

    // 描画解像度を変えても、画面上の大きさ（と位置）はそのまま（標準の 810x1080 相当）
    void KeepOnScreenSize(IntPtr item)
    {
        var f = (float)AvatarWidth / ResW;
        var sc = new Native.vec2 { x = f, y = f };
        Native.obs_sceneitem_set_scale(item, ref sc);
    }

    // 設定の切り替えを、すでにシーンにあるアバターへ反映
    // 歩かせるモード: アバターの枠を画面いっぱいに広げる（歩ける範囲 = 画面全体）。オフにしたら元の位置・大きさへ
    (Native.vec2 pos, Native.vec2 scale, int w, int h)? _walkSaved;
    void ApplyWalkLayout(SceneManager scenes)
    {
        var vm = scenes.Items.FirstOrDefault(i => i.Name == SourceName);
        if (vm == null || _native != null) return;
        if (Settings.WalkMode && _walkSaved == null)
        {
            var s0 = Native.obs_source_get_settings(vm.Source);
            Native.obs_sceneitem_get_pos(vm.Item, out var p0); Native.obs_sceneitem_get_scale(vm.Item, out var sc0);
            _walkSaved = (p0, sc0, (int)Native.obs_data_get_int(s0, "width"), (int)Native.obs_data_get_int(s0, "height"));
            Native.obs_data_release(s0);
            var k = Settings.HighRes ? 2 : 1;
            SetBrowserSize(vm, (int)ObsEngine.BaseWidth * k, (int)ObsEngine.BaseHeight * k);
            var p = new Native.vec2 { x = 0, y = 0 }; var sc = new Native.vec2 { x = 1f / k, y = 1f / k };
            Native.obs_sceneitem_set_pos(vm.Item, ref p); Native.obs_sceneitem_set_scale(vm.Item, ref sc);
        }
        else if (!Settings.WalkMode && _walkSaved is { } sv)
        {
            _walkSaved = null;
            SetBrowserSize(vm, sv.w, sv.h);
            var p = sv.pos; var sc = sv.scale;
            Native.obs_sceneitem_set_pos(vm.Item, ref p); Native.obs_sceneitem_set_scale(vm.Item, ref sc);
        }
    }

    static void SetBrowserSize(SceneItemVm vm, int w, int h)
    {
        var ns = Native.obs_data_create();
        Native.obs_data_set_int(ns, "width", w); Native.obs_data_set_int(ns, "height", h);
        Native.obs_source_update(vm.Source, ns);
        Native.obs_data_release(ns);
    }

    public void ApplyResolution(SceneManager scenes)
    {
        ApplyWalkLayout(scenes);
        if (Settings.WalkMode && _native == null && !Settings.NativeRender) return; // 歩行中は枠が画面いっぱい。解像度の補正は戻してから
        // ネイティブ描画の入り切り（実験）: 描画スレッドを起こす/止め、シーン上のソースを差し替える（位置と拡大率は引き継ぐ）
        if (Settings.NativeRender) StartNative(); else StopNative();
        var vm = scenes.Items.FirstOrDefault(i => i.Name == SourceName);
        if (vm == null) return;
        var isNativeSource = Marshal.PtrToStringUTF8(Native.obs_source_get_id(vm.Source)) == NativeAvatarSource.Id;
        if (isNativeSource != (_native != null))
        {
            Native.obs_sceneitem_get_pos(vm.Item, out var pos);
            Native.obs_sceneitem_get_scale(vm.Item, out var scale);
            scenes.RemoveItem(vm);
            var nv = AddToScene(scenes);
            if (nv != null) { Native.obs_sceneitem_set_pos(nv.Item, ref pos); Native.obs_sceneitem_set_scale(nv.Item, ref scale); }
            return;
        }
        if (isNativeSource)
        {
            // 描画の細かさを変えても画面上の大きさは保つ
            var oldW = Math.Max(1, _native!.Width);
            _native.SetResolution(ResW, ResH);
            Native.obs_sceneitem_get_scale(vm.Item, out var sc0);
            var r0 = (float)oldW / ResW; sc0.x *= r0; sc0.y *= r0;
            Native.obs_sceneitem_set_scale(vm.Item, ref sc0);
            return;
        }
        var s = Native.obs_source_get_settings(vm.Source);
        var curW = (int)Native.obs_data_get_int(s, "width");
        Native.obs_data_release(s);
        if (curW == ResW) return;
        // 拡大率は「画面上の大きさ」を保つように掛け直す（ユーザーが手で拡大縮小していても比率は保つ）
        Native.obs_sceneitem_get_scale(vm.Item, out var sc);
        var ratio = (float)curW / ResW;
        sc.x *= ratio; sc.y *= ratio;
        var ns = Native.obs_data_create();
        Native.obs_data_set_int(ns, "width", ResW);
        Native.obs_data_set_int(ns, "height", ResH);
        Native.obs_source_update(vm.Source, ns);
        Native.obs_data_release(ns);
        Native.obs_sceneitem_set_scale(vm.Item, ref sc);
    }

    public void Dispose()
    {
        _pump.Dispose();
        StopNative();
        _tracker?.Shutdown();
        _vmc?.Dispose();
        _server.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] static extern int GetSystemMetrics(int idx);
}
