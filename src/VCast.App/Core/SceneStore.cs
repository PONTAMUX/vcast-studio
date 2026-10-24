using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

// シーンコレクションの保存・読み込み。形式は OBS Studio のシーンコレクション JSON と同じ
//   sources / scene_order / current_scene に加え、常設音声は OBS と同じ DesktopAudioDevice1 / AuxAudioDevice1 に入れる
// → OBS の %APPDATA%\obs-studio\basic\scenes\*.json もそのまま読み込める
public static unsafe class SceneStore
{
    public static string DefaultPath { get { Profiles.EnsureInit(); return Profiles.ScenesPath; } }
    static readonly HashSet<IntPtr> Excluded = new();

    public static bool Save(string path, SceneManager scenes, AudioMixer mixer, IntPtr programScene, params IntPtr[] alsoExclude)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 常設音声は sources に入れず専用キーへ（読み込み時に二重に作られないように）
        Excluded.Clear();
        if (mixer.Desktop != IntPtr.Zero) Excluded.Add(mixer.Desktop);
        if (mixer.Mic != IntPtr.Zero) Excluded.Add(mixer.Mic);
        foreach (var x in alsoExclude) if (x != IntPtr.Zero) Excluded.Add(x); // 縦型レイアウトのシーンなど、起動ごとに作り直すもの
        var root = Native.obs_data_create();
        try
        {
            var sources = Native.obs_save_sources_filtered(&Filter, IntPtr.Zero);
            Native.obs_data_set_array(root, "sources", sources);
            Native.obs_data_array_release(sources);

            var order = Native.obs_data_array_create();
            foreach (var s in scenes.Scenes)
            {
                var o = Native.obs_data_create();
                Native.obs_data_set_string(o, "name", s.Name);
                Native.obs_data_array_push_back(order, o);
                Native.obs_data_release(o);
            }
            Native.obs_data_set_array(root, "scene_order", order);
            Native.obs_data_array_release(order);
            if (scenes.Current != null) Native.obs_data_set_string(root, "current_scene", scenes.Current.Name);
            // 本番とプレビュー（スタジオモードで編集中）は別々に
            var program = scenes.Scenes.FirstOrDefault(s => s.Source == programScene) ?? scenes.Current;
            if (program != null) Native.obs_data_set_string(root, "current_program_scene", program.Name);
            Native.obs_data_set_string(root, "name", "VCast");

            void SaveGlobal(string key, IntPtr src)
            {
                if (src == IntPtr.Zero) return;
                var d = Native.obs_save_source(src);
                Native.obs_data_set_obj(root, key, d);
                Native.obs_data_release(d);
            }
            SaveGlobal("DesktopAudioDevice1", mixer.Desktop);
            SaveGlobal("AuxAudioDevice1", mixer.Mic);

            var ok = Native.obs_data_save_json_safe(root, path, "tmp", "bak");
            ObsEngine.Log($"[store] save {(ok ? "ok" : "FAILED")} {path} ({scenes.Scenes.Count} scenes)");
            return ok;
        }
        finally { Native.obs_data_release(root); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static byte Filter(IntPtr param, IntPtr source) => (byte)(Excluded.Contains(source) ? 0 : 1);

    public sealed record Loaded(IntPtr DesktopData, IntPtr MicData, string? ProgramScene);

    // 読み込む前の確認: JSON として読めて、sources が配列であること（壊れたファイルで今のシーンを消さないため）
    public static bool LooksValid(string path)
    {
        var root = Native.obs_data_create_from_json_file_safe(path, "bak");
        if (root == IntPtr.Zero) return false;
        var arr = Native.obs_data_get_array(root, "sources");
        Native.obs_data_release(root);
        if (arr == IntPtr.Zero) return false;
        Native.obs_data_array_release(arr);
        return true;
    }

    static readonly List<IntPtr> LoadedScenes = new();

    // 読み込み: シーンとソースを作り、SceneManager に登録する。常設音声の設定は返すので AudioMixer に渡す（呼び出し側で release）
    // beforeScenes: シーンより先に作るべきもの（常設音声。シーン内から参照されることがある）を作る場所
    public static Loaded? Load(string path, SceneManager scenes, Func<string, string?> fixBrowserUrl, Action<IntPtr, IntPtr>? beforeScenes = null)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
        var root = Native.obs_data_create_from_json_file_safe(path, "bak");
        if (root == IntPtr.Zero) { ObsEngine.Log("[store] load failed: " + path); return null; }
        try
        {
            IntPtr Global(string key) => Native.obs_data_has_user_value(root, key) ? Native.obs_data_get_obj(root, key) : IntPtr.Zero;
            var desktop = Global("DesktopAudioDevice1"); var mic = Global("AuxAudioDevice1");
            beforeScenes?.Invoke(desktop, mic);

            // OBS はグループを別の配列（groups）に保存するので、sources と合わせて一度に読み込む
            var sources = Native.obs_data_array_create();
            foreach (var key in new[] { "sources", "groups" })
            {
                var part = Native.obs_data_get_array(root, key);
                if (part == IntPtr.Zero) continue;
                for (nuint i = 0; i < Native.obs_data_array_count(part); i++)
                {
                    var item = Native.obs_data_array_item(part, i);
                    Native.obs_data_array_push_back(sources, item);
                    Native.obs_data_release(item);
                }
                Native.obs_data_array_release(part);
            }
            LoadedScenes.Clear();
            {
                // アバター描画ページの URL は起動ごとにトークンが変わるので、読み込む前に差し替える
                for (nuint i = 0; i < Native.obs_data_array_count(sources); i++)
                {
                    var src = Native.obs_data_array_item(sources, i);
                    if (Native.Str(Native.obs_data_get_string(src, "id")) == "browser_source")
                    {
                        var settings = Native.obs_data_get_obj(src, "settings");
                        if (settings != IntPtr.Zero)
                        {
                            var url = Native.Str(Native.obs_data_get_string(settings, "url")) ?? "";
                            if (fixBrowserUrl(url) is { } fixedUrl) Native.obs_data_set_string(settings, "url", fixedUrl);
                            Native.obs_data_release(settings);
                        }
                    }
                    Native.obs_data_release(src);
                }
                Native.obs_load_sources(sources, &OnLoaded, IntPtr.Zero);
                Native.obs_data_array_release(sources);
            }

            // シーンの並び順（無ければ読み込んだシーンを名前順）
            var names = new List<string>();
            var order = Native.obs_data_get_array(root, "scene_order");
            if (order != IntPtr.Zero)
            {
                for (nuint i = 0; i < Native.obs_data_array_count(order); i++)
                {
                    var o = Native.obs_data_array_item(order, i);
                    if (Native.Str(Native.obs_data_get_string(o, "name")) is { Length: > 0 } n) names.Add(n);
                    Native.obs_data_release(o);
                }
                Native.obs_data_array_release(order);
            }
            var adopted = new HashSet<string>();
            foreach (var n in names)
            {
                var src = Native.obs_get_source_by_name(n);
                if (src == IntPtr.Zero) continue;
                if (Native.obs_source_is_scene(src) && adopted.Add(n)) scenes.Adopt(Native.obs_scene_get_ref(Native.obs_scene_from_source(src)));
                Native.obs_source_release(src);
            }
            // scene_order に無い（または scene_order 自体が無い）シーンも拾う。グループは除く
            foreach (var src in LoadedScenes)
            {
                var n = Native.Str(Native.obs_source_get_name(src)) ?? "";
                if (Native.Str(Native.obs_source_get_id(src)) == "scene" && adopted.Add(n)) scenes.Adopt(Native.obs_scene_get_ref(Native.obs_scene_from_source(src)));
                Native.obs_source_release(src);
            }
            LoadedScenes.Clear();
            var current = Native.Str(Native.obs_data_get_string(root, "current_scene"));
            var cur = scenes.Scenes.FirstOrDefault(s => s.Name == current) ?? scenes.Scenes.FirstOrDefault();
            if (cur != null) scenes.SetCurrent(cur);

            ObsEngine.Log($"[store] loaded {path}: {scenes.Scenes.Count} scenes");
            return new Loaded(desktop, mic, Native.Str(Native.obs_data_get_string(root, "current_program_scene")));
        }
        finally { Native.obs_data_release(root); }
    }

    // 読み込まれたシーンを控えておく（scene_order に無いものを拾うため。参照を 1 つ持つ）
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnLoaded(IntPtr param, IntPtr source)
    {
        if (Native.obs_source_is_scene(source)) LoadedScenes.Add(Native.obs_source_get_ref(source));
    }
}
