using System.Text.Json.Nodes;
using System.Windows;
using VCast.Core;
using VCast.Obs;

namespace VCast;

// 外部操作（obs-websocket v5 互換）: リクエストを VCast の機能に対応させる。UI スレッドで呼ばれる
public partial class MainWindow
{
    RemoteServer? _remote;

    const int Ok = 100, MissingField = 300, InvalidField = 400, OutputRunning = 500, OutputNotRunning = 501, StudioOff = 506, NotFound = 600, Failed = 702, Unknown = 204;

    static readonly string[] RemoteRequests =
    {
        "GetVersion", "GetStats", "GetSceneList", "GetCurrentProgramScene", "SetCurrentProgramScene", "GetCurrentPreviewScene", "SetCurrentPreviewScene",
        "GetStudioModeEnabled", "SetStudioModeEnabled", "TriggerStudioModeTransition",
        "GetStreamStatus", "StartStream", "StopStream", "ToggleStream", "GetRecordStatus", "StartRecord", "StopRecord", "ToggleRecord",
        "GetReplayBufferStatus", "StartReplayBuffer", "StopReplayBuffer", "ToggleReplayBuffer", "SaveReplayBuffer",
        "GetVirtualCamStatus", "StartVirtualCam", "StopVirtualCam", "ToggleVirtualCam",
        "GetInputList", "GetInputMute", "SetInputMute", "ToggleInputMute", "GetInputVolume", "SetInputVolume",
        "GetSceneItemList", "GetSceneItemId", "GetSceneItemEnabled", "SetSceneItemEnabled", "CallVendorRequest",
    };

    // 設定に合わせて起動・停止（ポートが使われていたら知らせる）
    internal void ApplyRemote()
    {
        _remote?.Dispose();
        _remote = null;
        if (!_settings.RemoteEnabled) return;
        // パスワード無しでは起動しない（LAN・この PC 内のどちらでも）。空なら自動で作る
        if (string.IsNullOrEmpty(_settings.RemotePassword)) { _settings.RemotePassword = AppSettings.NewPassword(); _settings.Save(); }
        try { _remote = new RemoteServer(_settings.RemotePort, _settings.RemoteLan, _settings.RemotePassword, HandleRemote); }
        catch (System.Net.Sockets.SocketException)
        {
            Status.Text = $"外部操作のポート {_settings.RemotePort} は使用中です（OBS の WebSocket サーバーが動いていませんか？）。配信設定でポートを変えてください";
            ObsEngine.Log($"[remote] port {_settings.RemotePort} busy");
        }
    }

    internal RemoteServer? Remote => _remote;

    static (bool, int, string?, JsonObject?) R(JsonObject? data = null) => (true, Ok, null, data);
    static (bool, int, string?, JsonObject?) E(int code, string comment) => (false, code, comment, null);

    SceneVm? FindScene(JsonObject? d)
    {
        var name = d?["sceneName"]?.GetValue<string>();
        var uuid = d?["sceneUuid"]?.GetValue<string>();
        return _scenes.Scenes.FirstOrDefault(s => (name != null && s.Name == name) || (uuid != null && Uuid(s.Source) == uuid));
    }

    // 入力（音声・映像ソース）を名前か UUID で探す。見つかったら参照付き（呼び出し側で release）
    static IntPtr FindInput(JsonObject? d)
    {
        var name = d?["inputName"]?.GetValue<string>() ?? d?["sourceName"]?.GetValue<string>();
        var uuid = d?["inputUuid"]?.GetValue<string>() ?? d?["sourceUuid"]?.GetValue<string>();
        if (name != null) return Native.obs_get_source_by_name(name);
        if (uuid != null) return Native.obs_get_source_by_uuid(uuid);
        return IntPtr.Zero;
    }

    static string Uuid(IntPtr src) => Native.Str(Native.obs_source_get_uuid(src)) ?? "";
    static string Name(IntPtr src) => Native.Str(Native.obs_source_get_name(src)) ?? "";

    JsonObject SceneInfo(IntPtr src) => new() { ["sceneName"] = Name(src), ["sceneUuid"] = Uuid(src) };

    (bool, int, string?, JsonObject?) HandleRemote(string type, JsonObject? d)
    {
        var studio = _settings.StudioMode && ProOn;
        switch (type)
        {
            case "GetVersion":
                return R(new JsonObject
                {
                    ["obsVersion"] = Native.Str(Native.obs_get_version_string()), ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1,
                    ["availableRequests"] = new JsonArray(RemoteRequests.Select(x => (JsonNode)x).ToArray()),
                    ["supportedImageFormats"] = new JsonArray(), ["platform"] = "windows", ["platformDescription"] = "VCast Studio",
                });
            case "GetStats":
            {
                var s = _perf.Sample();
                return R(new JsonObject
                {
                    ["cpuUsage"] = Math.Round(s.CpuPercent, 2), ["memoryUsage"] = Math.Round(s.MemoryMb, 1), ["activeFps"] = Math.Round(s.Fps, 2),
                    ["renderTotalFrames"] = Native.obs_get_total_frames(), ["renderSkippedFrames"] = Native.obs_get_lagged_frames(),
                    ["outputTotalFrames"] = Native.video_output_get_total_frames(Native.obs_get_video()), ["outputSkippedFrames"] = Native.video_output_get_skipped_frames(Native.obs_get_video()),
                    ["averageFrameRenderTime"] = Native.obs_get_average_frame_time_ns() / 1_000_000.0,
                });
            }

            // --- シーン ---
            case "GetSceneList":
            {
                var list = new JsonArray();
                var n = _scenes.Scenes.Count;
                for (var i = 0; i < n; i++)
                {
                    var sc = _scenes.Scenes[n - 1 - i]; // obs-websocket と同じく下から数える
                    list.Add(new JsonObject { ["sceneIndex"] = i, ["sceneName"] = sc.Name, ["sceneUuid"] = Uuid(sc.Source) });
                }
                var prog = _transitions.ProgramScene;
                return R(new JsonObject
                {
                    ["currentProgramSceneName"] = prog == IntPtr.Zero ? null : Name(prog), ["currentProgramSceneUuid"] = prog == IntPtr.Zero ? null : Uuid(prog),
                    ["currentPreviewSceneName"] = studio ? _scenes.Current?.Name : null, ["currentPreviewSceneUuid"] = studio && _scenes.Current != null ? Uuid(_scenes.Current.Source) : null,
                    ["scenes"] = list,
                });
            }
            case "GetCurrentProgramScene":
            {
                var prog = _transitions.ProgramScene;
                if (prog == IntPtr.Zero) return E(NotFound, "no program scene");
                var info = SceneInfo(prog);
                info["currentProgramSceneName"] = Name(prog); info["currentProgramSceneUuid"] = Uuid(prog);
                return R(info);
            }
            case "SetCurrentProgramScene":
            {
                var sc = FindScene(d);
                if (sc == null) return d?["sceneName"] == null && d?["sceneUuid"] == null ? E(MissingField, "sceneName") : E(NotFound, "scene not found");
                if (studio) _transitions.Take(sc.Source);
                else _scenes.SetCurrent(sc);
                return R();
            }
            case "GetCurrentPreviewScene":
                if (!studio) return E(StudioOff, "studio mode is not active");
                return _scenes.Current == null ? E(NotFound, "no scene") : R(SceneInfo(_scenes.Current.Source));
            case "SetCurrentPreviewScene":
            {
                if (!studio) return E(StudioOff, "studio mode is not active");
                var sc = FindScene(d);
                if (sc == null) return E(NotFound, "scene not found");
                _scenes.SetCurrent(sc);
                return R();
            }
            case "GetStudioModeEnabled": return R(new JsonObject { ["studioModeEnabled"] = studio });
            case "SetStudioModeEnabled":
            {
                var on = d?["studioModeEnabled"]?.GetValue<bool>();
                if (on == null) return E(MissingField, "studioModeEnabled");
                if (on.Value && _settings.SimpleMode) { _settings.SimpleMode = false; ApplyMode(); }
                _settings.StudioMode = on.Value; ApplyStudio();
                _remote?.Emit("StudioModeStateChanged", RemoteServer.IntentUi, new JsonObject { ["studioModeEnabled"] = on.Value });
                return R();
            }
            case "TriggerStudioModeTransition":
                if (!studio) return E(StudioOff, "studio mode is not active");
                if (_scenes.Current != null) _transitions.Take(_scenes.Current.Source);
                return R();

            // --- 配信・録画 ---
            case "GetStreamStatus":
            {
                var (_, dropped, total, cong) = _outputs.StreamStats();
                var dur = _outputs.StreamStartedAt is { } st ? (long)(DateTime.Now - st).TotalMilliseconds : 0;
                return R(new JsonObject
                {
                    ["outputActive"] = _outputs.Streaming, ["outputReconnecting"] = false, ["outputTimecode"] = TimeSpan.FromMilliseconds(dur).ToString(@"hh\:mm\:ss\.fff"),
                    ["outputDuration"] = dur, ["outputCongestion"] = cong, ["outputBytes"] = 0, ["outputSkippedFrames"] = dropped, ["outputTotalFrames"] = total,
                });
            }
            case "StartStream":
                if (_outputs.Streaming) return E(OutputRunning, "already streaming");
                // 外部からの開始は配信前チェックの画面を出さない（ボタン一発で始めたい用途）
                return _outputs.StartStream(out var serr) ? R() : E(Failed, serr);
            case "StopStream":
                if (!_outputs.Streaming) return E(OutputNotRunning, "not streaming");
                _outputs.StopStream(); return R();
            case "ToggleStream":
                if (_outputs.Streaming) { _outputs.StopStream(); return R(new JsonObject { ["outputActive"] = false }); }
                return _outputs.StartStream(out var terr) ? R(new JsonObject { ["outputActive"] = true }) : E(Failed, terr);
            case "GetRecordStatus":
            {
                var dur = _outputs.RecordStartedAt is { } rt ? (long)(DateTime.Now - rt).TotalMilliseconds : 0;
                return R(new JsonObject
                {
                    ["outputActive"] = _outputs.Recording, ["outputPaused"] = false, ["outputTimecode"] = TimeSpan.FromMilliseconds(dur).ToString(@"hh\:mm\:ss\.fff"),
                    ["outputDuration"] = dur, ["outputBytes"] = 0,
                });
            }
            case "StartRecord":
                if (_outputs.Recording) return E(OutputRunning, "already recording");
                return _outputs.StartRecord(out var rerr) ? R() : E(Failed, rerr);
            case "StopRecord":
                if (!_outputs.Recording) return E(OutputNotRunning, "not recording");
                _outputs.StopRecord(); return R(new JsonObject { ["outputPath"] = _outputs.LastRecordingPath });
            case "ToggleRecord":
                if (_outputs.Recording) { _outputs.StopRecord(); return R(new JsonObject { ["outputActive"] = false }); }
                return _outputs.StartRecord(out var trerr) ? R(new JsonObject { ["outputActive"] = true }) : E(Failed, trerr);
            case "GetReplayBufferStatus": return R(new JsonObject { ["outputActive"] = _outputs.ReplayActive });
            case "StartReplayBuffer":
                if (_outputs.ReplayActive) return E(OutputRunning, "already active");
                return _outputs.StartReplay(out var perr) ? R() : E(Failed, perr);
            case "StopReplayBuffer":
                if (!_outputs.ReplayActive) return E(OutputNotRunning, "not active");
                _outputs.StopReplay(); return R();
            case "ToggleReplayBuffer":
                if (_outputs.ReplayActive) { _outputs.StopReplay(); return R(new JsonObject { ["outputActive"] = false }); }
                return _outputs.StartReplay(out var tperr) ? R(new JsonObject { ["outputActive"] = true }) : E(Failed, tperr);
            case "SaveReplayBuffer":
                if (!_outputs.ReplayActive) return E(OutputNotRunning, "replay buffer is not active");
                return _outputs.SaveReplay() ? R() : E(Failed, "save failed");
            case "GetVirtualCamStatus": return R(new JsonObject { ["outputActive"] = _outputs.VirtualCamActive });
            case "StartVirtualCam":
                if (_outputs.VirtualCamActive) return E(OutputRunning, "already active");
                return _outputs.StartVirtualCam(out var verr) ? R() : E(Failed, verr);
            case "StopVirtualCam":
                if (!_outputs.VirtualCamActive) return E(OutputNotRunning, "not active");
                _outputs.StopVirtualCam(); return R();
            case "ToggleVirtualCam":
                if (_outputs.VirtualCamActive) { _outputs.StopVirtualCam(); return R(new JsonObject { ["outputActive"] = false }); }
                return _outputs.StartVirtualCam(out var tverr) ? R(new JsonObject { ["outputActive"] = true }) : E(Failed, tverr);

            // --- 入力（音量・ミュート） ---
            case "GetInputList":
            {
                var list = new JsonArray();
                var seen = new HashSet<IntPtr>();
                void Add(IntPtr src)
                {
                    if (src == IntPtr.Zero || !seen.Add(src) || Native.obs_source_is_scene(src)) return;
                    var id = Native.Str(Native.obs_source_get_id(src)) ?? "";
                    list.Add(new JsonObject { ["inputName"] = Name(src), ["inputUuid"] = Uuid(src), ["inputKind"] = id, ["unversionedInputKind"] = System.Text.RegularExpressions.Regex.Replace(id, "_v\\d+$", "") });
                }
                Add(_mixer.Desktop); Add(_mixer.Mic);
                foreach (var sc in _scenes.Scenes) foreach (var it in SceneManager.EnumItems(sc.Scene)) Add(Native.obs_sceneitem_get_source(it));
                return R(new JsonObject { ["inputs"] = list });
            }
            case "GetInputMute": case "SetInputMute": case "ToggleInputMute": case "GetInputVolume": case "SetInputVolume":
            {
                var src = FindInput(d);
                if (src == IntPtr.Zero) return d?["inputName"] == null && d?["inputUuid"] == null ? E(MissingField, "inputName") : E(NotFound, "input not found");
                try
                {
                    switch (type)
                    {
                        case "GetInputMute": return R(new JsonObject { ["inputMuted"] = Native.obs_source_muted(src) });
                        case "SetInputMute":
                        {
                            var m = d?["inputMuted"]?.GetValue<bool>();
                            if (m == null) return E(MissingField, "inputMuted");
                            SetMute(src, m.Value); return R();
                        }
                        case "ToggleInputMute":
                        {
                            var m = !Native.obs_source_muted(src);
                            SetMute(src, m); return R(new JsonObject { ["inputMuted"] = m });
                        }
                        case "GetInputVolume":
                        {
                            var mul = Native.obs_source_get_volume(src);
                            return R(new JsonObject { ["inputVolumeMul"] = mul, ["inputVolumeDb"] = mul <= 0 ? -100 : Math.Round(20 * Math.Log10(mul), 2) });
                        }
                        default: // SetInputVolume
                        {
                            var mul = d?["inputVolumeMul"]?.GetValue<double>();
                            var db = d?["inputVolumeDb"]?.GetValue<double>();
                            if (mul == null && db == null) return E(MissingField, "inputVolumeMul or inputVolumeDb");
                            var v = mul ?? Math.Pow(10, db!.Value / 20);
                            if (v < 0 || v > 20) return E(InvalidField, "volume out of range");
                            Native.obs_source_set_volume(src, (float)v);
                            foreach (var s in _mixer.Strips) s.Refresh();
                            return R();
                        }
                    }
                }
                finally { Native.obs_source_release(src); }
            }

            // --- シーンアイテム ---
            case "GetSceneItemList":
            {
                var sc = FindScene(d);
                if (sc == null) return E(NotFound, "scene not found");
                var arr = new JsonArray();
                var items = SceneManager.EnumItems(sc.Scene);
                for (var i = 0; i < items.Count; i++)
                {
                    var src = Native.obs_sceneitem_get_source(items[i]);
                    arr.Add(new JsonObject
                    {
                        ["sceneItemId"] = Native.obs_sceneitem_get_id(items[i]), ["sceneItemIndex"] = i, ["sceneItemEnabled"] = Native.obs_sceneitem_visible(items[i]),
                        ["sourceName"] = Name(src), ["sourceUuid"] = Uuid(src), ["inputKind"] = Native.Str(Native.obs_source_get_id(src)),
                    });
                }
                return R(new JsonObject { ["sceneItems"] = arr });
            }
            case "GetSceneItemId":
            {
                var sc = FindScene(d);
                var name = d?["sourceName"]?.GetValue<string>();
                if (sc == null || name == null) return E(MissingField, "sceneName / sourceName");
                var it = SceneManager.EnumItems(sc.Scene).FirstOrDefault(x => Name(Native.obs_sceneitem_get_source(x)) == name);
                return it == IntPtr.Zero ? E(NotFound, "source not in scene") : R(new JsonObject { ["sceneItemId"] = Native.obs_sceneitem_get_id(it) });
            }
            case "GetSceneItemEnabled": case "SetSceneItemEnabled":
            {
                var sc = FindScene(d);
                var id = d?["sceneItemId"]?.GetValue<long>();
                if (sc == null || id == null) return E(MissingField, "sceneName / sceneItemId");
                var it = SceneManager.EnumItems(sc.Scene).FirstOrDefault(x => Native.obs_sceneitem_get_id(x) == id);
                if (it == IntPtr.Zero) return E(NotFound, "scene item not found");
                if (type == "GetSceneItemEnabled") return R(new JsonObject { ["sceneItemEnabled"] = Native.obs_sceneitem_visible(it) });
                var en = d?["sceneItemEnabled"]?.GetValue<bool>();
                if (en == null) return E(MissingField, "sceneItemEnabled");
                Native.obs_sceneitem_set_visible(it, en.Value);
                foreach (var i in _scenes.Items) i.Refresh();
                return R();
            }

            // --- VCast 独自（表情・サウンドボード） ---
            case "CallVendorRequest":
            {
                var vendor = d?["vendorName"]?.GetValue<string>();
                var vtype = d?["requestType"]?.GetValue<string>();
                var vdata = d?["requestData"] as JsonObject;
                if (vendor != "vcast") return E(NotFound, "unknown vendor (use \"vcast\")");
                JsonObject Resp(JsonObject? data = null) => new() { ["vendorName"] = "vcast", ["requestType"] = vtype, ["responseData"] = data ?? new JsonObject() };
                switch (vtype)
                {
                    case "SetExpression":
                    {
                        var name = vdata?["expression"]?.GetValue<string>();
                        if (name == null || VCast.Avatar.AvatarController.Expressions.All(e => e.Id != name))
                            return E(InvalidField, "expression: " + string.Join("/", VCast.Avatar.AvatarController.Expressions.Select(e => e.Id)));
                        _avatar.SetExpression(name); return R(Resp());
                    }
                    case "PlaySound":
                    {
                        var pad = vdata?["pad"]?.GetValue<int>();
                        if (pad == null || pad < 1 || pad > Soundboard.PadCount) return E(InvalidField, $"pad: 1-{Soundboard.PadCount}");
                        _soundboard.Play(pad.Value - 1); return R(Resp());
                    }
                    case "StopAllSounds": _soundboard.StopAll(); return R(Resp());
                    case "CalibrateAvatar": _avatar.Calibrate(); return R(Resp());
                    default: return E(Unknown, "vendor requests: SetExpression, PlaySound, StopAllSounds, CalibrateAvatar");
                }
            }
            default:
                return E(Unknown, "this request is not supported by VCast Studio");
        }
    }

    void SetMute(IntPtr src, bool muted)
    {
        Native.obs_source_set_muted(src, muted);
        foreach (var s in _mixer.Strips) s.Refresh();
        _remote?.Emit("InputMuteStateChanged", RemoteServer.IntentInputs, new JsonObject { ["inputName"] = Name(src), ["inputUuid"] = Uuid(src), ["inputMuted"] = muted });
    }

    // アプリ側で起きた変化を外部へ知らせる
    void HookRemoteEvents()
    {
        _transitions.ProgramChanged += () =>
        {
            if (_transitions.ProgramScene != IntPtr.Zero) _remote?.Emit("CurrentProgramSceneChanged", RemoteServer.IntentScenes, SceneInfo(_transitions.ProgramScene));
        };
        _scenes.CurrentChanged += () =>
        {
            if (_settings.StudioMode && ProOn && _scenes.Current != null) _remote?.Emit("CurrentPreviewSceneChanged", RemoteServer.IntentScenes, SceneInfo(_scenes.Current.Source));
        };
        _scenes.Scenes.CollectionChanged += (_, _) =>
        {
            var arr = new JsonArray();
            foreach (var s in _scenes.Scenes) arr.Add(SceneInfo(s.Source));
            _remote?.Emit("SceneListChanged", RemoteServer.IntentScenes, new JsonObject { ["scenes"] = arr });
        };
        _outputs.Event += ev =>
        {
            var started = ev.Message.StartsWith("開始");
            var (name, active) = ev.Kind switch
            {
                OutputKind.Stream => ("StreamStateChanged", _outputs.Streaming),
                OutputKind.Record => ("RecordStateChanged", _outputs.Recording),
                OutputKind.Replay => ("ReplayBufferStateChanged", _outputs.ReplayActive),
                _ => ("VirtualcamStateChanged", _outputs.VirtualCamActive),
            };
            var data = new JsonObject { ["outputActive"] = active, ["outputState"] = started ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED" };
            if (ev.Kind == OutputKind.Record && !started) data["outputPath"] = _outputs.LastRecordingPath;
            if (ev.Kind == OutputKind.Replay && ev.Message.StartsWith("直前"))
                _remote?.Emit("ReplayBufferSaved", RemoteServer.IntentOutputs, new JsonObject { ["savedReplayPath"] = _outputs.LastReplayPath });
            else _remote?.Emit(name, RemoteServer.IntentOutputs, data);
        };
    }
}
