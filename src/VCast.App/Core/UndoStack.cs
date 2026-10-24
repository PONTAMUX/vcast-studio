namespace VCast.Core;

// 元に戻す（Ctrl+Z）。操作ごとに「元に戻す処理」を積む。やり直しは v1 では持たない
public sealed class UndoStack
{
    const int Max = 50;
    readonly LinkedList<(string Label, Action Undo)> _items = new();
    public event Action? Changed;

    public void Push(string label, Action undo)
    {
        _items.AddLast((label, undo));
        if (_items.Count > Max) _items.RemoveFirst();
        Changed?.Invoke();
    }

    public string? NextLabel => _items.Last?.Value.Label;

    public string? Undo()
    {
        if (_items.Last is not { } last) return null;
        _items.RemoveLast();
        try { last.Value.Undo(); }
        catch (Exception ex) { VCast.Obs.ObsEngine.Log("[undo] " + ex.Message); }
        Changed?.Invoke();
        return last.Value.Label;
    }

    public void Clear() { _items.Clear(); Changed?.Invoke(); }
}
