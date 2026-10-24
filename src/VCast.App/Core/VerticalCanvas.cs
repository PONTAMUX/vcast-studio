using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

// 縦型（1080x1920）の同時出力用キャンバス。レイアウトはプリセットから自動で組む
//   stack  : 上に本番の画面（横長をそのまま縮小）、下にアバターを大きく
//   avatar : アバターを画面いっぱい、本番の画面を上に小さく
//   crop   : 本番の画面の中央を縦に切り抜いて全面に
public sealed unsafe class VerticalCanvas : IDisposable
{
    public const uint Width = 1080, Height = 1920;
    IntPtr _canvas, _scene;
    readonly List<IntPtr> _items = new();

    public bool Active => _canvas != IntPtr.Zero;
    public IntPtr Video => _canvas == IntPtr.Zero ? IntPtr.Zero : Native.obs_canvas_get_video(_canvas);
    public IntPtr SceneSource => _scene == IntPtr.Zero ? IntPtr.Zero : Native.obs_scene_get_source(_scene);

    public bool Start(uint fps)
    {
        if (_canvas != IntPtr.Zero) return true;
        var ovi = new Native.obs_video_info
        {
            graphics_module = "libobs-d3d11", fps_num = fps, fps_den = 1,
            base_width = Width, base_height = Height, output_width = Width, output_height = Height,
            output_format = Native.VIDEO_FORMAT_NV12, adapter = 0, gpu_conversion = true,
            colorspace = Native.VIDEO_CS_709, range = Native.VIDEO_RANGE_PARTIAL, scale_type = Native.OBS_SCALE_BICUBIC,
        };
        // ACTIVATE: 中のソースを動かす / SCENE_REF: シーンを参照で持つ（音は本編と同じものを使うので MIX_AUDIO は付けない）
        _canvas = Native.obs_canvas_create("縦型", ref ovi, Native.OBS_CANVAS_ACTIVATE | Native.OBS_CANVAS_SCENE_REF);
        if (_canvas == IntPtr.Zero) { ObsEngine.Log("[vertical] canvas create failed"); return false; }
        _scene = Native.obs_canvas_scene_create(_canvas, "縦型レイアウト");
        Native.obs_canvas_set_channel(_canvas, 0, Native.obs_scene_get_source(_scene));
        ObsEngine.Log("[vertical] canvas started");
        return true;
    }

    // program: 本番（トランジション）のソース、avatar: アバターのソース（無ければ 0）
    public void Layout(string preset, IntPtr program, IntPtr avatar)
    {
        if (_scene == IntPtr.Zero) return;
        foreach (var it in _items) Native.obs_sceneitem_remove(it);
        _items.Clear();
        float mainW = ObsEngine.BaseWidth, mainH = ObsEngine.BaseHeight;

        IntPtr Add(IntPtr src, float x, float y, float scale)
        {
            var it = Native.obs_scene_add(_scene, src);
            if (it == IntPtr.Zero) return it;
            var p = new Native.vec2 { x = x, y = y }; var s = new Native.vec2 { x = scale, y = scale };
            Native.obs_sceneitem_set_pos(it, ref p); Native.obs_sceneitem_set_scale(it, ref s);
            _items.Add(it);
            return it;
        }

        switch (preset)
        {
            case "crop":
            {
                // 高さを合わせて中央を切り抜く
                var sc = Height / mainH;
                var w = mainW * sc;
                if (program != IntPtr.Zero) Add(program, (Width - w) / 2, 0, sc);
                break;
            }
            case "avatar":
            {
                if (avatar != IntPtr.Zero)
                {
                    float aw = Native.obs_source_get_width(avatar), ah = Native.obs_source_get_height(avatar);
                    if (aw > 0 && ah > 0) { var sc = Math.Min(Width / aw, Height / ah); Add(avatar, (Width - aw * sc) / 2, Height - ah * sc, sc); }
                }
                if (program != IntPtr.Zero) { var sc = Width * 0.9f / mainW; Add(program, Width * 0.05f, 80, sc); }
                break;
            }
            default: // stack
            {
                var sc = Width / mainW; var top = mainH * sc;
                if (program != IntPtr.Zero) Add(program, 0, 0, sc);
                if (avatar != IntPtr.Zero)
                {
                    float aw = Native.obs_source_get_width(avatar), ah = Native.obs_source_get_height(avatar);
                    if (aw > 0 && ah > 0)
                    {
                        var room = Height - top;
                        var asc = Math.Min(Width / aw, room / ah);
                        Add(avatar, (Width - aw * asc) / 2, Height - ah * asc, asc);
                    }
                }
                break;
            }
        }
    }

    public void Dispose()
    {
        foreach (var it in _items) Native.obs_sceneitem_remove(it);
        _items.Clear();
        if (_canvas != IntPtr.Zero)
        {
            Native.obs_canvas_set_channel(_canvas, 0, IntPtr.Zero);
            if (_scene != IntPtr.Zero) { Native.obs_source_remove(Native.obs_scene_get_source(_scene)); Native.obs_scene_release(_scene); _scene = IntPtr.Zero; }
            Native.obs_canvas_remove(_canvas);
            Native.obs_canvas_release(_canvas);
            _canvas = IntPtr.Zero;
        }
    }
}
