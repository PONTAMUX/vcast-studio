using System.Runtime.InteropServices;
using VCast.Obs;

namespace VCast.Core;

// 本番（出力チャンネル 0）は常に「トランジション」ソース。シーンの切り替えはトランジション経由で行う
//   通常: シーンを選ぶと選んだ効果で切り替わる
//   スタジオモード: プレビューで編集 → カット / オート / Tバー で本番へ
public sealed unsafe class TransitionManager : IDisposable
{
    public record TransitionType(string Id, string Name);
    readonly Dictionary<string, IntPtr> _transitions = new();
    IntPtr _current;
    bool _manual; // Tバー操作中
    IntPtr _manualDest; // Tバーで向かっている先（開始時に固定）
    public event Action? ManualCancelled; // Tバーを取り消した（UI のスライダーを 0 に戻す）
    public int DurationMs { get; set; } = 300;
    public string CurrentId { get; private set; } = "fade_transition";
    public IntPtr ProgramScene { get; private set; } // 本番に出ているシーンのソース（借用）
    public event Action? ProgramChanged;

    // よく使うものを先頭に
    static readonly string[] Preferred = { "cut_transition", "fade_transition", "swipe_transition", "slide_transition", "fade_to_color_transition", "wipe_transition" };

    public static List<TransitionType> Types()
    {
        var list = new List<TransitionType>();
        for (nuint i = 0; Native.obs_enum_transition_types(i, out var p); i++)
        {
            var id = Native.Str(p)!;
            if ((Native.obs_get_source_output_flags(id) & (Native.OBS_SOURCE_DEPRECATED | Native.OBS_SOURCE_CAP_DISABLED)) != 0) continue;
            if (id == "obs_stinger_transition") continue; // 動画ファイルの指定が必要なので v1 では出さない
            list.Add(new TransitionType(id, SceneManager.DisplayName(id)));
        }
        return list.OrderBy(t => { var i = Array.IndexOf(Preferred, t.Id); return i < 0 ? 99 : i; }).ToList();
    }

    public void Init(string id, int durationMs)
    {
        DurationMs = durationMs;
        SetType(id);
    }

    IntPtr Get(string id)
    {
        if (_transitions.TryGetValue(id, out var t)) return t;
        t = Native.obs_source_create_private(id, SceneManager.DisplayName(id), IntPtr.Zero);
        if (t != IntPtr.Zero) _transitions[id] = t;
        return t;
    }

    // 効果を変える。本番に出ているシーンはそのまま引き継ぐ
    public void SetType(string id)
    {
        var next = Get(id);
        if (next == IntPtr.Zero) { next = Get("fade_transition"); id = "fade_transition"; }
        if (next == _current) return;
        CancelManual();
        if (ProgramScene != IntPtr.Zero) Native.obs_transition_set(next, ProgramScene);
        if (_current != IntPtr.Zero) Native.obs_transition_clear(_current); // 使わなくなった効果が古いシーンを握り続けないように
        _current = next;
        CurrentId = id;
        Native.obs_set_output_source(0, _current);
    }

    // 出力（配信・録画・プレビュー）の出どころを、このトランジション（本番）へ戻す。緊急で別の画面に差し替えたあとの復帰用
    public void ReattachOutput() { if (_current != IntPtr.Zero) Native.obs_set_output_source(0, _current); }

    // 本番を切り替える（animate=false ならカット）
    public void Take(IntPtr sceneSource, bool animate = true)
    {
        if (_current == IntPtr.Zero || sceneSource == IntPtr.Zero) return;
        CancelManual();
        if (ProgramScene == IntPtr.Zero || !animate || CurrentId == "cut_transition")
            Native.obs_transition_set(_current, sceneSource);
        else
            Native.obs_transition_start(_current, Native.OBS_TRANSITION_MODE_AUTO, (uint)DurationMs, sceneSource);
        ProgramScene = sceneSource;
        ProgramChanged?.Invoke();
    }

    // Tバー: 0→1 で本番をプレビューのシーンへ。1 まで行くと確定
    public void TBar(IntPtr previewScene, double t)
    {
        if (_current == IntPtr.Zero || previewScene == IntPtr.Zero) return;
        // 途中でプレビューが変わったら、いったん取り消してから新しい行き先で始め直す
        if (_manual && previewScene != _manualDest) CancelManual();
        if (!_manual)
        {
            if (t <= 0.001 || previewScene == ProgramScene) return;
            if (!Native.obs_transition_start(_current, Native.OBS_TRANSITION_MODE_MANUAL, (uint)DurationMs, previewScene)) return;
            _manual = true;
            _manualDest = previewScene;
        }
        if (t <= 0.001) { CancelManual(); return; } // 0 に戻した = 取り消し（libobs は元の本番に戻して遷移を終える）
        Native.obs_transition_set_manual_time(_current, (float)Math.Clamp(t, 0, 1));
        if (t >= 0.999)
        {
            _manual = false;
            ProgramScene = _manualDest;
            _manualDest = IntPtr.Zero;
            ProgramChanged?.Invoke();
        }
    }

    // Tバー途中の取り消し: 本番は元のまま
    public void CancelManual()
    {
        if (!_manual) return;
        Native.obs_transition_set_manual_time(_current, 0f);
        _manual = false;
        _manualDest = IntPtr.Zero;
        ManualCancelled?.Invoke();
    }

    public bool ManualActive => _manual;

    // シーンが消されるとき: 本番がそれなら外す
    public void Forget(IntPtr sceneSource)
    {
        if (_manualDest == sceneSource) CancelManual();
        if (ProgramScene != sceneSource) return;
        ProgramScene = IntPtr.Zero;
        foreach (var t in _transitions.Values) Native.obs_transition_clear(t);
    }

    public void Dispose()
    {
        Native.obs_set_output_source(0, IntPtr.Zero);
        foreach (var t in _transitions.Values) { Native.obs_transition_clear(t); Native.obs_source_release(t); }
        _transitions.Clear();
        _current = IntPtr.Zero;
        ProgramScene = IntPtr.Zero;
    }
}
