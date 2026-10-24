using System.Runtime.InteropServices;

namespace VCast.Obs;

// シーンコレクションの保存・読み込み（OBS と同じ JSON 形式）
public static unsafe partial class Native
{
    [DllImport(O)] public static extern IntPtr obs_save_sources_filtered(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte> cb, IntPtr param);
    [DllImport(O)] public static extern IntPtr obs_save_source(IntPtr source);
    [DllImport(O)] public static extern void obs_load_sources(IntPtr array, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> cb, IntPtr param);
    [DllImport(O)] public static extern IntPtr obs_load_source(IntPtr data);
    [DllImport(O)] public static extern void obs_data_erase(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_data_save_json_safe(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, [MarshalAs(UnmanagedType.LPUTF8Str)] string tempExt, [MarshalAs(UnmanagedType.LPUTF8Str)] string backupExt);
    [DllImport(O)] public static extern IntPtr obs_data_create_from_json_file_safe([MarshalAs(UnmanagedType.LPUTF8Str)] string file, [MarshalAs(UnmanagedType.LPUTF8Str)] string backupExt);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_data_has_user_value(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern IntPtr obs_scene_from_source(IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_scene_get_ref(IntPtr scene);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_source_is_scene(IntPtr source);

    // --- キャンバス（OBS 31+。縦型の同時出力に使う） ---
    [DllImport(O)] public static extern IntPtr obs_canvas_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref obs_video_info ovi, uint flags);
    [DllImport(O)] public static extern IntPtr obs_canvas_scene_create(IntPtr canvas, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void obs_canvas_set_channel(IntPtr canvas, uint channel, IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_canvas_get_video(IntPtr canvas);
    [DllImport(O)] public static extern void obs_canvas_remove(IntPtr canvas);
    [DllImport(O)] public static extern void obs_canvas_release(IntPtr canvas);
    // enum obs_canvas_flags: MAIN=1, ACTIVATE=2, MIX_AUDIO=4, SCENE_REF=8, EPHEMERAL=16
    public const uint OBS_CANVAS_ACTIVATE = 1 << 1, OBS_CANVAS_MIX_AUDIO = 1 << 2, OBS_CANVAS_SCENE_REF = 1 << 3;
}
