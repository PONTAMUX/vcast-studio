using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VCast.Obs;

namespace VCast.Core;

public sealed record HotkeyAction(string Id, string Group, string Label, string Default, Action Run);

// 全体で効くホットキー（どのアプリを操作中でも）。割り当ては hotkeys.json に保存し、設定画面で変えられる
public sealed class HotkeyManager : IDisposable
{
    public static HotkeyManager? Current { get; private set; }
    readonly string _path = Path.Combine(ObsEngine.ConfigDir, "hotkeys.json");
    readonly List<HotkeyAction> _actions = new();
    readonly Dictionary<string, string> _bindings = new(); // id → "Ctrl+Alt+1"（空 = なし）
    readonly Dictionary<int, HotkeyAction> _registered = new();
    IntPtr _hwnd;
    HwndSource? _hook;
    int _nextId = 0x5800;
    public IReadOnlyList<HotkeyAction> Actions => _actions;
    public event Action? Changed;
    bool _suspended;                       // 設定画面でキーを取得している間は、既存のホットキーを止める（押した操作が実行されないように）
    public bool PausedAll { get; private set; } // 「すべてのホットキーを止める」（ゲーム中に他のアプリと衝突するとき）
    public List<string> Failed { get; } = new(); // 起動時・変更時に登録できなかった（他のアプリが使用中の）もの
    const string PausedKey = "__paused";

    public HotkeyManager()
    {
        Current = this;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } d)
                foreach (var (k, v) in d) { if (k == PausedKey) PausedAll = v == "1"; else _bindings[k] = v; }
        }
        catch (Exception ex) { ObsEngine.Log("[hotkeys] load: " + ex.Message); }
    }

    public void Add(HotkeyAction a) => _actions.Add(a);

    public string BindingOf(string id) => _bindings.TryGetValue(id, out var v) ? v : _actions.FirstOrDefault(a => a.Id == id)?.Default ?? "";
    public static string LabelFor(string id) => Current?.BindingOf(id) is { Length: > 0 } b ? b : "";

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _hook = HwndSource.FromHwnd(_hwnd);
        _hook?.AddHook(WndProc);
        RegisterAll();
    }

    // 設定画面の間は既存のホットキーを止める（キーを押して割り当てるとき、配信開始などが動いてしまわないように）
    public void Suspend(bool on)
    {
        if (_suspended == on) return;
        _suspended = on;
        RegisterAll();
    }

    public void SetPausedAll(bool on)
    {
        PausedAll = on;
        RegisterAll(); Save(); Changed?.Invoke();
    }

    // 割り当てを変える。使えない（他のアプリが使用中・形式不正・重複）ときは理由を返す
    public string? Set(string id, string binding)
    {
        binding = binding.Trim();
        uint mods = 0, vk = 0;
        if (binding.Length > 0)
        {
            if (!TryParse(binding, out mods, out vk)) return "このキーは使えません";
            // 文字の比較ではなく（修飾キー, キー）の実体で重複を判定（Ctrl+Alt+1 と Alt+Ctrl+1 は同じ）
            var dup = _actions.FirstOrDefault(a => a.Id != id && TryParse(BindingOf(a.Id), out var m2, out var v2) && m2 == mods && v2 == vk);
            if (dup != null) return $"「{dup.Label}」ですでに使っています";
            // 他のアプリが使っていないか、いったん自分の登録を外して試す
            var was = _suspended;
            _suspended = true; RegisterAll();
            var free = CanRegister(mods, vk);
            _suspended = was; RegisterAll();
            if (!free) return "他のアプリがこのキーを使っています";
        }
        _bindings[id] = binding;
        RegisterAll();
        Save();
        Changed?.Invoke();
        return null;
    }

    bool CanRegister(uint mods, uint vk)
    {
        if (_hwnd == IntPtr.Zero) return true;
        const int probe = 0x57FF;
        if (!RegisterHotKey(_hwnd, probe, mods | MOD_NOREPEAT, vk)) return false;
        UnregisterHotKey(_hwnd, probe);
        return true;
    }

    public void ResetAll()
    {
        _bindings.Clear();
        RegisterAll(); Save(); Changed?.Invoke();
    }

    void Save()
    {
        try
        {
            var all = new Dictionary<string, string>(_bindings);
            if (PausedAll) all[PausedKey] = "1";
            File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { ObsEngine.Log("[hotkeys] save: " + ex.Message); }
    }

    void RegisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in _registered.Keys) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        if (_suspended || PausedAll) return;
        Failed.Clear();
        foreach (var a in _actions)
        {
            var b = BindingOf(a.Id);
            if (b.Length == 0 || !TryParse(b, out var mods, out var vk)) continue;
            var hid = _nextId++;
            if (_nextId > 0x5FFF) _nextId = 0x5800;
            if (RegisterHotKey(_hwnd, hid, mods | MOD_NOREPEAT, vk)) _registered[hid] = a;
            else { Failed.Add($"{a.Label}（{b}）"); ObsEngine.Log($"[hotkeys] {b} ({a.Label}) is used by another app"); }
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && !_suspended && !PausedAll && _registered.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a.Run(); } catch (Exception ex) { ObsEngine.Log($"[hotkeys] {a.Id}: {ex.Message}"); }
        }
        return IntPtr.Zero;
    }

    // "Ctrl+Alt+F5" → 修飾キーと仮想キー
    public static bool TryParse(string s, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                {
                    Key key;
                    if (part.Length == 1 && char.IsDigit(part[0])) { if (!Enum.TryParse("D" + part, out key)) return false; }
                    else if (!Enum.TryParse(part, true, out key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
                }
            }
        }
        return vk != 0 && mods != 0; // 修飾キー無しの単独キーは、ふだんの入力を奪うので許可しない
    }

    // 押されたキーを "Ctrl+Alt+1" の形に。修飾キーだけのときは null
    public static string? Format(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return null;
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1].ToString();
        parts.Add(name);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) foreach (var id in _registered.Keys) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _hook?.RemoveHook(WndProc);
        if (Current == this) Current = null;
    }

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    [DllImport("user32")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
