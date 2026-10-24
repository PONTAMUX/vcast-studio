using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VCast.Avatar;

namespace VCast.Ui;

// アバターの設定パネル（非モーダル。配信しながら表情ボタンを押せるように開きっぱなしにできる）
public sealed class AvatarWindow : Window
{
    readonly AvatarController _av;
    readonly Action _addToScene;
    readonly Func<bool> _inScene;
    readonly Action _applyRes;
    readonly TextBlock _model = Theme.Label("", sub: true), _renderState = Theme.Label("", sub: true), _trackerState = Theme.Label("", sub: true), _vmcState = Theme.Label("", sub: true);
    readonly ComboBox _cameras = new() { MinWidth = 220 };
    readonly CheckBox _walk, _walkMouse, _camOn, _lipsync, _inputLink, _vmc, _vmcLan, _mirror;
    readonly ComboBox _framing = new() { Width = 140 };
    readonly Button _add;
    readonly WrapPanel _exprs = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _saveLater = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool _loading;

    public AvatarWindow(AvatarController av, Action addToScene, Func<bool> inScene, Action? applyRes = null)
    {
        _av = av; _addToScene = addToScene; _inScene = inScene; _applyRes = applyRes ?? (() => { });
        Title = "3Dアバター — VCast Studio";
        Width = 500; Height = 760; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;
        // プレビューを隠さないよう、メイン画面の右端に寄せて開く
        Loaded += (_, _) =>
        {
            if (Owner == null) return;
            var wa = SystemParameters.WorkArea;
            Left = Math.Min(Owner.Left + Owner.ActualWidth - Width - 12, wa.Right - Width);
            Top = Math.Max(wa.Top, Owner.Top + 60);
            Height = Math.Min(Height, wa.Bottom - Top - 12);
        };
        _saveLater.Tick += (_, _) => { _saveLater.Stop(); _av.ApplyConfig(); };

        var body = new StackPanel { Margin = new Thickness(18, 12, 18, 18) };

        // モデル
        body.Children.Add(Header("モデル（VRM）"));
        var pick = Theme.Button("VRM ファイルを選ぶ…", primary: true); pick.Margin = new Thickness(0, 0, 8, 0);
        pick.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "VRM モデル|*.vrm|すべてのファイル|*.*", RestoreDirectory = true };
            if (d.ShowDialog(this) != true) return;
            _av.SetModel(d.FileName);
            if (!_inScene()) _addToScene(); // 選んだらすぐ配信画面に出す
            Refresh();
        };
        _add = Theme.Button("配信画面に追加");
        _add.ToolTip = "いまのシーンにアバターを置きます（右下に立った構図）";
        _add.Click += (_, _) => { _addToScene(); Refresh(); };
        body.Children.Add(Line(pick, _add));
        body.Children.Add(_model);
        body.Children.Add(_renderState);

        // 見た目
        body.Children.Add(Header("映し方"));
        _framing.Items.Add(new ComboBoxItem { Content = "胸から上", Tag = "bust" });
        _framing.Items.Add(new ComboBoxItem { Content = "全身", Tag = "full" });
        _framing.SelectionChanged += (_, _) => { if (_loading) return; _av.Settings.Framing = (string)((ComboBoxItem)_framing.SelectedItem).Tag; _av.ApplyConfig(); };
        body.Children.Add(Line(Theme.Label("構図　"), _framing));
        body.Children.Add(SliderRow("ズーム", 0.5, 2.5, () => _av.Settings.Zoom, v => _av.Settings.Zoom = v, "0.00x"));
        body.Children.Add(SliderRow("高さ", -0.3, 0.3, () => _av.Settings.CameraHeight, v => _av.Settings.CameraHeight = v, "+0.00;-0.00"));
        body.Children.Add(SliderRow("回り込み", -45, 45, () => _av.Settings.CameraYaw, v => _av.Settings.CameraYaw = v, "+0°;-0°"));
        var hi = Check("高精細で描く（拡大しても綺麗。GPU を少し多く使います）", "描画の細かさを 2 倍にします。画面上の大きさは変わりません");
        hi.IsChecked = _av.Settings.HighRes;
        hi.Click += (_, _) => { _av.Settings.HighRes = hi.IsChecked == true; _av.ApplyConfig(); _applyRes(); };
        body.Children.Add(hi);
        var nat = Check("ネイティブ描画（実験。ブラウザ部品を使わず直接描く。軽いが、まだ試験中）", "うまく映らないときはオフに戻せます。ブラウザ方式のほうが安定しています");
        nat.IsChecked = _av.Settings.NativeRender;
        nat.Click += (_, _) => { _av.Settings.NativeRender = nat.IsChecked == true; _av.ApplyConfig(); _applyRes(); Refresh(); };
        body.Children.Add(nat);
        body.Children.Add(SliderRow("明るさ", 0.4, 2.0, () => _av.Settings.Brightness, v => _av.Settings.Brightness = v, "0.00"));

        // 動かし方
        body.Children.Add(Header("動かし方"));
        _camOn = Check("Webカメラで顔の動き・表情を追う", "カメラ映像はこの PC の中だけで処理し、どこにも送りません");
        _camOn.Click += (_, _) => { _av.Settings.CameraEnabled = _camOn.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_camOn);
        _cameras.SelectionChanged += (_, _) =>
        {
            if (_loading || _cameras.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            _av.Settings.CameraId = id; _av.ApplyConfig();
        };
        var check = Theme.Button("カメラ確認"); check.Margin = new Thickness(8, 0, 0, 0);
        check.ToolTip = "カメラの映り具合を画面右下に表示します";
        check.Click += (_, _) => _av.ShowCameraCheck(true);
        var calib = Theme.Button("正面リセット"); calib.Margin = new Thickness(8, 0, 0, 0);
        calib.ToolTip = "いまの顔の向きを「正面」として覚え直します（カメラがモニターの上や横にあるときに）";
        calib.Click += (_, _) => _av.Calibrate();
        body.Children.Add(Line(_cameras, check, calib));
        body.Children.Add(_trackerState);
        _mirror = Check("鏡のように動かす（自分が右を向くとアバターは画面の右を向く）", "オフにすると視聴者から見た向きに合わせます");
        _mirror.Click += (_, _) => { _av.Settings.Mirror = _mirror.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_mirror);
        body.Children.Add(SliderRow("頭の動きの大きさ", 0.5, 2.0, () => _av.Settings.HeadSensitivity, v => _av.Settings.HeadSensitivity = v, "0.0x"));
        _lipsync = Check("マイクの声で口を動かす（カメラがないとき）", "カメラなしでも、喋ると口が動き、まばたき・目線・呼吸は自動で入ります");
        _lipsync.Click += (_, _) => { _av.Settings.LipsyncMic = _lipsync.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_lipsync);
        _inputLink = Check("キーボード・マウスの操作に合わせて手を動かす", "左右どちらのキーを打っているかだけを見ます。どのキーを押したかは記録しません");
        _inputLink.Click += (_, _) => { _av.Settings.InputLink = _inputLink.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_inputLink);
        _walk = Check("WASD 歩行を使う（右パネルに「WASD ON/OFF」ボタンが出ます）", "ボタンを ON にすると、W/A/S/D（Shift で走る）でアバターが画面内を歩き回ります（他のアプリを使っている間も反応します）。ホットキー（設定の「ホットキー」）でもオン・オフできます。ブラウザ描画のみ対応");
        _walk.Click += (_, _) => { _av.Settings.WalkEnabled = _walk.IsChecked == true; if (!_av.Settings.WalkEnabled) _av.Settings.WalkMode = false; _av.ApplyConfig(); _applyRes(); };
        body.Children.Add(_walk);
        _walkMouse = Check("マウスでも歩かせる（左ボタンを押している間、カーソルの位置へ歩く）", "歩かせるモード中だけ有効です。ゲームなど他のアプリで左クリックしている間も反応するので、邪魔なときはオフにしてください");
        _walkMouse.Margin = new Thickness(24, 0, 0, 4);
        _walkMouse.Click += (_, _) => { _av.Settings.WalkMouse = _walkMouse.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_walkMouse);
        _vmc = Check($"VMC で受信する（VSeeFace などのトラッカーを使う・ポート {_av.Settings.VmcPort}）", "受信中はカメラ・待機モーションより VMC の動きが優先されます（頭の向きはカメラ優先）");
        _vmc.Click += (_, _) => { _av.Settings.VmcEnabled = _vmc.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_vmc);
        _vmcLan = Check("スマホなど同じネットワークの機器からも受け取る", "iPhone のトラッキングアプリなどから送るとき。オフならこの PC 内のみ");
        _vmcLan.Margin = new Thickness(24, 0, 0, 4);
        _vmcLan.Click += (_, _) => { _av.Settings.VmcFromLan = _vmcLan.IsChecked == true; _av.ApplyConfig(); };
        body.Children.Add(_vmcLan);
        body.Children.Add(_vmcState);

        // 表情
        body.Children.Add(Header("表情（どのアプリを使っていてもホットキーで切り替え）"));
        for (var i = 0; i < AvatarController.Expressions.Length; i++)
        {
            var (id, label) = AvatarController.Expressions[i];
            var b = new Button
            {
                Width = 138, Height = 44, Margin = new Thickness(0, 0, 6, 6), Tag = id,
                Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line,
                Content = new StackPanel { Children = { new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = AvatarController.HotkeyLabel(i), FontSize = 11, Foreground = Theme.Sub, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            b.Click += (_, _) => _av.SetExpression(id);
            _exprs.Children.Add(b);
        }
        body.Children.Add(_exprs);
        body.Children.Add(SliderRow("表情の強さ", 0.3, 1.0, () => _av.Settings.ExpressionStrength, v => _av.Settings.ExpressionStrength = v, "0%"));

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _av.StatusChanged += Refresh;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); if (_saveLater.IsEnabled) { _saveLater.Stop(); _av.ApplyConfig(); } _av.StatusChanged -= Refresh; };
        Refresh();
    }

    // スライダー: 動かしている間は描画ページにだけ送り、止まってから保存
    FrameworkElement SliderRow(string label, double min, double max, Func<double> get, Action<double> set, string fmt)
    {
        var value = new TextBlock { Width = 60, Foreground = Theme.Sub, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var s = new Slider { Minimum = min, Maximum = max, Value = get(), Width = 210, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        value.Text = get().ToString(fmt);
        s.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            value.Text = e.NewValue.ToString(fmt);
            _av.PreviewConfig();
            _saveLater.Stop(); _saveLater.Start();
        };
        var l = Theme.Label(label); l.Width = 120;
        return Line(l, s, value);
    }

    void Refresh()
    {
        _loading = true;
        var s = _av.Settings;
        _model.Text = s.ModelPath == null ? "まず VRM ファイルを選んでください" : $"{Path.GetFileName(s.ModelPath)} — {_av.ModelStatus}";
        var inScene = _inScene();
        _add.IsEnabled = s.ModelPath != null && !inScene; // モデル未選択だと空の枠ができるだけなので押せない
        _renderState.Text = _av.RenderConnected ? "● 配信画面に表示中" : inScene ? "○ 表示を準備中…" : "○ まだ配信画面に置かれていません";
        _renderState.Foreground = _av.RenderConnected ? Theme.Accent : Theme.Sub;
        _camOn.IsChecked = s.CameraEnabled;
        _mirror.IsChecked = s.Mirror;
        _lipsync.IsChecked = s.LipsyncMic;
        _inputLink.IsChecked = s.InputLink;
        _walk.IsChecked = s.WalkEnabled;
        _walkMouse.IsChecked = s.WalkMouse;
        _walkMouse.IsEnabled = s.WalkEnabled;
        _vmc.IsChecked = s.VmcEnabled;
        _vmcLan.IsChecked = s.VmcFromLan;
        _vmcLan.IsEnabled = s.VmcEnabled;
        _vmcState.Text = s.VmcEnabled ? $"VMC 受信パケット: {_av.VmcPackets}" : "";
        _trackerState.Text = s.CameraEnabled ? "カメラ: " + _av.TrackerStatus : "";
        foreach (ComboBoxItem it in _framing.Items) if ((string)it.Tag == s.Framing) _framing.SelectedItem = it;

        // カメラ一覧（トラッカーが報告したもの。初回はカメラをオンにすると埋まる）
        var ids = _cameras.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
        var fresh = new List<string> { "" };
        fresh.AddRange(_av.Cameras.Select(c => c.Id));
        if (!ids.SequenceEqual(fresh))
        {
            _cameras.Items.Clear();
            _cameras.Items.Add(new ComboBoxItem { Content = "既定のカメラ", Tag = "" });
            foreach (var c in _av.Cameras) _cameras.Items.Add(new ComboBoxItem { Content = c.Label, Tag = c.Id });
        }
        _cameras.SelectedItem = _cameras.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.CameraId) ?? _cameras.Items[0];
        _cameras.IsEnabled = s.CameraEnabled;

        foreach (Button b in _exprs.Children)
            b.BorderBrush = (string)b.Tag == _av.Expression ? Theme.Accent : Theme.Line;
        _loading = false;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };
    static CheckBox Check(string text, string tip) => new() { Content = text, ToolTip = tip, Foreground = Theme.Fg, Margin = new Thickness(0, 4, 0, 4) };
    static StackPanel Line(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }
}
