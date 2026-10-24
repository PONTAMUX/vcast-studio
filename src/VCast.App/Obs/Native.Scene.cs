using System.Runtime.InteropServices;

namespace VCast.Obs;

// シーン編集・プロパティ・オーバーレイ描画まわりの libobs API
public static unsafe partial class Native
{
    // --- ソース情報 ---
    [DllImport(O)] public static extern IntPtr obs_source_get_display_name([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(O)] public static extern uint obs_get_source_output_flags([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(O)] public static extern IntPtr obs_source_get_name(IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_source_get_id(IntPtr source);
    [DllImport(O)] public static extern void obs_source_set_name(IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern IntPtr obs_get_source_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern IntPtr obs_get_source_by_uuid([MarshalAs(UnmanagedType.LPUTF8Str)] string uuid);
    [DllImport(O)] public static extern IntPtr obs_source_get_uuid(IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_source_get_settings(IntPtr source);
    [DllImport(O)] public static extern void obs_source_update(IntPtr source, IntPtr settings);
    [DllImport(O)] public static extern void obs_source_reset_settings(IntPtr source, IntPtr settings);
    [DllImport(O)] public static extern IntPtr obs_source_properties(IntPtr source);
    [DllImport(O)] public static extern uint obs_source_get_width(IntPtr source);
    [DllImport(O)] public static extern uint obs_source_get_height(IntPtr source);
    [DllImport(O)] public static extern IntPtr obs_source_get_ref(IntPtr source);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_source_removed(IntPtr source);

    public const uint OBS_SOURCE_DEPRECATED = 1 << 8, OBS_SOURCE_CAP_DISABLED = 1 << 10, OBS_SOURCE_VIDEO = 1, OBS_SOURCE_AUDIO = 2;

    // --- シーンアイテム ---
    [DllImport(O)] public static extern void obs_scene_enum_items(IntPtr scene, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte> cb, IntPtr param);
    [DllImport(O)] public static extern IntPtr obs_sceneitem_get_source(IntPtr item);
    [DllImport(O)] public static extern long obs_sceneitem_get_id(IntPtr item);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_sceneitem_visible(IntPtr item);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_sceneitem_set_visible(IntPtr item, [MarshalAs(UnmanagedType.U1)] bool visible);
    [DllImport(O)] public static extern void obs_sceneitem_remove(IntPtr item);
    [DllImport(O)] public static extern void obs_sceneitem_set_order(IntPtr item, int movement);
    [DllImport(O)] public static extern void obs_sceneitem_get_pos(IntPtr item, out vec2 pos);
    [DllImport(O)] public static extern void obs_sceneitem_set_pos(IntPtr item, ref vec2 pos);
    [DllImport(O)] public static extern void obs_sceneitem_get_scale(IntPtr item, out vec2 scale);
    [DllImport(O)] public static extern void obs_sceneitem_set_scale(IntPtr item, ref vec2 scale);
    [DllImport(O)] public static extern int obs_sceneitem_get_bounds_type(IntPtr item);
    [DllImport(O)] public static extern void obs_sceneitem_get_bounds(IntPtr item, out vec2 bounds);
    [DllImport(O)] public static extern void obs_sceneitem_set_bounds(IntPtr item, ref vec2 bounds);
    [DllImport(O)] public static extern void obs_sceneitem_get_box_transform(IntPtr item, out matrix4 transform);
    [DllImport(O)] public static extern void obs_sceneitem_addref(IntPtr item);
    [DllImport(O)] public static extern void obs_sceneitem_release(IntPtr item);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_sceneitem_locked(IntPtr item);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_sceneitem_set_locked(IntPtr item, [MarshalAs(UnmanagedType.U1)] bool locked);
    [DllImport(O)] public static extern float obs_sceneitem_get_rot(IntPtr item);
    [DllImport(O)] public static extern void obs_sceneitem_set_rot(IntPtr item, float rot);
    [DllImport(O)] public static extern void obs_sceneitem_set_bounds_type(IntPtr item, int type);
    [DllImport(O)] public static extern void obs_sceneitem_set_order_position(IntPtr item, int position);

    public const int OBS_ORDER_MOVE_UP = 0, OBS_ORDER_MOVE_DOWN = 1, OBS_ORDER_MOVE_TOP = 2, OBS_ORDER_MOVE_BOTTOM = 3;
    public const int OBS_BOUNDS_NONE = 0;

    // --- プロパティ ---
    [DllImport(O)] public static extern void obs_properties_destroy(IntPtr props);
    [DllImport(O)] public static extern IntPtr obs_properties_first(IntPtr props);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_next(ref IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_name(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_description(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_long_description(IntPtr p);
    [DllImport(O)] public static extern int obs_property_get_type(IntPtr p);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_visible(IntPtr p);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_enabled(IntPtr p);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_modified(IntPtr p, IntPtr settings);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_button_clicked(IntPtr p, IntPtr obj);
    [DllImport(O)] public static extern int obs_property_int_min(IntPtr p);
    [DllImport(O)] public static extern int obs_property_int_max(IntPtr p);
    [DllImport(O)] public static extern int obs_property_int_step(IntPtr p);
    [DllImport(O)] public static extern int obs_property_int_type(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_int_suffix(IntPtr p);
    [DllImport(O)] public static extern double obs_property_float_min(IntPtr p);
    [DllImport(O)] public static extern double obs_property_float_max(IntPtr p);
    [DllImport(O)] public static extern double obs_property_float_step(IntPtr p);
    [DllImport(O)] public static extern int obs_property_float_type(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_float_suffix(IntPtr p);
    [DllImport(O)] public static extern int obs_property_text_type(IntPtr p);
    [DllImport(O)] public static extern int obs_property_path_type(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_path_filter(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_path_default_path(IntPtr p);
    [DllImport(O)] public static extern int obs_property_list_type(IntPtr p);
    [DllImport(O)] public static extern int obs_property_list_format(IntPtr p);
    [DllImport(O)] public static extern nuint obs_property_list_item_count(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_list_item_name(IntPtr p, nuint idx);
    [DllImport(O)] public static extern IntPtr obs_property_list_item_string(IntPtr p, nuint idx);
    [DllImport(O)] public static extern long obs_property_list_item_int(IntPtr p, nuint idx);
    [DllImport(O)] public static extern double obs_property_list_item_float(IntPtr p, nuint idx);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_list_item_disabled(IntPtr p, nuint idx);
    [DllImport(O)] public static extern int obs_property_editable_list_type(IntPtr p);
    [DllImport(O)] public static extern IntPtr obs_property_editable_list_filter(IntPtr p);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_property_list_item_bool(IntPtr p, nuint idx);
    [DllImport(O)] public static extern uint obs_properties_get_flags(IntPtr props);
    [DllImport(O)] public static extern IntPtr obs_source_get_signal_handler(IntPtr source);
    [DllImport(O)] public static extern void signal_handler_connect(IntPtr handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> cb, IntPtr data);
    [DllImport(O)] public static extern void signal_handler_disconnect(IntPtr handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> cb, IntPtr data);
    public const uint OBS_PROPERTIES_DEFER_UPDATE = 1;
    public const int OBS_EDITABLE_LIST_TYPE_STRINGS = 0, OBS_EDITABLE_LIST_TYPE_FILES = 1, OBS_EDITABLE_LIST_TYPE_FILES_AND_URLS = 2;
    [DllImport(O)] public static extern IntPtr obs_property_group_content(IntPtr p);
    [DllImport(O)] public static extern int obs_property_group_type(IntPtr p);

    public const int OBS_PROPERTY_BOOL = 1, OBS_PROPERTY_INT = 2, OBS_PROPERTY_FLOAT = 3, OBS_PROPERTY_TEXT = 4,
        OBS_PROPERTY_PATH = 5, OBS_PROPERTY_LIST = 6, OBS_PROPERTY_COLOR = 7, OBS_PROPERTY_BUTTON = 8,
        OBS_PROPERTY_FONT = 9, OBS_PROPERTY_EDITABLE_LIST = 10, OBS_PROPERTY_FRAME_RATE = 11,
        OBS_PROPERTY_GROUP = 12, OBS_PROPERTY_COLOR_ALPHA = 13;
    public const int OBS_NUMBER_SLIDER = 1;
    public const int OBS_TEXT_PASSWORD = 1, OBS_TEXT_MULTILINE = 2, OBS_TEXT_INFO = 3;
    public const int OBS_PATH_FILE = 0, OBS_PATH_FILE_SAVE = 1, OBS_PATH_DIRECTORY = 2;
    public const int OBS_COMBO_TYPE_EDITABLE = 1;
    public const int OBS_COMBO_FORMAT_INT = 1, OBS_COMBO_FORMAT_FLOAT = 2, OBS_COMBO_FORMAT_STRING = 3, OBS_COMBO_FORMAT_BOOL = 4;
    public const int OBS_GROUP_CHECKABLE = 2;

    // --- obs_data 追加 ---
    [DllImport(O)] public static extern IntPtr obs_data_get_string(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern long obs_data_get_int(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern double obs_data_get_double(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_data_get_bool(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void obs_data_set_double(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, double val);
    [DllImport(O)] public static extern IntPtr obs_data_get_obj(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void obs_data_set_obj(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr obj);
    [DllImport(O)] public static extern IntPtr obs_data_get_array(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void obs_data_set_array(IntPtr data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr array);
    [DllImport(O)] public static extern IntPtr obs_data_get_json(IntPtr data);
    [DllImport(O)] public static extern IntPtr obs_data_create_from_json([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport(O)] public static extern IntPtr obs_data_array_create();
    [DllImport(O)] public static extern void obs_data_array_release(IntPtr array);
    [DllImport(O)] public static extern nuint obs_data_array_count(IntPtr array);
    [DllImport(O)] public static extern IntPtr obs_data_array_item(IntPtr array, nuint idx);
    [DllImport(O)] public static extern nuint obs_data_array_push_back(IntPtr array, IntPtr obj);

    // --- オーバーレイ描画（グラフィックススレッド専用） ---
    [DllImport(O)] public static extern IntPtr obs_get_base_effect(int effect);
    [DllImport(O)] public static extern IntPtr gs_effect_get_param_by_name(IntPtr effect, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(O)] public static extern void gs_effect_set_vec4(IntPtr param, ref vec4 val);
    [DllImport(O)][return: MarshalAs(UnmanagedType.U1)] public static extern bool gs_effect_loop(IntPtr effect, [MarshalAs(UnmanagedType.LPUTF8Str)] string technique);
    [DllImport(O)] public static extern void gs_render_start([MarshalAs(UnmanagedType.U1)] bool bNew);
    [DllImport(O)] public static extern void gs_render_stop(int mode);
    [DllImport(O)] public static extern void gs_vertex2f(float x, float y);
    [DllImport(O)] public static extern void gs_matrix_push();
    [DllImport(O)] public static extern void gs_matrix_pop();
    [DllImport(O)] public static extern void gs_matrix_identity();

    [DllImport(O)] public static extern IntPtr obs_get_main_texture();
    [DllImport(O)] public static extern void gs_effect_set_texture(IntPtr param, IntPtr texture);
    [DllImport(O)] public static extern void gs_effect_set_vec2(IntPtr param, ref vec2 val);
    [DllImport(O)] public static extern void gs_effect_set_float(IntPtr param, float val);
    [DllImport(O)] public static extern void gs_draw_sprite(IntPtr texture, uint flip, uint width, uint height);

    // enum obs_base_effect: DEFAULT=0, DEFAULT_RECT, OPAQUE, SOLID=3, BICUBIC, LANCZOS, BILINEAR_LOWRES, PREMULTIPLIED_ALPHA, REPEAT, AREA=9
    public const int OBS_EFFECT_SOLID = 3, OBS_EFFECT_AREA = 9, GS_TRISTRIP = 4, GS_TRIS = 3;

    [StructLayout(LayoutKind.Sequential)] public struct vec2 { public float x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct vec4 { public float x, y, z, w; }
    [StructLayout(LayoutKind.Sequential)]
    public struct matrix4 { public vec4 x, y, z, t; }

    public static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
