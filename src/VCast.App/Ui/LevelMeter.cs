using System.Windows;
using System.Windows.Media;
using VCast.Core;

namespace VCast.Ui;

// 横向きのステレオ音量メーター。描画は OnRender だけ（レイアウトを走らせない）。
// 目盛りは -60〜0 dB、緑(〜-20) / 黄(〜-9) / 赤。ピークは 1.7 秒で 20dB 落ちる（OBS 準拠）＋ホールド線
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty StripProperty =
        DependencyProperty.Register(nameof(Strip), typeof(AudioStrip), typeof(LevelMeter), new PropertyMetadata(null));
    public AudioStrip? Strip { get => (AudioStrip?)GetValue(StripProperty); set => SetValue(StripProperty, value); }

    const double MinDb = -60;
    static readonly Brush Back = Frozen(Color.FromRgb(0x14, 0x14, 0x18));
    static readonly Brush Green = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50)), Yellow = Frozen(Color.FromRgb(0xE5, 0xC0, 0x3A)), Red = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    static readonly Brush GreenDim = Frozen(Color.FromRgb(0x1D, 0x4A, 0x25)), YellowDim = Frozen(Color.FromRgb(0x4D, 0x42, 0x19)), RedDim = Frozen(Color.FromRgb(0x4D, 0x1E, 0x20));
    static readonly Brush Hold = Frozen(Colors.White);

    readonly double[] _shown = { MinDb, MinDb }, _hold = { MinDb, MinDb };
    readonly long[] _holdAt = new long[2];
    long _last = Environment.TickCount64;
    public bool Muted { get; set; }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // UI タイマーから呼ぶ: 最新値を取り込み、減衰を計算して再描画
    public void Tick()
    {
        var s = Strip;
        if (s == null) return;
        var now = Environment.TickCount64;
        var dt = Math.Min(0.2, (now - _last) / 1000.0);
        _last = now;
        var (l, r, _, _) = s.Levels;
        Update(0, l, dt, now);
        Update(1, r, dt, now);
        InvalidateVisual();
    }

    void Update(int ch, float db, double dt, long now)
    {
        var v = float.IsNaN(db) || float.IsNegativeInfinity(db) ? MinDb : Math.Max(MinDb, Math.Min(0, db));
        var decayed = _shown[ch] - 20.0 / 1.7 * dt;
        _shown[ch] = Math.Max(v, decayed);
        if (v >= _hold[ch] || now - _holdAt[ch] > 1500) { _hold[ch] = v; _holdAt[ch] = now; }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Back, null, new Rect(0, 0, w, h));
        var bar = (h - 3) / 2;
        for (var ch = 0; ch < 2; ch++)
        {
            var y = 1 + ch * (bar + 1);
            DrawBar(dc, y, bar, w, _shown[ch]);
            var hx = X(_hold[ch], w);
            if (_hold[ch] > MinDb) dc.DrawRectangle(Hold, null, new Rect(Math.Max(0, hx - 1), y, 2, bar));
        }
    }

    void DrawBar(DrawingContext dc, double y, double hgt, double w, double db)
    {
        double yx = X(-20, w), rx = X(-9, w), level = X(db, w);
        // 背景（暗い色で区間を示す）
        dc.DrawRectangle(GreenDim, null, new Rect(0, y, yx, hgt));
        dc.DrawRectangle(YellowDim, null, new Rect(yx, y, rx - yx, hgt));
        dc.DrawRectangle(RedDim, null, new Rect(rx, y, w - rx, hgt));
        if (Muted) return; // ミュート中は暗い帯だけ
        dc.DrawRectangle(Green, null, new Rect(0, y, Math.Min(level, yx), hgt));
        if (level > yx) dc.DrawRectangle(Yellow, null, new Rect(yx, y, Math.Min(level, rx) - yx, hgt));
        if (level > rx) dc.DrawRectangle(Red, null, new Rect(rx, y, level - rx, hgt));
    }

    static double X(double db, double w) => Math.Clamp((db - MinDb) / -MinDb, 0, 1) * w;
}
