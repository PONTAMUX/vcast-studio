using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace VCast.AvatarNative;

// VRM の揺れ物（VRM1 の springBone / VRM0 の secondaryAnimation）。Verlet 積分で「尾」の先端位置を動かし、その向きをボーンの回転にする
public sealed class SpringBone
{
    readonly VrmModel _m;
    readonly Vector3[] _cur, _prev; bool _init;
    public bool Enabled = true;

    public SpringBone(VrmModel m) { _m = m; _cur = new Vector3[m.SpringJoints.Count]; _prev = new Vector3[m.SpringJoints.Count]; }
    public int Count => _m.SpringJoints.Count;

    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f) { var ax0 = Vector3.Cross(a, Vector3.UnitX); if (ax0.LengthSquared() < 1e-6f) ax0 = Vector3.Cross(a, Vector3.UnitY); return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax0), MathF.PI); }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(Math.Clamp(d, -1f, 1f)));
    }

    // raw: humanoid などで決まったボーン回転（ここへ揺れ物の回転を書き足す）
    public void Update(float dt, Dictionary<int, Quaternion> raw)
    {
        if (!Enabled || _m.SpringJoints.Count == 0) return;
        dt = Math.Clamp(dt, 0.001f, 1f / 30f);
        var baseRot = new Dictionary<int, Quaternion>(raw);
        var world = _m.ComputeWorld(raw);

        for (var ji = 0; ji < _m.SpringJoints.Count; ji++)
        {
            var j = _m.SpringJoints[ji]; var node = _m.Nodes[j.Node];
            var parentWorld = node.Parent >= 0 ? world[node.Parent] : Matrix4x4.Identity;
            var initRot = baseRot.TryGetValue(j.Node, out var q0) ? q0 : node.R;
            var m0 = Matrix4x4.CreateScale(node.S) * Matrix4x4.CreateFromQuaternion(initRot) * Matrix4x4.CreateTranslation(node.T) * parentWorld;
            var head = m0.Translation;
            // 尾（子の位置。なければ親から離れる向きに 7cm）
            var tailLocal = j.Tail >= 0 ? _m.Nodes[j.Tail].T : (node.T.LengthSquared() > 1e-8f ? Vector3.Normalize(node.T) * 0.07f : new Vector3(0, -0.07f, 0));
            var boneLen = tailLocal.Length(); if (boneLen < 1e-6f) continue;
            var axis = tailLocal / boneLen;
            Matrix4x4.Decompose(parentWorld, out _, out var pRot, out _); pRot = Quaternion.Normalize(pRot);
            var restTail = Vector3.Transform(tailLocal, m0);
            if (!_init) { _cur[ji] = restTail; _prev[ji] = restTail; }

            var stiffDir = Vector3.Transform(Vector3.Transform(axis, initRot), pRot);
            var inertia = (_cur[ji] - _prev[ji]) * (1f - Math.Clamp(j.Drag, 0, 1));
            var gdir = j.GravityDir.LengthSquared() > 0 ? Vector3.Normalize(j.GravityDir) : -Vector3.UnitY;
            var next = _cur[ji] + inertia + stiffDir * (j.Stiffness * dt) + gdir * (j.Gravity * dt);
            next = head + SafeNorm(next - head, stiffDir) * boneLen;

            // コライダー（球・カプセル）で押し出す
            foreach (var g in _m.Springs[j.Spring].ColliderGroups)
            {
                if (g < 0 || g >= _m.ColliderGroups.Count) continue;
                foreach (var ci in _m.ColliderGroups[g])
                {
                    var c = _m.Colliders[ci]; var cw = world[c.Node];
                    var center = Vector3.Transform(c.Offset, cw);
                    var r = c.Radius + j.HitRadius;
                    var closest = center;
                    if (c.Capsule)
                    {
                        var tail = Vector3.Transform(c.Tail, cw); var seg = tail - center; var len2 = seg.LengthSquared();
                        var t = len2 > 1e-10f ? Math.Clamp(Vector3.Dot(next - center, seg) / len2, 0, 1) : 0;
                        closest = center + seg * t;
                    }
                    var d = next - closest; var dl = d.Length();
                    if (dl < r) { next = closest + (dl > 1e-6f ? d / dl : stiffDir) * r; next = head + SafeNorm(next - head, stiffDir) * boneLen; }
                }
            }
            _prev[ji] = _cur[ji]; _cur[ji] = next;

            // 尾の向き → ボーンの局所回転
            Matrix4x4.Invert(m0, out var inv);
            var local = Vector3.Transform(next, inv);
            var rot = FromTo(axis, SafeNorm(local, axis));
            raw[j.Node] = Quaternion.Normalize(initRot * rot);
            Update(j.Node, raw, world);
        }
        _init = true;
    }

    // この節点と子孫のワールドを更新（次の関節の親になるため）
    void Update(int i, Dictionary<int, Quaternion> raw, Matrix4x4[] world)
    {
        var n = _m.Nodes[i]; var r2 = raw.TryGetValue(i, out var qq) ? qq : n.R;
        var lm = Matrix4x4.CreateScale(n.S) * Matrix4x4.CreateFromQuaternion(r2) * Matrix4x4.CreateTranslation(n.T);
        world[i] = n.Parent >= 0 ? lm * world[n.Parent] : lm;
        foreach (var c in n.Children) Update(c, raw, world);
    }

    static Vector3 SafeNorm(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}
