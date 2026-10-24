using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.AvatarNative;

// libobs のカスタムソース「ネイティブアバター」。NativeAvatar が描いた共有テクスチャを、プレビュー・配信・録画の映像に出す。
// obs_source_info の先頭部分だけを同じ並びで定義して obs_register_source_s に渡す（残りは 0 = 未使用。libobs は size バイトだけコピーする）
public static unsafe class NativeAvatarSource
{
    public const string Id = "vcast_native_avatar";
    const int OBS_SOURCE_TYPE_INPUT = 0;
    const uint OBS_SOURCE_VIDEO = 1, OBS_SOURCE_CUSTOM_DRAW = 1 << 3;
    const int OBS_EFFECT_PREMULTIPLIED_ALPHA = 7;

    [StructLayout(LayoutKind.Sequential)]
    struct SourceInfo
    {
        public IntPtr id; public int type; public uint output_flags;
        public IntPtr get_name, create, destroy, get_width, get_height, get_defaults, get_properties, update, activate, deactivate, show, hide, video_tick, video_render;
    }

    sealed class State { public IntPtr Texture; public uint Handle; }

    static IntPtr _nameUtf8;
    static SourceInfo _info; // libobs は登録時にコピーするが、念のため生存させる
    public static bool Registered { get; private set; }

    public static void Register()
    {
        if (Registered) return;
        _nameUtf8 = Marshal.StringToCoTaskMemUTF8("VCast ネイティブアバター（実験）");
        _info = new SourceInfo
        {
            id = Marshal.StringToCoTaskMemUTF8(Id), type = OBS_SOURCE_TYPE_INPUT, output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_CUSTOM_DRAW,
            get_name = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)&GetName,
            create = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&Create,
            destroy = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&Destroy,
            get_width = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&GetWidth,
            get_height = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint>)&GetHeight,
            video_render = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&VideoRender,
        };
        fixed (SourceInfo* p = &_info) Native.obs_register_source_s((IntPtr)p, (nuint)sizeof(SourceInfo));
        Registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static IntPtr GetName(IntPtr typeData) => _nameUtf8;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static IntPtr Create(IntPtr settings, IntPtr source) => GCHandle.ToIntPtr(GCHandle.Alloc(new State()));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Destroy(IntPtr data)
    {
        var gh = GCHandle.FromIntPtr(data);
        if (gh.Target is State st && st.Texture != IntPtr.Zero)
        {
            Native.obs_enter_graphics();
            Native.gs_texture_destroy(st.Texture);
            Native.obs_leave_graphics();
        }
        gh.Free();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static uint GetWidth(IntPtr data) => (uint)Math.Max(1, NativeAvatar.Current?.Width ?? 1);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static uint GetHeight(IntPtr data) => (uint)Math.Max(1, NativeAvatar.Current?.Height ?? 1);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void VideoRender(IntPtr data, IntPtr effect)
    {
        try
        {
            var st = (State)GCHandle.FromIntPtr(data).Target!;
            var av = NativeAvatar.Current;
            var handle = av?.SharedHandle ?? 0;
            if (handle != st.Handle)
            {
                if (st.Texture != IntPtr.Zero) { Native.gs_texture_destroy(st.Texture); st.Texture = IntPtr.Zero; }
                st.Handle = handle;
                if (handle != 0)
                {
                    st.Texture = Native.gs_texture_open_shared(handle);
                    if (st.Texture == IntPtr.Zero) ObsEngine.Log("[avatar-native] 共有テクスチャを開けませんでした（GPU が違う可能性）。ネイティブ描画をオフにしてください");
                }
            }
            if (st.Texture == IntPtr.Zero || av == null) return;
            var eff = Native.obs_get_base_effect(OBS_EFFECT_PREMULTIPLIED_ALPHA);
            var image = Native.gs_effect_get_param_by_name(eff, "image");
            Native.gs_effect_set_texture(image, st.Texture);
            while (Native.gs_effect_loop(eff, "Draw")) Native.gs_draw_sprite(st.Texture, 0, (uint)av.Width, (uint)av.Height);
        }
        catch { }
    }
}
