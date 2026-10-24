using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VCast.Obs;

// グラフィックススレッドから呼ばれるオーバーレイ描画（画面ピクセル座標系）
public interface IPreviewOverlay
{
    void Draw(float offX, float offY, float scale);
}

public record PreviewPointer(double X, double Y, double Scale, bool Shift, bool Ctrl);

// WPF に OBS の描画面(obs_display)を埋め込む。HWND の上に WPF 要素は重ねられない（エアスペース）ので
// 選択枠などのオーバーレイは描画コールバック内で gs_* を使って描く。マウスは子 HWND の WndProc で受ける。
public sealed unsafe class PreviewHost : HwndHost
{
    IntPtr _hwnd, _display;
    GCHandle _self;
    volatile IPreviewOverlay? _overlay;
    IntPtr _renderSource; // 0 なら本番（main texture）を描く
    readonly object _srcLock = new();
    // 描くキャンバスの大きさ（0 なら本編のキャンバス）。縦型プレビューは 1080x1920
    public uint CanvasWidth { get; set; }
    public uint CanvasHeight { get; set; }
    float BaseW => CanvasWidth > 0 ? CanvasWidth : ObsEngine.BaseWidth;
    float BaseH => CanvasHeight > 0 ? CanvasHeight : ObsEngine.BaseHeight;
    long _lastDownTick;

    public IPreviewOverlay? Overlay { get => _overlay; set => _overlay = value; }

    // 描くシーンを指定（参照を持つ）。IntPtr.Zero で本番に戻す
    public IntPtr RenderSource
    {
        get => _renderSource;
        set
        {
            if (value == _renderSource) return;
            if (value != IntPtr.Zero) Native.obs_source_get_ref(value);
            IntPtr old;
            lock (_srcLock) { old = _renderSource; _renderSource = value; }
            if (old != IntPtr.Zero) Native.obs_source_release(old); // 解放はロックの外で
        }
    }
    public event Action<PreviewPointer>? PointerDown, PointerMove, PointerUp, DoubleClick, RightClick;
    public Func<PreviewPointer, IntPtr>? CursorFor; // IntPtr.Zero=既定カーソル
    // エクスプローラーからのファイルドロップ（HwndHost 上では WPF の Drop が発生しないため WM_DROPFILES で受ける）
    public event Action<string[], PreviewPointer>? FilesDropped;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _hwnd = CreateWindowEx(WS_EX_ACCEPTFILES, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_NOTIFY,
                               0, 0, 16, 16, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (ObsEngine.Started)
        {
            var (w, h) = PixelSize();
            var init = new Native.gs_init_data
            {
                hwnd = _hwnd, cx = w, cy = h, num_backbuffers = 1,
                format = Native.GS_BGRA, zsformat = Native.GS_ZS_NONE, adapter = 0,
            };
            _display = Native.obs_display_create(ref init, 0xFF1A1A1E);
            _self = GCHandle.Alloc(this);
            Native.obs_display_add_draw_callback(_display, &Draw, GCHandle.ToIntPtr(_self));
        }
        return new HandleRef(this, _hwnd);
    }

    // 最小化中などプレビューが見えないときは描画自体を止める（配信・録画の映像は止まらない）
    public void SetRendering(bool on)
    {
        if (_display != IntPtr.Zero) Native.obs_display_set_enabled(_display, on);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_display == IntPtr.Zero) return;
        var (w, h) = PixelSize();
        Native.obs_display_resize(_display, w, h);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_display != IntPtr.Zero)
        {
            Native.obs_display_remove_draw_callback(_display, &Draw, GCHandle.ToIntPtr(_self));
            Native.obs_display_destroy(_display);
            _display = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        RenderSource = IntPtr.Zero;
        DestroyWindow(hwnd.Handle);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true; return (IntPtr)HTCLIENT;
            case WM_LBUTTONDOWN:
                SetCapture(hwnd);
                var p = ToCanvas(lParam, wParam);
                var now = Environment.TickCount64;
                if (now - _lastDownTick < GetDoubleClickTime()) { _lastDownTick = 0; DoubleClick?.Invoke(p); }
                else { _lastDownTick = now; PointerDown?.Invoke(p); }
                handled = true; return IntPtr.Zero;
            case WM_MOUSEMOVE:
                PointerMove?.Invoke(ToCanvas(lParam, wParam));
                handled = true; return IntPtr.Zero;
            case WM_LBUTTONUP:
                ReleaseCapture();
                PointerUp?.Invoke(ToCanvas(lParam, wParam));
                handled = true; return IntPtr.Zero;
            case WM_DROPFILES:
            {
                var drop = wParam;
                var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var files = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var sb = new System.Text.StringBuilder(1024);
                    DragQueryFile(drop, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                DragQueryPoint(drop, out var pt);
                DragFinish(drop);
                FilesDropped?.Invoke(files, ToCanvas((IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)), IntPtr.Zero));
                handled = true; return IntPtr.Zero;
            }
            case WM_RBUTTONUP:
                RightClick?.Invoke(ToCanvas(lParam, wParam));
                handled = true; return IntPtr.Zero;
            case WM_SETCURSOR:
                if (CursorFor != null && GetCursorPos(out var sp) && ScreenToClient(hwnd, ref sp))
                {
                    var cur = CursorFor(ToCanvas((IntPtr)((sp.Y << 16) | (sp.X & 0xFFFF)), IntPtr.Zero));
                    SetCursor(cur != IntPtr.Zero ? cur : LoadCursor(IntPtr.Zero, IDC_ARROW));
                    handled = true; return (IntPtr)1;
                }
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // 画面座標 → キャンバス座標（プレビュー外なら null）。アプリ一覧からの擬似ドラッグの着地判定に使う
    public PreviewPointer? ScreenToCanvas(int screenX, int screenY)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return null;
        var pt = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(_hwnd, ref pt)) return null;
        var (cx, cy) = PixelSize();
        if (pt.X < 0 || pt.Y < 0 || pt.X >= cx || pt.Y >= cy) return null;
        return ToCanvas((IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)), IntPtr.Zero);
    }

    PreviewPointer ToCanvas(IntPtr lParam, IntPtr wParam)
    {
        int px = (short)((long)lParam & 0xFFFF), py = (short)(((long)lParam >> 16) & 0xFFFF);
        var (cx, cy) = PixelSize();
        var (offX, offY, scale) = Layout(cx, cy, BaseW, BaseH);
        var keys = (long)wParam;
        return new PreviewPointer((px - offX) / scale, (py - offY) / scale, scale,
                                  (keys & MK_SHIFT) != 0 || (GetKeyState(VK_SHIFT) & 0x8000) != 0,
                                  (keys & MK_CONTROL) != 0);
    }

    // キャンバスを表示領域に収めたときの左上オフセットと倍率
    static (float offX, float offY, float scale) Layout(uint cx, uint cy, float bw, float bh)
    {
        var scale = Math.Min(cx / bw, cy / bh);
        return ((cx - bw * scale) / 2, (cy - bh * scale) / 2, scale);
    }

    (uint, uint) PixelSize()
    {
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return ((uint)Math.Max(1, ActualWidth * dpi), (uint)Math.Max(1, ActualHeight * dpi));
    }

    // グラフィックススレッドで呼ばれる。UI スレッドには一切触らないこと。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Draw(IntPtr param, uint cx, uint cy)
    {
        try
        {
            var host = (PreviewHost)GCHandle.FromIntPtr(param).Target!;
            float bw = host.BaseW, bh = host.BaseH;
            var (offX, offY, scale) = Layout(cx, cy, bw, bh);

            Native.gs_viewport_push();
            Native.gs_projection_push();
            Native.gs_ortho(0, bw, 0, bh, -100, 100);
            Native.gs_set_viewport((int)offX, (int)offY, (int)(bw * scale), (int)(bh * scale));
            // 描いている間に別スレッドで最後の参照が外されないよう、ロック内で強参照を取る
            IntPtr src;
            lock (host._srcLock) src = host._renderSource == IntPtr.Zero ? IntPtr.Zero : Native.obs_source_get_ref(host._renderSource);
            if (src != IntPtr.Zero) { Native.obs_source_video_render(src); Native.obs_source_release(src); }
            else if (host._renderSource == IntPtr.Zero && (scale >= 0.999f || !DrawMainDownscaled(bw, bh, (uint)(bw * scale), (uint)(bh * scale))))
                Native.obs_render_main_texture();
            Native.gs_projection_pop();
            Native.gs_viewport_pop();

            var overlay = host._overlay;
            if (overlay != null)
            {
                Native.gs_viewport_push();
                Native.gs_projection_push();
                Native.gs_ortho(0, cx, 0, cy, -100, 100);
                Native.gs_set_viewport(0, 0, (int)cx, (int)cy);
                Native.gs_matrix_push();
                Native.gs_matrix_identity();
                overlay.Draw(offX, offY, scale);
                Native.gs_matrix_pop();
                Native.gs_projection_pop();
                Native.gs_viewport_pop();
            }
        }
        catch (Exception ex) { ObsEngine.Log("[preview] " + ex.Message); }
    }

    // 縮小表示のときは OBS が出力の縮小に使う「エリア平均」で描く（単純な縮小だと文字や細部がギザギザ・ざらつく）
    static bool DrawMainDownscaled(float bw, float bh, uint w, uint h)
    {
        var tex = Native.obs_get_main_texture();
        var effect = Native.obs_get_base_effect(Native.OBS_EFFECT_AREA);
        if (tex == IntPtr.Zero || effect == IntPtr.Zero || w == 0 || h == 0) return false;
        var pDim = Native.gs_effect_get_param_by_name(effect, "base_dimension");
        var pDimI = Native.gs_effect_get_param_by_name(effect, "base_dimension_i");
        var pImage = Native.gs_effect_get_param_by_name(effect, "image");
        if (pDim == IntPtr.Zero || pDimI == IntPtr.Zero || pImage == IntPtr.Zero) return false; // 想定と違うエフェクトなら従来の描き方
        var dim = new Native.vec2 { x = bw, y = bh };
        var dimI = new Native.vec2 { x = 1f / bw, y = 1f / bh };
        Native.gs_effect_set_vec2(pDim, ref dim);
        Native.gs_effect_set_vec2(pDimI, ref dimI);
        Native.gs_effect_set_texture(pImage, tex);
        var pMul = Native.gs_effect_get_param_by_name(effect, "multiplier");
        if (pMul != IntPtr.Zero) Native.gs_effect_set_float(pMul, 1f);
        // 出力サイズの座標系で、出力サイズの板に貼る（libobs の出力縮小と同じ手順）
        Native.gs_ortho(0, w, 0, h, -100, 100);
        while (Native.gs_effect_loop(effect, "Draw")) Native.gs_draw_sprite(tex, 0, w, h);
        return true;
    }

    public static IntPtr SystemCursor(int id) => LoadCursor(IntPtr.Zero, id);
    public const int IDC_ARROW = 32512, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_SIZEALL = 32646;

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000, SS_NOTIFY = 0x100;
    const int WS_EX_ACCEPTFILES = 0x10, WM_DROPFILES = 0x233, WM_RBUTTONUP = 0x205;
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, WM_SETCURSOR = 0x20, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
    const long MK_SHIFT = 0x4, MK_CONTROL = 0x8;
    const int VK_SHIFT = 0x10;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32")] static extern bool ReleaseCapture();
    [DllImport("user32")] static extern uint GetDoubleClickTime();
    [DllImport("user32")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32")] static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32")] static extern short GetKeyState(int key);
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint idx, System.Text.StringBuilder? file, uint size);
    [DllImport("shell32")] static extern bool DragQueryPoint(IntPtr drop, out POINT pt);
    [DllImport("shell32")] static extern void DragFinish(IntPtr drop);
}
