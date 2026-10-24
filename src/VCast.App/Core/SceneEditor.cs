using VCast.Obs;

namespace VCast.Core;

// シーンアイテムの位置・大きさ（元に戻す用のスナップショット）
public readonly record struct ItemTransform(Native.vec2 Pos, Native.vec2 Scale, Native.vec2 Bounds, float Rot, int BoundsType)
{
    public static ItemTransform Of(IntPtr item)
    {
        Native.obs_sceneitem_get_pos(item, out var p);
        Native.obs_sceneitem_get_scale(item, out var s);
        Native.obs_sceneitem_get_bounds(item, out var b);
        return new ItemTransform(p, s, b, Native.obs_sceneitem_get_rot(item), Native.obs_sceneitem_get_bounds_type(item));
    }

    public void ApplyTo(IntPtr item)
    {
        var p = Pos; var s = Scale; var b = Bounds;
        Native.obs_sceneitem_set_bounds_type(item, BoundsType);
        Native.obs_sceneitem_set_bounds(item, ref b);
        Native.obs_sceneitem_set_rot(item, Rot);
        Native.obs_sceneitem_set_scale(item, ref s);
        Native.obs_sceneitem_set_pos(item, ref p);
    }
}

// プレビュー上の選択・移動・拡大縮小。入力は UI スレッド、選択枠の描画はグラフィックススレッド。
// ハンドル: 0=左上 1=右上 2=右下 3=左下（縦横比維持、Shift で自由）/ 4=上 5=右 6=下 7=左（その方向だけ伸縮）
public sealed class SceneEditor : IPreviewOverlay
{
    readonly SceneManager _scenes;
    readonly object _lock = new();
    IntPtr _selected; // グラフィックススレッドからも読むので addref して保持

    enum Mode { None, Move, Resize }
    Mode _mode;
    int _handle;
    double _startX, _startY;
    Native.vec2 _startPos, _startScale, _startBounds;
    (double L, double T, double R, double B) _startBox;
    ItemTransform _before;
    bool _moved;

    const double HandlePx = 10; // つかめる範囲（画面ピクセル）
    const double SnapPx = 8;

    public event Action<IntPtr>? SelectionChanged;
    public event Action<IntPtr, ItemTransform>? TransformCommitted; // (アイテム, 変更前) → 元に戻す履歴へ
    public IntPtr Selected { get { lock (_lock) return _selected; } }

    public SceneEditor(SceneManager scenes) => _scenes = scenes;

    public void Select(IntPtr item)
    {
        IntPtr old;
        if (item != IntPtr.Zero) Native.obs_sceneitem_addref(item);
        lock (_lock) // ロック内では参照の差し替えだけ（release は描画スレッドのmutexを取りうるので外で）
        {
            old = _selected;
            _selected = item;
        }
        if (old != IntPtr.Zero) Native.obs_sceneitem_release(old);
        if (old != item) SelectionChanged?.Invoke(item);
    }

    // --- 入力（キャンバス座標、scale=画面px/キャンバスpx） ---
    public void PointerDown(double x, double y, double scale, bool shift)
    {
        var sel = Selected;
        if (sel != IntPtr.Zero && !Native.obs_sceneitem_locked(sel))
        {
            var box = Box(sel);
            var h = HitHandle(box, x, y, HandlePx / scale);
            if (h >= 0) { BeginDrag(Mode.Resize, sel, x, y, box); _handle = h; return; }
        }
        var hit = HitTest(x, y);
        Select(hit);
        if (hit != IntPtr.Zero && !Native.obs_sceneitem_locked(hit)) BeginDrag(Mode.Move, hit, x, y, Box(hit));
    }

    public void PointerMove(double x, double y, double scale, bool shift)
    {
        var sel = Selected;
        if (_mode == Mode.None || sel == IntPtr.Zero) return;
        _moved = true;
        var snap = SnapPx / scale;
        if (_mode == Mode.Move)
        {
            double dx = x - _startX, dy = y - _startY;
            // キャンバス端・中央へ吸着
            double w = _startBox.R - _startBox.L, h = _startBox.B - _startBox.T;
            dx = SnapAxis(_startBox.L + dx, w, ObsEngine.BaseWidth, snap) - _startBox.L;
            dy = SnapAxis(_startBox.T + dy, h, ObsEngine.BaseHeight, snap) - _startBox.T;
            var p = new Native.vec2 { x = (float)(_startPos.x + dx), y = (float)(_startPos.y + dy) };
            Native.obs_sceneitem_set_pos(sel, ref p);
        }
        else Resize(sel, x, y, shift);
    }

    public void PointerUp()
    {
        var sel = Selected;
        if (_mode != Mode.None && _moved && sel != IntPtr.Zero) TransformCommitted?.Invoke(sel, _before);
        _mode = Mode.None;
        _moved = false;
    }

    // マウスカーソルの形（0〜3=斜め、4/6=上下、5/7=左右、-1=なし）
    public int HandleAt(double x, double y, double scale)
    {
        var sel = Selected;
        return sel == IntPtr.Zero || Native.obs_sceneitem_locked(sel) ? -1 : HitHandle(Box(sel), x, y, HandlePx / scale);
    }

    public bool IsOverSelected(double x, double y)
    {
        var sel = Selected;
        if (sel == IntPtr.Zero) return false;
        var b = Box(sel);
        return x >= b.L && x <= b.R && y >= b.T && y <= b.B;
    }

    public void Nudge(float dx, float dy)
    {
        var sel = Selected;
        if (sel == IntPtr.Zero || Native.obs_sceneitem_locked(sel)) return;
        var before = ItemTransform.Of(sel);
        Native.obs_sceneitem_get_pos(sel, out var p);
        p.x += dx; p.y += dy;
        Native.obs_sceneitem_set_pos(sel, ref p);
        TransformCommitted?.Invoke(sel, before);
    }

    // 画面いっぱいに合わせる（アスペクト維持）
    public void FitToScreen() => WithUndo(sel =>
    {
        var src = Native.obs_sceneitem_get_source(sel);
        float w = Native.obs_source_get_width(src), h = Native.obs_source_get_height(src);
        if (w <= 0 || h <= 0) return;
        Native.obs_sceneitem_set_bounds_type(sel, Native.OBS_BOUNDS_NONE);
        var s = Math.Min(ObsEngine.BaseWidth / w, ObsEngine.BaseHeight / h);
        var scale = new Native.vec2 { x = s, y = s };
        Native.obs_sceneitem_set_scale(sel, ref scale);
        CenterInternal(sel);
    });

    public void CenterOnScreen() => WithUndo(CenterInternal);

    public void ResetSize() => WithUndo(sel =>
    {
        Native.obs_sceneitem_set_bounds_type(sel, Native.OBS_BOUNDS_NONE);
        Native.obs_sceneitem_set_rot(sel, 0);
        var one = new Native.vec2 { x = 1, y = 1 };
        Native.obs_sceneitem_set_scale(sel, ref one);
    });

    void WithUndo(Action<IntPtr> change)
    {
        var sel = Selected;
        if (sel == IntPtr.Zero) return;
        var before = ItemTransform.Of(sel);
        change(sel);
        TransformCommitted?.Invoke(sel, before);
    }

    void CenterInternal(IntPtr sel)
    {
        var box = Box(sel);
        Native.obs_sceneitem_get_pos(sel, out var p);
        p.x += (float)((ObsEngine.BaseWidth - (box.R - box.L)) / 2 - box.L);
        p.y += (float)((ObsEngine.BaseHeight - (box.B - box.T)) / 2 - box.T);
        Native.obs_sceneitem_set_pos(sel, ref p);
    }

    void BeginDrag(Mode mode, IntPtr item, double x, double y, (double, double, double, double) box)
    {
        _mode = mode; _startX = x; _startY = y; _startBox = box; _moved = false;
        _before = ItemTransform.Of(item);
        _startPos = _before.Pos; _startScale = _before.Scale; _startBounds = _before.Bounds;
    }

    void Resize(IntPtr item, double x, double y, bool free)
    {
        var b = _startBox;
        double ow = b.R - b.L, oh = b.B - b.T;
        if (ow < 1 || oh < 1) return;
        double sx = 1, sy = 1, ax, ay;
        if (_handle < 4)
        {
            // 角: 反対側の角を固定点に
            ax = _handle is 0 or 3 ? b.R : b.L; ay = _handle is 0 or 1 ? b.B : b.T;
            sx = Math.Max(4, Math.Abs(x - ax)) / ow; sy = Math.Max(4, Math.Abs(y - ay)) / oh;
            if (!free) sx = sy = Math.Max(sx, sy); // 既定は縦横比維持、Shift で自由変形
        }
        else
        {
            // 辺: 反対側の辺を固定して、その方向だけ伸縮
            ax = _handle == 7 ? b.R : b.L; ay = _handle == 4 ? b.B : b.T;
            if (_handle is 5 or 7) sx = Math.Max(4, Math.Abs(x - ax)) / ow;
            else sy = Math.Max(4, Math.Abs(y - ay)) / oh;
        }

        if (Native.obs_sceneitem_get_bounds_type(item) != Native.OBS_BOUNDS_NONE)
        {
            var nb = new Native.vec2 { x = (float)(_startBounds.x * sx), y = (float)(_startBounds.y * sy) };
            Native.obs_sceneitem_set_bounds(item, ref nb);
        }
        else
        {
            var ns = new Native.vec2 { x = (float)(_startScale.x * sx), y = (float)(_startScale.y * sy) };
            Native.obs_sceneitem_set_scale(item, ref ns);
        }
        // アライメント設定に関係なく、固定点が動かないよう位置を補正
        var nbx = Box(item);
        double cx, cy;
        if (_handle < 4) { cx = _handle is 0 or 3 ? nbx.R : nbx.L; cy = _handle is 0 or 1 ? nbx.B : nbx.T; }
        else { cx = _handle == 7 ? nbx.R : nbx.L; cy = _handle == 4 ? nbx.B : nbx.T; }
        Native.obs_sceneitem_get_pos(item, out var p);
        p.x += (float)(ax - cx); p.y += (float)(ay - cy);
        Native.obs_sceneitem_set_pos(item, ref p);
    }

    static double SnapAxis(double start, double size, double canvas, double snap)
    {
        foreach (var target in new[] { 0.0, canvas - size, (canvas - size) / 2 })
            if (Math.Abs(start - target) < snap) return target;
        return start;
    }

    public IntPtr HitTest(double x, double y)
    {
        if (_scenes.Current == null) return IntPtr.Zero;
        var items = SceneManager.EnumItems(_scenes.Current.Scene);
        for (var i = items.Count - 1; i >= 0; i--) // 上にあるものから
        {
            if (!Native.obs_sceneitem_visible(items[i])) continue;
            var b = Box(items[i]);
            if (x >= b.L && x <= b.R && y >= b.T && y <= b.B) return items[i];
        }
        return IntPtr.Zero;
    }

    static (double X, double Y)[] Handles((double L, double T, double R, double B) b)
    {
        double mx = (b.L + b.R) / 2, my = (b.T + b.B) / 2;
        return new[] { (b.L, b.T), (b.R, b.T), (b.R, b.B), (b.L, b.B), (mx, b.T), (b.R, my), (mx, b.B), (b.L, my) };
    }

    static int HitHandle((double L, double T, double R, double B) b, double x, double y, double r)
    {
        var pts = Handles(b);
        for (var i = 0; i < pts.Length; i++)
            if (Math.Abs(x - pts[i].X) <= r && Math.Abs(y - pts[i].Y) <= r) return i;
        return -1;
    }

    // シーンアイテムの外接矩形（キャンバス座標）。回転していても外接で扱う
    public static (double L, double T, double R, double B) Box(IntPtr item)
    {
        Native.obs_sceneitem_get_box_transform(item, out var m);
        double L = double.MaxValue, T = double.MaxValue, R = double.MinValue, B = double.MinValue;
        foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
        {
            double px = m.x.x * u + m.y.x * v + m.t.x, py = m.x.y * u + m.y.y * v + m.t.y;
            L = Math.Min(L, px); R = Math.Max(R, px); T = Math.Min(T, py); B = Math.Max(B, py);
        }
        return (L, T, R, B);
    }

    // --- グラフィックススレッド ---
    public void Draw(float offX, float offY, float scale)
    {
        lock (_lock)
        {
            if (_selected == IntPtr.Zero) return;
            var b = Box(_selected);
            var locked = Native.obs_sceneitem_locked(_selected);
            float l = offX + (float)b.L * scale, t = offY + (float)b.T * scale;
            float r = offX + (float)b.R * scale, btm = offY + (float)b.B * scale;
            var solid = Native.obs_get_base_effect(Native.OBS_EFFECT_SOLID);
            var color = Native.gs_effect_get_param_by_name(solid, "color");
            // ロック中は灰色の枠だけ（ハンドルなし）
            var c = locked ? new Native.vec4 { x = 0.6f, y = 0.6f, z = 0.65f, w = 1f } : new Native.vec4 { x = 0.49f, y = 0.36f, z = 1f, w = 1f };
            Native.gs_effect_set_vec4(color, ref c);
            while (Native.gs_effect_loop(solid, "Solid"))
            {
                const float th = 2f, hs = 5f;
                Quad(l, t, r, t + th); Quad(l, btm - th, r, btm);
                Quad(l, t, l + th, btm); Quad(r - th, t, r, btm);
                if (locked) continue;
                float mx = (l + r) / 2, my = (t + btm) / 2;
                foreach (var (px, py) in new[] { (l, t), (r, t), (r, btm), (l, btm), (mx, t), (r, my), (mx, btm), (l, my) })
                    Quad(px - hs, py - hs, px + hs, py + hs);
            }
        }
    }

    static void Quad(float l, float t, float r, float b)
    {
        Native.gs_render_start(true);
        Native.gs_vertex2f(l, t); Native.gs_vertex2f(r, t); Native.gs_vertex2f(l, b); Native.gs_vertex2f(r, b);
        Native.gs_render_stop(Native.GS_TRISTRIP);
    }
}
