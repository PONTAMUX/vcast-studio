using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

public sealed class SceneVm : INotifyPropertyChanged
{
    public IntPtr Scene { get; }
    public IntPtr Source => Native.obs_scene_get_source(Scene);
    public string Name
    {
        get => Native.Str(Native.obs_source_get_name(Source)) ?? "";
        set { Native.obs_source_set_name(Source, value); OnChanged(); }
    }
    public SceneVm(IntPtr scene) => Scene = scene;
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

public sealed class SceneItemVm : INotifyPropertyChanged
{
    public IntPtr Item { get; }
    public long Id { get; }
    public IntPtr Source => Native.obs_sceneitem_get_source(Item);
    public string Name => Native.Str(Native.obs_source_get_name(Source)) ?? "";
    public string TypeName => SceneManager.DisplayName(Native.Str(Native.obs_source_get_id(Source)) ?? "");
    public bool Visible
    {
        get => Native.obs_sceneitem_visible(Item);
        set { Native.obs_sceneitem_set_visible(Item, value); OnChanged(); }
    }
    public SceneItemVm(IntPtr item) { Item = item; Id = Native.obs_sceneitem_get_id(item); }
    public void Refresh() { OnChanged(nameof(Name)); OnChanged(nameof(Visible)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

public record SourceType(string Id, string DisplayName);

// シーン一覧と「いま表示中のシーン」を持つ。libobs 呼び出しは UI スレッドから行う。
public sealed unsafe class SceneManager
{
    public ObservableCollection<SceneVm> Scenes { get; } = new();
    public ObservableCollection<SceneItemVm> Items { get; } = new(); // 上にある順（表示用に逆順）
    public SceneVm? Current { get; private set; }
    public event Action? CurrentChanged;
    public event Action<IntPtr>? SceneRemoving; // シーンのソース（本番から外すため）

    // よく使うものを先頭に。非推奨・無効なものは除外
    static readonly string[] Preferred =
    {
        "game_capture", "window_capture", "monitor_capture", "dshow_input", "wasapi_input_capture",
        "wasapi_output_capture", "ffmpeg_source", "image_source", "browser_source", "text_gdiplus_v3", "color_source_v3",
    };

    public static string DisplayName(string id) => id == "browser_source" && !ObsEngine.BrowserLoaded ? "ブラウザ" : DisplayNameCore(id);
    static string DisplayNameCore(string id) => Native.Str(Native.obs_source_get_display_name(id)) ?? id;

    public static List<SourceType> InputTypes()
    {
        var all = ObsEngine.InputTypes()
            .Where(id => (Native.obs_get_source_output_flags(id) & (Native.OBS_SOURCE_DEPRECATED | Native.OBS_SOURCE_CAP_DISABLED)) == 0)
            .Distinct().ToList();
        if (!all.Contains("browser_source") && !ObsEngine.BrowserLoaded) all.Add("browser_source"); // 後読み込みの対象
        return all.OrderBy(id => { var i = Array.IndexOf(Preferred, id); return i < 0 ? 999 : i; })
                  .ThenBy(DisplayName)
                  .Select(id => new SourceType(id, DisplayName(id))).ToList();
    }

    public SceneVm AddScene(string? name = null)
    {
        var scene = Native.obs_scene_create(UniqueName(name ?? "シーン"));
        var vm = new SceneVm(scene);
        Scenes.Add(vm);
        if (Current == null) SetCurrent(vm);
        return vm;
    }

    // 読み込んだシーン（参照を1つ受け取る）を一覧に加える
    public SceneVm Adopt(IntPtr scene)
    {
        var vm = new SceneVm(scene);
        Scenes.Add(vm);
        return vm;
    }

    public void RemoveScene(SceneVm vm)
    {
        if (Scenes.Count <= 1) return; // 最低1つは残す
        var idx = Scenes.IndexOf(vm);
        Scenes.Remove(vm);
        if (Current == vm) SetCurrent(Scenes[Math.Min(idx, Scenes.Count - 1)]);
        SceneRemoving?.Invoke(vm.Source);
        foreach (var item in EnumItems(vm.Scene)) RemoveSourceIfOrphan(item);
        Native.obs_source_remove(vm.Source);
        Native.obs_scene_release(vm.Scene);
    }

    public void SetCurrent(SceneVm vm)
    {
        Current = vm; // 本番への反映は TransitionManager（通常モードは即、スタジオモードはカット/オート/Tバー）
        RefreshItems();
        CurrentChanged?.Invoke();
    }

    public SceneItemVm? AddSource(string typeId, string? name = null, Action<IntPtr>? configure = null)
    {
        if (Current == null) return null;
        if (typeId == "browser_source" && !ObsEngine.EnsureBrowser()) { ObsEngine.Log("[source] browser is unavailable"); return null; }
        var settings = Native.obs_data_create();
        configure?.Invoke(settings); // 作成前に設定を入れる（ファイルパス・ウィンドウ指定など）
        var src = Native.obs_source_create(typeId, UniqueName(name ?? DisplayName(typeId)), settings, IntPtr.Zero);
        Native.obs_data_release(settings);
        if (src == IntPtr.Zero) { ObsEngine.Log($"source create failed: {typeId}"); return null; }
        var item = Native.obs_scene_add(Current.Scene, src);
        Native.obs_source_release(src); // 以降はシーンアイテムが参照を持つ
        RefreshItems();
        return Items.FirstOrDefault(i => i.Item == item);
    }

    // アイテムの中心をキャンバス上の指定位置に合わせる（ドロップ位置への配置）。サイズ未確定なら左上を合わせる
    public static void CenterAt(IntPtr item, double x, double y)
    {
        var src = Native.obs_sceneitem_get_source(item);
        Native.obs_sceneitem_get_scale(item, out var sc);
        double w = Native.obs_source_get_width(src) * sc.x, h = Native.obs_source_get_height(src) * sc.y;
        var p = new Native.vec2 { x = (float)(x - w / 2), y = (float)(y - h / 2) };
        Native.obs_sceneitem_set_pos(item, ref p);
    }

    public void RemoveItem(SceneItemVm vm)
    {
        var src = vm.Source;
        Native.obs_source_get_ref(src);
        Native.obs_sceneitem_remove(vm.Item);
        if (!IsSourceUsed(src)) Native.obs_source_remove(src);
        Native.obs_source_release(src);
        RefreshItems();
    }

    public void Move(SceneItemVm vm, int movement)
    {
        Native.obs_sceneitem_set_order(vm.Item, movement);
        RefreshItems();
    }

    public void RefreshItems()
    {
        Items.Clear();
        if (Current == null) return;
        var list = EnumItems(Current.Scene);
        for (var i = list.Count - 1; i >= 0; i--) Items.Add(new SceneItemVm(list[i]));
    }

    // 終了時: 全ソース・シーンを remove して release（OBS 31+ はキャンバスが参照を持つため）
    public void Clear()
    {
        Items.Clear();
        foreach (var vm in Scenes)
        {
            SceneRemoving?.Invoke(vm.Source);
            foreach (var item in EnumItems(vm.Scene)) Native.obs_source_remove(Native.obs_sceneitem_get_source(item));
            Native.obs_source_remove(vm.Source);
            Native.obs_scene_release(vm.Scene);
        }
        Scenes.Clear();
        Current = null;
    }

    public static string UniqueName(string baseName)
    {
        var name = baseName;
        for (var n = 2; ; n++)
        {
            var existing = Native.obs_get_source_by_name(name);
            if (existing == IntPtr.Zero) return name;
            Native.obs_source_release(existing);
            name = $"{baseName} {n}";
        }
    }

    bool IsSourceUsed(IntPtr src) =>
        Scenes.Any(s => EnumItems(s.Scene).Any(i => Native.obs_sceneitem_get_source(i) == src));

    void RemoveSourceIfOrphan(IntPtr item)
    {
        var src = Native.obs_sceneitem_get_source(item);
        var usedElsewhere = Scenes.Any(s => EnumItems(s.Scene).Any(i => i != item && Native.obs_sceneitem_get_source(i) == src));
        if (!usedElsewhere) Native.obs_source_remove(src);
    }

    // 下から上の順
    public static List<IntPtr> EnumItems(IntPtr scene)
    {
        var list = new List<IntPtr>();
        var h = GCHandle.Alloc(list);
        try { Native.obs_scene_enum_items(scene, &OnEnumItem, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static byte OnEnumItem(IntPtr scene, IntPtr item, IntPtr param)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(param).Target!).Add(item);
        return 1;
    }
}
