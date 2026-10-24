using System.Runtime.InteropServices;

namespace VCast.Obs;

// 配信・録画・リプレイバッファ・仮想カメラ（エンコーダ/サービス/出力）の libobs API
public static unsafe partial class Native
{
    // --- エンコーダ ---
    [DllImport(O)] public static extern IntPtr obs_video_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(O)] public static extern IntPtr obs_audio_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings, nuint mixerIdx, IntPtr hotkeyData);
    [DllImport(O)] public static extern void obs_encoder_release(IntPtr encoder);
    [DllImport(O)] public static extern void obs_encoder_update(IntPtr encoder, IntPtr settings);
    [DllImport(O)] public static extern void obs_encoder_set_video(IntPtr encoder, IntPtr video);
    [DllImport(O)] public static extern void obs_encoder_set_audio(IntPtr encoder, IntPtr audio);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_encoder_active(IntPtr encoder);
    [DllImport(O)] public static extern IntPtr obs_get_video();
    [DllImport(O)] public static extern IntPtr obs_get_audio();
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_encoder_types(nuint idx, out IntPtr id);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_output_types(nuint idx, out IntPtr id);
    [DllImport(O)] public static extern IntPtr obs_encoder_get_display_name([MarshalAs(UnmanagedType.LPUTF8Str)] string id);

    // --- サービス ---
    [DllImport(O)] public static extern IntPtr obs_service_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(O)] public static extern void obs_service_release(IntPtr service);

    // --- 出力 ---
    [DllImport(O)] public static extern IntPtr obs_output_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(O)] public static extern void obs_output_release(IntPtr output);
    [DllImport(O)] public static extern void obs_output_update(IntPtr output, IntPtr settings);
    [DllImport(O)] public static extern void obs_output_set_video_encoder(IntPtr output, IntPtr encoder);
    [DllImport(O)] public static extern void obs_output_set_audio_encoder(IntPtr output, IntPtr encoder, nuint idx);
    [DllImport(O)] public static extern void obs_output_set_service(IntPtr output, IntPtr service);
    [DllImport(O)] public static extern void obs_output_set_media(IntPtr output, IntPtr video, IntPtr audio);
    [DllImport(O)] public static extern void obs_output_set_reconnect_settings(IntPtr output, int retryCount, int retrySec);
    [DllImport(O)] public static extern void obs_output_set_delay(IntPtr output, uint delaySec, uint flags);
    public const uint OBS_OUTPUT_DELAY_PRESERVE = 1; // 再接続しても遅延を保つ
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_start(IntPtr output);
    [DllImport(O)] public static extern void obs_output_stop(IntPtr output);
    [DllImport(O)] public static extern void obs_output_force_stop(IntPtr output);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_active(IntPtr output);
    [DllImport(O)] public static extern IntPtr obs_output_get_last_error(IntPtr output);
    [DllImport(O)] public static extern ulong obs_output_get_total_bytes(IntPtr output);
    [DllImport(O)] public static extern int obs_output_get_frames_dropped(IntPtr output);
    [DllImport(O)] public static extern int obs_output_get_total_frames(IntPtr output);
    [DllImport(O)] public static extern float obs_output_get_congestion(IntPtr output);
    [DllImport(O)] public static extern IntPtr obs_output_get_signal_handler(IntPtr output);
    [DllImport(O)] public static extern IntPtr obs_output_get_proc_handler(IntPtr output);
    [DllImport(O)] public static extern IntPtr obs_output_get_id(IntPtr output);

    // --- シグナル引数・手続き呼び出し ---
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool calldata_get_data(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, void* output, nuint size);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool calldata_get_string(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr str);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool proc_handler_call(IntPtr handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, calldata* data);
    [DllImport(O)] public static extern void bfree(void* ptr);

    [StructLayout(LayoutKind.Sequential)]
    public struct calldata { public byte* stack; public nuint size, capacity; [MarshalAs(UnmanagedType.U1)] public bool isFixed; }

    public static long CallInt(IntPtr cd, string name) { long v = 0; calldata_get_data(cd, name, &v, 8); return v; }
    public static string? CallString(IntPtr cd, string name) => calldata_get_string(cd, name, out var p) ? Str(p) : null;

    // --- 全体の統計 ---
    [DllImport(O)] public static extern double obs_get_active_fps();
    [DllImport(O)] public static extern ulong obs_get_average_frame_time_ns();
    [DllImport(O)] public static extern uint obs_get_total_frames();
    [DllImport(O)] public static extern uint obs_get_lagged_frames();
    [DllImport(O)] public static extern uint video_output_get_skipped_frames(IntPtr video);
    [DllImport(O)] public static extern uint video_output_get_total_frames(IntPtr video);

    public const int OBS_OUTPUT_SUCCESS = 0, OBS_OUTPUT_BAD_PATH = -1, OBS_OUTPUT_CONNECT_FAILED = -2, OBS_OUTPUT_INVALID_STREAM = -3,
        OBS_OUTPUT_ERROR = -4, OBS_OUTPUT_DISCONNECTED = -5, OBS_OUTPUT_UNSUPPORTED = -6, OBS_OUTPUT_NO_SPACE = -7, OBS_OUTPUT_ENCODE_ERROR = -8;
}
