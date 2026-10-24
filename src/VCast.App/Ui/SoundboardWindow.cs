using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VCast.Avatar;
using VCast.Core;

namespace VCast.Ui;

// サウンドボード: クリックで鳴らす、右クリックで設定。開いたまま配信できる非モーダル
public sealed class SoundboardWindow : Window
{
    readonly Soundboard _sb;
    readonly UniformGrid _grid = new() { Columns = 5, Rows = 2 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public SoundboardWindow(Soundboard sb)
    {
        _sb = sb;
        Title = "サウンドボード — VCast Studio";
        Width = 720; Height = 330; MinWidth = 520; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var stop = Theme.Button("■ 全部止める"); stop.Click += (_, _) => _sb.StopAll();
        var hint = Theme.Label("クリックで鳴らす（もう一度で止める）・右クリックで音と表情を設定・どのアプリを使っていてもホットキーで鳴ります", sub: true);
        hint.FontSize = 11; hint.Margin = new Thickness(10, 0, 0, 0);
        var top = new DockPanel { Margin = new Thickness(12, 10, 12, 6) };
        DockPanel.SetDock(stop, Dock.Left);
        top.Children.Add(stop); top.Children.Add(hint);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _grid.Margin = new Thickness(8, 0, 8, 10);
        root.Children.Add(_grid);
        Content = root;

        Build();
        _sb.Changed += Refresh;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _sb.Changed -= Refresh; };
    }

    void Build()
    {
        _grid.Children.Clear();
        for (var i = 0; i < Soundboard.PadCount; i++)
        {
            var idx = i;
            var b = new Button { Margin = new Thickness(4), Foreground = Theme.Fg, BorderThickness = new Thickness(2), Tag = idx };
            b.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(_sb.Pads[idx].File)) Edit(idx); else _sb.Play(idx);
            };
            b.MouseRightButtonUp += (_, e) => { Edit(idx); e.Handled = true; };
            _grid.Children.Add(b);
        }
        Refresh();
    }

    void Refresh()
    {
        foreach (Button b in _grid.Children)
        {
            var i = (int)b.Tag;
            var pad = _sb.Pads[i];
            var empty = string.IsNullOrEmpty(pad.File);
            var playing = _sb.IsPlaying(i);
            b.Background = playing ? Theme.Accent : (Brush)new BrushConverter().ConvertFrom(pad.Color)!;
            b.BorderBrush = playing ? Brushes.White : Theme.Line;
            b.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = empty ? "＋ 音を入れる" : (pad.Name.Length > 0 ? pad.Name : Path.GetFileNameWithoutExtension(pad.File)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = empty ? Theme.Sub : Theme.Fg },
                    new TextBlock { Text = Soundboard.HotkeyLabel(i) + (pad.Expression.Length > 0 ? "  😊" : ""), FontSize = 11, Foreground = Theme.Sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) },
                },
            };
        }
    }

    void Edit(int i)
    {
        var pad = _sb.Pads[i];
        var w = new Window
        {
            Title = $"パッド {i + 1} の設定", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Bg, Foreground = Theme.Fg, FontFamily = FontFamily, FontSize = 13,
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        var file = new TextBox { Text = pad.File, IsReadOnly = true }; Theme.StyleInput(file);
        var browse = Theme.Button("選ぶ…");
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "音声・動画|*.mp3;*.wav;*.ogg;*.opus;*.flac;*.m4a;*.aac;*.mp4;*.webm;*.mkv;*.mov|すべてのファイル|*.*", RestoreDirectory = true };
            if (d.ShowDialog(w) == true) file.Text = d.FileName;
        };
        var fl = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); fl.Children.Add(browse); fl.Children.Add(file);
        var name = new TextBox { Text = pad.Name }; Theme.StyleInput(name);
        var vol = new Slider { Minimum = 0, Maximum = 1, Value = pad.Volume, IsMoveToPointEnabled = true };
        var expr = new ComboBox(); Theme.StyleInput(expr);
        expr.Items.Add(new ComboBoxItem { Content = "変えない", Tag = "" });
        foreach (var (id, label) in AvatarController.Expressions.Where(e => e.Id != "neutral")) expr.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        expr.SelectedItem = expr.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == pad.Expression) ?? expr.Items[0];
        var colors = new[] { ("#26262E", "黒"), ("#3B2F77", "紫"), ("#1E4D6B", "青"), ("#1D5B3A", "緑"), ("#6B4A1E", "茶"), ("#6B1E2E", "赤") };
        var color = new ComboBox(); Theme.StyleInput(color);
        foreach (var (hex, label) in colors) color.Items.Add(new ComboBoxItem { Content = label, Tag = hex });
        color.SelectedItem = color.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == pad.Color) ?? color.Items[0];

        void Row(string label, UIElement input) { sp.Children.Add(new TextBlock { Text = label, Foreground = Theme.Sub, Margin = new Thickness(0, 8, 0, 3) }); sp.Children.Add(input); }
        Row("音・動画ファイル", fl);
        Row("表示名（空ならファイル名）", name);
        Row("音量", vol);
        Row("鳴らしたときのアバターの表情（鳴り終わると戻る）", expr);
        Row("色", color);
        var ok = Theme.Button("保存", primary: true); ok.IsDefault = true;
        var clear = Theme.Button("このパッドを空にする");
        var cancel = Theme.Button("キャンセル"); cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            pad.File = file.Text; pad.Name = name.Text.Trim(); pad.Volume = vol.Value;
            pad.Expression = (string)((ComboBoxItem)expr.SelectedItem).Tag; pad.Color = (string)((ComboBoxItem)color.SelectedItem).Tag;
            w.DialogResult = true;
        };
        clear.Click += (_, _) => { _sb.Pads[i] = new SoundPad(); w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(clear); buttons.Children.Add(cancel); buttons.Children.Add(ok);
        sp.Children.Add(buttons);
        w.Content = sp;
        if (w.ShowDialog() == true) { _sb.Prepare(i); _sb.Save(); Refresh(); }
    }
}
