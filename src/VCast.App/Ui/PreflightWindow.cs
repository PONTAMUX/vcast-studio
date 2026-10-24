using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VCast.Core;
using VCast.Obs;

namespace VCast.Ui;

// 配信準備OKチェック: 3 秒間の実測（マイク・音割れ・描画の遅れ）と設定の確認。致命的なもの（キー未入力）は開始させない
public sealed class PreflightWindow : Window
{
    public sealed record Context(AppSettings Settings, AudioMixer Mixer, SceneManager Scenes, bool AvatarInScene, string AvatarModelStatus, bool Simple = false);
    enum Level { Ok, Warn, Block }
    record Item(Level Level, string Title, string Detail);

    readonly Context _c;
    readonly StackPanel _list = new();
    readonly TextBlock _head = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar _bar = new() { Height = 6, Maximum = 30, Margin = new Thickness(0, 10, 0, 10) };
    readonly Button _go = Theme.Button("このまま配信を開始", primary: true), _back = Theme.Button("やめる");
    public bool Proceed { get; private set; }

    public PreflightWindow(Context c)
    {
        _c = c;
        Title = "配信前チェック — VCast Studio";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;
        _go.IsEnabled = false;
        _go.Click += (_, _) => { Proceed = true; Close(); };
        _back.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(_back); footer.Children.Add(_go);
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(_head); body.Children.Add(_bar); body.Children.Add(_list); body.Children.Add(footer);
        Content = body;
        Loaded += async (_, _) => await Run();
    }

    async Task Run()
    {
        _head.Text = "確認中… 3 秒間、ふだんの声でマイクに話してみてください";
        var micStrip = _c.Mixer.Strips.FirstOrDefault(s => s.Source == _c.Mixer.Mic);
        var deskStrip = _c.Mixer.Strips.FirstOrDefault(s => s.Source == _c.Mixer.Desktop);
        float micMax = float.NegativeInfinity, deskMax = float.NegativeInfinity;
        var lagged0 = Native.obs_get_lagged_frames(); var total0 = Native.obs_get_total_frames();
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (micStrip != null) micMax = Math.Max(micMax, Math.Max(micStrip.Levels.L, micStrip.Levels.R));
            if (deskStrip != null) deskMax = Math.Max(deskMax, Math.Max(deskStrip.Levels.L, deskStrip.Levels.R));
            _bar.Value = i + 1;
        }
        var lagged = Native.obs_get_lagged_frames() - lagged0; var total = Math.Max(1, Native.obs_get_total_frames() - total0);

        var items = new List<Item>();
        // 配信先
        var dests = _c.Settings.Destinations.Where(d => d.Enabled).ToList();
        if (dests.Count == 0) items.Add(new(Level.Block, "配信先がありません", "「⚙ 設定」で配信先にチェックを入れてください"));
        foreach (var d in dests)
        {
            if (string.IsNullOrWhiteSpace(d.Key)) items.Add(new(Level.Block, $"{d.Name} のストリームキーが空です", "上の「⚙ 設定」でストリームキーを貼り付けてください"));
            else if (d.Service == "Custom" && !(d.ResolvedServer.StartsWith("rtmp://") || d.ResolvedServer.StartsWith("rtmps://")))
                items.Add(new(Level.Block, $"{d.Name} のサーバー URL が正しくありません", "rtmp:// か rtmps:// で始まる URL を入れてください"));
            else items.Add(new(Level.Ok, $"{d.Name} に配信します", dests.Count > 1 ? "同時配信" : ""));
        }
        // マイク
        if (micStrip == null) items.Add(new(Level.Warn, "マイクが見つかりません", _c.Simple ? "右の「② マイク」の一覧で、使うマイクを選んでください" : "ミキサーの ⚙ →「デバイスの選択」でマイクを選んでください"));
        else if (micStrip.Muted) items.Add(new(Level.Warn, "マイクがミュートになっています", _c.Simple ? "ミュートを解除してください（ホットキー Ctrl+Alt+M、または上級モードのミキサーの 🔇）" : "ミキサーの 🔇 を押して解除してください"));
        else if (micStrip.Monitoring == Native.OBS_MONITORING_TYPE_MONITOR_ONLY) items.Add(new(Level.Warn, "マイクが「自分だけ聞く」になっています", _c.Simple ? "このままだと声が配信に出ません。②の「🎧 自分の声を聞く」を押して切り替えてください" : "このままだと声が配信に出ません。ミキサーの 🎧 を「聞く」か「オフ」にしてください"));
        else if (micMax < -50) items.Add(new(Level.Warn, "マイクの音がほとんど入っていません", $"最大 {Db(micMax)}。マイクの接続と選択、音量スライダーを確認してください"));
        else if (micMax > -1) items.Add(new(Level.Warn, "マイクが音割れしています", _c.Simple ? $"最大 {Db(micMax)}。②の音量スライダーを下げるか、「🎙 声をきれいに」を押してください" : $"最大 {Db(micMax)}。フェーダーを下げるか、⚙ →「声をきれいにする」でリミッターを入れてください"));
        else if (micMax < -30) items.Add(new(Level.Warn, "マイクの声が小さめです", _c.Simple ? $"最大 {Db(micMax)}。聞き取りにくい場合はマイクに近づくか、「🎙 声をきれいに」を押してください" : $"最大 {Db(micMax)}。聞き取りにくい場合はマイクに近づくか、⚙ →「声をきれいにする」"));
        else items.Add(new(Level.Ok, "マイクの音量はちょうどいいです", $"最大 {Db(micMax)}"));
        // PC の音
        if (deskMax > -1) items.Add(new(Level.Warn, "PC の音が音割れしています", $"最大 {Db(deskMax)}。PC の音（ゲーム・BGM）の音量を下げてください"));
        // 映すもの
        if (_c.Scenes.Items.Count == 0) items.Add(new(Level.Warn, "シーンに何も映っていません", _c.Simple ? "右の「④ 映すもの」から、アバターやゲーム画面を追加してから配信しましょう" : "ソースを追加してから配信しましょう"));
        if (_c.AvatarInScene && !_c.AvatarModelStatus.StartsWith("表示中")) items.Add(new(Level.Warn, "アバターが表示されていません", $"「🧍 アバター」で VRM を選んでください（{_c.AvatarModelStatus}）"));
        // PC の負荷
        var enc = OutputManager.PickVideoEncoder(_c.Settings.VideoEncoder);
        if (OutputManager.IsHardware(enc)) items.Add(new(Level.Ok, "GPU で映像を圧縮します（軽い）", enc));
        else items.Add(new(Level.Warn, "CPU で映像を圧縮します（重め）", "カクつく場合は「⚙ 設定」で画質を「軽量」にしてください"));
        var lagPct = lagged * 100.0 / total;
        if (lagPct > 2) items.Add(new(Level.Warn, "PC の描画が追いついていません", $"直近 3 秒で {lagPct:0.#}% のコマ落ち。重いソースを減らすか、ゲームの画質を下げてください"));

        _list.Children.Clear();
        foreach (var it in items.OrderByDescending(i => i.Level)) _list.Children.Add(Row(it));
        var block = items.Any(i => i.Level == Level.Block);
        var warn = items.Count(i => i.Level == Level.Warn);
        _head.Text = block ? "このままでは配信できません" : warn > 0 ? $"気をつけたいことが {warn} 件あります" : "準備OK！いつでも配信できます";
        _head.Foreground = block ? Theme.Live : warn > 0 ? new SolidColorBrush(Color.FromRgb(0xE5, 0xA8, 0x4D)) : Theme.Accent;
        _go.IsEnabled = !block;
        _go.Content = warn > 0 ? "このまま配信を開始" : "配信を開始";
        _bar.Visibility = Visibility.Collapsed;
        ObsEngine.Log($"[preflight] block={block} warn={warn} mic={micMax:0.0} desk={deskMax:0.0} lag={lagPct:0.0}%");
    }

    static string Db(float v) => float.IsNegativeInfinity(v) ? "無音" : $"{v:0} dB";

    static FrameworkElement Row(Item it)
    {
        var (mark, color) = it.Level switch
        {
            Level.Ok => ("✓", Theme.Accent),
            Level.Warn => ("!", (Brush)new SolidColorBrush(Color.FromRgb(0xE5, 0xA8, 0x4D))),
            _ => ("✕", Theme.Live),
        };
        var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var m = new TextBlock { Text = mark, Foreground = color, FontWeight = FontWeights.Bold, Width = 22, FontSize = 15 };
        DockPanel.SetDock(m, Dock.Left);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = it.Title, TextWrapping = TextWrapping.Wrap });
        if (it.Detail.Length > 0) sp.Children.Add(new TextBlock { Text = it.Detail, Foreground = Theme.Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        dp.Children.Add(m); dp.Children.Add(sp);
        return dp;
    }
}
