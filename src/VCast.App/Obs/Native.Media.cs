using System.Runtime.InteropServices;

namespace VCast.Obs;

// メディアソース（ffmpeg_source）の再生操作と音量
public static unsafe partial class Native
{
    [DllImport(O)] public static extern void obs_source_media_play_pause(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool pause);
    [DllImport(O)] public static extern void obs_source_media_restart(IntPtr source);
    [DllImport(O)] public static extern void obs_source_media_stop(IntPtr source);
    [DllImport(O)] public static extern int obs_source_media_get_state(IntPtr source);
    [DllImport(O)] public static extern long obs_source_media_get_time(IntPtr source);
    [DllImport(O)] public static extern long obs_source_media_get_duration(IntPtr source);
    [DllImport(O)] public static extern void obs_source_set_volume(IntPtr source, float volume);
    [DllImport(O)] public static extern float obs_source_get_volume(IntPtr source);

    // --- トランジション ---
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_transition_types(nuint idx, out IntPtr id);
    [DllImport(O)] public static extern void obs_transition_set(IntPtr transition, IntPtr source);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_transition_start(IntPtr transition, int mode, uint durationMs, IntPtr dest);
    [DllImport(O)] public static extern void obs_transition_set_manual_time(IntPtr transition, float t);
    [DllImport(O)] public static extern void obs_transition_clear(IntPtr transition);
    [DllImport(O)] public static extern void obs_source_video_render(IntPtr source);
    public const int OBS_TRANSITION_MODE_AUTO = 0, OBS_TRANSITION_MODE_MANUAL = 1;

    public const int OBS_MEDIA_STATE_NONE = 0, OBS_MEDIA_STATE_PLAYING = 1, OBS_MEDIA_STATE_OPENING = 2, OBS_MEDIA_STATE_BUFFERING = 3,
        OBS_MEDIA_STATE_PAUSED = 4, OBS_MEDIA_STATE_STOPPED = 5, OBS_MEDIA_STATE_ENDED = 6, OBS_MEDIA_STATE_ERROR = 7;
}
