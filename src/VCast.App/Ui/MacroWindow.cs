using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VCast.Avatar;
using VCast.Core;

namespace VCast.Ui;

// マクロ: 1 回の操作でまとめて実行する（シーン切り替え＋表情＋マイク＋効果音＋録画）。スロットは 6 つで、それぞれにホットキーを割り当てられる（設定の「ホットキー」→ マクロ）
public sealed class MacroWindow : Window
{
    readonly AppSettings _s; readonly Func<IEnumerable<string>> _scenes; readonly Action<int> _run; readonly Action _save;
    bool _loading = true;

    public MacroWindow(AppSettings s, Func<IEnumerable<string>> sceneNames, Action<int> run, Action save)
    {
        _s = s; _scenes = sceneNames; _run = run; _save = save;
        Title = "マクロ — VCast Studio";
        Width = 880; Height = 460; MinWidth = 700;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;
        Closing += (_, e) => { if (!_force) { e.Cancel = true; Hide(); } };
        while (_s.Macros.Count < HotkeyCount) _s.Macros.Add(new MacroDef { Name = $"マクロ {_s.Macros.Count + 1}" });

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "1 つのボタンで、まとめて実行します（たとえば「休憩に入る」= 休憩シーンへ + マイクをミュート + 表情を「おだやか」）。何も選ばない項目は、何もしません。", Foreground = Theme.Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        var grid = new Grid();
        string[] heads = { "名前", "シーンへ移動", "表情", "マイク", "効果音", "録画", "" };
        double[] widths = { 150, 150, 90, 100, 80, 80, 70 };
        for (var i = 0; i < heads.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i] + 8) });
            var h = Theme.Label(heads[i], sub: true); Grid.SetColumn(h, i); grid.Children.Add(h);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < HotkeyCount; r++)
        {
            var m = _s.Macros[r]; var row = r + 1;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Put(UIElement e, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); ((FrameworkElement)e).Margin = new Thickness(0, 3, 8, 3); grid.Children.Add(e); }

            var name = new TextBox { Text = m.Name }; Theme.StyleInput(name);
            name.LostFocus += (_, _) => { m.Name = name.Text; _save(); };
            Put(name, 0);
            var sc = Combo(new[] { "" }.Concat(_scenes()).ToArray(), m.Scene, v => m.Scene = v, "（そのまま）"); Put(sc, 1);
            var ex = new ComboBox(); ex.Items.Add(new ComboBoxItem { Content = "（そのまま）", Tag = "" });
            foreach (var (id, label) in AvatarController.Expressions) ex.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            Pick(ex, m.Expression); ex.SelectionChanged += (_, _) => { if (!_loading && ex.SelectedItem is ComboBoxItem { Tag: string t }) { m.Expression = t; _save(); } };
            Put(ex, 2);
            Put(Combo(new[] { "", "mute", "unmute" }, m.Mic, v => m.Mic = v, "（そのまま）", new Dictionary<string, string> { ["mute"] = "ミュート", ["unmute"] = "ミュート解除" }), 3);
            Put(Combo(new[] { "" }.Concat(Enumerable.Range(1, Soundboard.PadCount).Select(i => i.ToString())).ToArray(), m.Pad > 0 ? m.Pad.ToString() : "", v => m.Pad = int.TryParse(v, out var n) ? n : 0, "なし", null, "パッド "), 4);
            Put(Combo(new[] { "", "start", "stop" }, m.Record, v => m.Record = v, "（そのまま）", new Dictionary<string, string> { ["start"] = "開始", ["stop"] = "停止" }), 5);
            var go = Theme.Button("▶ 実行", true); var idx = r; go.Margin = new Thickness(0, 3, 0, 3); go.Click += (_, _) => _run(idx); Put(go, 6);
        }
        root.Children.Add(grid);
        root.Children.Add(new TextBlock { Text = "ホットキーは、設定の「ホットキー」の「マクロ」グループで割り当てます。", Foreground = Theme.Sub, Margin = new Thickness(0, 10, 0, 0) });
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _loading = false;
    }

    public const int HotkeyCount = 6;
    bool _force;
    public void CloseForReal() { _force = true; Close(); }

    ComboBox Combo(string[] values, string current, Action<string> set, string emptyLabel, Dictionary<string, string>? labels = null, string prefix = "")
    {
        var c = new ComboBox();
        foreach (var v in values)
            c.Items.Add(new ComboBoxItem { Content = v.Length == 0 ? emptyLabel : (labels != null && labels.TryGetValue(v, out var l) ? l : prefix + v), Tag = v });
        Pick(c, current);
        c.SelectionChanged += (_, _) => { if (!_loading && c.SelectedItem is ComboBoxItem { Tag: string t }) { set(t); _save(); } };
        return c;
    }

    static void Pick(ComboBox c, string tag)
    {
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? c.Items[0];
    }
}
