using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using SharpGLTF.Schema2;

namespace VCast.NativeAvatar;

// VRM（0.x / 1.0）を、描画に必要な形で CPU 側に読み込む。glTF の幾何・スキン・素材は SharpGLTF、VRM 拡張（人体ボーン・表情・MToon）は JSON を直接読む
public sealed class VrmModel
{
    public sealed class Node
    {
        public int Index; public string Name = ""; public int Parent = -1;
        public Vector3 T; public Quaternion R = Quaternion.Identity; public Vector3 S = Vector3.One; // 静止姿勢（ローカル）
        public Matrix4x4 RestWorld; public Quaternion RestWorldRot = Quaternion.Identity;
        public List<int> Children = new();
        public int Mesh = -1, Skin = -1;
    }

    public sealed class Skin { public int[] Joints = Array.Empty<int>(); public Matrix4x4[] InverseBind = Array.Empty<Matrix4x4>(); }

    public sealed class Material
    {
        public string Name = "";
        public Vector4 BaseColor = Vector4.One; public int BaseTexture = -1;
        public Vector4 ShadeColor = new(0.6f, 0.6f, 0.6f, 1); public int ShadeTexture = -1;
        public int EmissiveTexture = -1; public Vector3 Emissive = Vector3.Zero;
        public string AlphaMode = "OPAQUE"; public float AlphaCutoff = 0.5f; public bool DoubleSided;
        public float ShadingShift = 0f, ShadingToony = 0.9f;
        public float OutlineWidth = 0f; public Vector3 OutlineColor = Vector3.Zero; // メートル（VRM1）／モデル幅比（VRM0 は概算）
        public bool IsMToon;
    }

    public sealed class Prim
    {
        public int Node; public int Mesh; public int Material = -1; public int Skin = -1;
        public Vector3[] Pos = Array.Empty<Vector3>(); public Vector3[] Normal = Array.Empty<Vector3>(); public Vector2[] Uv = Array.Empty<Vector2>();
        public Vector4[] Weights = Array.Empty<Vector4>(); public (ushort, ushort, ushort, ushort)[] Joints = Array.Empty<(ushort, ushort, ushort, ushort)>();
        public uint[] Indices = Array.Empty<uint>();
        // モーフターゲット（頂点ごとの差分）。表情の重みで CPU 側で合成する
        public List<(Vector3[] dPos, Vector3[] dNormal)> Morphs = new();
    }

    public sealed class ExpressionBind { public int Node; public int MorphIndex; public float Weight; }
    public sealed class Expression { public string Name = ""; public List<ExpressionBind> Binds = new(); public bool IsBinary; public string OverrideBlink = "none", OverrideMouth = "none", OverrideLookAt = "none"; }

    public sealed class SpringJoint { public int Node, Tail = -1; public float HitRadius, Stiffness = 1, Gravity, Drag = 0.4f; public Vector3 GravityDir = -Vector3.UnitY; public int Spring; }
    public sealed class SpringCollider { public int Node; public bool Capsule; public Vector3 Offset, Tail; public float Radius; }
    public sealed class Spring { public string Name = ""; public List<int> ColliderGroups = new(); }
    public List<SpringJoint> SpringJoints = new();   // 親→子の順
    public List<Spring> Springs = new();
    public List<List<int>> ColliderGroups = new();   // グループ → colliders の番号
    public List<SpringCollider> Colliders = new();
    public sealed class NodeConstraint { public int Node, Source; public string Kind = "rotation"; public Vector3 Axis; public float Weight = 1; }
    public List<NodeConstraint> Constraints = new();
    public List<Node> Nodes = new();
    public List<Skin> Skins = new();
    public List<Material> Materials = new();
    public List<Prim> Prims = new();
    public List<(byte[] Bgra, int W, int H)> Textures = new(); // glTF の texture 番号 → 画像
    public Dictionary<string, int> Humanoid = new(StringComparer.OrdinalIgnoreCase); // "head" → ノード番号
    public Dictionary<string, Expression> Expressions = new(StringComparer.OrdinalIgnoreCase);
    public bool IsVrm0;
    public bool LookAtIsBone; // false = 表情（lookLeft 等）
    public (float InMax, float Scale) RangeInner = (90, 10), RangeOuter = (90, 10), RangeDown = (90, 10), RangeUp = (90, 10);
    public string Title = "", CommercialUse = "";
    public Vector3 BoundsMin, BoundsMax; // 静止姿勢のモデル全体の大きさ

    public static VrmModel Load(string path)
    {
        var m = new VrmModel();
        var bytes = File.ReadAllBytes(path);
        var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip };
        var root = ModelRoot.ReadGLB(new MemoryStream(bytes), settings);
        using var json = JsonDocument.Parse(ReadGlbJson(bytes));
        var ext = json.RootElement.TryGetProperty("extensions", out var e) ? e : default;
        m.IsVrm0 = ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("VRM", out _);

        // ---- ノード ----
        foreach (var n in root.LogicalNodes)
        {
            var node = new Node { Index = n.LogicalIndex, Name = n.Name ?? "", Mesh = n.Mesh?.LogicalIndex ?? -1, Skin = n.Skin?.LogicalIndex ?? -1 };
            var lt = n.LocalTransform;
            node.T = lt.Translation; node.R = Quaternion.Normalize(lt.Rotation); node.S = lt.Scale;
            node.Parent = n.VisualParent?.LogicalIndex ?? -1;
            m.Nodes.Add(node);
        }
        foreach (var node in m.Nodes) if (node.Parent >= 0) m.Nodes[node.Parent].Children.Add(node.Index);
        // 静止姿勢のワールド（親から順に）
        foreach (var i in TopoOrder(m.Nodes))
        {
            var nd = m.Nodes[i];
            var local = Matrix4x4.CreateScale(nd.S) * Matrix4x4.CreateFromQuaternion(nd.R) * Matrix4x4.CreateTranslation(nd.T);
            nd.RestWorld = nd.Parent >= 0 ? local * m.Nodes[nd.Parent].RestWorld : local;
            nd.RestWorldRot = Matrix4x4.Decompose(nd.RestWorld, out _, out var wr, out _) ? Quaternion.Normalize(wr) : Quaternion.Identity;
        }

        // ---- スキン ----
        foreach (var s in root.LogicalSkins)
        {
            var skin = new Skin { Joints = new int[s.JointsCount], InverseBind = new Matrix4x4[s.JointsCount] };
            for (var i = 0; i < s.JointsCount; i++) { var (j, ibm) = s.GetJoint(i); skin.Joints[i] = j.LogicalIndex; skin.InverseBind[i] = ibm; }
            m.Skins.Add(skin);
        }

        // ---- 画像 ----
        foreach (var t in root.LogicalTextures)
        {
            try { m.Textures.Add(DecodeImage(t.PrimaryImage.Content.Content.ToArray())); }
            catch { m.Textures.Add((new byte[] { 255, 255, 255, 255 }, 1, 1)); }
        }

        // ---- 素材 ----
        foreach (var mat in root.LogicalMaterials) m.Materials.Add(ReadMaterial(mat, json.RootElement, m));

        // ---- メッシュ ----
        foreach (var n in root.LogicalNodes)
        {
            if (n.Mesh == null) continue;
            foreach (var p in n.Mesh.Primitives)
            {
                if (p.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                var prim = new Prim { Node = n.LogicalIndex, Mesh = n.Mesh.LogicalIndex, Material = p.Material?.LogicalIndex ?? -1, Skin = n.Skin?.LogicalIndex ?? -1 };
                prim.Pos = p.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
                prim.Normal = p.GetVertexAccessor("NORMAL")?.AsVector3Array().ToArray() ?? new Vector3[prim.Pos.Length];
                prim.Uv = p.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array().ToArray() ?? new Vector2[prim.Pos.Length];
                if (prim.Skin >= 0 && p.GetVertexAccessor("JOINTS_0") is { } ja && p.GetVertexAccessor("WEIGHTS_0") is { } wa)
                {
                    prim.Weights = wa.AsVector4Array().ToArray();
                    prim.Joints = ja.AsVector4Array().Select(v => ((ushort)v.X, (ushort)v.Y, (ushort)v.Z, (ushort)v.W)).ToArray();
                }
                else prim.Skin = -1;
                prim.Indices = p.IndexAccessor != null ? p.IndexAccessor.AsIndicesArray().ToArray() : Enumerable.Range(0, prim.Pos.Length).Select(i => (uint)i).ToArray();
                for (var mi = 0; mi < p.MorphTargetsCount; mi++)
                {
                    var acc = p.GetMorphTargetAccessors(mi);
                    var dp = acc.TryGetValue("POSITION", out var pa) ? pa.AsVector3Array().ToArray() : new Vector3[prim.Pos.Length];
                    var dn = acc.TryGetValue("NORMAL", out var na) ? na.AsVector3Array().ToArray() : new Vector3[prim.Pos.Length];
                    prim.Morphs.Add((dp, dn));
                }
                m.Prims.Add(prim);
            }
        }

        ReadVrmExtensions(m, json.RootElement);
        ReadSprings(m, json.RootElement);
        ReadConstraints(m, json.RootElement);
        ComputeBounds(m);
        return m;
    }

    public Matrix4x4[] ComputeWorld(Dictionary<int, Quaternion>? rot)
    {
        var world = new Matrix4x4[Nodes.Count]; var done = new bool[Nodes.Count];
        Matrix4x4 W(int i)
        {
            if (done[i]) return world[i];
            var n = Nodes[i];
            var r = rot != null && rot.TryGetValue(i, out var q) ? q : n.R;
            var local = Matrix4x4.CreateScale(n.S) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(n.T);
            world[i] = n.Parent >= 0 ? local * W(n.Parent) : local; done[i] = true; return world[i];
        }
        for (var i = 0; i < Nodes.Count; i++) W(i);
        return world;
    }

    static IEnumerable<int> TopoOrder(List<Node> nodes)
    {
        var seen = new bool[nodes.Count];
        var order = new List<int>();
        void Visit(int i) { if (seen[i]) return; if (nodes[i].Parent >= 0) Visit(nodes[i].Parent); seen[i] = true; order.Add(i); }
        for (var i = 0; i < nodes.Count; i++) Visit(i);
        return order;
    }

    static string ReadGlbJson(byte[] glb)
    {
        var len = BitConverter.ToInt32(glb, 12);
        return System.Text.Encoding.UTF8.GetString(glb, 20, len);
    }

    static (byte[], int, int) DecodeImage(byte[] encoded)
    {
        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(encoded), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(dec.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var w = frame.PixelWidth; var h = frame.PixelHeight;
        var px = new byte[w * h * 4];
        frame.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    static Material ReadMaterial(SharpGLTF.Schema2.Material src, JsonElement root, VrmModel m)
    {
        var mat = new Material { Name = src.Name ?? "", AlphaMode = src.Alpha.ToString().ToUpperInvariant(), AlphaCutoff = src.AlphaCutoff, DoubleSided = src.DoubleSided };
        var bc = src.FindChannel("BaseColor");
        if (bc != null) { mat.BaseColor = bc.Value.Color; mat.BaseTexture = bc.Value.Texture?.LogicalIndex ?? -1; }
        var em = src.FindChannel("Emissive");
        if (em != null) { mat.Emissive = new Vector3(em.Value.Color.X, em.Value.Color.Y, em.Value.Color.Z); mat.EmissiveTexture = em.Value.Texture?.LogicalIndex ?? -1; }
        mat.ShadeColor = new Vector4(mat.BaseColor.X * 0.6f, mat.BaseColor.Y * 0.6f, mat.BaseColor.Z * 0.6f, 1);

        var idx = src.LogicalIndex;
        // VRM 1.0: materials[i].extensions.VRMC_materials_mtoon
        if (root.TryGetProperty("materials", out var mats) && idx < mats.GetArrayLength()
            && mats[idx].TryGetProperty("extensions", out var mx) && mx.TryGetProperty("VRMC_materials_mtoon", out var mt))
        {
            mat.IsMToon = true;
            if (mt.TryGetProperty("shadeColorFactor", out var sc) && sc.GetArrayLength() >= 3) mat.ShadeColor = new Vector4(sc[0].GetSingle(), sc[1].GetSingle(), sc[2].GetSingle(), 1);
            if (mt.TryGetProperty("shadeMultiplyTexture", out var st) && st.TryGetProperty("index", out var si)) mat.ShadeTexture = TexIndex(root, si.GetInt32());
            if (mt.TryGetProperty("shadingShiftFactor", out var ss)) mat.ShadingShift = ss.GetSingle();
            if (mt.TryGetProperty("shadingToonyFactor", out var stf)) mat.ShadingToony = stf.GetSingle();
            if (mt.TryGetProperty("outlineWidthMode", out var ow) && ow.GetString() != "none")
            {
                mat.OutlineWidth = mt.TryGetProperty("outlineWidthFactor", out var owf) ? owf.GetSingle() : 0;
                if (mt.TryGetProperty("outlineColorFactor", out var oc) && oc.GetArrayLength() >= 3) mat.OutlineColor = new Vector3(oc[0].GetSingle(), oc[1].GetSingle(), oc[2].GetSingle());
            }
        }
        return mat;
    }

    static int TexIndex(JsonElement root, int textureIndex) => textureIndex; // glTF の texture 番号のまま使う

    // VRM 0.x の素材（materialProperties: シェード色・アウトライン）
    static void ReadVrm0Materials(VrmModel m, JsonElement vrm)
    {
        if (!vrm.TryGetProperty("materialProperties", out var props)) return;
        var i = 0;
        foreach (var p in props.EnumerateArray())
        {
            if (i >= m.Materials.Count) break;
            var mat = m.Materials[i++];
            if (!(p.TryGetProperty("shader", out var sh) && (sh.GetString() ?? "").Contains("MToon"))) continue;
            mat.IsMToon = true;
            if (p.TryGetProperty("vectorProperties", out var vp))
            {
                if (vp.TryGetProperty("_ShadeColor", out var sc) && sc.GetArrayLength() >= 3) mat.ShadeColor = new Vector4(sc[0].GetSingle(), sc[1].GetSingle(), sc[2].GetSingle(), 1);
                if (vp.TryGetProperty("_OutlineColor", out var oc) && oc.GetArrayLength() >= 3) mat.OutlineColor = new Vector3(oc[0].GetSingle(), oc[1].GetSingle(), oc[2].GetSingle());
            }
            if (p.TryGetProperty("textureProperties", out var tp) && tp.TryGetProperty("_ShadeTexture", out var st)) mat.ShadeTexture = st.GetInt32();
            if (p.TryGetProperty("floatProperties", out var fp))
            {
                if (fp.TryGetProperty("_ShadeShift", out var ss)) mat.ShadingShift = ss.GetSingle();
                if (fp.TryGetProperty("_ShadeToony", out var stn)) mat.ShadingToony = stn.GetSingle();
                if (fp.TryGetProperty("_OutlineWidthMode", out var owm) && owm.GetSingle() > 0 && fp.TryGetProperty("_OutlineWidth", out var ow)) mat.OutlineWidth = ow.GetSingle() * 0.01f; // 単位は「モデルの 1/100」
            }
        }
    }

    static void ReadVrmExtensions(VrmModel m, JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var ext)) return;
        if (ext.TryGetProperty("VRMC_vrm", out var v1))
        {
            if (v1.TryGetProperty("meta", out var meta)) { m.Title = meta.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""; m.CommercialUse = meta.TryGetProperty("commercialUsage", out var c) ? c.GetString() ?? "" : ""; }
            if (v1.TryGetProperty("humanoid", out var hu) && hu.TryGetProperty("humanBones", out var hb))
                foreach (var b in hb.EnumerateObject()) if (b.Value.TryGetProperty("node", out var nn)) m.Humanoid[b.Name] = nn.GetInt32();
            if (v1.TryGetProperty("lookAt", out var la))
            {
                m.LookAtIsBone = la.TryGetProperty("type", out var lt) && lt.GetString() == "bone";
                var dflt = m.LookAtIsBone ? 10f : 1f;
                (float, float) R(string k) => la.TryGetProperty(k, out var r) ? (r.TryGetProperty("inputMaxValue", out var im) ? im.GetSingle() : 90f, r.TryGetProperty("outputScale", out var os) ? os.GetSingle() : dflt) : (90f, dflt);
                m.RangeInner = R("rangeMapHorizontalInner"); m.RangeOuter = R("rangeMapHorizontalOuter"); m.RangeDown = R("rangeMapVerticalDown"); m.RangeUp = R("rangeMapVerticalUp");
            }
            if (v1.TryGetProperty("expressions", out var ex))
            {
                void Add(string name, JsonElement e)
                {
                    var exp = new Expression { Name = name, IsBinary = e.TryGetProperty("isBinary", out var ib) && ib.GetBoolean() };
                    if (e.TryGetProperty("overrideBlink", out var ob)) exp.OverrideBlink = ob.GetString() ?? "none";
                    if (e.TryGetProperty("overrideMouth", out var om)) exp.OverrideMouth = om.GetString() ?? "none";
                    if (e.TryGetProperty("overrideLookAt", out var ol)) exp.OverrideLookAt = ol.GetString() ?? "none";
                    if (e.TryGetProperty("morphTargetBinds", out var binds))
                        foreach (var b in binds.EnumerateArray())
                            exp.Binds.Add(new ExpressionBind { Node = b.GetProperty("node").GetInt32(), MorphIndex = b.GetProperty("index").GetInt32(), Weight = b.TryGetProperty("weight", out var w) ? w.GetSingle() : 1f });
                    m.Expressions[name] = exp;
                }
                if (ex.TryGetProperty("preset", out var pre)) foreach (var p in pre.EnumerateObject()) Add(p.Name, p.Value);
                if (ex.TryGetProperty("custom", out var cus)) foreach (var p in cus.EnumerateObject()) Add(p.Name, p.Value);
            }
        }
        else if (ext.TryGetProperty("VRM", out var v0))
        {
            if (v0.TryGetProperty("meta", out var meta)) { m.Title = meta.TryGetProperty("title", out var n) ? n.GetString() ?? "" : ""; m.CommercialUse = meta.TryGetProperty("commercialUssageName", out var c) ? c.GetString() ?? "" : ""; }
            if (v0.TryGetProperty("humanoid", out var hu) && hu.TryGetProperty("humanBones", out var hb))
                foreach (var b in hb.EnumerateArray()) if (b.TryGetProperty("bone", out var bn) && b.TryGetProperty("node", out var nn)) m.Humanoid[bn.GetString()!] = nn.GetInt32();
            if (v0.TryGetProperty("blendShapeMaster", out var bsm) && bsm.TryGetProperty("blendShapeGroups", out var groups))
            {
                // VRM0 のプリセット名を VRM1 の名前へそろえる
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["a"] = "aa", ["i"] = "ih", ["u"] = "ou", ["e"] = "ee", ["o"] = "oh", ["joy"] = "happy", ["angry"] = "angry", ["sorrow"] = "sad", ["fun"] = "relaxed", ["blink"] = "blink", ["blink_l"] = "blinkLeft", ["blink_r"] = "blinkRight", ["neutral"] = "neutral" };
                foreach (var g in groups.EnumerateArray())
                {
                    var preset = g.TryGetProperty("presetName", out var pn) ? pn.GetString() ?? "" : "";
                    var name = map.TryGetValue(preset, out var mapped) ? mapped : (g.TryGetProperty("name", out var gn) ? gn.GetString() ?? preset : preset);
                    var exp = new Expression { Name = name, IsBinary = g.TryGetProperty("isBinary", out var ib) && ib.GetBoolean() };
                    if (g.TryGetProperty("binds", out var binds))
                        foreach (var b in binds.EnumerateArray())
                        {
                            // VRM0 の bind は「メッシュ番号 + モーフ番号」。メッシュを持つノードに直す（重みは 0〜100）
                            var meshIdx = b.GetProperty("mesh").GetInt32();
                            foreach (var nd in m.Nodes.Where(nd => nd.Mesh == meshIdx))
                                exp.Binds.Add(new ExpressionBind { Node = nd.Index, MorphIndex = b.GetProperty("index").GetInt32(), Weight = (b.TryGetProperty("weight", out var w) ? w.GetSingle() : 100f) / 100f });
                        }
                    if (name.Length > 0) m.Expressions[name] = exp;
                }
            }
            ReadVrm0Materials(m, v0);
        }
    }

    static Vector3 V3(JsonElement e) => e.ValueKind == JsonValueKind.Array ? new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()) : new Vector3(e.GetProperty("x").GetSingle(), e.GetProperty("y").GetSingle(), e.GetProperty("z").GetSingle());

    static void ReadSprings(VrmModel m, JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var ext)) return;
        if (ext.TryGetProperty("VRMC_springBone", out var sb))
        {
            if (sb.TryGetProperty("colliders", out var cols))
                foreach (var c in cols.EnumerateArray())
                {
                    var col = new SpringCollider { Node = c.GetProperty("node").GetInt32() };
                    var sh = c.GetProperty("shape");
                    if (sh.TryGetProperty("sphere", out var sp)) { col.Offset = sp.TryGetProperty("offset", out var o) ? V3(o) : default; col.Radius = sp.TryGetProperty("radius", out var r) ? r.GetSingle() : 0; }
                    else if (sh.TryGetProperty("capsule", out var cp)) { col.Capsule = true; col.Offset = cp.TryGetProperty("offset", out var o) ? V3(o) : default; col.Tail = cp.TryGetProperty("tail", out var t) ? V3(t) : default; col.Radius = cp.TryGetProperty("radius", out var r) ? r.GetSingle() : 0; }
                    m.Colliders.Add(col);
                }
            if (sb.TryGetProperty("colliderGroups", out var groups))
                foreach (var g in groups.EnumerateArray()) m.ColliderGroups.Add(g.GetProperty("colliders").EnumerateArray().Select(x => x.GetInt32()).ToList());
            if (sb.TryGetProperty("springs", out var springs))
                foreach (var sp in springs.EnumerateArray())
                {
                    var spring = new Spring { Name = sp.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "" };
                    if (sp.TryGetProperty("colliderGroups", out var cg)) spring.ColliderGroups = cg.EnumerateArray().Select(x => x.GetInt32()).ToList();
                    m.Springs.Add(spring); var si = m.Springs.Count - 1;
                    var joints = sp.GetProperty("joints").EnumerateArray().ToList();
                    for (var k = 0; k < joints.Count; k++)
                    {
                        var j = joints[k];
                        m.SpringJoints.Add(new SpringJoint
                        {
                            Node = j.GetProperty("node").GetInt32(), Tail = k + 1 < joints.Count ? joints[k + 1].GetProperty("node").GetInt32() : -1, Spring = si,
                            HitRadius = j.TryGetProperty("hitRadius", out var hr) ? hr.GetSingle() : 0,
                            Stiffness = j.TryGetProperty("stiffness", out var st) ? st.GetSingle() : 1,
                            Gravity = j.TryGetProperty("gravityPower", out var gp) ? gp.GetSingle() : 0,
                            Drag = j.TryGetProperty("dragForce", out var df) ? df.GetSingle() : 0.5f,
                            GravityDir = j.TryGetProperty("gravityDir", out var gd) ? V3(gd) : -Vector3.UnitY,
                        });
                    }
                }
        }
        else if (ext.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("secondaryAnimation", out var sa))
        {
            if (sa.TryGetProperty("colliderGroups", out var cgs))
                foreach (var g in cgs.EnumerateArray())
                {
                    var list = new List<int>(); var node = g.GetProperty("node").GetInt32();
                    if (g.TryGetProperty("colliders", out var cs))
                        foreach (var c in cs.EnumerateArray()) { m.Colliders.Add(new SpringCollider { Node = node, Offset = c.TryGetProperty("offset", out var o) ? V3(o) : default, Radius = c.TryGetProperty("radius", out var r) ? r.GetSingle() : 0 }); list.Add(m.Colliders.Count - 1); }
                    m.ColliderGroups.Add(list);
                }
            if (sa.TryGetProperty("boneGroups", out var bgs))
                foreach (var bg in bgs.EnumerateArray())
                {
                    var spring = new Spring();
                    if (bg.TryGetProperty("colliderGroups", out var cg)) spring.ColliderGroups = cg.EnumerateArray().Select(x => x.GetInt32()).ToList();
                    m.Springs.Add(spring); var si = m.Springs.Count - 1;
                    float F(string k, float d) => bg.TryGetProperty(k, out var v) ? v.GetSingle() : d;
                    var stiff = F("stiffiness", F("stiffness", 1)); var gp = F("gravityPower", 0); var drag = F("dragForce", 0.4f); var hr = F("hitRadius", 0.02f);
                    var gd = bg.TryGetProperty("gravityDir", out var gdv) ? V3(gdv) : -Vector3.UnitY;
                    void Walk(int node)
                    {
                        var n = m.Nodes[node];
                        m.SpringJoints.Add(new SpringJoint { Node = node, Tail = n.Children.Count > 0 ? n.Children[0] : -1, Spring = si, HitRadius = hr, Stiffness = stiff, Gravity = gp, Drag = drag, GravityDir = gd });
                        foreach (var c in n.Children) Walk(c);
                    }
                    if (bg.TryGetProperty("bones", out var bones)) foreach (var b in bones.EnumerateArray()) Walk(b.GetInt32());
                }
        }
    }

    static void ReadConstraints(VrmModel m, JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes)) return;
        var i = 0;
        foreach (var n in nodes.EnumerateArray())
        {
            var idx = i++;
            if (!n.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("VRMC_node_constraint", out var nc) || !nc.TryGetProperty("constraint", out var c)) continue;
            if (c.TryGetProperty("rotation", out var r) && r.TryGetProperty("source", out var src))
                m.Constraints.Add(new NodeConstraint { Node = idx, Source = src.GetInt32(), Kind = "rotation", Weight = r.TryGetProperty("weight", out var w) ? w.GetSingle() : 1f });
            else if (c.TryGetProperty("roll", out var ro) && ro.TryGetProperty("source", out var rsrc))
            {
                var axis = ro.TryGetProperty("rollAxis", out var ra) ? ra.GetString() : "X";
                m.Constraints.Add(new NodeConstraint { Node = idx, Source = rsrc.GetInt32(), Kind = "roll", Axis = axis == "Y" ? Vector3.UnitY : axis == "Z" ? Vector3.UnitZ : Vector3.UnitX, Weight = ro.TryGetProperty("weight", out var w) ? w.GetSingle() : 1f });
            }
        }
    }

    // ノード制約（VRMC_node_constraint）。元のボーンの回転を別のノードに写す（例: 本体の腕 → ロボアーム）。ソースが別の制約先のことがあるので順序が落ち着くまで繰り返す
    public void ApplyConstraints(Dictionary<int, Quaternion> raw)
    {
        if (Constraints.Count == 0) return;
        for (var pass = 0; pass < 3; pass++)
            foreach (var c in Constraints)
            {
                var srcRest = Nodes[c.Source].R; var dstRest = Nodes[c.Node].R;
                var src = raw.TryGetValue(c.Source, out var q) ? q : srcRest;
                var delta = Quaternion.Normalize(Quaternion.Inverse(srcRest) * src);
                if (c.Kind == "roll")
                {
                    var v = new Vector3(delta.X, delta.Y, delta.Z);
                    var tw = new Quaternion(c.Axis * Vector3.Dot(c.Axis, v), delta.W);
                    delta = tw.LengthSquared() > 1e-10f ? Quaternion.Normalize(tw) : Quaternion.Identity;
                }
                raw[c.Node] = Quaternion.Normalize(dstRest * Quaternion.Slerp(Quaternion.Identity, delta, c.Weight));
            }
    }

    static void ComputeBounds(VrmModel m)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var p in m.Prims)
        {
            var world = p.Skin >= 0 ? Matrix4x4.Identity : m.Nodes[p.Node].RestWorld;
            foreach (var v in p.Pos)
            {
                Vector3 w;
                if (p.Skin >= 0)
                {
                    // 静止姿勢では「逆バインド × ジョイントのワールド」が単位行列に近いので、そのまま
                    w = v;
                }
                else w = Vector3.Transform(v, world);
                min = Vector3.Min(min, w); max = Vector3.Max(max, w);
            }
        }
        m.BoundsMin = min; m.BoundsMax = max;
    }
}
