using System.Diagnostics;
using VCast.Obs;

namespace VCast.Core;

// 「軽さの見える化」: 本体＋同梱ブラウザ（アバター描画など）の CPU・メモリ、描画 fps、描画/エンコードのコマ落ち
public sealed class PerfMonitor
{
    readonly Process _self = Process.GetCurrentProcess();
    readonly string _runtime = ObsEngine.RuntimeDir;
    readonly Dictionary<(int, DateTime), TimeSpan> _cpuByProc = new();
    DateTime _lastAt = DateTime.Now;
    uint _lastLagged, _lastTotal, _lastSkipped, _lastEncTotal;
    List<int> _helpers = new();
    readonly Dictionary<int, bool> _isOurs = new(); // PID → 同梱ランタイムの子か（MainModule の取得は重いので一度だけ）
    DateTime _helpersAt = DateTime.MinValue;

    public double MainMb { get; private set; }
    public double HelperMb { get; private set; }

    public record Snapshot(double CpuPercent, double MemoryMb, double Fps, double RenderLagPercent, double EncodeSkipPercent);

    public Snapshot Sample()
    {
        var now = DateTime.Now;
        // 同梱 CEF の子プロセス（obs-browser-page.exe）は数秒ごとに探し直す
        if ((now - _helpersAt).TotalSeconds > 5) { _helpers = FindHelpers(); _helpersAt = now; }
        // CPU はプロセスごとの差分を合算（子プロセスが増えても過去の累積時間を足さない）
        var cpuDelta = 0.0; double mem = 0, mainMem = 0;
        var alive = new HashSet<(int, DateTime)>();
        foreach (var p in ProcessesToMeasure())
        {
            using (p)
            {
                try
                {
                    p.Refresh();
                    var key = (p.Id, p.StartTime);
                    alive.Add(key);
                    var cpuTotal = p.TotalProcessorTime;
                    if (_cpuByProc.TryGetValue(key, out var last)) cpuDelta += (cpuTotal - last).TotalSeconds;
                    _cpuByProc[key] = cpuTotal;
                    mem += p.PrivateMemorySize64;
                    if (p.Id == _self.Id) mainMem = p.PrivateMemorySize64;
                }
                catch { }
            }
        }
        foreach (var k in _cpuByProc.Keys.Where(k => !alive.Contains(k)).ToList()) _cpuByProc.Remove(k);
        MainMb = mainMem / 1024 / 1024; HelperMb = (mem - mainMem) / 1024 / 1024;
        var elapsed = (now - _lastAt).TotalSeconds;
        var cpuPct = elapsed > 0 ? Math.Max(0, cpuDelta / elapsed / Environment.ProcessorCount * 100) : 0;
        _lastAt = now;

        uint lagged = Native.obs_get_lagged_frames(), total = Native.obs_get_total_frames();
        var video = Native.obs_get_video();
        uint skipped = Native.video_output_get_skipped_frames(video), encTotal = Native.video_output_get_total_frames(video);
        // 録画・配信を始め直すと libobs がカウンタを 0 に戻すので、減っていたら基準を取り直す
        if (skipped < _lastSkipped || encTotal < _lastEncTotal) { _lastSkipped = skipped; _lastEncTotal = encTotal; }
        if (lagged < _lastLagged || total < _lastTotal) { _lastLagged = lagged; _lastTotal = total; }
        var lagPct = Pct(lagged - _lastLagged, total - _lastTotal);
        var skipPct = Pct(skipped - _lastSkipped, encTotal - _lastEncTotal);
        _lastLagged = lagged; _lastTotal = total; _lastSkipped = skipped; _lastEncTotal = encTotal;
        return new Snapshot(cpuPct, mem / 1024 / 1024, Native.obs_get_active_fps(), lagPct, skipPct);
    }

    static double Pct(uint part, uint whole) => whole == 0 ? 0 : part * 100.0 / whole;

    IEnumerable<Process> ProcessesToMeasure()
    {
        yield return Process.GetProcessById(_self.Id); // using で破棄されるので毎回取り直す
        foreach (var id in _helpers)
        {
            Process? p = null;
            try { p = Process.GetProcessById(id); } catch { }
            if (p != null) yield return p;
        }
    }

    // 自分の同梱ランタイムから起動された obs-browser-page だけを数える（別途起動している本家 OBS は含めない）
    List<int> FindHelpers()
    {
        var list = new List<int>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("obs-browser-page"))
        {
            seen.Add(p.Id);
            if (!_isOurs.TryGetValue(p.Id, out var ours))
            {
                try { ours = p.MainModule?.FileName.StartsWith(_runtime, StringComparison.OrdinalIgnoreCase) == true; } catch { ours = false; }
                _isOurs[p.Id] = ours;
            }
            if (ours) list.Add(p.Id);
        }
        foreach (var k in _isOurs.Keys.Where(k => !seen.Contains(k)).ToList()) _isOurs.Remove(k);
        return list;
    }
}
