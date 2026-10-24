using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VCast.NativeAvatar;

// D3D11 オフスクリーン描画。スキニング/モーフは CPU、影は MToon 風の 2 段階、輪郭線は法線方向へ押し出した裏面
public sealed class Renderer : IDisposable
{
    const string Hlsl = @"#pragma pack_matrix(row_major)
cbuffer Frame : register(b0) { row_major float4x4 VP; float3 LightDir; float pad0; float3 CamPos; float pad1; };
cbuffer Mat : register(b1) { float4 Base; float4 Shade; float3 Emissive; float Cutoff; float Shift; float Toony; float OutlineW; float Mode; float3 OutlineColor; float HasTex; float HasShadeTex; float3 pad2; };
Texture2D BaseTex : register(t0); Texture2D ShadeTex : register(t1); StructuredBuffer<float4x4> Bones : register(t2);
SamplerState S : register(s0);
struct VI { float3 p : POSITION; float3 n : NORMAL; float2 uv : TEXCOORD; uint4 j : BLENDINDICES; float4 w : BLENDWEIGHT; };
struct PI { float4 pos : SV_POSITION; float3 n : NORMAL; float2 uv : TEXCOORD; float3 wp : TEXCOORD1; };
void Skin(VI v, out float3 p, out float3 n) { float4x4 m = transpose(Bones[v.j.x] * v.w.x + Bones[v.j.y] * v.w.y + Bones[v.j.z] * v.w.z + Bones[v.j.w] * v.w.w); p = mul(float4(v.p, 1), m).xyz; n = mul(float4(v.n, 0), m).xyz; }
PI VS(VI v) { PI o; float3 p, n; Skin(v, p, n); o.pos = mul(float4(p, 1), VP); o.n = n; o.uv = v.uv; o.wp = p; return o; }
PI VSOutline(VI v) { PI o; float3 p0, n; Skin(v, p0, n); float3 p = p0 + normalize(n) * OutlineW; o.pos = mul(float4(p, 1), VP); o.n = n; o.uv = v.uv; o.wp = p; return o; }
float4 PS(PI i, bool front : SV_IsFrontFace) : SV_Target
{
  float4 tex = HasTex > 0.5 ? BaseTex.Sample(S, i.uv) : float4(1,1,1,1);
  float4 baseC = Base * tex;
  if (Mode == 1 && baseC.a < Cutoff) discard;
  float3 n = normalize(i.n); if (!front) n = -n;
  float3 shadeC = Shade.rgb;
  if (HasShadeTex > 0.5) shadeC *= ShadeTex.Sample(S, i.uv).rgb; else shadeC *= tex.rgb;
  float nl = dot(n, normalize(-LightDir)) * 0.5 + 0.5;
  float lo = Shift * -0.5 + 0.5 - (1 - Toony) * 0.5, hi = Shift * -0.5 + 0.5 + (1 - Toony) * 0.5;
  float t = smoothstep(lo, max(hi, lo + 0.001), nl);
  float3 col = lerp(shadeC, baseC.rgb, t) + Emissive;
  float a = Mode == 2 ? baseC.a : 1;
  return float4(col * a, a);
}
float4 PSOutline(PI i) : SV_Target
{
  float4 tex = HasTex > 0.5 ? BaseTex.Sample(S, i.uv) : float4(1,1,1,1);
  if (Mode == 1 && (Base * tex).a < Cutoff) discard;
  return float4(OutlineColor, 1);
}";

    [StructLayout(LayoutKind.Sequential)] struct Vtx { public Vector3 P, N; public Vector2 UV; public ushort J0, J1, J2, J3; public Vector4 W; }
    [StructLayout(LayoutKind.Sequential)] struct FrameCb { public Matrix4x4 VP; public Vector3 Light; public float p0; public Vector3 Cam; public float p1; }
    [StructLayout(LayoutKind.Sequential)]
    struct MatCb
    {
        public Vector4 Base, Shade; public Vector3 Emissive; public float Cutoff, Shift, Toony, OutlineW, Mode;
        public Vector3 OutlineColor; public float HasTex, HasShadeTex; public Vector3 pad;
    }

    ID3D11Device _dev = null!; ID3D11DeviceContext _ctx = null!;
    ID3D11VertexShader _vs = null!, _vsOutline = null!; ID3D11PixelShader _ps = null!, _psOutline = null!;
    ID3D11InputLayout _layout = null!;
    ID3D11Buffer _frameCb = null!, _matCb = null!;
    ID3D11SamplerState _sampler = null!;
    ID3D11RasterizerState _rsBack = null!, _rsNone = null!, _rsFront = null!;
    ID3D11BlendState _blendOpaque = null!, _blendAlpha = null!;
    ID3D11DepthStencilState _dsOn = null!, _dsNoWrite = null!;
    ID3D11Texture2D _msaa = null!, _resolve = null!, _depth = null!, _staging = null!;
    ID3D11RenderTargetView _rtv = null!; ID3D11DepthStencilView _dsv = null!;
    public int W, H;
    readonly List<ID3D11ShaderResourceView?> _tex = new();
    sealed class PrimGpu { public ID3D11Buffer Vb = null!, Ib = null!, BoneBuf = null!; public ID3D11ShaderResourceView BoneSrv = null!; public Vtx[] Cpu = null!; public int IndexCount, BoneCount; public float[]? MorphSig; public Matrix4x4[] Bones = null!; }
    readonly List<PrimGpu> _prims = new();
    public VrmModel Model = null!;
    public Vector3 HeadWorld; public Matrix4x4[] World = Array.Empty<Matrix4x4>();

    public Renderer(int w, int h)
    {
        W = w; H = h;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out _dev!, out _ctx!).CheckError();
        var vsB = Compile("VS", "vs_5_0"); var vsoB = Compile("VSOutline", "vs_5_0");
        _vs = _dev.CreateVertexShader(vsB); _vsOutline = _dev.CreateVertexShader(vsoB);
        _ps = _dev.CreatePixelShader(Compile("PS", "ps_5_0")); _psOutline = _dev.CreatePixelShader(Compile("PSOutline", "ps_5_0"));
        _layout = _dev.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
            new InputElementDescription("BLENDINDICES", 0, Format.R16G16B16A16_UInt, 32, 0),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 40, 0),
        }, vsB);
        _frameCb = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FrameCb>(), BindFlags.ConstantBuffer));
        _matCb = _dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<MatCb>(), BindFlags.ConstantBuffer));
        _sampler = _dev.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap) { MaxAnisotropy = 8, MaxLOD = float.MaxValue });
        _rsBack = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true });
        _rsNone = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true });
        _rsFront = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.Front, FillMode.Solid) { FrontCounterClockwise = true, MultisampleEnable = true });
        var bd = new BlendDescription();
        bd.RenderTarget[0] = new RenderTargetBlendDescription { BlendEnable = false, SourceBlend = Blend.One, DestinationBlend = Blend.Zero, BlendOperation = BlendOperation.Add, SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add, RenderTargetWriteMask = ColorWriteEnable.All };
        _blendOpaque = _dev.CreateBlendState(bd);
        var ba = new BlendDescription();
        ba.RenderTarget[0] = new RenderTargetBlendDescription { BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add, SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add, RenderTargetWriteMask = ColorWriteEnable.All };
        _blendAlpha = _dev.CreateBlendState(ba);
        _dsOn = _dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));
        _dsNoWrite = _dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Less));

        const uint ms = 4;
        _msaa = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)W, (uint)H, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, ms, 0));
        _depth = _dev.CreateTexture2D(new Texture2DDescription(Format.D24_UNorm_S8_UInt, (uint)W, (uint)H, 1, 1, BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None, ms, 0));
        _resolve = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)W, (uint)H, 1, 1, BindFlags.ShaderResource));
        _staging = _dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)W, (uint)H, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _rtv = _dev.CreateRenderTargetView(_msaa); _dsv = _dev.CreateDepthStencilView(_depth);
    }

    static Blob Compile(string entry, string profile)
    {
        Compiler.Compile(Hlsl, entry, "avatar.hlsl", profile, out var blob, out var err);
        if (blob == null) throw new Exception(err != null ? System.Text.Encoding.UTF8.GetString(err.AsBytes()) : "shader compile failed");
        return blob;
    }

    public void SetModel(VrmModel m)
    {
        Model = m;
        foreach (var (px, w, h) in m.Textures)
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 0, BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips);
            var tex = _dev.CreateTexture2D(desc);
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { _ctx.UpdateSubresource(tex, 0, null, handle.AddrOfPinnedObject(), (uint)(w * 4), 0); } finally { handle.Free(); }
            var srv = _dev.CreateShaderResourceView(tex);
            _ctx.GenerateMips(srv);
            _tex.Add(srv);
        }
        foreach (var p in m.Prims)
        {
            var g = new PrimGpu { Cpu = new Vtx[p.Pos.Length], IndexCount = p.Indices.Length };
            for (var i = 0; i < g.Cpu.Length; i++)
            {
                var v = new Vtx { P = p.Pos[i], N = p.Normal[i], UV = p.Uv[i], W = new Vector4(1, 0, 0, 0) };
                if (p.Skin >= 0) { var j = p.Joints[i]; v.J0 = j.Item1; v.J1 = j.Item2; v.J2 = j.Item3; v.J3 = j.Item4; v.W = p.Weights[i]; }
                g.Cpu[i] = v;
            }
            g.Vb = _dev.CreateBuffer(new BufferDescription((uint)(g.Cpu.Length * Marshal.SizeOf<Vtx>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            UploadVb(g);
            g.Ib = _dev.CreateBuffer(p.Indices, BindFlags.IndexBuffer);
            g.BoneCount = p.Skin >= 0 ? m.Skins[p.Skin].Joints.Length : 1;
            g.Bones = new Matrix4x4[g.BoneCount];
            g.BoneBuf = _dev.CreateBuffer(new BufferDescription((uint)(g.BoneCount * 64), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, 64));
            g.BoneSrv = _dev.CreateShaderResourceView(g.BoneBuf, new ShaderResourceViewDescription { Format = Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer, Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)g.BoneCount } });
            _prims.Add(g);
        }
    }

    void UploadVb(PrimGpu g)
    {
        var mapped = _ctx.Map(g.Vb, MapMode.WriteDiscard);
        unsafe { fixed (Vtx* src = g.Cpu) Buffer.MemoryCopy(src, (void*)mapped.DataPointer, g.Cpu.Length * sizeof(Vtx), g.Cpu.Length * sizeof(Vtx)); }
        _ctx.Unmap(g.Vb, 0);
    }

    // 姿勢: ノードごとのローカル回転（未指定は静止姿勢）、表情の重み
    public void Pose(Dictionary<int, Quaternion>? rot, Dictionary<string, float>? expr)
    {
        var m = Model;
        var world = new Matrix4x4[m.Nodes.Count]; World = world;
        var done = new bool[m.Nodes.Count];
        Matrix4x4 W_(int i)
        {
            if (done[i]) return world[i];
            var n = m.Nodes[i];
            var r = rot != null && rot.TryGetValue(i, out var q) ? q : n.R;
            var local = Matrix4x4.CreateScale(n.S) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(n.T);
            world[i] = n.Parent >= 0 ? local * W_(n.Parent) : local; done[i] = true; return world[i];
        }
        for (var i = 0; i < m.Nodes.Count; i++) W_(i);

        // 表情 → (ノード, モーフ番号) ごとの重み
        var mw = new Dictionary<(int, int), float>();
        if (expr != null)
            foreach (var (name, v) in expr)
                if (m.Expressions.TryGetValue(name, out var e))
                    foreach (var b in e.Binds) { var k = (b.Node, b.MorphIndex); mw[k] = mw.GetValueOrDefault(k) + b.Weight * (e.IsBinary ? (v > 0.5f ? 1 : 0) : v); }

        for (var pi = 0; pi < m.Prims.Count; pi++)
        {
            var p = m.Prims[pi]; var g = _prims[pi];
            // ボーン行列（GPU スキニング用）。スキンなしのメッシュはノード行列 1 個
            if (p.Skin >= 0)
            {
                var s = m.Skins[p.Skin];
                for (var j = 0; j < g.BoneCount; j++) g.Bones[j] = s.InverseBind[j] * world[s.Joints[j]];
            }
            else g.Bones[0] = world[p.Node];
            var bm = _ctx.Map(g.BoneBuf, MapMode.WriteDiscard);
            unsafe { fixed (Matrix4x4* src = g.Bones) Buffer.MemoryCopy(src, (void*)bm.DataPointer, g.BoneCount * 64, g.BoneCount * 64); }
            _ctx.Unmap(g.BoneBuf, 0);

            // モーフは重みが変わったときだけ CPU で作り直す
            if (p.Morphs.Count > 0)
            {
                var sig = new float[p.Morphs.Count];
                for (var mi = 0; mi < sig.Length; mi++) sig[mi] = mw.GetValueOrDefault((p.Node, mi));
                if (g.MorphSig == null || !sig.AsSpan().SequenceEqual(g.MorphSig))
                {
                    g.MorphSig = sig;
                    for (var i = 0; i < g.Cpu.Length; i++)
                    {
                        var pos = p.Pos[i]; var nor = p.Normal[i];
                        for (var mi = 0; mi < sig.Length; mi++)
                            if (sig[mi] != 0) { pos += p.Morphs[mi].dPos[i] * sig[mi]; nor += p.Morphs[mi].dNormal[i] * sig[mi]; }
                        g.Cpu[i].P = pos; g.Cpu[i].N = nor;
                    }
                    UploadVb(g);
                }
            }
        }
        HeadWorld = m.Humanoid.TryGetValue("head", out var h) ? Vector3.Transform(Vector3.Zero, world[h]) : new Vector3(0, 1.4f, 0);
    }

    void SetMat(VrmModel.Material? mat, int mode)
    {
        mat ??= new VrmModel.Material();
        var cb = new MatCb
        {
            Base = mat.BaseColor, Shade = mat.ShadeColor, Emissive = mat.Emissive, Cutoff = mat.AlphaCutoff, Shift = mat.ShadingShift, Toony = mat.ShadingToony,
            OutlineW = mat.OutlineWidth, Mode = mode, OutlineColor = mat.OutlineColor,
            HasTex = mat.BaseTexture >= 0 ? 1 : 0, HasShadeTex = mat.ShadeTexture >= 0 ? 1 : 0,
        };
        _ctx.UpdateSubresource(ref cb, _matCb);
        _ctx.PSSetShaderResource(0, mat.BaseTexture >= 0 && mat.BaseTexture < _tex.Count ? _tex[mat.BaseTexture] : null);
        _ctx.PSSetShaderResource(1, mat.ShadeTexture >= 0 && mat.ShadeTexture < _tex.Count ? _tex[mat.ShadeTexture] : null);
    }

    // カメラ: 縦 fov 30°。target を中心に height(m) の範囲を映す。VRM1 は +Z が正面、VRM0 は -Z
    public void Render(Vector3 target, float height, float yawRad, bool vrm0Flip)
    {
        var dist = height / 2f / MathF.Tan(MathF.PI / 12f);
        var dir = new Vector3(MathF.Sin(yawRad), 0, MathF.Cos(yawRad)); if (vrm0Flip) dir.Z = -dir.Z;
        var cam = target + dir * dist;
        var view = Matrix4x4.CreateLookAt(cam, target, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 6f, (float)W / H, 0.05f, 50f);
        var fcb = new FrameCb { VP = view * proj, Light = Vector3.Normalize(new Vector3(-0.4f, -0.5f, -0.75f)), Cam = cam };
        _ctx.UpdateSubresource(ref fcb, _frameCb);

        _ctx.OMSetRenderTargets(_rtv, _dsv);
        _ctx.RSSetViewport(0, 0, W, H);
        _ctx.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 0));
        _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetInputLayout(_layout);
        _ctx.VSSetConstantBuffer(0, _frameCb); _ctx.PSSetConstantBuffer(0, _frameCb);
        _ctx.VSSetConstantBuffer(1, _matCb); _ctx.PSSetConstantBuffer(1, _matCb);
        _ctx.PSSetSampler(0, _sampler);
        var stride = (uint)Marshal.SizeOf<Vtx>();

        void Draw(int pi)
        {
            var g = _prims[pi];
            _ctx.VSSetShaderResource(2, g.BoneSrv);
            _ctx.IASetVertexBuffer(0, g.Vb, stride, 0);
            _ctx.IASetIndexBuffer(g.Ib, Format.R32_UInt, 0);
            _ctx.DrawIndexed((uint)g.IndexCount, 0, 0);
        }
        static int ModeOf(VrmModel.Material? m) => m == null ? 0 : m.AlphaMode == "BLEND" ? 2 : m.AlphaMode == "MASK" ? 1 : 0;
        VrmModel.Material? MatOf(int pi) { var id = Model.Prims[pi].Material; return id >= 0 ? Model.Materials[id] : null; }
        var order = Enumerable.Range(0, Model.Prims.Count).ToList();

        // 1) 不透明・マスク
        _ctx.OMSetBlendState(_blendOpaque); _ctx.OMSetDepthStencilState(_dsOn);
        foreach (var pi in order.Where(i => ModeOf(MatOf(i)) != 2))
        {
            var mat = MatOf(pi);
            _ctx.VSSetShader(_vs); _ctx.PSSetShader(_ps);
            _ctx.RSSetState(mat?.DoubleSided == true ? _rsNone : _rsBack);
            SetMat(mat, ModeOf(mat)); Draw(pi);
        }
        // 2) 輪郭線（裏面を法線方向に太らせる）
        foreach (var pi in order.Where(i => MatOf(i) is { OutlineWidth: > 0 } && ModeOf(MatOf(i)) != 2))
        {
            var mat = MatOf(pi)!;
            _ctx.VSSetShader(_vsOutline); _ctx.PSSetShader(_psOutline); _ctx.RSSetState(_rsFront);
            SetMat(mat, ModeOf(mat)); Draw(pi);
        }
        // 3) 半透明（奥行き書き込みなし）
        _ctx.OMSetBlendState(_blendAlpha); _ctx.OMSetDepthStencilState(_dsNoWrite);
        foreach (var pi in order.Where(i => ModeOf(MatOf(i)) == 2))
        {
            var mat = MatOf(pi);
            _ctx.VSSetShader(_vs); _ctx.PSSetShader(_ps); _ctx.RSSetState(_rsNone);
            SetMat(mat, 2); Draw(pi);
        }
        _ctx.ResolveSubresource(_resolve, 0, _msaa, 0, Format.B8G8R8A8_UNorm);
        _ctx.CopyResource(_staging, _resolve);
    }

    public void SavePng(string path)
    {
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        var px = new byte[W * H * 4];
        for (var y = 0; y < H; y++) Marshal.Copy(map.DataPointer + (int)map.RowPitch * y, px, y * W * 4, W * 4);
        _ctx.Unmap(_staging, 0);
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, px, W * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path); enc.Save(fs);
    }

    public void Dispose() { _ctx?.Dispose(); _dev?.Dispose(); }
}
