using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

// ソースに付けるフィルタ（音声: ノイズ除去など / 映像: クロマキーなど）
public static unsafe class Filters
{
    static readonly Dictionary<string, string> Descriptions = new()
    {
        ["noise_suppress_filter_v2"] = "ファンやキーボードなどの雑音を消す",
        ["noise_gate_filter"] = "喋っていないときの小さな雑音をカットする",
        ["compressor_filter"] = "大きい声と小さい声の差を縮めて聞きやすくする",
        ["limiter_filter"] = "音割れを防ぐ（一定以上の大きさにしない）",
        ["gain_filter"] = "音量をまとめて上げ下げする",
        ["expander_filter"] = "小さい音を自然に下げる（ゲートより穏やか）",
        ["upward_compressor_filter"] = "小さい音を持ち上げて聞こえやすくする",
        ["basic_eq_filter"] = "低音・中音・高音の量を調整する",
        ["invert_polarity_filter"] = "音の位相を反転する（上級者向け）",
        ["chroma_key_filter_v2"] = "グリーンバックなど指定した色を透明にする",
        ["color_key_filter_v2"] = "指定した色を透明にする",
        ["luma_key_filter_v2"] = "明るい部分・暗い部分を透明にする",
        ["color_filter_v2"] = "明るさ・コントラスト・色味を補正する",
        ["crop_filter"] = "上下左右の端を切り取る",
        ["mask_filter_v2"] = "画像を使って形を切り抜く・重ねる",
        ["scroll_filter"] = "映像を流れるように動かす",
        ["sharpness_filter_v2"] = "輪郭をくっきりさせる",
        ["scale_filter"] = "拡大縮小の方法と解像度を変える",
        ["async_delay_filter"] = "映像を遅らせて音とタイミングを合わせる",
        ["gpu_delay"] = "映像を遅らせる（GPU）",
        ["hdr_tonemap_filter"] = "HDR の映像を普通の明るさに変換する",
    };

    public static string Describe(string id) => Descriptions.TryGetValue(id, out var d) ? d : "";

    public static List<IntPtr> List(IntPtr source)
    {
        var list = new List<IntPtr>();
        var h = GCHandle.Alloc(list);
        try { Native.obs_source_enum_filters(source, &OnEnum, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnEnum(IntPtr parent, IntPtr child, IntPtr param) => ((List<IntPtr>)GCHandle.FromIntPtr(param).Target!).Add(child);

    // このソースに付けられるフィルタ（音が出るなら音声フィルタ、映像があるなら映像フィルタ）
    public static List<SourceType> AvailableFor(IntPtr source)
    {
        var flags = Native.obs_source_get_output_flags(source);
        bool hasAudio = (flags & Native.OBS_SOURCE_AUDIO) != 0, hasVideo = (flags & Native.OBS_SOURCE_VIDEO) != 0;
        var result = new List<SourceType>();
        for (nuint i = 0; Native.obs_enum_filter_types(i, out var p); i++)
        {
            var id = Native.Str(p)!;
            var f = Native.obs_get_source_output_flags(id);
            if ((f & (Native.OBS_SOURCE_DEPRECATED | Native.OBS_SOURCE_CAP_DISABLED)) != 0) continue;
            var ok = ((f & Native.OBS_SOURCE_AUDIO) != 0 && hasAudio) || ((f & Native.OBS_SOURCE_VIDEO) != 0 && hasVideo);
            if (ok && result.All(r => r.Id != id)) result.Add(new SourceType(id, SceneManager.DisplayName(id)));
        }
        // 説明のあるもの（よく使うもの）を先に
        return result.OrderBy(t => Describe(t.Id).Length == 0).ThenBy(t => t.DisplayName).ToList();
    }

    public static IntPtr Add(IntPtr source, string id, Action<IntPtr>? configure = null)
    {
        var baseName = SceneManager.DisplayName(id);
        var name = baseName;
        for (var n = 2; ; n++)
        {
            var ex = Native.obs_source_get_filter_by_name(source, name);
            if (ex == IntPtr.Zero) break;
            Native.obs_source_release(ex);
            name = $"{baseName} {n}";
        }
        var settings = Native.obs_data_create();
        configure?.Invoke(settings);
        var filter = Native.obs_source_create_private(id, name, settings);
        Native.obs_data_release(settings);
        if (filter == IntPtr.Zero) { ObsEngine.Log($"filter create failed: {id}"); return IntPtr.Zero; }
        Native.obs_source_filter_add(source, filter);
        Native.obs_source_release(filter); // 以降は親ソースが参照を持つ
        return filter;
    }

    public static void Remove(IntPtr source, IntPtr filter) => Native.obs_source_filter_remove(source, filter);

    // 自動ダッキング: マイクの声をきっかけに、このソースの音量を下げるコンプレッサー（OBS のサイドチェイン）
    public const string DuckingName = "自動ダッキング（喋ると下がる）";

    public static bool HasDucking(IntPtr source)
    {
        var f = Native.obs_source_get_filter_by_name(source, DuckingName);
        if (f == IntPtr.Zero) return false;
        Native.obs_source_release(f);
        return true;
    }

    public static void SetDucking(IntPtr source, string micName, bool on)
    {
        var f = Native.obs_source_get_filter_by_name(source, DuckingName);
        if (f != IntPtr.Zero)
        {
            if (!on) Native.obs_source_filter_remove(source, f);
            Native.obs_source_release(f);
            if (!on) return;
        }
        if (!on || f != IntPtr.Zero) return;
        var s = Native.obs_data_create();
        Native.obs_data_set_double(s, "ratio", 8);
        Native.obs_data_set_double(s, "threshold", -30);
        Native.obs_data_set_int(s, "attack_time", 20);
        Native.obs_data_set_int(s, "release_time", 400);
        Native.obs_data_set_double(s, "output_gain", 0);
        Native.obs_data_set_string(s, "sidechain_source", micName);
        var filter = Native.obs_source_create_private("compressor_filter", DuckingName, s);
        Native.obs_data_release(s);
        if (filter == IntPtr.Zero) return;
        Native.obs_source_filter_add(source, filter);
        Native.obs_source_release(filter);
    }

    // 「マイクをきれいにする」: ノイズ除去 → ゲート → コンプレッサー → リミッター（最後に音割れ防止）
    public static int ApplyCleanMicPreset(IntPtr mic)
    {
        var added = 0;
        var existing = List(mic).Select(f => Native.Str(Native.obs_source_get_id(f))).ToHashSet();
        void AddIf(string id, Action<IntPtr>? cfg = null)
        {
            if (existing.Contains(id)) return;
            if (Add(mic, id, cfg) != IntPtr.Zero) added++;
        }
        AddIf("noise_suppress_filter_v2", s => Native.obs_data_set_string(s, "method", "rnnoise"));
        AddIf("noise_gate_filter");
        AddIf("compressor_filter");
        AddIf("limiter_filter");
        return added;
    }
}
