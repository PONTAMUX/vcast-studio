using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VCast.Obs;

namespace VCast.AvatarNative;

// ブラウザ部品なしで VRM を直接描く（実験）。専用スレッドで 60fps: 入力（顔・VMC・マイク・キー入力・設定）→ 動き → 揺れ物 → D3D11 描画 → 共有テクスチャ。
// 入力はブラウザ方式が描画ページへ送るのと同じ JSON をそのまま受け取る（AvatarServer.Tap）
public sealed class NativeAvatar : IDisposable
{
    public static NativeAvatar? Current { get; private set; }

    readonly Thread _thread;
    volatile bool _run = true;
    readonly object _lk = new();
    int _w, _h;
    string? _pendingModel; bool _modelDirty;
    int _pendingW, _pendingH;
    Renderer? _r; VrmModel? _model; Motion? _motion; SpringBone? _spring;
    string _status = "モデル未選択";

    // 入力（受信スレッドから書き、描画スレッドが読む）
    readonly MotionInput _inp = new();
    long _faceAt, _vmcAt;
    volatile bool _calibrate;
    string _framing = "bust"; double _zoom = 1, _camHeight, _camYaw, _brightness = 1;
    public long Frames;
    public double LastFrameMs;

    public int Width => _w; public int Height => _h;
    public uint SharedHandle { get { var r = _r; return r == null ? 0 : r.SharedHandle; } }
    public string Status => _status;
    public string? ModelName => _model?.Title;
    public event Action<string>? StatusChanged;

    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);

    public NativeAvatar(int width, int height)
    {
        _w = width; _h = height; _pendingW = width; _pendingH = height;
        Current = this;
        _thread = new Thread(Loop) { IsBackground = true, Name = "NativeAvatar", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void SetModel(string? path) { lock (_lk) { _pendingModel = path; _modelDirty = true; } }
    public void SetResolution(int w, int h) { lock (_lk) { _pendingW = w; _pendingH = h; } _w = w; _h = h; }
    void SetStatus(string s) { _status = s; StatusChanged?.Invoke(s); ObsEngine.Log("[avatar-native] " + s); }

    // ---- 入力（AvatarServer が描画ページへ送る JSON と同じ形式） ----
    public void Feed(string type, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (type)
            {
                case "face":
                {
                    var blend = new Dictionary<string, float>();
                    if (root.TryGetProperty("blend", out var b)) foreach (var p in b.EnumerateObject()) blend[p.Name] = (float)p.Value.GetDouble();
                    var h = root.GetProperty("head");
                    lock (_lk)
                    {
                        _inp.FaceBlend = blend;
                        _inp.Yaw = (float)h.GetProperty("yaw").GetDouble(); _inp.Pitch = (float)h.GetProperty("pitch").GetDouble(); _inp.Roll = (float)h.GetProperty("roll").GetDouble();
                        _inp.Tx = h.TryGetProperty("tx", out var tx) ? (float)tx.GetDouble() : 0; _inp.Ty = h.TryGetProperty("ty", out var ty) ? (float)ty.GetDouble() : 0;
                        _faceAt = Environment.TickCount64;
                    }
                    break;
                }
                case "vmc":
                {
                    var blend = new Dictionary<string, float>(); var bones = new Dictionary<string, float[]>();
                    if (root.TryGetProperty("blend", out var b)) foreach (var p in b.EnumerateObject()) blend[p.Name] = (float)p.Value.GetDouble();
                    if (root.TryGetProperty("bones", out var bs)) foreach (var p in bs.EnumerateObject()) bones[p.Name] = p.Value.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                    lock (_lk) { _inp.VmcBlend = blend; _inp.VmcBones = bones; _vmcAt = Environment.TickCount64; }
                    break;
                }
                case "mic": lock (_lk) _inp.MicDb = (float)root.GetProperty("db").GetDouble(); break;
                case "expression": lock (_lk) _inp.Expression = root.GetProperty("name").GetString() ?? "neutral"; break;
                case "input":
                    lock (_lk)
                    {
                        _inp.TypingL = root.GetProperty("typingL").GetInt32() != 0; _inp.TypingR = root.GetProperty("typingR").GetInt32() != 0;
                        _inp.MouseActive = root.GetProperty("mouseActive").GetBoolean(); _inp.MouseX = (float)root.GetProperty("mouseX").GetDouble();
                    }
                    break;
                case "calibrate": _calibrate = true; break;
                case "config":
                {
                    var c = root.GetProperty("config");
                    lock (_lk)
                    {
                        var m = _motion;
                        if (m != null) ApplyMotionCfg(m.Cfg, c);
                        _pendingCfg = c.Clone();
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { ObsEngine.Log("[avatar-native] feed " + type + ": " + ex.Message); }
    }

    int _errors;
    JsonElement? _pendingCfg;
    void ApplyMotionCfg(MotionConfig cfg, JsonElement c)
    {
        if (c.TryGetProperty("lipsyncMic", out var a)) cfg.LipsyncMic = a.GetBoolean();
        if (c.TryGetProperty("inputLink", out a)) cfg.InputLink = a.GetBoolean();
        if (c.TryGetProperty("mirror", out a)) cfg.Mirror = a.GetBoolean();
        if (c.TryGetProperty("headSensitivity", out a)) cfg.HeadSensitivity = (float)a.GetDouble();
        if (c.TryGetProperty("expressionStrength", out a)) cfg.ExpressionStrength = (float)a.GetDouble();
        if (c.TryGetProperty("framing", out a)) _framing = a.GetString() ?? "bust";
        if (c.TryGetProperty("zoom", out a)) _zoom = a.GetDouble();
        if (c.TryGetProperty("cameraHeight", out a)) _camHeight = a.GetDouble();
        if (c.TryGetProperty("cameraYaw", out a)) _camYaw = a.GetDouble();
        if (c.TryGetProperty("brightness", out a)) _brightness = a.GetDouble();
    }

    // ---- 描画スレッド ----
    void Loop()
    {
        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        double next = 0, last = 0;
        try
        {
            while (_run)
            {
                var now = sw.Elapsed.TotalSeconds;
                if (now < next) { Thread.Sleep(1); continue; }
                next = Math.Max(next + 1.0 / 60, now - 0.05);
                var dt = (float)Math.Clamp(now - last, 0.001, 0.1); last = now;
                try { Tick((float)now, dt); _errors = 0; }
                catch (Exception ex)
                {
                    SetStatus("描画エラー: " + ex.Message); ObsEngine.Log("[avatar-native] " + ex);
                    // GPU のリセットなどで続けて失敗するときは、描画の土台ごと作り直す（モデルも読み直される）
                    if (++_errors >= 3) { _errors = 0; try { _r?.Dispose(); } catch { } _r = null; _model = null; lock (_lk) { _modelDirty = true; } }
                    Thread.Sleep(500);
                }
            }
        }
        finally { _r?.Dispose(); timeEndPeriod(1); }
    }

    void Tick(float now, float dt)
    {
        string? modelPath = null; bool dirty; int rw, rh;
        lock (_lk) { dirty = _modelDirty; modelPath = _pendingModel; _modelDirty = false; rw = _pendingW; rh = _pendingH; }
        if (_r == null || _r.W != rw || _r.H != rh)
        {
            _r?.Dispose(); _r = null; _model = null;
            _r = new Renderer(rw, rh);
            dirty = true; lock (_lk) { modelPath = _pendingModel; _modelDirty = false; }
        }
        if (dirty)
        {
            _model = null; _motion = null; _spring = null;
            if (string.IsNullOrEmpty(modelPath)) { SetStatus("モデル未選択"); }
            else
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var m = VrmModel.Load(modelPath);
                    _r.SetModel(m);
                    _model = m; _motion = new Motion(m); _spring = new SpringBone(m);
                    lock (_lk) { if (_pendingCfg is JsonElement c) ApplyMotionCfg(_motion.Cfg, c); }
                    SetStatus($"表示中（ネイティブ）: {m.Title}（{(m.IsVrm0 ? "VRM0" : "VRM1")}、読み込み {sw.ElapsedMilliseconds}ms）");
                }
                catch (Exception ex) { SetStatus("読み込み失敗: " + ex.Message); }
            }
        }
        if (_model == null || _motion == null || _spring == null) return;

        if (_calibrate) { _calibrate = false; _motion.Calibrate(); }
        MotionInput snap;
        lock (_lk)
        {
            var tick = Environment.TickCount64;
            _inp.FaceLive = tick - _faceAt < 800; _inp.VmcLive = tick - _vmcAt < 800;
            snap = new MotionInput
            {
                FaceLive = _inp.FaceLive, Yaw = _inp.Yaw, Pitch = _inp.Pitch, Roll = _inp.Roll, Tx = _inp.Tx, Ty = _inp.Ty, FaceBlend = _inp.FaceBlend,
                VmcLive = _inp.VmcLive, VmcBones = _inp.VmcBones, VmcBlend = _inp.VmcBlend, MicDb = _inp.MicDb, Expression = _inp.Expression,
                TypingL = _inp.TypingL, TypingR = _inp.TypingR, MouseActive = _inp.MouseActive, MouseX = _inp.MouseX,
            };
        }
        var t0 = Stopwatch.GetTimestamp();
        _motion.Update(now, dt, snap);
        _model.ApplyConstraints(_motion.Raw);
        _spring.Update(dt, _motion.Raw);
        _r.Pose(_motion.Raw, _motion.Expr);
        _r.Brightness = (float)_brightness;
        FrameCamera(_r, _model);
        if (_capReq is { } cap) { _capReq = null; try { _capResult = _r.Capture(); } catch { } cap.Set(); }
        Interlocked.Increment(ref Frames);
        LastFrameMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // avatar.js の frame() と同じ画角: 胸から上 / 全身、ズーム・高さ・回り込み
    void FrameCamera(Renderer r, VrmModel m)
    {
        var minY = m.BoundsMin.Y; var maxY = m.BoundsMax.Y;
        var height = Math.Max(0.1f, maxY - minY);
        var head = r.HeadWorld;
        float yMin, yMax;
        if (_framing == "full") { yMin = minY - height * 0.03f; yMax = maxY + height * 0.05f; }
        else { yMin = head.Y - Math.Max(height * 0.3f, (maxY - head.Y) * 2.2f); yMax = maxY + height * 0.06f; }
        yMin = Math.Max(yMin, minY - height * 0.03f);
        var span = (yMax - yMin) / (float)Math.Max(0.3, _zoom);
        var centerY = (yMin + yMax) / 2 + (float)_camHeight * height;
        const float fov = 28f;
        var dist = span / 2 / MathF.Tan(fov * MathF.PI / 360f);
        var yaw = (float)_camYaw * MathF.PI / 180f;
        var cz = (m.BoundsMin.Z + m.BoundsMax.Z) / 2;
        if (m.IsVrm0)
        {
            // VRM0 は元のモデルが -Z 向き。ブラウザ方式は全体を 180° 回して表示するので、同じ画角をモデル側の座標へ写す
            var czRot = -cz;
            var cam = new Vector3(-MathF.Sin(yaw) * (dist + 0.1f), centerY, -(czRot + MathF.Cos(yaw) * (dist + 0.1f)));
            r.RenderView(cam, new Vector3(0, centerY, cz), fov);
        }
        else r.RenderView(new Vector3(MathF.Sin(yaw) * (dist + 0.1f), centerY, cz + MathF.Cos(yaw) * (dist + 0.1f)), new Vector3(0, centerY, cz), fov);
    }

    // 自己テスト用: 直前のフレームを BGRA で取り出す（D3D の即時コンテキストは描画スレッド専用なので、依頼して待つ）
    ManualResetEventSlim? _capReq; byte[]? _capResult;
    public byte[]? CaptureFrame(int timeoutMs = 3000)
    {
        var ev = new ManualResetEventSlim(false); _capResult = null; _capReq = ev;
        return ev.Wait(timeoutMs) ? _capResult : null;
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(3000);
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
