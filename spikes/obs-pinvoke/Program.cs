using System.Runtime.InteropServices;

const string ObsBin = @"C:\Program Files\obs-studio\bin\64bit";
Directory.SetCurrentDirectory(ObsBin);
Native.SetDefaultDllDirectories(0x1000);
Native.AddDllDirectory(ObsBin);
foreach (var t in new[]{"obs-nvenc-test.exe","obs-qsv-test.exe","obs-amf-test.exe"}) { var src=Path.Combine(ObsBin,t); var dst=Path.Combine(AppContext.BaseDirectory,t); if (File.Exists(src)&&!File.Exists(dst)) File.Copy(src,dst); }

Console.WriteLine("libobs " + Marshal.PtrToStringUTF8(Native.obs_get_version_string()));
var cfg = Path.Combine(AppContext.BaseDirectory, "obs-config");
Directory.CreateDirectory(cfg);
Console.WriteLine("startup: " + Native.obs_startup("ja-JP", cfg, IntPtr.Zero));

var ovi = new Native.obs_video_info {
    graphics_module = "libobs-d3d11",
    fps_num = 60, fps_den = 1,
    base_width = 1920, base_height = 1080, output_width = 1920, output_height = 1080,
    output_format = 3 /*NV12*/, adapter = 0, gpu_conversion = 1,
    colorspace = 2 /*709*/, range = 1 /*partial*/, scale_type = 3 /*bicubic*/
};
Console.WriteLine("reset_video: " + Native.obs_reset_video(ref ovi));
var oai = new Native.obs_audio_info { samples_per_sec = 48000, speakers = 2 };
Console.WriteLine("reset_audio: " + Native.obs_reset_audio(ref oai));

foreach (var m in new[]{"frontend-tools","aja-output-ui","decklink-output-ui","decklink-captions","obs-websocket"}) Native.obs_add_disabled_module(m);
Native.obs_load_all_modules();
Native.obs_post_load_modules();

for (nuint i = 0; Native.obs_enum_input_types(i, out var id); i++)
    Console.WriteLine("  input: " + Marshal.PtrToStringUTF8(id));
for (nuint i = 0; Native.obs_enum_encoder_types(i, out var id); i++)
    Console.WriteLine("  encoder: " + Marshal.PtrToStringUTF8(id));

Native.obs_shutdown();
Console.WriteLine("shutdown ok");

static class Native {
    [DllImport("kernel32")] public static extern bool SetDefaultDllDirectories(uint f);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr AddDllDirectory(string p);
    const string O = "obs";
    [DllImport(O)] public static extern IntPtr obs_get_version_string();
    [DllImport(O)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_startup([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, [MarshalAs(UnmanagedType.LPUTF8Str)] string cfg, IntPtr store);
    [DllImport(O)] public static extern int obs_reset_video(ref obs_video_info ovi);
    [DllImport(O)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_reset_audio(ref obs_audio_info oai);
    [DllImport(O)] public static extern void obs_load_all_modules();
    [DllImport(O)] public static extern void obs_add_disabled_module([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void obs_post_load_modules();
    [DllImport(O)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_input_types(nuint idx, out IntPtr id);
    [DllImport(O)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_encoder_types(nuint idx, out IntPtr id);
    [DllImport(O)] public static extern void obs_shutdown();

    [StructLayout(LayoutKind.Sequential)]
    public struct obs_video_info {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string graphics_module;
        public uint fps_num, fps_den, base_width, base_height, output_width, output_height;
        public int output_format; public uint adapter;
        [MarshalAs(UnmanagedType.U1)] public byte gpu_conversion;
        public int colorspace, range, scale_type;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct obs_audio_info { public uint samples_per_sec; public int speakers; }
}
