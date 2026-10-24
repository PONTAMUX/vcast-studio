using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VCast.Core;

namespace VCast.Ui;

// 配信・録画の設定。上はかんたん（配信先・画質）、下の「詳細設定」を開くとエンコーダ等
public sealed class SettingsWindow : Window
{
    readonly AppSettings _s;
    readonly StackPanel _dests = new();
    readonly bool _locked;

    public SettingsWindow(AppSettings settings, bool outputsActive)
    {
        _s = settings; _locked = outputsActive;
        Title = "設定（配信・録画・画質など）— VCast Studio";
        Width = 600; Height = 720; MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };
        if (_locked) body.Children.Add(Warn("配信・録画中は画質とエンコーダを変更できません（配信先のキーなどは次回開始時に反映）"));

        // 使いかた
        var pro = new CheckBox { Content = "プロモードにする（詳細モードに、スタジオモード・プロファイル・マクロ・レイアウト集などの vMix のような機能を足す）", IsChecked = _s.ProMode, Foreground = Theme.Fg, Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "画面左上の「詳細モードへ」で出る画面に、プロ向けの機能が加わります。いつでも戻せます" };
        pro.Click += (_, _) => _s.ProMode = pro.IsChecked == true;
        body.Children.Add(pro);
        body.Children.Add(new TextBlock { Text = "かんたんモード → 詳細モード（上のバーのボタンで切り替え）→ プロモード（ここでオン）の 3 段階です。", Foreground = Theme.Sub, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        // 配信先
        body.Children.Add(Header("配信先", "チェックを入れた配信先すべてに同時配信します"));
        body.Children.Add(_dests);
        var addDest = Theme.Button("＋ 配信先を追加"); addDest.HorizontalAlignment = HorizontalAlignment.Left; addDest.Margin = new Thickness(0, 4, 0, 0);
        addDest.Click += (_, _) => { _s.Destinations.Add(new StreamDestination { Name = "Twitch", Service = "Twitch" }); BuildDests(); };
        body.Children.Add(addDest);
        BuildDests();

        // 画質
        body.Children.Add(Header("画質", "映像は 1920x1080・60fps。回線に合わせて選んでください"));
        foreach (var p in AppSettings.Presets)
        {
            var rb = new RadioButton { GroupName = "q", IsChecked = _s.Quality == p.Id, Foreground = Theme.Fg, Margin = new Thickness(0, 3, 0, 3), IsEnabled = !_locked,
                Content = new TextBlock { Inlines = { new System.Windows.Documents.Run($"{p.Label}（{p.Kbps / 1000.0:0.#} Mbps）") { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run("　" + p.Note) { Foreground = Theme.Sub } } } };
            rb.Checked += (_, _) => _s.Quality = p.Id;
            body.Children.Add(rb);
        }
        var custom = new RadioButton { GroupName = "q", IsChecked = _s.Quality == "custom", Content = "自分で決める（詳細設定のビットレート）", Foreground = Theme.Fg, Margin = new Thickness(0, 3, 0, 3), IsEnabled = !_locked };
        custom.Checked += (_, _) => _s.Quality = "custom";
        body.Children.Add(custom);

        var hevcAvail = OutputManager.PickVideoEncoder("auto", "hevc") is { } he && OutputManager.IsHevc(he);
        var codecCombo = Combo(new[] { ("h264", "H.264（どこでも見られる・標準）"), ("hevc", hevcAvail ? "HEVC / H.265（同じ回線でより綺麗・YouTube 向け）" : "HEVC / H.265（この PC の GPU は非対応）") }, _s.Codec, v => _s.Codec = v);
        codecCombo.IsEnabled = !_locked && hevcAvail;
        body.Children.Add(Labeled("映像の圧縮方式（HEVC は YouTube など対応した配信先のみ。Twitch は H.264）", codecCombo));
        body.Children.Add(Labeled("フレームレート（再起動で反映）", Combo(new[] { ("60", "60fps（なめらか・おすすめ）"), ("30", "30fps（PC が重いとき）") }, _s.Fps.ToString(), v => _s.Fps = int.Parse(v))));

        body.Children.Add(Labeled("遅延配信（放送事故対策。映したくないものが映っても、視聴者に届く前に止められる）",
            Combo(new[] { ("0", "なし（おすすめ）"), ("2", "2 秒（低遅延）"), ("5", "5 秒"), ("10", "10 秒"), ("30", "30 秒"), ("60", "60 秒") }, _s.StreamDelaySec.ToString(), v => _s.StreamDelaySec = int.Parse(v))));

        // 録画
        body.Children.Add(Header("録画", null));
        var recWith = new CheckBox { Content = "配信を始めたら録画も自動で始める（配信を終えたら一緒に止める）", IsChecked = _s.RecordWithStream, Foreground = Theme.Fg, Margin = new Thickness(0, 2, 0, 4),
            ToolTip = "配信が落ちたときの控えや、あとで切り抜く素材になります" };
        recWith.Click += (_, _) => _s.RecordWithStream = recWith.IsChecked == true;
        body.Children.Add(recWith);
        var folder = new TextBox { Text = _s.RecordFolder };
        Theme.StyleInput(folder);
        folder.TextChanged += (_, _) => _s.RecordFolder = folder.Text;
        var browse = Theme.Button("参照…");
        browse.Click += (_, _) => { var d = new OpenFolderDialog { InitialDirectory = _s.RecordFolder }; if (d.ShowDialog(this) == true) folder.Text = d.FolderName; };
        var fl = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); fl.Children.Add(browse); fl.Children.Add(folder);
        body.Children.Add(Labeled("保存先", fl));
        var fmt = Combo(new[] { ("mp4", "MP4（おすすめ・途中で落ちても壊れにくい）"), ("mkv", "MKV") }, _s.RecordFormat, v => _s.RecordFormat = v);
        body.Children.Add(Labeled("形式", fmt));
        var recSep = new CheckBox { Content = "録画は配信より高画質で残す（編集・切り抜き用。ファイルは大きくなります）", IsChecked = _s.RecordSeparate, Foreground = Theme.Fg, IsEnabled = !_locked,
            ToolTip = "配信は回線に合わせて圧縮し、録画は別のエンコーダで画質重視（ほぼ劣化なし）に保存します。GPU の負荷が少し増えます" };
        recSep.Click += (_, _) => _s.RecordSeparate = recSep.IsChecked == true;
        body.Children.Add(recSep);
        var recHevcAvail = OutputManager.PickVideoEncoder("auto", "hevc") is { } rhe && OutputManager.IsHevc(rhe);
        body.Children.Add(Labeled("録画の圧縮方式（配信は Twitch 向けに H.264 のまま）",
            Combo(new[] { ("hevc", recHevcAvail ? "HEVC / H.265（おすすめ・ファイルが小さい）" : "HEVC / H.265（この PC では使えないため H.264 になります）"), ("h264", "H.264（どこでも再生できる）"), ("same", "配信と同じ") }, _s.RecordCodec, v => _s.RecordCodec = v)));
        body.Children.Add(Labeled("録画の画質", Combo(new[] { ("14", "最高（ファイル大）"), ("18", "高画質（おすすめ）"), ("23", "標準（ファイル小）") }, _s.RecordQuality.ToString(), v => _s.RecordQuality = int.Parse(v))));

        // リプレイ
        body.Children.Add(Header("リプレイ（直前を保存）", "起動中ずっと直前の映像をためておき、ボタンで保存します（メモリを少し使います）"));
        var rep = new CheckBox { Content = "起動したらリプレイバッファを開始する", IsChecked = _s.ReplayEnabled, Foreground = Theme.Fg };
        rep.Click += (_, _) => _s.ReplayEnabled = rep.IsChecked == true;
        body.Children.Add(rep);
        body.Children.Add(Labeled("保存する長さ", Combo(new[] { ("15", "15 秒"), ("30", "30 秒"), ("60", "60 秒"), ("120", "2 分") }, _s.ReplaySeconds.ToString(), v => _s.ReplaySeconds = int.Parse(v))));

        // 縦型の同時出力
        body.Children.Add(Header("縦型の同時出力（ショート・TikTok 用）", "横の配信と同時に、1080x1920 の縦型も作って配信・録画します（GPU を少し多く使います）"));
        var vOn = new CheckBox { Content = "縦型も作る", IsChecked = _s.VerticalEnabled, Foreground = Theme.Fg, IsEnabled = !_locked };
        vOn.Click += (_, _) => _s.VerticalEnabled = vOn.IsChecked == true;
        body.Children.Add(vOn);
        body.Children.Add(Labeled("レイアウト", Combo(new[] { ("stack", "上に画面、下にアバター（おすすめ）"), ("avatar", "アバターを大きく、画面は上に小さく"), ("crop", "画面の中央を縦に切り抜く") }, _s.VerticalLayout, v => _s.VerticalLayout = v)));
        var vRec = new CheckBox { Content = "縦型も録画する（横の録画と同時に別ファイル）", IsChecked = _s.VerticalRecord, Foreground = Theme.Fg };
        vRec.Click += (_, _) => _s.VerticalRecord = vRec.IsChecked == true;
        body.Children.Add(vRec);
        var vd = _s.VerticalDestinations.FirstOrDefault() ?? new StreamDestination { Name = "縦型", Service = "Custom" };
        if (_s.VerticalDestinations.Count == 0) _s.VerticalDestinations.Add(vd);
        var vEn = new CheckBox { Content = "縦型を配信する（配信開始で横と同時に）", IsChecked = vd.Enabled, Foreground = Theme.Fg };
        vEn.Click += (_, _) => vd.Enabled = vEn.IsChecked == true;
        body.Children.Add(vEn);
        var vServer = new TextBox { Text = vd.Server }; Theme.StyleInput(vServer);
        vServer.TextChanged += (_, _) => { vd.Service = "Custom"; vd.Server = vServer.Text; };
        body.Children.Add(Labeled("縦型の配信先サーバー URL（rtmp:// または rtmps://。YouTube の縦型用配信枠など）", vServer));
        var vKey = new PasswordBox { Password = vd.Key }; Theme.StyleInput(vKey);
        vKey.PasswordChanged += (_, _) => vd.Key = vKey.Password;
        body.Children.Add(Labeled("縦型のストリームキー", vKey));

        // 外部から操作
        body.Children.Add(Header("外部から操作（Stream Deck など）", "OBS 用のリモコン（obs-websocket v5 対応の Stream Deck プラグイン、Touch Portal、スマホアプリなど）で VCast を操作できます"));
        var rOn = new CheckBox { Content = "外部からの操作を受け付ける", IsChecked = _s.RemoteEnabled, Foreground = Theme.Fg };
        var rPass = new TextBox { Width = 260, IsReadOnly = true }; Theme.StyleInput(rPass);
        var passShown = false;
        void ShowPass() => rPass.Text = passShown || string.IsNullOrEmpty(_s.RemotePassword) ? _s.RemotePassword : new string('●', 12); // 配信画面に映り込んでも読まれないよう、普段は伏せる
        void NewPassword() { _s.RemotePassword = AppSettings.NewPassword(); ShowPass(); }
        ShowPass();
        rOn.Click += (_, _) => { _s.RemoteEnabled = rOn.IsChecked == true; if (_s.RemoteEnabled && string.IsNullOrEmpty(_s.RemotePassword)) NewPassword(); };
        body.Children.Add(rOn);
        var rPort = new TextBox { Text = _s.RemotePort.ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left }; Theme.StyleInput(rPort);
        rPort.TextChanged += (_, _) => { if (int.TryParse(rPort.Text, out var p) && p is > 1024 and < 65536) _s.RemotePort = p; };
        body.Children.Add(Labeled("ポート（OBS の WebSocket と同時に使うなら 4455 以外に）", rPort));
        var regen = Theme.Button("作り直す"); regen.Click += (_, _) => NewPassword();
        var copyPass = Theme.Button("コピー"); copyPass.Click += (_, _) => { try { Clipboard.SetText(_s.RemotePassword); } catch { } };
        var showPass = Theme.Button("表示"); showPass.Click += (_, _) => { passShown = !passShown; showPass.Content = passShown ? "隠す" : "表示"; ShowPass(); };
        var passLine = new StackPanel { Orientation = Orientation.Horizontal }; passLine.Children.Add(rPass); passLine.Children.Add(showPass); passLine.Children.Add(regen); passLine.Children.Add(copyPass);
        body.Children.Add(Labeled("パスワード（リモコン側の設定に入れる）", passLine));
        var rLan = new CheckBox { Content = "ほかの機器（スマホ・別の PC）からの接続も受け付ける", IsChecked = _s.RemoteLan, Foreground = Theme.Fg,
            ToolTip = "オフならこの PC の中からだけ。オンにするとファイアウォールの許可を求められることがあります" };
        rLan.Click += (_, _) => _s.RemoteLan = rLan.IsChecked == true;
        body.Children.Add(rLan);
        body.Children.Add(Theme.Label("サーバーは 127.0.0.1（ほかの機器からはこの PC の IP アドレス）。VCast 独自の操作: CallVendorRequest（vendorName \"vcast\"）で SetExpression・PlaySound・StopAllSounds", sub: true));

        // 配信前チェック
        body.Children.Add(Header("配信前チェック", null));
        var pre = new CheckBox { Content = "配信開始の前に、マイク・音割れ・キー・PC の負荷を確認する（おすすめ）", IsChecked = _s.PreflightCheck, Foreground = Theme.Fg };
        pre.Click += (_, _) => _s.PreflightCheck = pre.IsChecked == true;
        body.Children.Add(pre);

        // 詳細
        var adv = new StackPanel { Margin = new Thickness(0, 6, 0, 0), IsEnabled = !_locked };
        var encoders = OutputManager.EncoderTypes().Where(id => id.Contains("264") || id.Contains("hevc") || id.Contains("av1") || id.Contains("nvenc") || id.Contains("amf") || id.Contains("qsv"))
            .Where(id => !id.StartsWith("ffmpeg_")).Select(id => (id, VCast.Obs.Native.Str(VCast.Obs.Native.obs_encoder_get_display_name(id)) ?? id)).ToList();
        encoders.Insert(0, ("auto", $"自動（いまは {OutputManager.PickVideoEncoder("auto")}）"));
        adv.Children.Add(Labeled("映像エンコーダ", Combo(encoders.ToArray(), _s.VideoEncoder, v => _s.VideoEncoder = v)));
        adv.Children.Add(Labeled("レート制御", Combo(new[] { ("CBR", "CBR（配信向け・一定）"), ("VBR", "VBR（可変）"), ("CQP", "CQP（録画向け・画質一定）") }, _s.RateControl, v => _s.RateControl = v)));
        adv.Children.Add(Labeled("映像ビットレート (kbps)", Num(_s.VideoBitrate, 500, 50000, v => _s.VideoBitrate = v)));
        adv.Children.Add(Labeled("キーフレーム間隔 (秒)", Num(_s.KeyintSec, 1, 10, v => _s.KeyintSec = v)));
        adv.Children.Add(Labeled("音声ビットレート (kbps)", Combo(new[] { ("128", "128"), ("160", "160"), ("192", "192"), ("256", "256"), ("320", "320") }, _s.AudioBitrate.ToString(), v => _s.AudioBitrate = int.Parse(v))));
        body.Children.Add(new Expander { Header = "詳細設定（エンコーダ・ビットレートなど）", Content = adv, Foreground = Theme.Fg, Margin = new Thickness(0, 16, 0, 0) });

        var ok = Theme.Button("保存して閉じる", primary: true);
        ok.Click += (_, _) => { _s.Save(); DialogResult = true; };
        var cancel = Theme.Button("キャンセル"); cancel.IsCancel = true;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        footer.Children.Add(cancel); footer.Children.Add(ok);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    void BuildDests()
    {
        _dests.Children.Clear();
        foreach (var d in _s.Destinations.ToList())
        {
            var box = new StackPanel();
            var head = new DockPanel();
            var en = new CheckBox { IsChecked = d.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "この配信先に配信する" };
            en.Click += (_, _) => d.Enabled = en.IsChecked == true;
            var remove = Theme.Button("削除"); DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) => { _s.Destinations.Remove(d); BuildDests(); };
            var svc = Combo(new[] { ("YouTube", "YouTube"), ("Twitch", "Twitch"), ("Custom", "その他（RTMP の URL を入力）") }, d.Service, v => { d.Service = v; d.Name = v == "Custom" ? "カスタム" : v; BuildDests(); });
            svc.Width = 260;
            head.Children.Add(en); head.Children.Add(remove); head.Children.Add(svc);
            box.Children.Add(head);
            if (d.Service == "Custom")
            {
                var server = new TextBox { Text = d.Server }; Theme.StyleInput(server);
                server.TextChanged += (_, _) => d.Server = server.Text;
                box.Children.Add(Labeled("サーバー URL（rtmp:// または rtmps://）", server));
            }
            var key = new PasswordBox { Password = d.Key }; Theme.StyleInput(key);
            key.PasswordChanged += (_, _) => d.Key = key.Password;
            var keyText = new TextBox { Text = d.Key, Visibility = Visibility.Collapsed }; Theme.StyleInput(keyText);
            keyText.TextChanged += (_, _) => { if (keyText.IsVisible) { d.Key = keyText.Text; key.Password = keyText.Text; } };
            var show = new CheckBox { Content = "キーを表示して確認する", Foreground = Theme.Sub, Margin = new Thickness(0, 4, 0, 0) };
            show.Click += (_, _) =>
            {
                var on = show.IsChecked == true;
                keyText.Text = key.Password;
                keyText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                key.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            };
            var keyBox = new StackPanel(); keyBox.Children.Add(key); keyBox.Children.Add(keyText); keyBox.Children.Add(show);
            var hint = d.Service switch
            {
                "YouTube" => "YouTube Studio →「ライブ配信を開始」→ ストリームキーをコピー",
                "Twitch" => "Twitch クリエイターダッシュボード → 設定 → 配信 → プライマリストリームキー",
                _ => "配信サービスの管理画面に表示されるキー",
            };
            box.Children.Add(Labeled($"ストリームキー（{hint}）", keyBox));
            _dests.Children.Add(new Border { Child = box, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4) });
        }
    }

    static TextBlock Warn(string t) => new() { Text = t, Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xA8, 0x4D)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };

    static StackPanel Header(string title, string? note)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 16, 0, 6) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (note != null) sp.Children.Add(Theme.Label(note, sub: true));
        return sp;
    }

    static StackPanel Labeled(string label, UIElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Theme.Sub, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(input);
        return sp;
    }

    static ComboBox Combo((string Value, string Label)[] items, string current, Action<string> set)
    {
        var c = new ComboBox();
        Theme.StyleInput(c);
        foreach (var (v, l) in items) c.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? c.Items[0];
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem { Tag: string v }) set(v); };
        return c;
    }

    static TextBox Num(int value, int min, int max, Action<int> set)
    {
        var t = new TextBox { Text = value.ToString(), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        Theme.StyleInput(t);
        t.TextChanged += (_, _) => { if (int.TryParse(t.Text, out var v) && v >= min && v <= max) set(v); };
        return t;
    }
}
