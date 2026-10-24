using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace VCast.Ui;

// ドラッグ中にカーソルへ付いてくる「つまんでいる」カード。クリック透過・非アクティブの最前面ウィンドウ
public sealed class DragGhost : Window
{
    readonly Border _card;
    readonly TextBlock _hint;
    readonly RotateTransform _tilt = new(-4);
    readonly ScaleTransform _scale = new(0.85, 0.85);

    public DragGhost(ImageSource? icon, string title)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) row.Children.Add(new Image { Source = icon, Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = title.Length > 28 ? title[..28] + "…" : title, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        _hint = new TextBlock { Foreground = Theme.Sub, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(row); body.Children.Add(_hint);

        _card = new Border
        {
            Child = body, Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(14), CornerRadius = new CornerRadius(8),
            Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _scale, _tilt } },
        };
        Content = _card;
        SetOver(false);

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        // 持ち上げたときに少し膨らむ
        Loaded += (_, _) =>
        {
            var grow = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }

    // 追加できる場所の上にいるかどうかで見た目を変える
    public void SetOver(bool over)
    {
        _card.BorderBrush = over ? Theme.Accent : Theme.Line;
        _card.Opacity = over ? 1.0 : 0.7;
        _hint.Text = over ? "離して追加" : "プレビューの上まで運んでください";
        _hint.Foreground = over ? Theme.Accent : Theme.Sub;
        _tilt.Angle = over ? 0 : -4;
    }

    // 画面座標（物理ピクセル）でカーソルの少し右下に置く
    public void MoveTo(int screenX, int screenY, double dpiScale)
    {
        Left = screenX / dpiScale + 4;
        Top = screenY / dpiScale + 6;
    }

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    [DllImport("user32")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32")] static extern int SetWindowLong(IntPtr h, int idx, int val);
}
