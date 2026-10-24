using System.Runtime.InteropServices;

namespace VCast.Obs;

// カスタムソース（ネイティブアバター）用の libobs 関数
public static unsafe partial class Native
{
    [DllImport(O)] public static extern void obs_register_source_s(IntPtr info, nuint size);
    [DllImport(O)] public static extern void obs_enter_graphics();
    [DllImport(O)] public static extern void obs_leave_graphics();
    [DllImport(O)] public static extern void gs_texture_destroy(IntPtr tex);
    [DllImport(O)] public static extern IntPtr gs_texture_open_shared(uint handle);
}
