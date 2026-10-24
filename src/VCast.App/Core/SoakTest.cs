using System.Diagnostics;
using System.Windows;
using VCast.Avatar;
using VCast.Obs;

namespace VCast.Core;

// 長時間の連続運転テスト（--soak <分>）。この PC の中の RTMP 受信口（ffmpeg）へ本物の配信をし続け、
// 録画・リプレイ・アバター・サウンド・シーン切り替えを回して、メモリ・コマ落ち・音と映像のズレを記録する
public static class SoakTest
{
    const int Port = 19351;

    public static async Task Run(MainWindow main, int minutes)
    {
        var log = (string m) => ObsEngine.Log("[soak] " + m);
        var sinkFile = Path.Combine(Path.GetTempPath(), "vcast-soak", "sink.flv");
        Directory.CreateDirectory(Path.GetDirectoryName(sinkFile)!);
        if (File.Exists(sinkFile)) File.Delete(sinkFile);

        var st = main.Settings; var outs = main.Outputs; var scenes = main.Scenes;
        st.Destinations = new() { new StreamDestination { Name = "soak", Service = "Custom", Server = $"rtmp://127.0.0.1:{Port}/live", Key = "sk_soak_0123456789abcdef", Enabled = true } };
        st.PreflightCheck = false; st.RecordFolder = Path.Combine(Path.GetTempPath(), "vcast-soak"); st.ReplayEnabled = false; st.ReplaySeconds = 30;

        // 受信口（ffmpeg。受け取った内容をそのまま保存）
        var sink = Process.Start(new ProcessStartInfo("ffmpeg", $"-v warning -y -listen 1 -i rtmp://127.0.0.1:{Port}/live/sk_soak_0123456789abcdef -c copy -f flv \"{sinkFile}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        var sinkErr = new System.Text.StringBuilder();
        sink.ErrorDataReceived += (_, e) => { if (e.Data != null) sinkErr.AppendLine(e.Data); };
        sink.BeginErrorReadLine();
        await Task.Delay(1500);

        // 映すもの: アバター + 時計の文字 + 色の板（動きを付ける）、シーンは 2 つ
        var vrm = Environment.GetEnvironmentVariable("VCAST_VRM") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Seed-san.vrm");
        if (File.Exists(vrm)) main.Avatar.SetModel(vrm);
        if (Environment.GetEnvironmentVariable("VCAST_NATIVE") == "1") { main.Avatar.Settings.NativeRender = true; log("avatar renderer: native (experimental)"); }
        main.AddAvatar();
        var clock = scenes.AddSource("text_gdiplus_v3", "clock");
        var sceneB = scenes.AddScene("soak-B");
        scenes.SetCurrent(sceneB);
        scenes.AddSource("color_source_v3", "soak-color");
        scenes.SetCurrent(scenes.Scenes[0]);
        var tone = Path.Combine(ObsEngine.ConfigDir, "soak-tone.wav");
        WriteTone(tone);
        main.SoundboardCore.Pads[0] = new SoundPad { Name = "tone", File = tone, Volume = 0.3, Expression = "happy" };
        main.SoundboardCore.Prepare(0);
        var mic = main.Mixer.Mic;

        var startedStream = outs.StartStream(out var serr);
        var startedRec = outs.StartRecord(out var rerr);
        var startedReplay = outs.StartReplay(out var perr);
        log($"start stream={startedStream}{(serr.Length > 0 ? " " + serr : "")} record={startedRec}{(rerr.Length > 0 ? " " + rerr : "")} replay={startedReplay}{(perr.Length > 0 ? " " + perr : "")}  duration={minutes}min");
        if (!startedStream) { log("RESULT FAIL stream did not start"); sink.Kill(); main.Close(); return; }

        var t0 = DateTime.Now;
        var perf = new PerfMonitor(); perf.Sample();
        var mem = new List<(double min, double mb)>();
        var mainMem = new List<(double min, double mb)>(); var helperMem = new List<(double min, double mb)>();
        var skips = new List<double>(); var lags = new List<double>();
        var handles = new List<(double min, double n)>(); var managed = new List<(double min, double mb)>(); var threads = new List<(double min, double n)>();
        var self = Process.GetCurrentProcess();
        double maxLag = 0, maxSkip = 0; long lastDropped = 0;
        var exprs = new[] { "happy", "sad", "surprised", "neutral" };
        var tick = 0;
        var reconnects = 0;
        outs.Event += ev => { if (ev.Message.Contains("再接続")) reconnects++; if (ev.IsError) log($"EVENT error {ev.Name}: {ev.Message}"); };

        while ((DateTime.Now - t0).TotalMinutes < minutes)
        {
            await Task.Delay(10_000);
            tick++;
            var s = perf.Sample();
            var (kbps, dropped, total, cong) = outs.StreamStats();
            var min = (DateTime.Now - t0).TotalMinutes;
            mem.Add((min, s.MemoryMb)); mainMem.Add((min, perf.MainMb)); helperMem.Add((min, perf.HelperMb));
            skips.Add(s.EncodeSkipPercent); lags.Add(s.RenderLagPercent);
            self.Refresh();
            handles.Add((min, self.HandleCount)); threads.Add((min, self.Threads.Count));
            var gcMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0; managed.Add((min, gcMb));
            maxLag = Math.Max(maxLag, s.RenderLagPercent); maxSkip = Math.Max(maxSkip, s.EncodeSkipPercent);
            log($"t={min,5:0.0}min mem={s.MemoryMb,5:0}MB (main {perf.MainMb:0} / browser {perf.HelperMb:0}; .NET heap {gcMb:0}, handles {self.HandleCount}, threads {self.Threads.Count}) cpu={s.CpuPercent,4:0.0}% fps={s.Fps:0} lag={s.RenderLagPercent:0.#}% skip={s.EncodeSkipPercent:0.#}% stream={kbps:0}kbps dropped={dropped}/{total} congestion={cong:0.00} streaming={outs.Streaming} rec={outs.Recording}");
            if (!outs.Streaming) { log("EVENT stream stopped unexpectedly"); break; }

            // 動きを付ける: 表情・サウンド・シーン・時計・ミュート・リプレイ保存を順番に
            main.Avatar.SetExpression(exprs[tick % exprs.Length]);
            if (tick % 3 == 0) main.SoundboardCore.Play(0);
            if (tick % 4 == 0) scenes.SetCurrent(scenes.Scenes[tick / 4 % 2]);
            if (tick % 5 == 0) { var m = Native.obs_source_muted(mic); Native.obs_source_set_muted(mic, !m); Native.obs_source_set_muted(mic, m); }
            if (tick % 12 == 0) outs.SaveReplay();
            var cs = Native.obs_source_get_settings(clock!.Source);
            Native.obs_data_set_string(cs, "text", DateTime.Now.ToString("HH:mm:ss"));
            Native.obs_source_update(clock.Source, cs);
            Native.obs_data_release(cs);
            lastDropped = dropped;
        }

        outs.StopStream(); outs.StopRecord(); outs.StopReplay();
        for (var i = 0; i < 100 && (outs.Streaming || outs.Recording); i++) await Task.Delay(100);
        await Task.Delay(1500);
        try { if (!sink.WaitForExit(8000)) sink.Kill(); } catch { }

        // --- 結果 ---
        var first = mem.Where(m => m.min <= 3).Select(m => m.mb).DefaultIfEmpty(0).Average();
        var last = mem.Where(m => m.min >= mem.Max(x => x.min) - 3).Select(m => m.mb).DefaultIfEmpty(0).Average();
        log($"memory: first3min={first:0}MB last3min={last:0}MB growth={last - first:+0;-0}MB max={mem.Select(m => m.mb).DefaultIfEmpty(0).Max():0}MB");
        // 本体とブラウザ部品それぞれの増え方（最初の 3 分 → 最後の 3 分）と、1 時間あたりの増加の見積もり
        double Avg(List<(double min, double mb)> l, bool head) { var end = l.Count == 0 ? 0 : l.Max(x => x.min); return l.Where(m => head ? m.min <= 3 : m.min >= end - 3).Select(m => m.mb).DefaultIfEmpty(0).Average(); }
        var span = mem.Count == 0 ? 1 : Math.Max(1, mem.Max(x => x.min) - 3);
        double mg = Avg(mainMem, false) - Avg(mainMem, true), hg = Avg(helperMem, false) - Avg(helperMem, true);
        log($"memory by part: main {Avg(mainMem, true):0}→{Avg(mainMem, false):0}MB ({mg:+0;-0}MB, ~{mg / span * 60:+0;-0}MB/h)  browser {Avg(helperMem, true):0}→{Avg(helperMem, false):0}MB ({hg:+0;-0}MB, ~{hg / span * 60:+0;-0}MB/h)");
        // .NET が管理するメモリ・ハンドル・スレッドの増え方（うっかりリークの検出）
        double HeadTail(List<(double min, double v)> l, bool head) { var end = l.Count == 0 ? 0 : l.Max(x => x.min); return l.Where(m => head ? m.min <= 3 : m.min >= end - 3).Select(m => m.v).DefaultIfEmpty(0).Average(); }
        var handleGrowth = HeadTail(handles.Select(x => (x.min, x.n)).ToList(), false) - HeadTail(handles.Select(x => (x.min, x.n)).ToList(), true);
        var threadGrowth = HeadTail(threads.Select(x => (x.min, x.n)).ToList(), false) - HeadTail(threads.Select(x => (x.min, x.n)).ToList(), true);
        var heapGrowth = HeadTail(managed.Select(x => (x.min, x.mb)).ToList(), false) - HeadTail(managed.Select(x => (x.min, x.mb)).ToList(), true);
        log($".NET heap {HeadTail(managed.Select(x => (x.min, x.mb)).ToList(), true):0}→{HeadTail(managed.Select(x => (x.min, x.mb)).ToList(), false):0}MB ({heapGrowth:+0;-0}MB)  handles {handleGrowth:+0;-0}  threads {threadGrowth:+0;-0}");
        // 平均と 95 パーセンタイルで判定（1 回のスパイクでは落とさない。ほかの作業と重なることがあるため）
        double P95(List<double> l) { if (l.Count == 0) return 0; var o = l.OrderBy(x => x).ToList(); return o[Math.Min(o.Count - 1, (int)(o.Count * 0.95))]; }
        log($"encode skipped: avg {(skips.Count == 0 ? 0 : skips.Average()):0.00}% p95 {P95(skips):0.0}% max {maxSkip:0.0}%  render lag: avg {(lags.Count == 0 ? 0 : lags.Average()):0.00}% p95 {P95(lags):0.0}% max {maxLag:0.0}%");
        var fail = new List<string>();
        if (handleGrowth > 300) fail.Add($"handles grew {handleGrowth:0}");
        if (threadGrowth > 50) fail.Add($"threads grew {threadGrowth:0}");
        if (last - first > 300) fail.Add($"memory grew {last - first:0}MB");
        if (mem.Count > 0 && (last - first) / span * 60 > 400) fail.Add($"memory growth rate {(last - first) / span * 60:0}MB/h");
        if (lags.Count > 0 && (lags.Average() > 1 || P95(lags) > 3)) fail.Add($"render lag avg {lags.Average():0.#}% p95 {P95(lags):0.#}%");
        if (skips.Count > 0 && (skips.Average() > 1 || P95(skips) > 3)) fail.Add($"encode skipped avg {skips.Average():0.#}% p95 {P95(skips):0.#}%");
        if (reconnects > 0) fail.Add($"reconnects {reconnects}");

        if (File.Exists(sinkFile))
        {
            var (v, a, dur) = ProbeSync(sinkFile);
            log($"received file: {new FileInfo(sinkFile).Length / 1024 / 1024} MB, video {v}, audio {a}, duration {dur}");
            var vd = ParseD(v); var ad = ParseD(a);
            if (vd > 0 && ad > 0)
            {
                var diff = Math.Abs(vd - ad);
                log($"A/V duration difference: {diff:0.000}s over {vd:0}s");
                if (diff > 0.5) fail.Add($"A/V drift {diff:0.00}s");
            }
            else fail.Add("could not read received file streams");
            if (dur < minutes * 60 * 0.9) fail.Add($"received only {dur:0}s of {minutes * 60}s");
        }
        else fail.Add("no received file");
        if (sinkErr.Length > 0) log("sink stderr: " + sinkErr.ToString().Trim().Replace("\r\n", " | "));
        log(fail.Count == 0 ? "RESULT PASS" : "RESULT FAIL " + string.Join("; ", fail));
        main.Close();
    }

    static double ParseD(string s) => double.TryParse(s.Split(' ').LastOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    // 受け取ったファイルの映像/音声それぞれの長さ（ffprobe。各ストリームの最後のパケット時刻）
    static (string video, string audio, double duration) ProbeSync(string file)
    {
        string Last(string sel)
        {
            var p = Process.Start(new ProcessStartInfo("ffprobe", $"-v error -select_streams {sel} -show_entries packet=pts_time -of csv=p=0 \"{file}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = p.StandardOutput.ReadToEnd(); p.WaitForExit(120000);
            var times = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => double.TryParse(l.Trim(','), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : -1).Where(d => d >= 0).ToList();
            return times.Count == 0 ? "none 0" : $"packets={times.Count} last {times.Max():0.000}";
        }
        var v = Last("v:0"); var a = Last("a:0");
        return (v, a, Math.Max(ParseD(v), ParseD(a)));
    }

    static void WriteTone(string path)
    {
        const int rate = 48000, secs = 2;
        using var w = new BinaryWriter(File.Create(path));
        var samples = rate * secs;
        w.Write("RIFF"u8); w.Write(36 + samples * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 4);
        for (var i = 0; i < samples; i++) { var v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.25 * short.MaxValue); w.Write(v); w.Write(v); }
    }
}
