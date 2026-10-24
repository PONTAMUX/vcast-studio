using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VCast.Core;

namespace VCast.Ui;

// コメントビュー: Twitch のチャットを一覧し、VOICEVOX で読み上げる。閉じても隠れるだけで、接続と読み上げは続く（メイン画面の「💬 コメント」で表示/非表示）
public sealed class CommentWindow : Window
{
    readonly AppSettings _s; readonly TwitchChat _chat; readonly VoiceVoxReader _reader; readonly Action _save;
    readonly ListBox _list = new() { Background = Theme.Panel, BorderBrush = Theme.Line, Foreground = Theme.Fg, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _channel = new() { MinWidth = 150 };
    readonly Button _connect = Theme.Button("接続", true);
    readonly TextBlock _chatState = Theme.Label("未接続", sub: true), _voxState = Theme.Label("", sub: true);
    readonly ComboBox _voices = new() { MinWidth = 260 };
    readonly CheckBox _read = new() { Content = "コメントを読み上げる", Foreground = Theme.Fg, Margin = new Thickness(0, 4, 12, 4) };
    bool _loading = true;

    public CommentWindow(AppSettings s, TwitchChat chat, VoiceVoxReader reader, Action save)
    {
        _s = s; _chat = chat; _reader = reader; _save = save;
        Title = "コメント — VCast Studio";
        Width = 430; Height = 700; MinWidth = 340; MinHeight = 420;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;
        Topmost = s.ChatTopmost;
        Closing += (_, e) => { if (!_forceClose) { e.Cancel = true; Hide(); } }; // 閉じる = 隠す（読み上げは続ける）

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel();
        Grid.SetRow(top, 0); root.Children.Add(top);
        Grid.SetRow(_list, 1); _list.Margin = new Thickness(0, 8, 0, 0); root.Children.Add(_list);

        // 接続
        Theme.StyleInput(_channel); _channel.Text = s.ChatChannel; _channel.ToolTip = "自分のチャンネル名（または URL）。読み取り専用で、ログインは要りません";
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(Theme.Label("チャンネル　")); line1.Children.Add(_channel); line1.Children.Add(_connect);
        top.Children.Add(line1);
        _chatState.Margin = new Thickness(0, 4, 0, 0); top.Children.Add(_chatState);
        var auto = new CheckBox { Content = "起動したら自動で接続する", IsChecked = s.ChatAutoConnect, Foreground = Theme.Fg, Margin = new Thickness(0, 4, 0, 0) };
        auto.Click += (_, _) => { _s.ChatAutoConnect = auto.IsChecked == true; _save(); };
        top.Children.Add(auto);
        var topmost = new CheckBox { Content = "常に手前に表示", IsChecked = s.ChatTopmost, Foreground = Theme.Fg, Margin = new Thickness(0, 4, 0, 0) };
        topmost.Click += (_, _) => { _s.ChatTopmost = topmost.IsChecked == true; Topmost = _s.ChatTopmost; _save(); };
        top.Children.Add(topmost);
        _connect.Click += (_, _) => ToggleConnect();
        _channel.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ToggleConnect(); };

        // 読み上げ
        top.Children.Add(Header("読み上げ（VOICEVOX）"));
        _read.IsChecked = s.ChatRead;
        _read.Click += (_, _) => { _s.ChatRead = _read.IsChecked == true; _save(); if (!_s.ChatRead) _reader.StopAll(); };
        var stop = Theme.Button("止める"); stop.Click += (_, _) => _reader.StopAll();
        var test = Theme.Button("試し読み"); test.Click += (_, _) => { ApplyReader(); _reader.Enqueue("テスト", "こんにちは、コメントの読み上げテストです"); };
        var line2 = new StackPanel { Orientation = Orientation.Horizontal };
        line2.Children.Add(_read); line2.Children.Add(stop); line2.Children.Add(test);
        top.Children.Add(line2);
        _voxState.Margin = new Thickness(0, 0, 0, 4); top.Children.Add(_voxState);
        var vline = new StackPanel { Orientation = Orientation.Horizontal };
        _voices.SelectionChanged += (_, _) =>
        {
            if (_loading || _voices.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            _s.ChatSpeaker = id; _save(); ApplyReader();
        };
        var reload = Theme.Button("声を探す"); reload.Click += async (_, _) => await LoadVoices();
        vline.Children.Add(Theme.Label("声　　　　　")); vline.Children.Add(_voices); vline.Children.Add(reload);
        top.Children.Add(vline);
        top.Children.Add(SliderRow("速さ", 0.7, 1.8, () => _s.ChatSpeed, v => _s.ChatSpeed = v, "0.00x"));
        top.Children.Add(SliderRow("音量", 0.2, 1.5, () => _s.ChatVolume, v => _s.ChatVolume = v, "0.00"));
        var name = new CheckBox { Content = "名前も読む（「○○さん、…」）", IsChecked = s.ChatReadName, Foreground = Theme.Fg, Margin = new Thickness(0, 4, 0, 0) };
        name.Click += (_, _) => { _s.ChatReadName = name.IsChecked == true; _save(); ApplyReader(); };
        top.Children.Add(name);
        var dictBox = new TextBox { Text = s.ChatDict, AcceptsReturn = true, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
            ToolTip = "読み間違えるものを直す辞書。1 行に「元=読み」（例: 草=くさ）" };
        Theme.StyleInput(dictBox);
        dictBox.LostFocus += (_, _) => { _s.ChatDict = dictBox.Text; _save(); ApplyReader(); };
        top.Children.Add(new Expander { Header = "読み方の辞書（1 行に「元=読み」）", Foreground = Theme.Fg, Content = dictBox, Margin = new Thickness(0, 4, 0, 0) });
        top.Children.Add(new TextBlock { Text = "読み上げ音声はこの PC のスピーカーから出ます。配信にも流すには、デスクトップ音声が配信に入っている必要があります。", Foreground = Theme.Sub, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) });
        top.Children.Add(Header("コメント"));

        Content = root;
        _chat.Message += OnMessage;
        _chat.State += s2 => Dispatcher.BeginInvoke(() => { _chatState.Text = s2; _connect.Content = _chat.Running ? "切断" : "接続"; });
        _reader.StatusChanged += s2 => Dispatcher.BeginInvoke(() => { if (s2.Length > 0) _voxState.Text = s2; });
        _chatState.Text = _chat.Running ? $"接続中（#{_chat.Channel}）" : "未接続";
        _connect.Content = _chat.Running ? "切断" : "接続";
        Loaded += async (_, _) => { await LoadVoices(); };
        _loading = false;
    }

    bool _forceClose;
    public void CloseForReal() { _forceClose = true; Close(); }

    void ToggleConnect()
    {
        if (_chat.Running) { _chat.Stop(); _connect.Content = "接続"; return; }
        var ch = TwitchChat.NormalizeChannel(_channel.Text);
        if (ch.Length == 0) { _chatState.Text = "チャンネル名が正しくありません（例: samplech）"; return; }
        _channel.Text = ch; _s.ChatChannel = ch; _save();
        if (_chat.Start(ch)) _connect.Content = "切断";
    }

    // 受信（別スレッド）→ 画面に追加し、必要なら読み上げ
    void OnMessage(ChatMessage m)
    {
        if (_s.ChatRead) { ApplyReader(); _reader.Enqueue(m.Name, m.Text); }
        Dispatcher.BeginInvoke(() => Add(m));
    }

    public void Add(ChatMessage m)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 2, 1) };
        tb.Inlines.Add(new System.Windows.Documents.Run(m.At.ToString("HH:mm ")) { Foreground = Theme.Sub, FontSize = 11 });
        Brush nameBrush = Theme.Accent;
        try { if (m.Color.StartsWith('#')) nameBrush = (Brush)new BrushConverter().ConvertFromString(m.Color)!; } catch { }
        tb.Inlines.Add(new System.Windows.Documents.Run(m.Name) { Foreground = nameBrush, FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new System.Windows.Documents.Run("  " + m.Text) { Foreground = Theme.Fg });
        var atBottom = _list.Items.Count == 0 || _list.Items.Count - 1 == _list.SelectedIndex || IsNearBottom();
        _list.Items.Add(tb);
        while (_list.Items.Count > 500) _list.Items.RemoveAt(0);
        if (atBottom) _list.ScrollIntoView(tb);
    }

    bool IsNearBottom()
    {
        var sv = FindScroll(_list);
        return sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 2;
    }

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var r = FindScroll(VisualTreeHelper.GetChild(d, i)); if (r != null) return r; }
        return null;
    }

    public void ApplyReader()
    {
        _reader.Speaker = _s.ChatSpeaker; _reader.Speed = _s.ChatSpeed; _reader.Volume = _s.ChatVolume;
        _reader.ReadName = _s.ChatReadName; _reader.Dictionary = _s.ChatDict;
    }

    async Task LoadVoices()
    {
        var styles = await _reader.GetStyles();
        _loading = true;
        _voices.Items.Clear();
        if (styles == null || styles.Count == 0)
        {
            _voxState.Text = "VOICEVOX が見つかりません。VOICEVOX（無料）を起動してから「声を探す」を押してください。";
        }
        else
        {
            foreach (var st in styles) _voices.Items.Add(new ComboBoxItem { Content = st.Label, Tag = st.Id });
            var cur = _voices.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == _s.ChatSpeaker) ?? (ComboBoxItem)_voices.Items[0];
            _voices.SelectedItem = cur;
            _voxState.Text = $"VOICEVOX に接続しました（声 {styles.Count} 種類）";
        }
        _loading = false;
        ApplyReader();
    }

    StackPanel SliderRow(string label, double min, double max, Func<double> get, Action<double> set, string fmt)
    {
        var val = Theme.Label(get().ToString(fmt)); val.Width = 56; val.Margin = new Thickness(8, 0, 0, 0);
        var sl = new Slider { Minimum = min, Maximum = max, Value = get(), Width = 220, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => { if (_loading) return; set(e.NewValue); val.Text = e.NewValue.ToString(fmt); ApplyReader(); };
        sl.PreviewMouseLeftButtonUp += (_, _) => _save();
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var l = Theme.Label(label); l.Width = 90;
        sp.Children.Add(l); sp.Children.Add(sl); sp.Children.Add(val);
        return sp;
    }

    static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 4) };
}
