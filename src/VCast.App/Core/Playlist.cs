using System.Runtime.InteropServices;
using System.Windows;
using VCast.Obs;

namespace VCast.Core;

// 動画・音声のプレイリスト。実体は ffmpeg_source で、リストはソースの設定（vcast_playlist 等）に入れる
// → シーンの保存・読み込みにそのまま乗る。1 本終わる（media_ended）ごとに次のファイルへ差し替える
public sealed unsafe class PlaylistManager : IDisposable
{
    public const string ListKey = "vcast_playlist", LoopKey = "vcast_loop", ShuffleKey = "vcast_shuffle", IndexKey = "vcast_index";
    readonly Dictionary<IntPtr, int> _attached = new(); // source(参照保持) → 未使用
    readonly Random _rng = new();
    GCHandle _self;
    public event Action<IntPtr>? Changed;

    public PlaylistManager() => _self = GCHandle.Alloc(this);

    public static bool IsPlaylist(IntPtr source)
    {
        if (Native.Str(Native.obs_source_get_id(source)) != "ffmpeg_source") return false;
        var s = Native.obs_source_get_settings(source);
        var arr = Native.obs_data_get_array(s, ListKey);
        Native.obs_data_release(s);
        if (arr == IntPtr.Zero) return false;
        Native.obs_data_array_release(arr);
        return true;
    }

    public static List<string> Files(IntPtr source)
    {
        var list = new List<string>();
        var s = Native.obs_source_get_settings(source);
        var arr = Native.obs_data_get_array(s, ListKey);
        if (arr != IntPtr.Zero)
        {
            for (nuint i = 0; i < Native.obs_data_array_count(arr); i++)
            {
                var o = Native.obs_data_array_item(arr, i);
                list.Add(Native.Str(Native.obs_data_get_string(o, "value")) ?? "");
                Native.obs_data_release(o);
            }
            Native.obs_data_array_release(arr);
        }
        Native.obs_data_release(s);
        return list;
    }

    public static (bool loop, bool shuffle, int index) Options(IntPtr source)
    {
        var s = Native.obs_source_get_settings(source);
        var r = (Native.obs_data_get_bool(s, LoopKey), Native.obs_data_get_bool(s, ShuffleKey), (int)Native.obs_data_get_int(s, IndexKey));
        Native.obs_data_release(s);
        return r;
    }

    // リストと再生位置を書き込み、必要ならそのファイルに差し替える
    public void Write(IntPtr source, List<string> files, bool loop, bool shuffle, int index, bool loadFile)
    {
        var s = Native.obs_data_create();
        var arr = Native.obs_data_array_create();
        foreach (var f in files)
        {
            var o = Native.obs_data_create();
            Native.obs_data_set_string(o, "value", f);
            Native.obs_data_array_push_back(arr, o);
            Native.obs_data_release(o);
        }
        Native.obs_data_set_array(s, ListKey, arr);
        Native.obs_data_array_release(arr);
        Native.obs_data_set_bool(s, LoopKey, loop);
        Native.obs_data_set_bool(s, ShuffleKey, shuffle);
        index = files.Count == 0 ? 0 : Math.Clamp(index, 0, files.Count - 1);
        Native.obs_data_set_int(s, IndexKey, index);
        Native.obs_data_set_bool(s, "is_local_file", true);
        Native.obs_data_set_bool(s, "looping", false); // 1 本ずつ終わらせて次へ進むため
        if (loadFile && files.Count > 0) Native.obs_data_set_string(s, "local_file", files[index]);
        Native.obs_source_update(source, s);
        Native.obs_data_release(s);
        Changed?.Invoke(source);
    }

    public IntPtr Create(SceneManager scenes)
    {
        var vm = scenes.AddSource("ffmpeg_source", SceneManager.UniqueName("プレイリスト"), s =>
        {
            var empty = Native.obs_data_array_create();
            Native.obs_data_set_array(s, ListKey, empty);
            Native.obs_data_array_release(empty);
            Native.obs_data_set_bool(s, "is_local_file", true);
            Native.obs_data_set_bool(s, "looping", false);
            Native.obs_data_set_bool(s, LoopKey, true);
        });
        if (vm == null) return IntPtr.Zero;
        Attach(vm.Source);
        return vm.Source;
    }

    // シーンにあるプレイリストをすべて見張る（読み込み直後・アイテムの増減時に呼ぶ）
    public void Sync(IEnumerable<IntPtr> sources)
    {
        var current = sources.Where(IsPlaylist).ToHashSet();
        foreach (var s in current) Attach(s);
        foreach (var s in _attached.Keys.Where(k => !current.Contains(k) && Native.obs_source_removed(k)).ToList()) Detach(s);
    }

    void Attach(IntPtr source)
    {
        if (_attached.ContainsKey(source)) return;
        Native.obs_source_get_ref(source);
        _attached[source] = 0;
        Native.signal_handler_connect(Native.obs_source_get_signal_handler(source), "media_ended", &OnEnded, GCHandle.ToIntPtr(_self));
    }

    void Detach(IntPtr source)
    {
        if (!_attached.Remove(source)) return;
        Native.signal_handler_disconnect(Native.obs_source_get_signal_handler(source), "media_ended", &OnEnded, GCHandle.ToIntPtr(_self));
        Native.obs_source_release(source);
    }

    public void Next(IntPtr source, int step = 1)
    {
        var files = Files(source);
        if (files.Count == 0) return;
        var (loop, shuffle, index) = Options(source);
        int next;
        if (shuffle && files.Count > 1) { do next = _rng.Next(files.Count); while (next == index); }
        else next = index + step;
        if (next >= files.Count) { if (!loop) { Native.obs_source_media_stop(source); return; } next = 0; }
        if (next < 0) next = loop ? files.Count - 1 : 0;
        Write(source, files, loop, shuffle, next, loadFile: true);
        Native.obs_source_media_restart(source);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnEnded(IntPtr data, IntPtr cd)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not PlaylistManager m) return;
            IntPtr src = IntPtr.Zero;
            Native.calldata_get_data(cd, "source", &src, (nuint)IntPtr.Size);
            var ended = src; // アドレスを取った変数はラムダで使えないので写す
            // 手動で曲を送った直後など、既に次が再生中なら古い通知なので進めない
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (m._attached.ContainsKey(ended) && Native.obs_source_media_get_state(ended) == Native.OBS_MEDIA_STATE_ENDED) m.Next(ended);
            });
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var s in _attached.Keys.ToList()) Detach(s);
        if (_self.IsAllocated) _self.Free();
    }
}
