using System.Runtime.InteropServices;

namespace VCast.Obs;

// libobs (obs.dll 32.1.x) の C API。構造体レイアウトは同梱バイナリのバージョンに合わせること。
public static unsafe partial class Native
{
    const string O = "obs";

    // --- kernel32 ---
    [DllImport("kernel32")] public static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr AddDllDirectory(string path);
    public const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;

    // --- ログ ---
    [DllImport(O)] public static extern void base_set_log_handler(delegate* unmanaged[Cdecl]<int, byte*, IntPtr, IntPtr, void> handler, IntPtr param);
    [DllImport(O)] public static extern void base_set_crash_handler(delegate* unmanaged[Cdecl]<byte*, IntPtr, IntPtr, void> handler, IntPtr param);
    // ucrtbase は vsnprintf をエクスポートしていない（ヘッダ内インライン）ため msvcrt の _vsnprintf を使う
    [DllImport("msvcrt", EntryPoint = "_vsnprintf", CallingConvention = CallingConvention.Cdecl)] public static extern int vsnprintf(byte* buf, nuint size, byte* fmt, IntPtr args);

    // --- コア ---
    [DllImport(O)] public static extern IntPtr obs_get_version_string();
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_startup([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, [MarshalAs(UnmanagedType.LPUTF8Str)] string moduleConfigPath, IntPtr store);
    [DllImport(O)] public static extern void obs_shutdown();
    // モジュールが「UIスレッドで実行して」と依頼してくる処理の受け口（本来は OBS 本体の画面側が設定する）
    [DllImport(O)] public static extern void obs_set_ui_task_handler(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, void> handler);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_wait_for_destroy_queue();
    [DllImport(O)] public static extern void obs_add_data_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(O)] public static extern void obs_add_module_path([MarshalAs(UnmanagedType.LPUTF8Str)] string bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(O)] public static extern void obs_apply_private_data(IntPtr settings);
    [DllImport(O)] public static extern int obs_reset_video(ref obs_video_info ovi);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_reset_audio(ref obs_audio_info oai);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_get_video_info(out obs_video_info_raw ovi);

    // --- モジュール ---
    [DllImport(O)] public static extern void obs_find_modules2(delegate* unmanaged[Cdecl]<IntPtr, obs_module_info2*, void> cb, IntPtr param);
    [DllImport(O)] public static extern int obs_open_module(out IntPtr module, byte* path, byte* dataPath);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_init_module(IntPtr module);
    [DllImport(O)] public static extern void obs_post_load_modules();
    [DllImport(O)] public static extern void obs_log_loaded_modules();
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_input_types(nuint idx, out IntPtr id);

    // --- obs_data ---
    [DllImport(O)] public static extern IntPtr obs_data_create();
    [DllImport(O)] public static extern void obs_data_release(IntPtr data);
    [DllImport(O)] public static extern void obs_data_set_string(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val);
    [DllImport(O)] public static extern void obs_data_set_int(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long val);
    [DllImport(O)] public static extern void obs_data_set_bool(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.U1)] bool val);

    // --- ソース / シーン ---
    [DllImport(O)] public static extern IntPtr obs_source_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(O)] public static extern void obs_source_release(IntPtr source);
    [DllImport(O)] public static extern void obs_source_remove(IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_scene_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern IntPtr obs_scene_get_source(IntPtr scene);
    [DllImport(O)] public static extern IntPtr obs_scene_add(IntPtr scene, IntPtr source);
    [DllImport(O)] public static extern void obs_scene_release(IntPtr scene);
    [DllImport(O)] public static extern void obs_set_output_source(uint channel, IntPtr source);

    // --- 描画 ---
    [DllImport(O)] public static extern IntPtr obs_display_create(ref gs_init_data data, uint backgroundColor);
    [DllImport(O)] public static extern void obs_display_destroy(IntPtr display);
    [DllImport(O)] public static extern void obs_display_resize(IntPtr display, uint cx, uint cy);
    [DllImport(O)] public static extern void obs_display_set_enabled(IntPtr display, [MarshalAs(UnmanagedType.U1)] bool enable);
    [DllImport(O)] public static extern void obs_display_add_draw_callback(IntPtr display, delegate* unmanaged[Cdecl]<IntPtr, uint, uint, void> draw, IntPtr param);
    [DllImport(O)] public static extern void obs_display_remove_draw_callback(IntPtr display, delegate* unmanaged[Cdecl]<IntPtr, uint, uint, void> draw, IntPtr param);
    [DllImport(O)] public static extern void obs_render_main_texture();
    [DllImport(O)] public static extern void gs_viewport_push();
    [DllImport(O)] public static extern void gs_viewport_pop();
    [DllImport(O)] public static extern void gs_projection_push();
    [DllImport(O)] public static extern void gs_projection_pop();
    [DllImport(O)] public static extern void gs_ortho(float left, float right, float top, float bottom, float znear, float zfar);
    [DllImport(O)] public static extern void gs_set_viewport(int x, int y, int width, int height);

    [StructLayout(LayoutKind.Sequential)]
    public struct obs_video_info
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string graphics_module;
        public uint fps_num, fps_den, base_width, base_height, output_width, output_height;
        public int output_format;
        public uint adapter;
        [MarshalAs(UnmanagedType.U1)] public bool gpu_conversion;
        public int colorspace, range, scale_type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct obs_video_info_raw
    {
        public IntPtr graphics_module;
        public uint fps_num, fps_den, base_width, base_height, output_width, output_height;
        public int output_format;
        public uint adapter;
        public byte gpu_conversion;
        public int colorspace, range, scale_type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct obs_audio_info { public uint samples_per_sec; public int speakers; }

    [StructLayout(LayoutKind.Sequential)]
    public struct obs_module_info2 { public byte* bin_path; public byte* data_path; public byte* name; }

    [StructLayout(LayoutKind.Sequential)]
    public struct gs_init_data
    {
        public IntPtr hwnd; // struct gs_window
        public uint cx, cy, num_backbuffers;
        public int format;    // gs_color_format
        public int zsformat;  // gs_zstencil_format
        public uint adapter;
    }

    public const int GS_BGRA = 5, GS_ZS_NONE = 0;
    public const int VIDEO_FORMAT_NV12 = 2, // enum video_format: NONE=0, I420=1, NV12=2, YVYU=3（3にするとGPU変換が効かず録画でクラッシュした）
                    VIDEO_CS_709 = 2, VIDEO_RANGE_PARTIAL = 1, OBS_SCALE_BICUBIC = 2;
    public const int SPEAKERS_STEREO = 2;
}
