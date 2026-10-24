using System.Runtime.InteropServices;
using System.Windows;
using VCast.Obs;

namespace VCast.Core;

public enum OutputKind { Stream, Record, Replay, VirtualCam }

public sealed record OutputEvent(OutputKind Kind, string Name, string Message, bool IsError);

// 配信（複数同時）・録画・リプレイバッファ・仮想カメラ。エンコーダは配信と録画で共有（負荷は1回分）
public sealed unsafe class OutputManager
{
    readonly AppSettings _settings;
    IntPtr _venc, _aenc, _vencV; // _vencV: 縦型キャンバス用の映像エンコーダ
    IntPtr _vencR; // 録画専用（高画質）の映像エンコーダ
    IntPtr _recordV;
    public VerticalCanvas? Vertical { get; set; }
    public string? LastVerticalRecordingPath { get; private set; }
    string _vencId = "";
    readonly List<(IntPtr output, IntPtr service, string name)> _streams = new();
    IntPtr _record, _replay, _vcam;
    GCHandle _self;
    readonly Dictionary<IntPtr, (OutputKind kind, string name)> _known = new();

    public event Action<OutputEvent>? Event;       // UI スレッドで通知
    public event Action? StateChanged;
    public DateTime? StreamStartedAt { get; private set; }
    public DateTime? RecordStartedAt { get; private set; }
    public string? LastRecordingPath { get; private set; }
    public string? LastReplayPath { get; private set; }

    // RTMP は接続が終わるまで active=false なので、開始要求〜stop 通知までを「配信中」とみなす
    bool _streamRequested, _recordRequested;
    public bool Streaming => _streamRequested || _streams.Any(s => Native.obs_output_active(s.output));
    bool _recordStopRequested;
    public bool RecordStopping => _recordStopRequested && Recording;
    public bool Recording => _recordRequested || (_record != IntPtr.Zero && Native.obs_output_active(_record)) || (_recordV != IntPtr.Zero && Native.obs_output_active(_recordV));
    bool AnyEncoderUser => Streaming || Recording || ReplayActive;
    string _encSignature = "";
    public bool ReplayActive => _replay != IntPtr.Zero && Native.obs_output_active(_replay);
    public bool VirtualCamActive => _vcam != IntPtr.Zero && Native.obs_output_active(_vcam);
    public string VideoEncoderId => _vencId;

    public OutputManager(AppSettings settings)
    {
        _settings = settings;
        _self = GCHandle.Alloc(this);
    }

    // --- 利用できるもの ---
    public static List<string> EncoderTypes()
    {
        var list = new List<string>();
        for (nuint i = 0; Native.obs_enum_encoder_types(i, out var p); i++) list.Add(Native.Str(p)!);
        return list;
    }

    public static bool HasOutput(string id)
    {
        for (nuint i = 0; Native.obs_enum_output_types(i, out var p); i++) if (Native.Str(p) == id) return true;
        return false;
    }

    // NVENC（GPU）→ QSV → AMF → x264（CPU）の順に選ぶ
    public static string PickVideoEncoder(string preferred, string codec = "h264")
    {
        var types = EncoderTypes();
        // 明示指定は、選んだ圧縮方式（H.264/HEVC）と合っているときだけ使う（合わなければ自動選択）
        if (preferred != "auto" && types.Contains(preferred) && IsHevc(preferred) == (codec == "hevc")) return preferred;
        // HEVC は GPU が対応しているときだけ（無ければ H.264 へ）
        if (codec == "hevc")
            foreach (var id in new[] { "obs_nvenc_hevc_tex", "h265_texture_amf", "obs_qsv11_hevc" })
                if (types.Contains(id)) return id;
        foreach (var id in new[] { "obs_nvenc_h264_tex", "jim_nvenc", "obs_qsv11_v2", "obs_qsv11", "h264_texture_amf", "amd_amf_h264", "obs_x264" })
            if (types.Contains(id)) return id;
        return "obs_x264";
    }

    public static bool IsHardware(string id) => !id.Contains("x264");
    public static bool IsHevc(string id) => id.Contains("hevc") || id.Contains("h265");

    // --- エンコーダ（出力が止まっているときだけ作り直す） ---
    string _vencRId = "";

    // 録画に使うエンコーダ。設定が「配信と同じ」か、HEVC を選んでも GPU が未対応なら、配信のものをそのまま使う（別のエンコーダは作らない）
    string RecordEncoderId(string streamId, out bool separate)
    {
        var want = _settings.RecordCodec switch { "h264" => "h264", "same" => "", _ => "hevc" };
        if (want.Length == 0) { separate = _settings.RecordSeparate; return _settings.RecordSeparate ? PickVideoEncoder(_settings.VideoEncoder, IsHevc(streamId) ? "hevc" : "h264") : streamId; }
        var id = PickVideoEncoder(_settings.VideoEncoder, want);
        if (want == "hevc" && !IsHevc(id)) id = PickVideoEncoder(_settings.VideoEncoder, "h264"); // 未対応 → H.264
        separate = _settings.RecordSeparate || IsHevc(id) != IsHevc(streamId);
        return separate ? id : streamId;
    }

    void EnsureEncoders()
    {
        if (_venc != IntPtr.Zero && Native.obs_encoder_active(_venc)) return;
        var id = PickVideoEncoder(_settings.VideoEncoder, _settings.Codec);
        var recId = RecordEncoderId(id, out var recSeparate);
        var sig = $"{id}|{_settings.RateControl}|{_settings.EffectiveVideoBitrate}|{_settings.KeyintSec}|{_settings.AudioBitrate}|{recSeparate}|{recId}|{_settings.RecordQuality}";
        if (_venc != IntPtr.Zero && sig != _encSignature && !AnyEncoderUser)
        {
            Native.obs_encoder_release(_venc); _venc = IntPtr.Zero;
            if (_aenc != IntPtr.Zero) { Native.obs_encoder_release(_aenc); _aenc = IntPtr.Zero; }
        }
        _encSignature = sig;
        var vs = Native.obs_data_create();
        Native.obs_data_set_string(vs, "rate_control", _settings.RateControl);
        Native.obs_data_set_int(vs, "bitrate", _settings.EffectiveVideoBitrate);
        Native.obs_data_set_int(vs, "keyint_sec", _settings.KeyintSec);
        Native.obs_data_set_string(vs, "profile", IsHevc(id) ? "main" : "high");
        if (id.Contains("nvenc")) { Native.obs_data_set_string(vs, "preset2", "p5"); Native.obs_data_set_string(vs, "tune", "hq"); Native.obs_data_set_string(vs, "multipass", "qres"); Native.obs_data_set_int(vs, "bf", 2); }
        if (id == "obs_x264") Native.obs_data_set_string(vs, "preset", "veryfast");
        if (_venc != IntPtr.Zero && id == _vencId) Native.obs_encoder_update(_venc, vs);
        else
        {
            if (_venc != IntPtr.Zero) Native.obs_encoder_release(_venc);
            _venc = Native.obs_video_encoder_create(id, "vcast_video", vs, IntPtr.Zero);
            _vencId = id;
            if (_venc != IntPtr.Zero) Native.obs_encoder_set_video(_venc, Native.obs_get_video());
        }
        Native.obs_data_release(vs);

        if (_aenc == IntPtr.Zero || !Native.obs_encoder_active(_aenc))
        {
            var a = Native.obs_data_create();
            Native.obs_data_set_int(a, "bitrate", _settings.AudioBitrate);
            if (_aenc == IntPtr.Zero)
            {
                _aenc = Native.obs_audio_encoder_create("ffmpeg_aac", "vcast_audio", a, 0, IntPtr.Zero);
                if (_aenc != IntPtr.Zero) Native.obs_encoder_set_audio(_aenc, Native.obs_get_audio());
            }
            else Native.obs_encoder_update(_aenc, a);
            Native.obs_data_release(a);
        }
        if (_venc == IntPtr.Zero || _aenc == IntPtr.Zero) throw new InvalidOperationException($"エンコーダを作れませんでした（{id}）");

        // 録画だけ別の高画質設定（画質一定）。配信と同じ映像を、より高いビットレートで保存する
        if (recSeparate)
        {
            if (_vencR != IntPtr.Zero && (!Native.obs_encoder_active(_vencR)) && _vencRId != recId) { Native.obs_encoder_release(_vencR); _vencR = IntPtr.Zero; }
            if (_vencR == IntPtr.Zero)
            {
                _vencRId = recId;
                var rs = Native.obs_data_create();
                if (recId == "obs_x264") { Native.obs_data_set_string(rs, "rate_control", "CRF"); Native.obs_data_set_int(rs, "crf", _settings.RecordQuality); Native.obs_data_set_string(rs, "preset", "veryfast"); }
                else { Native.obs_data_set_string(rs, "rate_control", "CQP"); Native.obs_data_set_int(rs, "cqp", _settings.RecordQuality); if (recId.Contains("nvenc")) { Native.obs_data_set_string(rs, "preset2", "p5"); Native.obs_data_set_string(rs, "tune", "hq"); } }
                Native.obs_data_set_string(rs, "profile", IsHevc(recId) ? "main" : "high");
                Native.obs_data_set_int(rs, "keyint_sec", _settings.KeyintSec);
                _vencR = Native.obs_video_encoder_create(recId, "vcast_video_record", rs, IntPtr.Zero);
                Native.obs_data_release(rs);
                if (_vencR != IntPtr.Zero) Native.obs_encoder_set_video(_vencR, Native.obs_get_video());
            }
        }
        else if (_vencR != IntPtr.Zero && !Native.obs_encoder_active(_vencR)) { Native.obs_encoder_release(_vencR); _vencR = IntPtr.Zero; }

        // 縦型: 同じ設定で別のエンコーダ（映像の元が縦型キャンバス）
        if (_settings.VerticalEnabled && Vertical is { Active: true } v && (_vencV == IntPtr.Zero || !Native.obs_encoder_active(_vencV)))
        {
            if (_vencV != IntPtr.Zero) Native.obs_encoder_release(_vencV);
            var vsv = vs2();
            _vencV = Native.obs_video_encoder_create(id, "vcast_video_vertical", vsv, IntPtr.Zero);
            Native.obs_data_release(vsv);
            if (_vencV != IntPtr.Zero) Native.obs_encoder_set_video(_vencV, v.Video);
        }

        IntPtr vs2()
        {
            var d = Native.obs_data_create();
            Native.obs_data_set_string(d, "rate_control", _settings.RateControl);
            Native.obs_data_set_int(d, "bitrate", _settings.EffectiveVideoBitrate);
            Native.obs_data_set_int(d, "keyint_sec", _settings.KeyintSec);
            Native.obs_data_set_string(d, "profile", IsHevc(id) ? "main" : "high");
            return d;
        }
    }

    // --- 配信（有効な配信先すべてに同時） ---
    public bool StartStream(out string error)
    {
        error = "";
        if (Streaming) return true;
        var dests = _settings.Destinations.Where(d => d.Enabled).Select(d => (d, vertical: false)).ToList();
        if (_settings.VerticalEnabled && Vertical is { Active: true })
            dests.AddRange(_settings.VerticalDestinations.Where(d => d.Enabled && !string.IsNullOrEmpty(d.KeyProtected)).Select(d => (d, vertical: true)));
        if (dests.Count == 0) { error = "配信先が設定されていません"; return false; }
        try { EnsureEncoders(); } catch (Exception ex) { error = ex.Message; return false; }
        ReleaseStreams();
        var started = 0;
        foreach (var (d, vertical) in dests)
        {
            ObsEngine.AddSecret(d.Key);
            var ss = Native.obs_data_create();
            Native.obs_data_set_string(ss, "server", d.ResolvedServer);
            Native.obs_data_set_string(ss, "key", d.Key);
            var service = Native.obs_service_create("rtmp_custom", "vcast_service_" + d.Name, ss, IntPtr.Zero);
            Native.obs_data_release(ss);
            var output = Native.obs_output_create("rtmp_output", "vcast_stream_" + d.Name, IntPtr.Zero, IntPtr.Zero);
            if (service == IntPtr.Zero || output == IntPtr.Zero) { error += $"{d.Name}: 作成に失敗 "; continue; }
            Native.obs_output_set_service(output, service);
            Native.obs_output_set_video_encoder(output, vertical && _vencV != IntPtr.Zero ? _vencV : _venc);
            Native.obs_output_set_audio_encoder(output, _aenc, 0);
            Native.obs_output_set_reconnect_settings(output, 20, 3); // 切れたら初回 3 秒後から間隔を伸ばしつつ最大 20 回再接続
            if (_settings.StreamDelaySec > 0) Native.obs_output_set_delay(output, (uint)_settings.StreamDelaySec, Native.OBS_OUTPUT_DELAY_PRESERVE);
            var label = vertical ? "縦型:" + d.Name : d.Name;
            Hook(output, OutputKind.Stream, label);
            _streams.Add((output, service, label));
            if (Native.obs_output_start(output)) started++;
            else error += $"{d.Name}: {Native.Str(Native.obs_output_get_last_error(output)) ?? "開始できませんでした"} ";
        }
        if (started > 0) { StreamStartedAt = DateTime.Now; _streamRequested = true; }
        // 配信と同時に録画（配信が落ちたときの控え・あとで切り抜く用）。録画が始められなくても配信は続ける
        if (started > 0 && _settings.RecordWithStream && !Recording)
        {
            if (StartRecord(out var rerr)) _autoRecord = true;
            else error += "録画: " + rerr + " ";
        }
        StateChanged?.Invoke();
        return started > 0;
    }

    bool _autoRecord; // 配信と一緒に始めた録画（配信を終えるときに一緒に止める）

    public void StopStream()
    {
        foreach (var s in _streams) Native.obs_output_stop(s.output);
        if (_autoRecord) { _autoRecord = false; StopRecord(); }
    }

    void ReleaseStreams()
    {
        foreach (var s in _streams)
        {
            Unhook(s.output);
            Native.obs_output_release(s.output);
            Native.obs_service_release(s.service);
        }
        _streams.Clear();
    }

    // --- 録画 ---
    public bool StartRecord(out string error)
    {
        error = "";
        if (Recording) return true;
        try { EnsureEncoders(); } catch (Exception ex) { error = ex.Message; return false; }
        Directory.CreateDirectory(_settings.RecordFolder);
        var mp4 = _settings.RecordFormat == "mp4" && HasOutput("mp4_output");
        var path = Path.Combine(_settings.RecordFolder, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}.{(mp4 ? "mp4" : "mkv")}");
        if (_record != IntPtr.Zero) { Unhook(_record); Native.obs_output_release(_record); }
        var rs = Native.obs_data_create();
        Native.obs_data_set_string(rs, "path", path);
        _record = Native.obs_output_create(mp4 ? "mp4_output" : "ffmpeg_muxer", "vcast_record", rs, IntPtr.Zero);
        Native.obs_data_release(rs);
        if (_record == IntPtr.Zero) { error = "録画を準備できませんでした"; return false; }
        Native.obs_output_set_video_encoder(_record, _vencR != IntPtr.Zero ? _vencR : _venc);
        Native.obs_output_set_audio_encoder(_record, _aenc, 0);
        Hook(_record, OutputKind.Record, "録画");
        LastRecordingPath = path;
        if (!Native.obs_output_start(_record)) { error = Native.Str(Native.obs_output_get_last_error(_record)) ?? "録画を開始できませんでした"; return false; }
        RecordStartedAt = DateTime.Now;
        _recordRequested = true; _recordStopRequested = false;
        if (_settings.VerticalEnabled && _settings.VerticalRecord && _vencV != IntPtr.Zero)
        {
            if (_recordV != IntPtr.Zero) { Unhook(_recordV); Native.obs_output_release(_recordV); }
            var vpath = Path.Combine(_settings.RecordFolder, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} 縦型.{(mp4 ? "mp4" : "mkv")}");
            var vr = Native.obs_data_create();
            Native.obs_data_set_string(vr, "path", vpath);
            _recordV = Native.obs_output_create(mp4 ? "mp4_output" : "ffmpeg_muxer", "vcast_record_vertical", vr, IntPtr.Zero);
            Native.obs_data_release(vr);
            if (_recordV != IntPtr.Zero)
            {
                Native.obs_output_set_video_encoder(_recordV, _vencV);
                Native.obs_output_set_audio_encoder(_recordV, _aenc, 0);
                Hook(_recordV, OutputKind.Record, "縦型録画");
                LastVerticalRecordingPath = vpath;
                if (!Native.obs_output_start(_recordV)) ObsEngine.Log("[vertical] record start failed");
            }
        }
        StateChanged?.Invoke();
        return true;
    }

    public void StopRecord()
    {
        _recordStopRequested = true;
        if (_record != IntPtr.Zero) Native.obs_output_stop(_record);
        if (_recordV != IntPtr.Zero && Native.obs_output_active(_recordV)) Native.obs_output_stop(_recordV);
    }

    // 停止の処理が進まないときの最後の手段（保存は途中までになる可能性がある）
    public void ForceStopRecord()
    {
        if (_record != IntPtr.Zero) Native.obs_output_force_stop(_record);
        if (_recordV != IntPtr.Zero && Native.obs_output_active(_recordV)) Native.obs_output_force_stop(_recordV);
    }

    // --- リプレイバッファ（直前 N 秒を保存） ---
    public bool StartReplay(out string error)
    {
        error = "";
        if (ReplayActive) return true;
        try { EnsureEncoders(); } catch (Exception ex) { error = ex.Message; return false; }
        Directory.CreateDirectory(_settings.RecordFolder);
        if (_replay != IntPtr.Zero) { Unhook(_replay); Native.obs_output_release(_replay); }
        var rs = Native.obs_data_create();
        Native.obs_data_set_string(rs, "directory", _settings.RecordFolder);
        Native.obs_data_set_string(rs, "format", "リプレイ %CCYY-%MM-%DD %hh-%mm-%ss");
        Native.obs_data_set_string(rs, "extension", "mkv");
        Native.obs_data_set_int(rs, "max_time_sec", _settings.ReplaySeconds);
        Native.obs_data_set_int(rs, "max_size_mb", 1024);
        _replay = Native.obs_output_create("replay_buffer", "vcast_replay", rs, IntPtr.Zero);
        Native.obs_data_release(rs);
        if (_replay == IntPtr.Zero) { error = "リプレイバッファを準備できませんでした"; return false; }
        Native.obs_output_set_video_encoder(_replay, _venc);
        Native.obs_output_set_audio_encoder(_replay, _aenc, 0);
        Hook(_replay, OutputKind.Replay, "リプレイ");
        if (!Native.obs_output_start(_replay)) { error = Native.Str(Native.obs_output_get_last_error(_replay)) ?? "開始できませんでした"; return false; }
        StateChanged?.Invoke();
        return true;
    }

    public void StopReplay() { if (_replay != IntPtr.Zero) Native.obs_output_stop(_replay); }

    public bool SaveReplay()
    {
        if (!ReplayActive) return false;
        var ph = Native.obs_output_get_proc_handler(_replay);
        var cd = new Native.calldata();
        var ok = Native.proc_handler_call(ph, "save", &cd);
        if (cd.stack != null) Native.bfree(cd.stack);
        ObsEngine.Log($"[replay] save requested ok={ok}");
        return ok;
    }

    // --- 仮想カメラ（OBS の仮想カメラドライバが登録されている PC で使える） ---
    public static bool VirtualCamInstalled()
    {
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\InprocServer32");
        return key != null && HasOutput("virtualcam_output");
    }

    public bool StartVirtualCam(out string error)
    {
        error = "";
        if (VirtualCamActive) return true;
        if (!VirtualCamInstalled()) { error = "仮想カメラのドライバが見つかりません（OBS Studio をインストールすると使えます）"; return false; }
        if (_vcam == IntPtr.Zero)
        {
            _vcam = Native.obs_output_create("virtualcam_output", "vcast_virtualcam", IntPtr.Zero, IntPtr.Zero);
            if (_vcam == IntPtr.Zero) { error = "仮想カメラを準備できませんでした"; return false; }
            Native.obs_output_set_media(_vcam, Native.obs_get_video(), Native.obs_get_audio());
            Hook(_vcam, OutputKind.VirtualCam, "仮想カメラ");
        }
        if (!Native.obs_output_start(_vcam)) { error = Native.Str(Native.obs_output_get_last_error(_vcam)) ?? "開始できませんでした"; return false; }
        StateChanged?.Invoke();
        return true;
    }

    public void StopVirtualCam() { if (_vcam != IntPtr.Zero) Native.obs_output_stop(_vcam); }

    // --- 統計 ---
    ulong _lastBytes; DateTime _lastBytesAt = DateTime.Now;
    public (double kbps, int dropped, int total, float congestion) StreamStats()
    {
        ulong bytes = 0; int dropped = 0, total = 0; float cong = 0;
        foreach (var s in _streams)
        {
            if (!Native.obs_output_active(s.output)) continue;
            bytes += Native.obs_output_get_total_bytes(s.output);
            dropped += Native.obs_output_get_frames_dropped(s.output);
            total += Native.obs_output_get_total_frames(s.output);
            cong = Math.Max(cong, Native.obs_output_get_congestion(s.output));
        }
        var now = DateTime.Now;
        var secs = (now - _lastBytesAt).TotalSeconds;
        var kbps = secs > 0 && bytes >= _lastBytes ? (bytes - _lastBytes) * 8 / 1000.0 / secs : 0;
        _lastBytes = bytes; _lastBytesAt = now;
        return (kbps, dropped, total, cong);
    }

    // stop 通知は active が false になる前に届くことがあるので、少し後にもう一度ボタン表示を確定する
    void RecheckSoon()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); StateChanged?.Invoke(); };
        t.Start();
    }

    // --- シグナル（別スレッドから届くので UI スレッドへ移す） ---
    void Hook(IntPtr output, OutputKind kind, string name)
    {
        _known[output] = (kind, name);
        if (Environment.GetEnvironmentVariable("VCAST_NOHOOK") == "1") return; // 切り分け用
        var sh = Native.obs_output_get_signal_handler(output);
        foreach (var sig in new[] { "start", "stop", "reconnect", "reconnect_success", "saved" })
            Native.signal_handler_connect(sh, sig, SignalFor(sig), GCHandle.ToIntPtr(_self));
    }

    void Unhook(IntPtr output)
    {
        if (!_known.Remove(output)) return;
        var sh = Native.obs_output_get_signal_handler(output);
        foreach (var sig in new[] { "start", "stop", "reconnect", "reconnect_success", "saved" })
            Native.signal_handler_disconnect(sh, sig, SignalFor(sig), GCHandle.ToIntPtr(_self));
    }

    static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> SignalFor(string sig) => sig switch
    {
        "start" => &OnStart,
        "stop" => &OnStop,
        "reconnect" => &OnReconnect,
        "reconnect_success" => &OnReconnected,
        _ => &OnSaved,
    };

    // calldata の "output" から送り元を特定
    static (OutputManager? m, IntPtr output) From(IntPtr data, IntPtr cd)
    {
        var m = GCHandle.FromIntPtr(data).Target as OutputManager;
        IntPtr o = IntPtr.Zero;
        Native.calldata_get_data(cd, "output", &o, (nuint)IntPtr.Size);
        return (m, o);
    }

    void Post(IntPtr output, Func<OutputKind, string, OutputEvent?> make)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_known.TryGetValue(output, out var k)) return;
            var e = make(k.kind, k.name);
            if (e != null) Event?.Invoke(e);
            StateChanged?.Invoke();
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnStart(IntPtr data, IntPtr cd)
    {
        try { var (m, o) = From(data, cd); m?.Post(o, (k, n) => new OutputEvent(k, n, "開始しました", false)); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnStop(IntPtr data, IntPtr cd)
    {
        try
        {
            var (m, o) = From(data, cd);
            var code = (int)Native.CallInt(cd, "code");
            var last = Native.CallString(cd, "last_error");
            m?.Post(o, (k, n) =>
            {
                if (k == OutputKind.Stream)
                {
                    // 同時配信: 全部の配信先が止まったら配信終了
                    if (m._streams.All(s => s.output == o || !Native.obs_output_active(s.output))) { m._streamRequested = false; m.StreamStartedAt = null; }
                }
                if (k == OutputKind.Record && o == m._record) { m._recordRequested = false; m._recordStopRequested = false; m.RecordStartedAt = null; }
                m.RecheckSoon();
                return code == Native.OBS_OUTPUT_SUCCESS
                    ? new OutputEvent(k, n, k == OutputKind.Record ? $"録画を保存しました: {(o == m._recordV ? m.LastVerticalRecordingPath : m.LastRecordingPath)}" : "停止しました", false)
                    : new OutputEvent(k, n, StopMessage(code, last), true);
            });
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnReconnect(IntPtr data, IntPtr cd)
    {
        try { var (m, o) = From(data, cd); m?.Post(o, (k, n) => new OutputEvent(k, n, "接続が切れました。自動で再接続しています…", false)); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnReconnected(IntPtr data, IntPtr cd)
    {
        try { var (m, o) = From(data, cd); m?.Post(o, (k, n) => new OutputEvent(k, n, "再接続しました", false)); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static void OnSaved(IntPtr data, IntPtr cd)
    {
        try
        {
            var (m, o) = From(data, cd);
            if (m == null) return;
            if (o == IntPtr.Zero) o = m._replay; // "saved" には送り元の output が入っていない
            // 次の保存で上書きされる前に、この場で保存先を取得する（replay_buffer の "get_last_replay" 手続き）
            string? path = null;
            var ph = Native.obs_output_get_proc_handler(o);
            var c = new Native.calldata();
            if (Native.proc_handler_call(ph, "get_last_replay", &c)) path = Native.CallString((IntPtr)(&c), "path");
            if (c.stack != null) Native.bfree(c.stack);
            m.Post(o, (k, n) =>
            {
                m.LastReplayPath = path;
                return path == null
                    ? new OutputEvent(k, n, "リプレイの保存先を確認できませんでした", true)
                    : new OutputEvent(k, n, $"直前 {m._settings.ReplaySeconds} 秒を保存しました: {path}", false);
            });
        }
        catch { }
    }

    static string StopMessage(int code, string? last) => code switch
    {
        Native.OBS_OUTPUT_BAD_PATH => "サーバーの URL が正しくありません",
        Native.OBS_OUTPUT_CONNECT_FAILED => "サーバーに接続できませんでした（回線・URL・ファイアウォールを確認してください）",
        Native.OBS_OUTPUT_INVALID_STREAM => "ストリームキーが正しくないか、配信枠が準備できていません",
        Native.OBS_OUTPUT_DISCONNECTED => "接続が切れ、再接続もできませんでした",
        Native.OBS_OUTPUT_NO_SPACE => "ディスクの空きがありません",
        Native.OBS_OUTPUT_ENCODE_ERROR => "エンコードに失敗しました（エンコーダの設定を確認してください）",
        Native.OBS_OUTPUT_UNSUPPORTED => "この設定には対応していません",
        _ => "エラーで停止しました" + (string.IsNullOrEmpty(last) ? "" : $"（{last}）"),
    };

    // --- 終了 ---
    public void Shutdown()
    {
        foreach (var s in _streams) if (Native.obs_output_active(s.output)) Native.obs_output_force_stop(s.output);
        foreach (var o in new[] { _record, _recordV, _replay, _vcam }) if (o != IntPtr.Zero && Native.obs_output_active(o)) Native.obs_output_force_stop(o);
        ReleaseStreams();
        foreach (var o in new[] { _record, _recordV, _replay, _vcam }) if (o != IntPtr.Zero) { Unhook(o); Native.obs_output_release(o); }
        _record = _recordV = _replay = _vcam = IntPtr.Zero;
        if (_vencV != IntPtr.Zero) { Native.obs_encoder_release(_vencV); _vencV = IntPtr.Zero; }
        if (_vencR != IntPtr.Zero) { Native.obs_encoder_release(_vencR); _vencR = IntPtr.Zero; }
        if (_venc != IntPtr.Zero) Native.obs_encoder_release(_venc);
        if (_aenc != IntPtr.Zero) Native.obs_encoder_release(_aenc);
        _venc = _aenc = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
    }
}
