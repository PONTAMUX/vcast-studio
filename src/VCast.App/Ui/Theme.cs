using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VCast.Ui;

// コードで組む画面用の共通スタイル（App.xaml のブラシと同じ色）
public static class Theme
{
    public static readonly SolidColorBrush Bg = Brush("#16161A"), Panel = Brush("#1F1F25"), Input = Brush("#26262E"),
        Line = Brush("#34343E"), Record = Brush("#D9822B"), Fg = Brush("#E6E6EA"), Sub = Brush("#8C8C99"), Accent = Brush("#7C5CFF"), Live = Brush("#E5484D");

    static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Button Button(string text, bool primary = false) => new()
    {
        Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(6, 0, 0, 0), MinWidth = 64,
        Background = primary ? Accent : Input, Foreground = Fg, BorderBrush = primary ? Accent : Line,
    };

    public static TextBlock Label(string text, bool sub = false) => new()
    {
        Text = text, Foreground = sub ? Sub : Fg, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static void StyleInput(Control c)
    {
        c.Padding = new Thickness(6, 4, 6, 4);
        c.BorderBrush = Line;
        if (c is ComboBox) return; // ComboBox は既定テンプレートの背景を使う（暗色にすると矢印が見えない）
        c.Background = Input; c.Foreground = Fg;
        if (c is TextBox tb) tb.CaretBrush = Fg;
        if (c is PasswordBox pb) pb.CaretBrush = Fg;
    }
}
