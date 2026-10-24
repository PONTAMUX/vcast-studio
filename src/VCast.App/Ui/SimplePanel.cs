using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VCast.Avatar;
using VCast.Core;
using VCast.Obs;

namespace VCast.Ui;

// かんたんモードの右パネル: 「アバター」「マイク」「映すもの」「配信先」の 4 枚だけ。細かい設定はプロモードへ
public sealed class SimplePanel : ScrollViewer
{
    public sealed record Actions(
        Action OpenAvatar, Action PickModel, Action AddAvatar, Action<string> AddSourceType, Action AddFiles, Action OpenSettings,
        Action<LevelMeter> RegisterMeter, Action<LevelMeter> UnregisterMeter, Func<IEnumerable<IntPtr>> DuckTargets, Func<string> MicName);

    readonly AvatarController _av;
    readonly AudioMixer _mixer;
    readonly SceneManager _scenes;
    readonly AppSettings _settings;
    readonly Actions _act;
    readonly StackPanel _root = new() { Margin = new Thickness(12, 10, 12, 12) };
    readonly TextBlock _avatarState = Theme.Label("", sub: true), _destState = Theme.Label("", sub: true);
    readonly Button _walkBtn = SmallToggle("🚶 WASD OFF");
    readonly CheckBox _cam = new() { Content = "Webカメラで顔を追う", Foreground = Theme.Fg };
    readonly ComboBox _micDevice = new();
    readonly LevelMeter _micMeter = new() { Height = 14, Margin = new Thickness(0, 6, 0, 6) };
    readonly Slider _micVol = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true };
    readonly Button _clean = SmallToggle("🎙 声をきれいに"), _listen = SmallToggle("🎧 自分の声を聞く"), _duck = SmallToggle("🎵 喋るとBGMを下げる");
    bool _cleanOn, _listenOn, _duckOn;
    readonly WrapPanel _sceneButtons = new();
    readonly StackPanel _sceneBlock = new();
    readonly List<Button> _exprButtons = new();
    readonly TextBlock _step1 = new(), _step2 = new(), _step3 = new(), _stepsDone = new();
    Border? _onboarding;
    bool _micHeard;
    readonly System.Windows.Threading.DispatcherTimer _stepTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly StackPanel _items = new();
    bool _loading;

    public SimplePanel(AvatarController av, AudioMixer mixer, SceneManager scenes, AppSettings settings, Actions act)
    {
        _av = av; _mixer = mixer; _scenes = scenes; _settings = settings; _act = act;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Theme.Panel;
        Content = _root;

        // 1. アバター
        var pick = Theme.Button("VRM ファイルを選ぶ…", primary: true); pick.Margin = new Thickness(0, 0, 6, 0);
        pick.ToolTip = "あなたの 3D アバター（.vrm）を選ぶと、配信画面に出ます";
        pick.Click += (_, _) => _act.PickModel();
        var more = Theme.Button("アバターの細かい設定…"); more.Margin = new Thickness(0, 6, 0, 0); more.HorizontalAlignment = HorizontalAlignment.Left;
        more.Click += (_, _) => _act.OpenAvatar();
        pick.HorizontalAlignment = HorizontalAlignment.Stretch; pick.Margin = new Thickness(0);
        _walkBtn.ToolTip = "オンの間、W/A/S/D（Shift で走る）でアバターを歩かせます";
        _walkBtn.Click += (_, _) => _av.SetWalk(!_av.Settings.WalkMode, _scenes);
        _cam.Click += (_, _) => { _av.Settings.CameraEnabled = _cam.IsChecked == true; _av.ApplyConfig(); };
        var exprs = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (id, label) in AvatarController.Expressions)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3, 8, 3), Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line, Tag = id,
                ToolTip = $"表情: {label}（ホットキー Ctrl+Alt+{Array.FindIndex(AvatarController.Expressions, e => e.Id == id) + 1}）" };
            b.Click += (_, _) => _av.SetExpression(id);
            exprs.Children.Add(b);
            _exprButtons.Add(b);
        }
        // カードの並び: 1 アバター → 2 マイク → 3 配信先 → 4 映すもの（配信先が画面の下に隠れないように）
        _onboarding = BuildOnboarding();
        _root.Children.Add(_onboarding);
        _root.Children.Add(Card("1", "アバター", pick, _avatarState, _cam, _walkBtn, exprs, more));

        // 2. マイク
        _micDevice.SelectionChanged += (_, _) => { if (!_loading) SetMicDevice(); };
        Theme.StyleInput(_micDevice);
        _micVol.ValueChanged += (_, e) => { if (!_loading && MicStrip is { } s) s.Deflection = e.NewValue; };
        _clean.ToolTip = "雑音除去・ゲート・コンプ・リミッターをまとめてオン/オフ";
        _clean.Click += (_, _) => { ToggleClean(!_cleanOn); Refresh(); };
        _listen.ToolTip = "自分の声をヘッドホンで確認（配信の音には影響しません）";
        _listen.Click += (_, _) => { if (MicStrip is { } s) s.Monitoring = !_listenOn ? Native.OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT : Native.OBS_MONITORING_TYPE_NONE; Refresh(); };
        _micMeter.Loaded += (_, _) => _act.RegisterMeter(_micMeter);
        _micMeter.Unloaded += (_, _) => _act.UnregisterMeter(_micMeter);
        _duck.ToolTip = "喋っている間だけ PC の音・BGM・動画の音量を自動で下げます";
        _duck.Click += (_, _) => { foreach (var t in _act.DuckTargets()) Filters.SetDucking(t, _act.MicName(), !_duckOn); Refresh(); };
        var toggles = new WrapPanel(); toggles.Children.Add(_clean); toggles.Children.Add(_listen); toggles.Children.Add(_duck);
        _root.Children.Add(Card("2", "マイク", _micDevice, _micMeter, Labeled("音量", _micVol), toggles));

        // 3. 映すもの
        var add = new WrapPanel();
        void Big(string icon, string label, string tip, Action a)
        {
            var b = new Button { Width = 96, Height = 64, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip, Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line,
                Content = new StackPanel { Children = { new TextBlock { Text = icon, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } } } };
            b.Click += (_, _) => a();
            add.Children.Add(b);
        }
        Big("🎮", "ゲーム画面", SourceCatalog.Describe("game_capture"), () => _act.AddSourceType("game_capture"));
        Big("🖥", "画面全体", SourceCatalog.Describe("monitor_capture"), () => _act.AddSourceType("monitor_capture"));
        Big("🖼", "画像・動画", "画像・動画・音声ファイルを選んで追加", _act.AddFiles);
        Big("📷", "カメラ", SourceCatalog.Describe("dshow_input"), () => _act.AddSourceType("dshow_input"));
        Big("🔤", "文字", SourceCatalog.Describe("text_gdiplus_v3"), () => _act.AddSourceType("text_gdiplus_v3"));
        Big("🧍", "アバター", SourceCatalog.Describe(SourceCatalog.AvatarId), _act.AddAvatar);
        var hint = Theme.Label("ウィンドウは下の「開いているアプリ」から。プレビュー上でドラッグして位置・大きさを調整できます。", sub: true);
        hint.FontSize = 11;

        // 3. 配信先
        var dest = Theme.Button("配信設定（配信先・ストリームキー）…"); dest.HorizontalAlignment = HorizontalAlignment.Left;
        dest.Click += (_, _) => { _act.OpenSettings(); Refresh(); };
        _root.Children.Add(Card("3", "配信先", _destState, dest));

        // 4. 映すもの（場面が 1 つだけのときは「場面」の行を出さない。「シーン」という言葉に戸惑わせないため）
        _sceneBlock.Children.Add(Theme.Label("場面（ゲーム中・雑談などの切り替え）", sub: true));
        _sceneBlock.Children.Add(_sceneButtons);
        _root.Children.Add(Card("4", "映すもの", _sceneBlock, add, Theme.Label("いま映っているもの", sub: true), _items, hint));
        _stepTimer.Tick += (_, _) => UpdateSteps();
        _stepTimer.Start();

        _scenes.Scenes.CollectionChanged += (_, _) => Refresh();
        _scenes.Items.CollectionChanged += (_, _) => Refresh();
        _scenes.CurrentChanged += Refresh;
        _mixer.Strips.CollectionChanged += (_, _) => Refresh();
        _av.StatusChanged += Refresh;
        Loaded += (_, _) => Refresh();
    }

    // はじめの3ステップ（全部済んだら消える）
    Border BuildOnboarding()
    {
        foreach (var t in new[] { _step1, _step2, _step3 }) { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 3, 0, 3); }
        _stepsDone.TextWrapping = TextWrapping.Wrap; _stepsDone.Margin = new Thickness(0, 6, 0, 0); _stepsDone.Foreground = Theme.Accent; _stepsDone.FontWeight = FontWeights.SemiBold;
        var card = Card("✓", "はじめの3ステップ", _step1, _step2, _step3, _stepsDone);
        card.BorderBrush = Theme.Accent;
        return card;
    }

    void UpdateSteps()
    {
        if (_onboarding == null) return;
        if (_settings.OnboardingDone) { _onboarding.Visibility = Visibility.Collapsed; _stepTimer.Stop(); return; }
        var mic = MicStrip;
        if (!_micHeard && mic != null && Math.Max(mic.Levels.L, mic.Levels.R) > -50) _micHeard = true;
        var ok1 = _av.Settings.ModelPath != null && _av.RenderConnected;
        var ok3 = _settings.Destinations.Any(d => d.Enabled && !string.IsNullOrEmpty(d.KeyProtected));
        void Set(TextBlock t, bool ok, string done, string todo)
        {
            t.Text = (ok ? "✓  " : "○  ") + (ok ? done : todo);
            t.Foreground = ok ? Theme.Sub : Theme.Fg;
        }
        Set(_step1, ok1, "アバターを選んだ", "アバターを選ぶ（下の①「VRM ファイルを選ぶ」）");
        Set(_step2, _micHeard, "マイクの声を確認できた", "マイクに話してみる（②のバーが緑に動けば OK）");
        Set(_step3, ok3, "配信先を設定した", "配信先とストリームキーを入れる（③の「配信設定」）");
        var all = ok1 && _micHeard && ok3;
        _stepsDone.Text = all ? "準備OK！ 上の「● 配信開始」を押すと、3 秒のチェックのあとに配信が始まります。" : "";
        if (all && !_settings.OnboardingDone) { _settings.OnboardingDone = true; _settings.Save(); _stepTimer.Interval = TimeSpan.FromSeconds(8); } // 少し見せてから消す
    }

    AudioStrip? MicStrip => _mixer.Strips.FirstOrDefault(s => s.Source == _mixer.Mic);

    public void Refresh()
    {
        _loading = true;
        var s = _av.Settings;
        _avatarState.Text = s.ModelPath == null ? "まだモデルが選ばれていません" : $"{Path.GetFileName(s.ModelPath)} — {(_av.RenderConnected ? "表示中" : "配信画面に置かれていません")}";
        _cam.IsChecked = s.CameraEnabled;
        var hasModel = s.ModelPath != null;
        _cam.IsEnabled = hasModel;
        _walkBtn.Visibility = s.WalkEnabled ? Visibility.Visible : Visibility.Collapsed;
        _walkBtn.Content = s.WalkMode ? "🚶 WASD ON" : "🚶 WASD OFF";
        _walkBtn.Background = s.WalkMode ? Theme.Accent : Theme.Input;
        _walkBtn.IsEnabled = hasModel;
        foreach (var b in _exprButtons) b.IsEnabled = hasModel; // モデルが無いと何も起きないボタンは押させない

        var mic = MicStrip;
        _micMeter.Strip = mic;
        if (mic != null)
        {
            _micVol.Value = mic.Deflection;
            _listenOn = mic.Monitoring != Native.OBS_MONITORING_TYPE_NONE;
            _cleanOn = Filters.List(mic.Source).Any(f => Native.Str(Native.obs_source_get_id(f)) == "noise_suppress_filter_v2");
            _duckOn = _mixer.Desktop != IntPtr.Zero && Filters.HasDucking(_mixer.Desktop);
            SetOn(_listen, _listenOn); SetOn(_clean, _cleanOn); SetOn(_duck, _duckOn);
            FillMicDevices(mic.Source);
        }

        _sceneButtons.Children.Clear();
        foreach (var sc in _scenes.Scenes)
        {
            var b = new Button { Content = sc.Name, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 6, 12, 6),
                Background = sc == _scenes.Current ? Theme.Accent : Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line };
            b.Click += (_, _) => _scenes.SetCurrent(sc);
            _sceneButtons.Children.Add(b);
        }
        var addScene = new Button { Content = "＋ 場面", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6, 10, 6), Background = Theme.Input, Foreground = Theme.Sub, BorderBrush = Theme.Line, ToolTip = "「ゲーム中」「雑談」など場面を増やす" };
        addScene.Click += (_, _) =>
        {
            var name = InputDialog.Ask(Window.GetWindow(this)!, "場面を追加", "場面の名前（例: ゲーム中・雑談・休憩中）", SceneManager.UniqueName("シーン"));
            if (name != null) _scenes.SetCurrent(_scenes.AddScene(SceneManager.UniqueName(name)));
        };
        _sceneButtons.Children.Add(addScene);
        // 場面が 1 つだけなら、場面の説明と一覧は出さず「＋ 場面を増やす」だけ
        if (_scenes.Scenes.Count <= 1)
        {
            _sceneButtons.Children.Clear();
            addScene.Content = "＋ 場面を増やす（ゲーム中・雑談など）";
            _sceneButtons.Children.Add(addScene);
            if (_sceneBlock.Children.Count > 0) _sceneBlock.Children[0].Visibility = Visibility.Collapsed;
        }
        else if (_sceneBlock.Children.Count > 0) _sceneBlock.Children[0].Visibility = Visibility.Visible;

        _items.Children.Clear();
        if (_scenes.Items.Count == 0) _items.Children.Add(Theme.Label("（まだ何もありません）", sub: true));
        foreach (var it in _scenes.Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var vis = new CheckBox { IsChecked = it.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "表示/非表示" };
            vis.Click += (_, _) => it.Visible = vis.IsChecked == true;
            var del = new Button { Content = "✕", Width = 26, Background = Brushes.Transparent, Foreground = Theme.Sub, BorderThickness = new Thickness(0), ToolTip = "削除" };
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(Window.GetWindow(this)!, $"「{it.Name}」を削除しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _scenes.RemoveItem(it);
            };
            DockPanel.SetDock(vis, Dock.Left); DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(vis); row.Children.Add(del);
            row.Children.Add(new TextBlock { Text = it.Name, Foreground = Theme.Fg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            _items.Children.Add(row);
        }

        var dests = _settings.Destinations.Where(d => d.Enabled).ToList();
        _destState.Text = dests.Count == 0 ? "配信先が未設定です" :
            string.Join("\n", dests.Select(d => $"{d.Name}　{(string.IsNullOrEmpty(d.KeyProtected) ? "⚠ キー未入力" : "✓ 準備OK")}"));
        _destState.Foreground = dests.Count > 0 && dests.All(d => !string.IsNullOrEmpty(d.KeyProtected)) ? Theme.Fg : Theme.Live;
        _loading = false;
        UpdateSteps();
    }

    // マイクの一覧は wasapi ソースのプロパティ（device_id の選択肢）から取る
    void FillMicDevices(IntPtr mic)
    {
        var props = Native.obs_source_properties(mic);
        if (props == IntPtr.Zero) return;
        try
        {
            var p = Native.obs_properties_first(props);
            while (p != IntPtr.Zero && Native.Str(Native.obs_property_name(p)) != "device_id") if (!Native.obs_property_next(ref p)) p = IntPtr.Zero;
            if (p == IntPtr.Zero) return;
            var settings = Native.obs_source_get_settings(mic);
            var current = Native.Str(Native.obs_data_get_string(settings, "device_id")) ?? "default";
            Native.obs_data_release(settings);
            var items = new List<(string id, string name)>();
            for (nuint i = 0; i < Native.obs_property_list_item_count(p); i++)
                items.Add((Native.Str(Native.obs_property_list_item_string(p, i)) ?? "", Native.Str(Native.obs_property_list_item_name(p, i)) ?? ""));
            var existing = _micDevice.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
            if (!existing.SequenceEqual(items.Select(i => i.id)))
            {
                _micDevice.Items.Clear();
                foreach (var (id, name) in items) _micDevice.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            }
            _micDevice.SelectedItem = _micDevice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
        }
        finally { Native.obs_properties_destroy(props); }
    }

    void SetMicDevice()
    {
        if (MicStrip is not { } mic || _micDevice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var s = Native.obs_data_create();
        Native.obs_data_set_string(s, "device_id", id);
        Native.obs_source_update(mic.Source, s);
        Native.obs_data_release(s);
    }

    void ToggleClean(bool on)
    {
        if (MicStrip is not { } mic) return;
        if (on) Filters.ApplyCleanMicPreset(mic.Source);
        else
        {
            var ids = new[] { "noise_suppress_filter_v2", "noise_gate_filter", "compressor_filter", "limiter_filter" };
            foreach (var f in Filters.List(mic.Source)) if (ids.Contains(Native.Str(Native.obs_source_get_id(f)))) Filters.Remove(mic.Source, f);
        }
    }

    // WPF 標準の ToggleButton はオン時に薄い水色になり読みにくいので、普通のボタンの色で状態を表す
    static void SetOn(Button b, bool on) { b.Background = on ? Theme.Accent : Theme.Input; b.BorderBrush = on ? Theme.Accent : Theme.Line; }
    static Button SmallToggle(string text) => new() { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 4, 8, 4), Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line };

    static FrameworkElement Labeled(string label, FrameworkElement input)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var l = Theme.Label(label, sub: true); l.Width = 40; DockPanel.SetDock(l, Dock.Left);
        dp.Children.Add(l); dp.Children.Add(input);
        return dp;
    }

    static Border Card(string no, string title, params UIElement[] children)
    {
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Accent, Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = no, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold } });
        head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        foreach (var c in children) { if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 2, 0, 4); sp.Children.Add(c); }
        return new Border { Child = sp, Background = Theme.Bg, CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), BorderBrush = Theme.Line, BorderThickness = new Thickness(1) };
    }
}
