using System.Runtime.InteropServices;

namespace VCast.Obs;

// 音声（フェーダー・メーター・ミュート・モニタリング）とフィルタの libobs API
public static unsafe partial class Native
{
    // --- フェーダー ---
    [DllImport(O)] public static extern IntPtr obs_fader_create(int type);
    [DllImport(O)] public static extern void obs_fader_destroy(IntPtr fader);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_fader_attach_source(IntPtr fader, IntPtr source);
    [DllImport(O)] public static extern void obs_fader_detach_source(IntPtr fader);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_fader_set_deflection(IntPtr fader, float def);
    [DllImport(O)] public static extern float obs_fader_get_deflection(IntPtr fader);
    [DllImport(O)] public static extern float obs_fader_get_db(IntPtr fader);
    [DllImport(O)] public static extern void obs_fader_add_callback(IntPtr fader, delegate* unmanaged[Cdecl]<IntPtr, float, void> cb, IntPtr param);
    [DllImport(O)] public static extern void obs_fader_remove_callback(IntPtr fader, delegate* unmanaged[Cdecl]<IntPtr, float, void> cb, IntPtr param);
    public const int OBS_FADER_CUBIC = 0, OBS_FADER_IEC = 1, OBS_FADER_LOG = 2;

    // --- メーター（コールバックは音声スレッド。配列は MAX_AUDIO_CHANNELS=8 要素、単位 dBFS） ---
    [DllImport(O)] public static extern IntPtr obs_volmeter_create(int type);
    [DllImport(O)] public static extern void obs_volmeter_destroy(IntPtr volmeter);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_volmeter_attach_source(IntPtr volmeter, IntPtr source);
    [DllImport(O)] public static extern void obs_volmeter_detach_source(IntPtr volmeter);
    [DllImport(O)] public static extern void obs_volmeter_set_peak_meter_type(IntPtr volmeter, int type);
    [DllImport(O)] public static extern int obs_volmeter_get_nr_channels(IntPtr volmeter);
    [DllImport(O)] public static extern void obs_volmeter_add_callback(IntPtr volmeter, delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, void> cb, IntPtr param);
    [DllImport(O)] public static extern void obs_volmeter_remove_callback(IntPtr volmeter, delegate* unmanaged[Cdecl]<IntPtr, float*, float*, float*, void> cb, IntPtr param);
    public const int SAMPLE_PEAK_METER = 0, MAX_AUDIO_CHANNELS = 8;

    // --- 音声の状態 ---
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_source_muted(IntPtr source);
    [DllImport(O)] public static extern void obs_source_set_muted(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool muted);
    [DllImport(O)] public static extern int obs_source_get_monitoring_type(IntPtr source);
    [DllImport(O)] public static extern void obs_source_set_monitoring_type(IntPtr source, int type);
    [DllImport(O)] public static extern uint obs_source_get_output_flags(IntPtr source);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_set_audio_monitoring_device([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(O)] public static extern IntPtr obs_get_output_source(uint channel);
    public const int OBS_MONITORING_TYPE_NONE = 0, OBS_MONITORING_TYPE_MONITOR_ONLY = 1, OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT = 2;

    // --- フィルタ ---
    [DllImport(O)] public static extern IntPtr obs_source_create_private([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr settings);
    [DllImport(O)] public static extern void obs_source_filter_add(IntPtr source, IntPtr filter);
    [DllImport(O)] public static extern void obs_source_filter_remove(IntPtr source, IntPtr filter);
    [DllImport(O)] public static extern void obs_source_filter_set_order(IntPtr source, IntPtr filter, int movement);
    [DllImport(O)] public static extern void obs_source_enum_filters(IntPtr source, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> cb, IntPtr param);
    [DllImport(O)] public static extern IntPtr obs_source_get_filter_by_name(IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_source_enabled(IntPtr source);
    [DllImport(O)] public static extern void obs_source_set_enabled(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool enabled);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_filter_types(nuint idx, out IntPtr id);
    [DllImport(O)] public static extern nuint obs_source_filter_count(IntPtr source);
}
