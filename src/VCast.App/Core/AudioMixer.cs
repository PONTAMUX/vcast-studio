using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

// ミキサーの1行。メーター値は音声スレッドが書き、UI のタイマーが読む（ロック不要の volatile float）
public sealed unsafe class AudioStrip : INotifyPropertyChanged, IDisposable
{
    public IntPtr Source { get; }
    public bool IsGlobal { get; }
    readonly IntPtr _fader, _meter;
    GCHandle _self;
    bool _disposed;

    // dBFS（無音は -inf）。音声スレッドから書き込み
    volatile float _peakL = float.NegativeInfinity, _peakR = float.NegativeInfinity, _magL = float.NegativeInfinity, _magR = float.NegativeInfinity;
    public int Channels { get; }
    long _levelTick;
    // 0.5 秒以上更新が無ければ無音（デバイス停止時に最後の値が残らないように）
    public (float L, float R, float MagL, float MagR) Levels => Environment.TickCount64 - Interlocked.Read(ref _levelTick) > 500
        ? (float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity)
        : (_peakL, _peakR, _magL, _magR);

    public AudioStrip(IntPtr source, bool isGlobal)
    {
        Source = Native.obs_source_get_ref(source);
        IsGlobal = isGlobal;
        _self = GCHandle.Alloc(this);
        _fader = Native.obs_fader_create(Native.OBS_FADER_CUBIC);
        Native.obs_fader_attach_source(_fader, Source);
        Native.obs_fader_add_callback(_fader, &OnFader, GCHandle.ToIntPtr(_self));
        _meter = Native.obs_volmeter_create(Native.OBS_FADER_LOG);
        Native.obs_volmeter_set_peak_meter_type(_meter, Native.SAMPLE_PEAK_METER);
        Native.obs_volmeter_attach_source(_meter, Source);
        Native.obs_volmeter_add_callback(_meter, &OnLevels, GCHandle.ToIntPtr(_self));
        Channels = Math.Max(1, Native.obs_volmeter_get_nr_channels(_meter));
    }

    public string Name => Native.Str(Native.obs_source_get_name(Source)) ?? "";

    // フェーダー位置 0〜1（見た目のスライダー位置。dB への変換は libobs の CUBIC カーブ）
    public double Deflection
    {
        get => Native.obs_fader_get_deflection(_fader);
        set { Native.obs_fader_set_deflection(_fader, (float)Math.Clamp(value, 0, 1)); Changed(); Changed(nameof(DbText)); }
    }

    public string DbText
    {
        get
        {
            var db = Native.obs_fader_get_db(_fader);
            return float.IsNegativeInfinity(db) || db < -96 ? "-∞ dB" : $"{db:0.0} dB";
        }
    }

    public bool Muted
    {
        get => Native.obs_source_muted(Source);
        set { Native.obs_source_set_muted(Source, value); Changed(); }
    }

    // 0=聞かない 1=自分だけ聞く（配信には出さない） 2=聞く＋配信にも出す
    public int Monitoring
    {
        get => Native.obs_source_get_monitoring_type(Source);
        set { Native.obs_source_set_monitoring_type(Source, value); Changed(); Changed(nameof(MonitoringText)); }
    }

    public string MonitoringText => Monitoring switch
    {
        Native.OBS_MONITORING_TYPE_MONITOR_ONLY => "🎧 自分だけ",
        Native.OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT => "🎧 聞く",
        _ => "🎧 オフ",
    };

    public void Refresh() { Changed(nameof(Name)); Changed(nameof(Deflection)); Changed(nameof(DbText)); Changed(nameof(Muted)); Changed(nameof(Monitoring)); Changed(nameof(MonitoringText)); }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnLevels(IntPtr param, float* magnitude, float* peak, float* inputPeak)
    {
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not AudioStrip s || s._disposed) return;
            s._peakL = peak[0];
            s._peakR = s.Channels > 1 ? peak[1] : peak[0];
            s._magL = magnitude[0];
            s._magR = s.Channels > 1 ? magnitude[1] : magnitude[0];
            Interlocked.Exchange(ref s._levelTick, Environment.TickCount64);
        }
        catch { }
    }

    // 別の場所（ホットキー等）で音量が変わったときも UI を追従させる
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnFader(IntPtr param, float db)
    {
        try
        {
            if (GCHandle.FromIntPtr(param).Target is not AudioStrip s || s._disposed) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (!s._disposed) { s.Changed(nameof(Deflection)); s.Changed(nameof(DbText)); } });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.obs_volmeter_remove_callback(_meter, &OnLevels, GCHandle.ToIntPtr(_self));
        Native.obs_volmeter_detach_source(_meter);
        Native.obs_volmeter_destroy(_meter);
        Native.obs_fader_remove_callback(_fader, &OnFader, GCHandle.ToIntPtr(_self));
        Native.obs_fader_detach_source(_fader);
        Native.obs_fader_destroy(_fader);
        _self.Free();
        Native.obs_source_release(Source);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

// 常設の音声（デスクトップ音声・マイク）と、ミキサーに並べる行の管理
public sealed class AudioMixer
{
    // OBS と同じ出力チャンネル: 1=デスクトップ音声 3=マイク
    public const uint ChDesktop = 1, ChMic = 3;
    readonly SceneManager _scenes;
    readonly List<IntPtr> _globals = new();
    public ObservableCollection<AudioStrip> Strips { get; } = new();
    volatile AudioStrip? _micStrip;
    // マイクの現在の音量（dBFS）。アバターの口パク用にタイマースレッドから読む
    public float MicDb => _micStrip?.Levels.MagL ?? -100f;

    public AudioMixer(SceneManager scenes) => _scenes = scenes;

    // 保存済みの設定（音量・フィルタ・デバイス）があればそれで作る。無ければ既定のデバイスで作る
    public void CreateGlobalSources(IntPtr desktopData = default, IntPtr micData = default)
    {
        Native.obs_set_audio_monitoring_device("既定", "default");
        AddGlobal(ChDesktop, "wasapi_output_capture", "デスクトップ音声", desktopData);
        AddGlobal(ChMic, "wasapi_input_capture", "マイク", micData);
    }

    void AddGlobal(uint ch, string id, string name, IntPtr saved)
    {
        if (saved != IntPtr.Zero)
        {
            var loaded = Native.obs_load_source(saved);
            if (loaded != IntPtr.Zero) { Native.obs_set_output_source(ch, loaded); _globals.Add(loaded); return; }
        }
        var settings = Native.obs_data_create();
        Native.obs_data_set_string(settings, "device_id", "default");
        var src = Native.obs_source_create(id, name, settings, IntPtr.Zero);
        Native.obs_data_release(settings);
        if (src == IntPtr.Zero) { ObsEngine.Log($"global audio create failed: {id}"); return; }
        Native.obs_set_output_source(ch, src);
        _globals.Add(src);
    }

    // プロファイル切り替え: 常設音声（デバイス・音量・ミュート・フィルタ）を新しいものに作り直す。IntPtr.Zero なら既定の状態
    public void ReplaceGlobals(IntPtr desktopData, IntPtr micData)
    {
        _micStrip = null;
        foreach (var s in Strips) s.Dispose();
        Strips.Clear();
        Native.obs_set_output_source(ChDesktop, IntPtr.Zero);
        Native.obs_set_output_source(ChMic, IntPtr.Zero);
        foreach (var g in _globals) { Native.obs_source_remove(g); Native.obs_source_release(g); }
        _globals.Clear();
        CreateGlobalSources(desktopData, micData);
    }

    public IntPtr Mic => _globals.Count > 1 ? _globals[1] : IntPtr.Zero;
    public IntPtr Desktop => _globals.Count > 0 ? _globals[0] : IntPtr.Zero;

    // 常設音声 + いまのシーンにある「音の出るソース」を並べ直す
    public void Rebuild()
    {
        var wanted = new List<(IntPtr src, bool global)>();
        foreach (var g in _globals) wanted.Add((g, true));
        foreach (var item in _scenes.Items.Reverse()) // 下にあるものから（OBS のミキサーと同じ並び）
        {
            var src = item.Source;
            if ((Native.obs_source_get_output_flags(src) & Native.OBS_SOURCE_AUDIO) != 0 && wanted.All(w => w.src != src))
                wanted.Add((src, false));
        }
        // 同じ並びなら作り直さない（メーターの途切れ防止）
        if (wanted.Count == Strips.Count && wanted.Select(w => w.src).SequenceEqual(Strips.Select(s => s.Source))) return;
        foreach (var s in Strips) s.Dispose();
        Strips.Clear();
        foreach (var (src, global) in wanted) Strips.Add(new AudioStrip(src, global));
        _micStrip = Strips.FirstOrDefault(s => s.Source == Mic);
    }

    public void Shutdown()
    {
        _micStrip = null;
        foreach (var s in Strips) s.Dispose();
        Strips.Clear();
        Native.obs_set_output_source(ChDesktop, IntPtr.Zero);
        Native.obs_set_output_source(ChMic, IntPtr.Zero);
        foreach (var g in _globals) { Native.obs_source_remove(g); Native.obs_source_release(g); }
        _globals.Clear();
    }
}
