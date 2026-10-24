using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using VCast.Obs;

namespace VCast.Core;

public sealed class SoundPad
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public double Volume { get; set; } = 0.8;      // 0〜1（音量の倍率）
    public string Expression { get; set; } = "";   // 鳴らしたときのアバターの表情（空なら変えない）
    public string Color { get; set; } = "#26262E";
}

// サウンドボード: パッドごとに非表示のメディアソースを持ち、出力チャンネルに挿して配信へ流す（自分にも聞こえる）
// ホットキー Ctrl+Shift+1〜0（どのアプリを使っていても鳴る）
public sealed unsafe class Soundboard : IDisposable
{
    public const int PadCount = 10;
    const uint FirstChannel = 10; // 出力チャンネル 10〜19 をサウンドボード用に使う（1〜6 は OBS 互換の常設音声）
    readonly string _path = Path.Combine(ObsEngine.ConfigDir, "soundboard.json");
    readonly IntPtr[] _sources = new IntPtr[PadCount];
    readonly Action<string>? _setExpression;
    GCHandle _self;
    public SoundPad[] Pads { get; private set; } = Enumerable.Range(0, PadCount).Select(_ => new SoundPad()).ToArray();
    public event Action? Changed;
    public static bool Silent => ObsEngine.TestMode; // 自己テスト・連続運転テストでは、効果音をスピーカーに出さない

    public Soundboard(Action<string>? setExpression)
    {
        _setExpression = setExpression;
        _self = GCHandle.Alloc(this);
        try
        {
            if (System.IO.File.Exists(_path) && JsonSerializer.Deserialize<SoundPad[]>(System.IO.File.ReadAllText(_path)) is { } loaded)
                for (var i = 0; i < Math.Min(PadCount, loaded.Length); i++) Pads[i] = loaded[i];
        }
        catch (Exception ex) { ObsEngine.Log("[soundboard] load: " + ex.Message); }
        for (var i = 0; i < PadCount; i++) Prepare(i);
    }

    public void Save()
    {
        try { System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(Pads, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { ObsEngine.Log("[soundboard] save: " + ex.Message); }
    }

    // パッドのソースを作り直す（ファイル・音量の変更時）
    public void Prepare(int i)
    {
        Release(i);
        var pad = Pads[i];
        if (string.IsNullOrEmpty(pad.File) || !System.IO.File.Exists(pad.File)) return;
        var s = Native.obs_data_create();
        Native.obs_data_set_bool(s, "is_local_file", true);
        Native.obs_data_set_string(s, "local_file", pad.File);
        Native.obs_data_set_bool(s, "looping", false);
        // 非アクティブ（出力に挿していない）間はファイルを閉じてデコードしない。挿すと頭から再生
        Native.obs_data_set_bool(s, "restart_on_activate", true);
        Native.obs_data_set_bool(s, "close_when_inactive", true);
        // 非公開ソース: シーン一覧や保存データに出てこない
        var src = Native.obs_source_create_private("ffmpeg_source", $"サウンドボード {i + 1}", s);
        Native.obs_data_release(s);
        if (src == IntPtr.Zero) return;
        Native.obs_source_set_volume(src, (float)pad.Volume);
        // 配信にも自分の耳にも（自己テスト中は音を出さない）
        Native.obs_source_set_monitoring_type(src, Silent ? Native.OBS_MONITORING_TYPE_NONE : Native.OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT);
        Native.signal_handler_connect(Native.obs_source_get_signal_handler(src), "media_ended", &OnEnded, GCHandle.ToIntPtr(_self));
        _sources[i] = src; // 出力チャンネルには鳴らすときだけ挿す（待機中はデコードしない）
    }

    void Release(int i)
    {
        var src = _sources[i];
        if (src == IntPtr.Zero) return;
        Native.signal_handler_disconnect(Native.obs_source_get_signal_handler(src), "media_ended", &OnEnded, GCHandle.ToIntPtr(_self));
        if (_live[i]) Plug(i, false);
        Native.obs_source_release(src);
        _sources[i] = IntPtr.Zero;
    }

    readonly bool[] _live = new bool[PadCount];
    readonly int[] _playGen = new int[PadCount]; // 再生ごとの世代（遅れて届いた終了通知を見分ける）
    public bool IsPlaying(int i) => _live[i];

    void Plug(int i, bool on)
    {
        _live[i] = on;
        Native.obs_set_output_source(FirstChannel + (uint)i, on ? _sources[i] : IntPtr.Zero);
    }

    // 鳴らす（鳴っている最中にもう一度押すと止める）
    public void Play(int i)
    {
        if (i < 0 || i >= PadCount || _sources[i] == IntPtr.Zero) return;
        if (IsPlaying(i)) { Native.obs_source_media_stop(_sources[i]); Plug(i, false); Changed?.Invoke(); return; }
        Native.obs_source_set_volume(_sources[i], (float)Pads[i].Volume);
        Plug(i, true); // 挿すと（アクティブになると）頭から再生が始まる
        _playGen[i]++;
        if (!string.IsNullOrEmpty(Pads[i].Expression)) _setExpression?.Invoke(Pads[i].Expression);
        Changed?.Invoke();
    }

    public void StopAll()
    {
        for (var i = 0; i < PadCount; i++) if (_live[i]) { Native.obs_source_media_stop(_sources[i]); Plug(i, false); }
        Changed?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnEnded(IntPtr data, IntPtr cd)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not Soundboard sb) return;
            IntPtr src = IntPtr.Zero;
            Native.calldata_get_data(cd, "source", &src, (nuint)IntPtr.Size);
            var ended = src;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var i = Array.IndexOf(sb._sources, ended);
                // 既に止めた・鳴らし直した（再生中に戻っている）なら古い通知なので無視
                if (i < 0 || !sb._live[i] || Native.obs_source_media_get_state(ended) == Native.OBS_MEDIA_STATE_PLAYING) return;
                sb.Plug(i, false);
                // 表情を付けたパッドは、鳴り終わったら元の表情へ（ほかの表情付きパッドが鳴っていなければ）
                var otherExpr = Enumerable.Range(0, PadCount).Any(j => j != i && sb._live[j] && !string.IsNullOrEmpty(sb.Pads[j].Expression));
                if (!string.IsNullOrEmpty(sb.Pads[i].Expression) && !otherExpr) sb._setExpression?.Invoke("neutral");
                sb.Changed?.Invoke();
            });
        }
        catch { }
    }

    // ホットキーの割り当ては HotkeyManager が持つ
    public static string HotkeyLabel(int i) => HotkeyManager.LabelFor("sound." + (i + 1));

    public void Dispose()
    {
        for (var i = 0; i < PadCount; i++) Release(i);
        if (_self.IsAllocated) _self.Free();
    }

}
