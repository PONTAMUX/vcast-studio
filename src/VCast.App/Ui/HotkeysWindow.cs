using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VCast.Core;

namespace VCast.Ui;

// ホットキーの設定: 行のボタンを押して、割り当てたいキーの組み合わせを押す（Esc でやめる / Delete で外す）
public sealed class HotkeysWindow : Window
{
    readonly HotkeyManager _hk;
    readonly Dictionary<string, Button> _buttons = new();
    readonly TextBlock _msg = Theme.Label("", sub: true);
    string? _capturing;

    public HotkeysWindow(HotkeyManager hk)
    {
        _hk = hk;
        Title = "ホットキー — VCast Studio";
        Width = 540; Height = 640; MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        // キーを取得している間に、既存のホットキー（配信開始など）が動かないよう止める
        var failed = _hk.Failed.ToList();
        _hk.Suspend(true);
        Closed += (_, _) => _hk.Suspend(false);

        var body = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
        body.Children.Add(Theme.Label("どのアプリを使っていても効くキーです。右のボタンを押して、割り当てたいキーを押してください。", sub: true));
        _msg.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_msg);
        if (failed.Count > 0)
        {
            var warn = Theme.Label("次のキーは他のアプリが使っていて登録できていません: " + string.Join("、", failed), sub: false);
            warn.Foreground = Theme.Live; warn.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(warn);
        }
        var pause = new CheckBox { Content = "すべてのホットキーを止める（ゲーム中に他のアプリのキーと重なるとき）", IsChecked = _hk.PausedAll, Foreground = Theme.Fg, Margin = new Thickness(0, 10, 0, 0) };
        pause.Click += (_, _) => _hk.SetPausedAll(pause.IsChecked == true);
        body.Children.Add(pause);

        foreach (var group in _hk.Actions.GroupBy(a => a.Group))
        {
            body.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 4) });
            foreach (var a in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var b = new Button { Width = 170, Height = 28, Tag = a.Id, Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line, ToolTip = "クリックして、キーを押す" };
                DockPanel.SetDock(b, Dock.Right);
                b.Click += (_, _) => BeginCapture(a.Id);
                _buttons[a.Id] = b;
                row.Children.Add(b);
                row.Children.Add(new TextBlock { Text = a.Label, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Fg });
                body.Children.Add(row);
            }
        }

        var reset = Theme.Button("すべて初期値に戻す"); reset.HorizontalAlignment = HorizontalAlignment.Left; reset.Margin = new Thickness(0, 16, 0, 0);
        reset.Click += (_, _) => { _hk.ResetAll(); Refresh(); _msg.Text = "初期値に戻しました"; };
        body.Children.Add(reset);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => EndCapture();
        Refresh();
    }

    void Refresh()
    {
        foreach (var (id, b) in _buttons)
        {
            var v = _hk.BindingOf(id);
            var capturing = id == _capturing;
            b.Content = capturing ? "キーを押してください…" : v.Length == 0 ? "（なし）" : v;
            b.Background = capturing ? Theme.Accent : Theme.Input;
            b.Foreground = v.Length == 0 && !capturing ? Theme.Sub : Theme.Fg;
        }
    }

    void BeginCapture(string id) { _capturing = id; _msg.Text = "割り当てたいキーを押してください（Esc: やめる / Delete: 外す）。Ctrl・Alt・Shift のどれかと一緒に押します"; Refresh(); }
    void EndCapture() { if (_capturing == null) return; _capturing = null; Refresh(); }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_capturing == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape) { _msg.Text = ""; EndCapture(); return; }
        if (key is Key.Delete or Key.Back)
        {
            _hk.Set(_capturing, ""); _msg.Text = "外しました"; EndCapture(); return;
        }
        var text = HotkeyManager.Format(key, Keyboard.Modifiers);
        if (text == null) { if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)) _msg.Text = "Ctrl・Alt・Shift のどれかと一緒に押してください"; return; }
        var err = _hk.Set(_capturing, text);
        _msg.Text = err ?? $"{text} に設定しました";
        _msg.Foreground = err == null ? Theme.Sub : Theme.Live;
        EndCapture();
    }
}
