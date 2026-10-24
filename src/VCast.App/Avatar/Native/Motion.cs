using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace VCast.AvatarNative;

// avatar.js の applyMotion の C# 版。入力（顔・VMC・マイク・キーボード/マウス・表情）から、毎フレームのボーン回転と表情の重みを作る
public sealed class MotionInput
{
    public bool FaceLive; public float Yaw, Pitch, Roll, Tx, Ty; public Dictionary<string, float> FaceBlend = new();
    public bool VmcLive; public Dictionary<string, float[]> VmcBones = new(); public Dictionary<string, float> VmcBlend = new();
    public float MicDb = -100;
    public string Expression = "neutral";
    public bool TypingL, TypingR, MouseActive; public float MouseX = 0.5f;
}

public sealed class MotionConfig
{
    public bool LipsyncMic = true, InputLink = true, Mirror = true;
    public float HeadSensitivity = 1.2f, ExpressionStrength = 1f;
}

public sealed class Motion
{
    sealed class OneEuro
    {
        readonly float _min, _beta, _d; float? _x; float _dx;
        public OneEuro(float min = 1f, float beta = 0.3f, float d = 1f) { _min = min; _beta = beta; _d = d; }
        static float Alpha(float cutoff, float dt) { var tau = 1f / (2f * MathF.PI * cutoff); return 1f / (1f + tau / dt); }
        public float Filter(float v, float dt)
        {
            if (_x == null) { _x = v; return v; }
            var dv = (v - _x.Value) / dt;
            _dx += Alpha(_d, dt) * (dv - _dx);
            var cutoff = _min + _beta * MathF.Abs(_dx);
            _x += Alpha(cutoff, dt) * (v - _x.Value);
            return _x.Value;
        }
    }

    static float Approach(float cur, float target, float dt, float up, float? down = null)
        => cur + (target - cur) * (1f - MathF.Exp(-dt / (target > cur ? up : (down ?? up))));
    static float Clamp(float v, float a, float b) => MathF.Max(a, MathF.Min(b, v));
    static float C01(float v) => Clamp(v, 0, 1);

    readonly VrmModel _m; readonly float _sx; readonly Random _rnd = new(1234);
    readonly OneEuro _fy = new(), _fp = new(), _fr = new(), _ftx = new(0.8f, 0.2f), _fty = new(0.8f, 0.2f);
    readonly Dictionary<string, float> _sm = new();
    float _nextBlink = 3f, _blinkPhase = -1f; bool _double;
    float _micFloor = -60f, _micFloorAt;
    float _lastTypeL = -1e9f, _lastTypeR = -1e9f;
    float _wYaw, _wPitch, _wNext;
    float _calYaw, _calPitch, _calRoll, _calBlinkL = 0.15f, _calBlinkR = 0.15f; bool _needCalib = true; float _firstFaceAt;
    public MotionConfig Cfg = new();

    // 正規化ボーン（T ポーズ・ワールド軸そろい）の局所回転
    readonly Dictionary<string, Quaternion> _norm = new();
    public Dictionary<string, float> Expr { get; } = new();
    public Dictionary<int, Quaternion> Raw { get; } = new();

    public Motion(VrmModel m) { _m = m; _sx = m.IsVrm0 ? -1f : 1f; }
    float S(string k) => _sm.GetValueOrDefault(k);
    public void Calibrate() { _needCalib = true; _firstFaceAt = 0; }

    Quaternion Euler(float x, float y, float z) => Quaternion.CreateFromYawPitchRoll(y, x, z); // three.js の YXZ と同じ合成
    void SetRot(string n, float x, float y, float z) { if (_m.Humanoid.ContainsKey(n)) _norm[n] = Euler(x * _sx, y, z * _sx); }

    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        var ax = Vector3.Normalize(Vector3.Cross(a, b));
        return Quaternion.CreateFromAxisAngle(ax, MathF.Acos(d));
    }
    Vector3 ArmDir(float side, float x, float y, float z) => Vector3.Normalize(new Vector3(side * x * _sx, y, z * _sx));

    void ApplyArms(float downL, float downR, float t, MotionInput inp)
    {
        var mouse = Cfg.InputLink && inp.MouseActive ? inp.MouseX - 0.5f : 0f;
        void One(string name, float side, float down, float tap, float mx)
        {
            if (!_m.Humanoid.ContainsKey(name + "UpperArm") || !_m.Humanoid.ContainsKey(name + "LowerArm")) return;
            var rest = ArmDir(side, 1, 0, 0);
            var k = 1 - down;
            var v1 = Vector3.Normalize(Vector3.Lerp(ArmDir(side, 0.32f, -0.95f, 0.02f), ArmDir(side, 0.22f, -0.9f, 0.3f), k));
            var v2 = Vector3.Normalize(Vector3.Lerp(ArmDir(side, 0.18f, -0.97f, 0.12f), ArmDir(side, -0.12f + mx * 0.5f * -side, -0.18f + tap, 0.97f), k));
            var qa = FromTo(rest, v1); var qb = FromTo(rest, v2);
            _norm[name + "UpperArm"] = qa;
            _norm[name + "LowerArm"] = Quaternion.Normalize(Quaternion.Inverse(qa) * qb);
        }
        One("left", 1, downL, inp.TypingL ? MathF.Sin(t * 22f) * 0.06f : 0f, 0);
        One("right", -1, downR, inp.TypingR ? MathF.Sin(t * 19f + 1f) * 0.06f : 0f, mouse);
    }

    // now: 秒（単調増加）、dt: 秒
    public void Update(float now, float dt, MotionInput inp)
    {
        dt = Math.Clamp(dt, 0.001f, 0.1f);
        var t = now; var mir = Cfg.Mirror ? 1f : -1f;
        var face = inp.FaceLive; var vmc = inp.VmcLive;

        if (face && _needCalib)
        {
            if (_firstFaceAt == 0) _firstFaceAt = now;
            if (now - _firstFaceAt > 2f)
            {
                _calYaw = inp.Yaw; _calPitch = inp.Pitch; _calRoll = inp.Roll;
                _calBlinkL = MathF.Min(0.5f, inp.FaceBlend.GetValueOrDefault("eyeBlinkLeft")); _calBlinkR = MathF.Min(0.5f, inp.FaceBlend.GetValueOrDefault("eyeBlinkRight"));
                _needCalib = false;
            }
        }

        // --- 頭 ---
        float yaw = 0, pitch = 0, roll = 0, tx = 0, ty = 0;
        if (face)
        {
            var k = Cfg.HeadSensitivity;
            yaw = Clamp((inp.Yaw - _calYaw) * k, -0.8f, 0.8f) * mir;
            pitch = Clamp((inp.Pitch - _calPitch) * k, -0.5f, 0.5f);
            roll = Clamp((inp.Roll - _calRoll) * k, -0.45f, 0.45f) * mir;
            tx = inp.Tx * mir; ty = inp.Ty;
        }
        else if (!vmc)
        {
            yaw = MathF.Sin(t * 0.31f) * 0.04f + MathF.Sin(t * 0.77f) * 0.015f;
            pitch = MathF.Sin(t * 0.23f) * 0.025f + MathF.Sin(t * 0.61f) * 0.01f;
            roll = MathF.Sin(t * 0.19f) * 0.02f;
        }
        yaw = _fy.Filter(yaw, dt); pitch = _fp.Filter(pitch, dt); roll = _fr.Filter(roll, dt);
        tx = _ftx.Filter(tx, dt); ty = _fty.Filter(ty, dt);

        if (!vmc || face)
        {
            SetRot("neck", pitch * 0.4f, yaw * 0.4f, roll * 0.4f);
            SetRot("head", pitch * 0.6f, yaw * 0.6f, roll * 0.6f);
            var breath = MathF.Sin(t * 1.6f);
            _sm["sway"] = Approach(S("sway"), Clamp(tx * 2f, -0.12f, 0.12f), dt, 0.25f);
            _sm["lean"] = Approach(S("lean"), Clamp(-ty * 1.5f, -0.1f, 0.1f), dt, 0.25f);
            SetRot("spine", breath * 0.012f + S("lean") * 0.5f, yaw * 0.1f, S("sway") * 0.6f);
            SetRot("chest", breath * 0.008f, yaw * 0.05f, S("sway") * 0.3f);
            SetRot("hips", 0, MathF.Sin(t * 0.13f) * 0.02f, 0);
        }
        if (vmc) ApplyVmcBones(inp, face);

        // --- 目線 ---
        float eyeYaw = 0, eyePitch = 0;
        if (face)
        {
            float F(string k) => inp.FaceBlend.GetValueOrDefault(k);
            var horiz = ((F("eyeLookInLeft") - F("eyeLookOutLeft")) + (F("eyeLookOutRight") - F("eyeLookInRight"))) / 2f;
            eyeYaw = horiz * 22f * mir;
            eyePitch = (((F("eyeLookUpLeft") + F("eyeLookUpRight")) - (F("eyeLookDownLeft") + F("eyeLookDownRight"))) / 2f) * 15f;
        }
        else if (!vmc)
        {
            if (now > _wNext)
            {
                _wYaw = ((float)_rnd.NextDouble() - 0.5f) * 16f; _wPitch = ((float)_rnd.NextDouble() - 0.5f) * 8f;
                _wNext = now + 3f + (float)_rnd.NextDouble() * 5f;
                if (_rnd.NextDouble() < 0.4) _wYaw = _wPitch = 0;
            }
            eyeYaw = _wYaw; eyePitch = _wPitch;
        }
        _sm["eyeYaw"] = Approach(S("eyeYaw"), eyeYaw, dt, 0.05f); _sm["eyePitch"] = Approach(S("eyePitch"), eyePitch, dt, 0.05f);
        ApplyLookAt(S("eyeYaw"), S("eyePitch"));

        // --- 口・目・表情の目標 ---
        var tgt = new Dictionary<string, float> { ["aa"] = 0, ["ih"] = 0, ["ou"] = 0, ["ee"] = 0, ["oh"] = 0, ["blinkL"] = 0, ["blinkR"] = 0, ["happy"] = 0, ["angry"] = 0, ["sad"] = 0, ["relaxed"] = 0, ["surprised"] = 0 };
        var perfectSync = _m.Expressions.ContainsKey("jawOpen");
        if (face)
        {
            float f(string k) => inp.FaceBlend.GetValueOrDefault(k);
            if (!perfectSync)
            {
                tgt["aa"] = C01(f("jawOpen") * 1.6f); tgt["oh"] = C01(f("mouthFunnel") * 1.4f); tgt["ou"] = C01(f("mouthPucker") * 1.2f);
                tgt["ee"] = C01((f("mouthStretchLeft") + f("mouthStretchRight")) * 0.8f); tgt["ih"] = C01((f("mouthSmileLeft") + f("mouthSmileRight")) * 0.25f);
            }
            var bl = C01((f("eyeBlinkLeft") - _calBlinkL) / MathF.Max(0.2f, 0.75f - _calBlinkL));
            var br = C01((f("eyeBlinkRight") - _calBlinkR) / MathF.Max(0.2f, 0.75f - _calBlinkR));
            var (L, R) = Cfg.Mirror ? (br, bl) : (bl, br);
            tgt["blinkL"] = L > 0.6f ? 1 : L; tgt["blinkR"] = R > 0.6f ? 1 : R;
            tgt["happy"] = C01((f("mouthSmileLeft") + f("mouthSmileRight")) * 0.6f - 0.15f);
            tgt["surprised"] = C01((f("browInnerUp") - 0.4f) * 1.5f) * C01((f("eyeWideLeft") + f("eyeWideRight")) * 1.5f);
        }
        else if (vmc)
        {
            foreach (var (k2, val) in inp.VmcBlend)
            {
                var key = k2.ToLowerInvariant();
                switch (key)
                {
                    case "a": case "aa": tgt["aa"] = val; break;
                    case "i": case "ih": tgt["ih"] = val; break;
                    case "u": case "ou": tgt["ou"] = val; break;
                    case "e": case "ee": tgt["ee"] = val; break;
                    case "o": case "oh": tgt["oh"] = val; break;
                    case "blink_l": case "blinkleft": tgt["blinkL"] = val; break;
                    case "blink_r": case "blinkright": tgt["blinkR"] = val; break;
                    case "blink": tgt["blinkL"] = val; tgt["blinkR"] = val; break;
                    case "joy": case "happy": tgt["happy"] = val; break;
                    case "sorrow": tgt["sad"] = val; break;
                    case "fun": tgt["relaxed"] = val; break;
                    default: if (tgt.ContainsKey(key)) tgt[key] = val; break;
                }
            }
        }
        if (!face && !vmc && Cfg.LipsyncMic)
        {
            var db = inp.MicDb;
            if (db < _micFloor || now - _micFloorAt > 2f) { _micFloor = MathF.Max(-80f, MathF.Min(db, _micFloor + 1f)); _micFloorAt = now; }
            var v = MathF.Pow(C01((db - (_micFloor + 6f)) / 25f), 0.7f);
            tgt["aa"] = v * 0.75f; tgt["oh"] = v * 0.15f;
        }
        var vsum = tgt["aa"] + tgt["ih"] + tgt["ou"] + tgt["ee"] + tgt["oh"];
        if (vsum > 1) foreach (var k in new[] { "aa", "ih", "ou", "ee", "oh" }) tgt[k] /= vsum;

        if (!face && !vmc)
        {
            if (_blinkPhase < 0 && now > _nextBlink) _blinkPhase = 0;
            if (_blinkPhase >= 0)
            {
                _blinkPhase += dt / 0.18f;
                var v = _blinkPhase < 0.35f ? _blinkPhase / 0.35f : MathF.Max(0, 1 - MathF.Pow((_blinkPhase - 0.35f) / 0.65f, 0.6f));
                tgt["blinkL"] = tgt["blinkR"] = v;
                if (_blinkPhase >= 1)
                {
                    _blinkPhase = -1;
                    if (!_double && _rnd.NextDouble() < 0.15) { _double = true; _nextBlink = now + 0.12f; }
                    else { _double = false; _nextBlink = now + 2f + (float)_rnd.NextDouble() * 4f; }
                }
            }
        }
        var hot = !string.IsNullOrEmpty(inp.Expression) && inp.Expression != "neutral" ? inp.Expression : null;
        if (hot != null) foreach (var e in new[] { "happy", "angry", "sad", "relaxed", "surprised" }) tgt[e] = e == hot ? Cfg.ExpressionStrength : 0;

        foreach (var k in new[] { "aa", "ih", "ou", "ee", "oh" }) _sm[k] = Approach(S(k), tgt[k], dt, 0.03f, 0.1f);
        foreach (var k in new[] { "blinkL", "blinkR" }) _sm[k] = face ? Approach(S(k), tgt[k], dt, 0.02f, 0.04f) : tgt[k];
        foreach (var k in new[] { "happy", "angry", "sad", "relaxed", "surprised" }) _sm[k] = Approach(S(k), tgt[k], dt, 0.12f);

        Expr.Clear();
        void Set(string name, float v) { if (_m.Expressions.ContainsKey(name)) Expr[name] = v; }
        Set("aa", S("aa")); Set("ih", S("ih")); Set("ou", S("ou")); Set("ee", S("ee")); Set("oh", S("oh"));
        Set("blinkLeft", S("blinkL")); Set("blinkRight", S("blinkR"));
        Set("happy", S("happy")); Set("angry", S("angry")); Set("sad", S("sad")); Set("relaxed", S("relaxed")); Set("surprised", S("surprised"));
        if (face) foreach (var (name, v) in inp.FaceBlend) if (_m.Expressions.ContainsKey(name)) Expr[name] = v;
        foreach (var (k, v) in _lookExpr) if (_m.Expressions.ContainsKey(k)) Expr[k] = v;

        // --- キーボード・マウスと手 ---
        if (!vmc)
        {
            if (Cfg.InputLink && inp.TypingL) _lastTypeL = now;
            if (Cfg.InputLink && (inp.TypingR || inp.MouseActive)) _lastTypeR = now;
            _sm["armL"] = Approach(S("armL"), now - _lastTypeL < 2.5f ? 1 : 0, dt, 0.15f, 0.5f);
            _sm["armR"] = Approach(S("armR"), now - _lastTypeR < 2.5f ? 1 : 0, dt, 0.15f, 0.5f);
            ApplyArms(1 - S("armL"), 1 - S("armR"), t, inp);
        }
        BuildRaw();
    }

    void ApplyVmcBones(MotionInput inp, bool faceLive)
    {
        foreach (var (name, v) in inp.VmcBones)
        {
            var key = char.ToLowerInvariant(name[0]) + name[1..];
            if (faceLive && (key == "head" || key == "neck")) continue;
            if (!_m.Humanoid.ContainsKey(key) || v.Length < 4) continue;
            var q = _sx < 0 ? new Quaternion(-v[0], -v[1], v[2], v[3]) : new Quaternion(v[0], -v[1], -v[2], v[3]);
            var cur = _norm.TryGetValue(key, out var c) ? c : Quaternion.Identity;
            _norm[key] = Quaternion.Slerp(cur, Quaternion.Normalize(q), 0.5f);
        }
    }

    readonly Dictionary<string, float> _lookExpr = new();
    void ApplyLookAt(float yaw, float pitch)
    {
        static float Map((float InMax, float Scale) r, float v) => MathF.Min(MathF.Max(v, 0), r.InMax) / r.InMax * r.Scale;
        _lookExpr.Clear();
        if (!_m.LookAtIsBone)
        {
            if (pitch < 0) { _lookExpr["lookDown"] = 0; _lookExpr["lookUp"] = Map(_m.RangeUp, -pitch); } else { _lookExpr["lookUp"] = 0; _lookExpr["lookDown"] = Map(_m.RangeDown, pitch); }
            if (yaw < 0) { _lookExpr["lookLeft"] = 0; _lookExpr["lookRight"] = Map(_m.RangeOuter, -yaw); } else { _lookExpr["lookRight"] = 0; _lookExpr["lookLeft"] = Map(_m.RangeOuter, yaw); }
            return;
        }
        var x = pitch < 0 ? -Map(_m.RangeDown, -pitch) : Map(_m.RangeUp, pitch);
        var yL = yaw < 0 ? -Map(_m.RangeInner, -yaw) : Map(_m.RangeOuter, yaw);
        var yR = yaw < 0 ? -Map(_m.RangeOuter, -yaw) : Map(_m.RangeInner, yaw);
        const float D = MathF.PI / 180f;
        if (_m.Humanoid.ContainsKey("leftEye")) _norm["leftEye"] = Euler(x * D * _sx, yL * D, 0);
        if (_m.Humanoid.ContainsKey("rightEye")) _norm["rightEye"] = Euler(x * D * _sx, yR * D, 0);
    }

    // 正規化ボーンの回転 → 元の（生の）ボーンの局所回転:  raw = inv(P) * q * W （P = 親の静止ワールド回転、W = 自分の静止ワールド回転）
    void BuildRaw()
    {
        Raw.Clear();
        foreach (var (name, q) in _norm)
        {
            if (!_m.Humanoid.TryGetValue(name, out var idx)) continue;
            var n = _m.Nodes[idx];
            var P = n.Parent >= 0 ? _m.Nodes[n.Parent].RestWorldRot : Quaternion.Identity;
            Raw[idx] = Quaternion.Normalize(Quaternion.Inverse(P) * q * n.RestWorldRot);
        }
    }
}
