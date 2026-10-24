using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using VCast.NativeAvatar;

var path = args.Length > 0 ? args[0] : @"C:\Users\user\Downloads\Seed-san.vrm";
var outDir = args.Length > 1 ? args[1] : AppContext.BaseDirectory;
var name = Path.GetFileNameWithoutExtension(path);
var sw = Stopwatch.StartNew();
var m = VrmModel.Load(path);
Console.WriteLine($"springs: joints={m.SpringJoints.Count} groups={m.Springs.Count} colliders={m.Colliders.Count}");
Console.WriteLine($"load {sw.ElapsedMilliseconds} ms  VRM{(m.IsVrm0 ? 0 : 1)} '{m.Title}' lookAt={(m.LookAtIsBone ? "bone" : "expr")}");
using var r = new Renderer(1024, 1024);
r.SetModel(m);

// シナリオごとに 3 秒ぶん(60fps)回して最後のフレームを撮る
void Scene(string tag, Action<MotionInput> setup, float seconds = 3f, float height = 1.5f)
{
    var mo = new Motion(m);
    var inp = new MotionInput();
    var sp = new SpringBone(m);
    for (var i = 0; i < (int)(seconds * 60); i++) { setup(inp); mo.Update(i / 60f, 1f / 60f, inp); m.ApplyConstraints(mo.Raw); sp.Update(1f / 60f, mo.Raw); }
    r.Pose(mo.Raw, mo.Expr);
    var c = r.HeadWorld - new Vector3(0, height * 0.32f, 0);
    r.Render(c, height, 0, m.IsVrm0);
    r.SavePng(Path.Combine(outDir, $"m_{name}_{tag}.png"));
}
Scene("idle", _ => { });
Scene("face", i =>
{
    i.FaceLive = true; i.Yaw = 0.45f; i.Pitch = -0.15f; i.Roll = 0.1f;
    i.FaceBlend = new() { ["jawOpen"] = 0.6f, ["mouthSmileLeft"] = 0.4f, ["mouthSmileRight"] = 0.4f, ["eyeBlinkLeft"] = 0.9f, ["eyeLookInLeft"] = 0.5f, ["eyeLookOutRight"] = 0.5f };
}, 4f);
Scene("typing", i => { i.TypingL = true; i.TypingR = true; }, 3f);
Scene("mic", i => { i.MicDb = -20; }, 1f);

var mo2 = new Motion(m); var inp2 = new MotionInput();
sw.Restart();
for (var i = 0; i < 300; i++) { inp2.TypingL = i % 2 == 0; mo2.Update(i / 60f, 1f / 60f, inp2); r.Pose(mo2.Raw, mo2.Expr); r.Render(r.HeadWorld, 1.5f, 0, m.IsVrm0); }
Console.WriteLine($"300 frames (motion+pose+render): {sw.ElapsedMilliseconds / 300.0:F2} ms/frame");
