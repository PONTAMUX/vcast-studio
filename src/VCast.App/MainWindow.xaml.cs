using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VCast.Core;
using VCast.Obs;
using VCast.Ui;

namespace VCast;

public partial class MainWindow : Window
{
    readonly SceneManager _scenes = new();
    readonly SceneEditor _editor;
    readonly UndoStack _undo = new();
    readonly AudioMixer _mixer;
    readonly AppSettings _settings = AppSettings.Load();
    readonly OutputManager _outputs;
    readonly PerfMonitor _perf = new();
    readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _closingConfirmed;
    SimplePanel? _simple;
    readonly VCast.Avatar.AvatarController _avatar;
    readonly Soundboard _soundboard;
    readonly PlaylistManager _playlists = new();
    readonly TransitionManager _transitions = new();
    readonly VerticalCanvas _vertical = new();
    Window? _verticalWindow;
    SoundboardWindow? _soundboardWindow;
    AvatarWindow? _avatarWindow;
    readonly List<LevelMeter> _meters = new();
    readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    bool _syncing;
    readonly DispatcherTimer _appRefresh = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        _editor = new SceneEditor(_scenes);
        _mixer = new AudioMixer(_scenes);
        MixerList.ItemsSource = _mixer.Strips;
        _avatar = new VCast.Avatar.AvatarController(() => _mixer.MicDb);
        _avatar.StatusChanged += RefreshWalkButton;
        _outputs = new OutputManager(_settings);
        _outputs.Event += OnOutputEvent;
        _outputs.StateChanged += UpdateOutputButtons;
        _secondTimer.Tick += (_, _) => { if (Environment.GetEnvironmentVariable("VCAST_NOPERF") != "1") OnSecond(); };
        _secondTimer.Start();
        Closing += OnClosingConfirm;
        Loaded += (_, _) => StartChatIfWanted();
        StateChanged += (_, _) => Preview.SetRendering(WindowState != WindowState.Minimized);
        _soundboard = new Soundboard(name => _avatar.SetExpression(name));
        InitHotkeys();
        // メーター: 音声スレッドは値を置くだけ、描画はこのタイマー1本で全行まとめて（約30fps）
        _meterTimer.Tick += (_, _) => { if (Environment.GetEnvironmentVariable("VCAST_NOMETER") == "1") return; foreach (var m in _meters) { m.Muted = m.Strip?.Muted ?? false; m.Tick(); } };

        SceneList.ItemsSource = _scenes.Scenes;
        SourceList.ItemsSource = _scenes.Items;
        _scenes.CurrentChanged += () => { SceneList.SelectedItem = _scenes.Current; _editor.Select(IntPtr.Zero); OnSceneChosen(); };
        _scenes.SceneRemoving += src =>
        {
            _transitions.Forget(src);
            // 本番のシーンが消えたら、残っているシーンを本番に出す（黒画面のままにしない）
            Dispatcher.BeginInvoke(() => { if (_transitions.ProgramScene == IntPtr.Zero && _scenes.Current != null) _transitions.Take(_scenes.Current.Source, animate: false); });
        };
        _editor.SelectionChanged += item => Sync(() => SourceList.SelectedItem = _scenes.Items.FirstOrDefault(i => i.Item == item));

        Preview.Overlay = _editor;
        Preview.PointerDown += p => { _editor.PointerDown(p.X, p.Y, p.Scale, p.Shift); Keyboard.ClearFocus(); };
        Preview.PointerMove += p => _editor.PointerMove(p.X, p.Y, p.Scale, p.Shift);
        Preview.PointerUp += _ => _editor.PointerUp();
        Preview.DoubleClick += p => { _editor.PointerDown(p.X, p.Y, p.Scale, p.Shift); _editor.PointerUp(); OpenProperties(); };
        Preview.RightClick += p =>
        {
            var hit = _editor.HitTest(p.X, p.Y);
            if (hit != IntPtr.Zero) _editor.Select(hit);
            ShowItemMenu(null);
        };
        // 移動・拡大縮小などが確定したら「元に戻す」に積む
        _editor.TransformCommitted += (item, before) =>
        {
            Native.obs_sceneitem_addref(item);
            _undo.Push("移動・大きさの変更", () => { before.ApplyTo(item); Native.obs_sceneitem_release(item); });
        };
        Preview.CursorFor = p => _editor.HandleAt(p.X, p.Y, p.Scale) switch
        {
            0 or 2 => PreviewHost.SystemCursor(PreviewHost.IDC_SIZENWSE),
            1 or 3 => PreviewHost.SystemCursor(PreviewHost.IDC_SIZENESW),
            4 or 6 => PreviewHost.SystemCursor(PreviewHost.IDC_SIZENS),
            5 or 7 => PreviewHost.SystemCursor(PreviewHost.IDC_SIZEWE),
            _ when _editor.IsOverSelected(p.X, p.Y) => PreviewHost.SystemCursor(PreviewHost.IDC_SIZEALL),
            _ => IntPtr.Zero,
        };

        Preview.FilesDropped += (files, p) => AddFiles(files, p);
        _scenes.Items.CollectionChanged += (_, _) => { UpdateEmptyState(); _mixer.Rebuild(); SyncPlaylists(); RelayoutVertical(); };
        BuildQuickAdd();

        // 開いているアプリ一覧: 起動時・ウィンドウに戻ったとき・5秒ごとに更新
        Loaded += (_, _) => RefreshApps();
        Activated += (_, _) => RefreshApps();
        _appRefresh.Tick += (_, _) => { if (IsActive && _appDown == null) RefreshApps(); };
        _appRefresh.Start();

        static bool Off(string n) => Environment.GetEnvironmentVariable("VCAST_NO" + n) == "1"; // 切り分け用
        // 前回のシーンを復元（自己テストはユーザーのデータに触らない）
        var selfTestRun = ObsEngine.TestMode;
        // 常設音声はシーンより先に作る（シーンの中から参照されていることがあるため）
        var globalsCreated = false;
        var loaded = selfTestRun ? null : SceneStore.Load(SceneStore.DefaultPath, _scenes, FixAvatarUrl,
            (d, m) => { if (!Off("MIXER")) _mixer.CreateGlobalSources(d, m); globalsCreated = true; });
        if (!globalsCreated && !Off("MIXER")) _mixer.CreateGlobalSources();
        if (loaded?.DesktopData is { } dd && dd != IntPtr.Zero) Native.obs_data_release(dd);
        if (loaded?.MicData is { } md && md != IntPtr.Zero) Native.obs_data_release(md);
        if (_scenes.Scenes.Count == 0) _scenes.AddScene("メイン");
        _transitions.Init(_settings.TransitionId, _settings.TransitionMs);
        TakeProgramAfterLoad(loaded?.ProgramScene);
        _transitions.ManualCancelled += () => ResetTBar();
        _transitions.ProgramChanged += UpdateStudioLabels;
        _outputs.Vertical = _vertical;
        ApplyVertical();
        HookRemoteEvents();
        ApplyRemote();
        UpdateProfileButton();
        InitTransitionUi();
        SyncPlaylists();
        if (!selfTestRun)
        {
            var autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            autosave.Tick += (_, _) => SaveCurrentProfile();
            autosave.Start();
            Closed += (_, _) => autosave.Stop();
        }
        UpdateEmptyState();
        _mixer.Rebuild();
        _meterTimer.Start();
        UpdateOutputButtons();
        ApplyMode();
        if (_settings.ReplayEnabled) Loaded += (_, _) => { if (!_outputs.StartReplay(out var err)) Status.Text = "リプレイを開始できませんでした: " + err; };
        var args = Environment.GetCommandLineArgs();
        var si = Array.IndexOf(args, "--soak");
        if (si >= 0 && si + 1 < args.Length && int.TryParse(args[si + 1], out var soakMin))
            Loaded += async (_, _) => await SoakTest.Run(this, soakMin);
        var ri = Array.IndexOf(args, "--rectest");
        if (ri >= 0 && ri + 2 < args.Length)
            Loaded += async (_, _) => await SelfTest.RecordProbe(this, _outputs, _settings.Clone() is var c && true ? _settings : c, args[ri + 1], args[ri + 2]);
        if (Environment.GetCommandLineArgs().Contains("--selftest"))
            Loaded += async (_, _) => await SelfTest.Run(this, _scenes, _editor);
        Status.Text = App.UncleanLastExit
            ? "前回は正常に終了しなかったようです。シーンは直前の自動保存（1 分ごと）から復元しました"
            : DefaultStatus;

        // エンジン停止(App.OnExit)より前に display とソースを必ず解放する
        Closed += (_, _) =>
        {
            _appRefresh.Stop(); _meterTimer.Stop(); _meters.Clear();
            Preview.Overlay = null; _editor.Select(IntPtr.Zero); Preview.Dispose();
            _secondTimer.Stop();
            _remote?.Dispose();
            SaveCurrentProfile();
            ObsEngine.BeginTeardown(); // 出力の解放中にモジュールが UI タスクを待つとデッドロックするため
            _outputs.Shutdown();
            _avatarWindow?.Close();
            _soundboardWindow?.Close();
            _chat.Dispose(); _voice.Dispose(); _commentWin?.CloseForReal(); _macroWin?.CloseForReal();
            _hotkeys.Dispose();
            _soundboard.Dispose();
            _playlists.Dispose();
            _avatar.Dispose();
            ProgramView.Dispose();
            _verticalWindow?.Close();
            _vertical.Dispose();
            _transitions.Dispose();
            _mixer.Shutdown();
            _scenes.Clear();
        };
    }

    void Sync(Action a)
    {
        if (_syncing) return;
        _syncing = true;
        try { a(); } finally { _syncing = false; }
    }

    SceneItemVm? SelectedItem => SourceList.SelectedItem as SceneItemVm;

    static bool NameTaken(string name)
    {
        var ex = Native.obs_get_source_by_name(name);
        if (ex == IntPtr.Zero) return false;
        Native.obs_source_release(ex);
        return true;
    }

    // --- シーン ---
    void OnAddScene(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "シーンを追加", "シーン名", SceneManager.UniqueName("シーン"));
        if (name == null) return;
        if (NameTaken(name)) { MessageBox.Show(this, "同じ名前がすでにあります。", "VCast Studio"); return; }
        _scenes.SetCurrent(_scenes.AddScene(name));
    }

    void OnRemoveScene(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneVm vm) return;
        if (_scenes.Scenes.Count <= 1) { MessageBox.Show(this, "シーンは最低1つ必要です。", "VCast Studio"); return; }
        if (MessageBox.Show(this, $"シーン「{vm.Name}」を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _editor.Select(IntPtr.Zero);
        _scenes.RemoveScene(vm);
    }

    void OnRenameScene(object sender, RoutedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneVm vm) return;
        var name = InputDialog.Ask(this, "名前の変更", "シーン名", vm.Name);
        if (name == null || name == vm.Name) return;
        if (NameTaken(name)) { MessageBox.Show(this, "同じ名前がすでにあります。", "VCast Studio"); return; }
        vm.Name = name;
    }

    void OnSceneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SceneList.SelectedItem is SceneVm vm && vm != _scenes.Current) _scenes.SetCurrent(vm);
    }

    // --- ソース ---
    internal bool IsEmptyStateShown => EmptyState.Visibility == Visibility.Visible;
    internal int AppCount => (AppStrip.ItemsSource as System.Collections.ICollection)?.Count ?? 0;

    static string DefaultStatus => $"キャンバス {ObsEngine.BaseWidth}x{ObsEngine.BaseHeight}";

    // 追加メニュー: 「映像／音声／素材・文字／その他」に分けて一行説明を付ける
    void OnAddSource(object sender, RoutedEventArgs e) => ShowAddMenu(AddSourceButton);

    void ShowAddMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var types = SceneManager.InputTypes();
        var used = new HashSet<string>();
        void Section(string title, IEnumerable<SourceType> list)
        {
            var items = list.ToList();
            if (items.Count == 0) return;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.Gray },
                IsHitTestVisible = false, Focusable = false,
            });
            foreach (var t in items)
            {
                used.Add(t.Id);
                var header = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                header.Children.Add(new TextBlock { Text = t.DisplayName, FontWeight = FontWeights.SemiBold });
                var desc = SourceCatalog.Describe(t.Id);
                if (desc.Length > 0) header.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray });
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => AddSource(t);
                menu.Items.Add(mi);
            }
        }
        types.Insert(0, new SourceType(SourceCatalog.AvatarId, "3Dアバター"));
        types.Insert(1, new SourceType(SourceCatalog.PlaylistId, "プレイリスト"));
        foreach (var c in SourceCatalog.Categories)
            Section(c.Name, c.Ids.Select(id => types.FirstOrDefault(t => t.Id == id)).OfType<SourceType>());
        Section("その他", types.Where(t => !used.Contains(t.Id)));
        menu.IsOpen = true;
    }

    // 名前は自動採番（F2で変更）。追加直後の設定画面をキャンセルしたら追加自体を取り消す（OBSと同じ）
    void AddSource(SourceType t)
    {
        if (t.Id == SourceCatalog.AvatarId) { AddAvatar(); return; }
        if (t.Id == SourceCatalog.PlaylistId)
        {
            var pl = _playlists.Create(_scenes);
            if (pl != IntPtr.Zero) new PlaylistWindow(_playlists, pl) { Owner = this }.Show();
            return;
        }
        var vm = _scenes.AddSource(t.Id, SceneManager.UniqueName(t.DisplayName));
        if (vm == null)
        {
            MessageBox.Show(this, t.Id == "browser_source" || t.Id == SourceCatalog.AvatarId
                ? "ブラウザ部品を読み込めなかったため、追加できませんでした。VCast を入れ直すか、ログ（%APPDATA%\\VCastStudio\\logs）を確認してください。"
                : "ソースを作成できませんでした。", "VCast Studio");
            return;
        }
        _editor.Select(vm.Item);
        if (!OpenProperties())
        {
            _editor.Select(IntPtr.Zero);
            _scenes.RemoveItem(vm);
            Status.Text = "キャンセルしたので、追加を取り消しました";
        }
    }

    void RenameSelected()
    {
        if (SelectedItem is not { } vm) return;
        var name = InputDialog.Ask(this, "名前の変更", "ソース名", vm.Name);
        if (name == null || name == vm.Name) return;
        if (NameTaken(name)) { MessageBox.Show(this, "同じ名前がすでにあります。", "VCast Studio"); return; }
        Native.obs_source_set_name(vm.Source, name);
        vm.Refresh();
    }

    void OnRemoveSource(object sender, RoutedEventArgs e) => RemoveSelected();

    // 削除は確認を出さない代わりに Ctrl+Z で戻せる（ソースの設定・フィルタ・位置・順番・表示状態ごと）
    void RemoveSelected()
    {
        if (SelectedItem is not { } vm || _scenes.Current is not { } scene) return;
        var src = vm.Source;
        var name = vm.Name;
        var data = Native.obs_save_source(src);
        // 削除直後は元のソースが破棄待ちで残っていることがあるので、作り直すときは新しい UUID にする
        Native.obs_data_erase(data, "uuid");
        var xf = ItemTransform.Of(vm.Item);
        var visible = vm.Visible;
        var locked = Native.obs_sceneitem_locked(vm.Item);
        var order = SceneManager.EnumItems(scene.Scene).IndexOf(vm.Item);
        _editor.Select(IntPtr.Zero);
        Native.obs_source_get_ref(src);
        _scenes.RemoveItem(vm);
        // 取り除かれたソースは破棄されるまで名前を持ち続けるので、元に戻したとき同じ名前で作れるよう別名にしておく
        if (Native.obs_source_removed(src)) Native.obs_source_set_name(src, $"__removed__{Guid.NewGuid():N}");
        Native.obs_source_release(src);
        Status.Text = $"「{name}」を削除しました（Ctrl+Z で元に戻す）";
        _undo.Push($"「{name}」の削除", () =>
        {
            // 別のシーンでまだ使われていればそれを、消えていれば保存しておいた内容から作り直す
            var existing = Native.obs_get_source_by_name(name);
            var s = existing != IntPtr.Zero && !Native.obs_source_removed(existing) ? existing : Native.obs_load_source(data);
            if (existing != IntPtr.Zero && s != existing) Native.obs_source_release(existing);
            Native.obs_data_release(data);
            if (s == IntPtr.Zero || !_scenes.Scenes.Contains(scene)) { ObsEngine.Log($"[undo] restore failed: {name}"); if (s != IntPtr.Zero) Native.obs_source_release(s); return; }
            var item = Native.obs_scene_add(scene.Scene, s);
            Native.obs_source_release(s);
            if (item == IntPtr.Zero) return;
            xf.ApplyTo(item);
            Native.obs_sceneitem_set_visible(item, visible);
            Native.obs_sceneitem_set_locked(item, locked);
            if (order >= 0) Native.obs_sceneitem_set_order_position(item, order);
            if (_scenes.Current == scene) { _scenes.RefreshItems(); _editor.Select(item); }
        });
    }

    internal void RemoveSelectedForTest() => RemoveSelected();
    internal void UndoForTest() => UndoLast();

    void UndoLast()
    {
        var label = _undo.Undo();
        Status.Text = label == null ? "元に戻せる操作はありません" : $"元に戻しました: {label}";
        _scenes.RefreshItems();
    }

    // 右クリックメニュー（プレビュー・ソース一覧共通）
    void ShowItemMenu(UIElement? target)
    {
        var item = _editor.Selected;
        var menu = new ContextMenu { PlacementTarget = target ?? this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Add(string header, Action a, bool enabled = true, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        var has = item != IntPtr.Zero;
        var vm = has ? _scenes.Items.FirstOrDefault(i => i.Item == item) : null;
        if (has && vm != null)
        {
            Add("設定…", () => OpenProperties(), gesture: "ダブルクリック");
            Add("フィルタ…", () => new FiltersWindow(vm.Source) { Owner = this }.ShowDialog());
            Add("名前を変更…", RenameSelected, gesture: "F2");
            menu.Items.Add(new Separator());
            Add("手前へ", () => MoveSelected(Native.OBS_ORDER_MOVE_UP));
            Add("奥へ", () => MoveSelected(Native.OBS_ORDER_MOVE_DOWN));
            Add("いちばん手前へ", () => MoveSelected(Native.OBS_ORDER_MOVE_TOP));
            Add("いちばん奥へ", () => MoveSelected(Native.OBS_ORDER_MOVE_BOTTOM));
            menu.Items.Add(new Separator());
            var locked = Native.obs_sceneitem_locked(item);
            Add("画面に合わせる", _editor.FitToScreen, !locked);
            Add("中央に置く", _editor.CenterOnScreen, !locked);
            Add("元の大きさに戻す", _editor.ResetSize, !locked);
            Add(locked ? "ロックを外す" : "ロックする（動かないように）", () => { Native.obs_sceneitem_set_locked(item, !locked); });
            Add(vm.Visible ? "非表示にする" : "表示する", () => vm.Visible = !vm.Visible);
            menu.Items.Add(new Separator());
            Add("削除", RemoveSelected, gesture: "Delete");
        }
        else
        {
            Add("ソースを追加…", () => ShowAddMenu(AddSourceButton));
        }
        if (_undo.NextLabel is { } label) { menu.Items.Add(new Separator()); Add($"元に戻す（{label}）", UndoLast, gesture: "Ctrl+Z"); }
        menu.IsOpen = true;
    }

    void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(Native.OBS_ORDER_MOVE_UP);
    void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(Native.OBS_ORDER_MOVE_DOWN);

    void MoveSelected(int movement)
    {
        if (SelectedItem is not { } vm) return;
        var item = vm.Item;
        var scene = _scenes.Current;
        var oldOrder = scene == null ? -1 : SceneManager.EnumItems(scene.Scene).IndexOf(item);
        if (oldOrder >= 0)
        {
            Native.obs_sceneitem_addref(item);
            _undo.Push("並べ替え", () => { Native.obs_sceneitem_set_order_position(item, oldOrder); Native.obs_sceneitem_release(item); });
        }
        // 一覧の作り直しで選択解除イベントが走っても編集対象を失わないよう、全体を同期抑制で囲む
        Sync(() =>
        {
            _scenes.Move(vm, movement);
            SourceList.SelectedItem = _scenes.Items.FirstOrDefault(i => i.Item == item);
        });
    }

    void OnSourceRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SourceList, (DependencyObject)e.OriginalSource) is ListBoxItem li) li.IsSelected = true;
        ShowItemMenu(SourceList);
        e.Handled = true;
    }

    void OnSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        Sync(() => _editor.Select(SelectedItem?.Item ?? IntPtr.Zero));
    }

    void OnProperties(object sender, RoutedEventArgs e)
    {
        // リストの空白部分のダブルクリックは無視
        if (e is MouseButtonEventArgs m && ItemsControl.ContainerFromElement(SourceList, (DependencyObject)m.OriginalSource) == null) return;
        OpenProperties();
    }

    bool OpenProperties()
    {
        var item = _editor.Selected;
        if (item == IntPtr.Zero) return false;
        // プレイリストは専用の画面で編集する
        var selSrc = Native.obs_sceneitem_get_source(item);
        if (PlaylistManager.IsPlaylist(selSrc)) { new PlaylistWindow(_playlists, selSrc) { Owner = this }.Show(); return true; }
        var w = new PropertiesWindow(Native.obs_sceneitem_get_source(item)) { Owner = this };
        w.ShowDialog();
        foreach (var i in _scenes.Items) i.Refresh();
        return w.Accepted;
    }

    void OnFit(object sender, RoutedEventArgs e) => _editor.FitToScreen();

    void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();

    internal AudioMixer Mixer => _mixer;

    // かんたんモード: VRM を選んだらそのまま配信画面に出す（別のウィンドウを開かせない）
    void PickVrm()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "VRM モデル|*.vrm|すべてのファイル|*.*", RestoreDirectory = true };
        if (d.ShowDialog(this) != true) return;
        _avatar.SetModel(d.FileName);
        if (!_scenes.Items.Any(i => i.Name == VCast.Avatar.AvatarController.SourceName)) _avatar.AddToScene(_scenes);
        _simple?.Refresh();
    }
    internal VerticalCanvas Vertical => _vertical;

    // --- 縦型の同時出力（有効なときだけキャンバスを動かす） ---
    internal void ApplyVertical()
    {
        var busy = _outputs.Streaming || _outputs.Recording;
        if (_settings.VerticalEnabled && !_vertical.Active) _vertical.Start((uint)_settings.Fps);
        else if (!_settings.VerticalEnabled && _vertical.Active && !busy) { _verticalWindow?.Close(); _vertical.Dispose(); }
        VerticalButton.Visibility = _settings.VerticalEnabled ? Visibility.Visible : Visibility.Collapsed;
        RelayoutVertical();
    }

    // 縦型のレイアウト: 本番（トランジション）とアバターを配置し直す
    internal void RelayoutVertical()
    {
        if (!_vertical.Active) return;
        var avatar = _scenes.Scenes.SelectMany(s => SceneManager.EnumItems(s.Scene)).Select(Native.obs_sceneitem_get_source)
            .FirstOrDefault(src => Native.Str(Native.obs_source_get_name(src)) == VCast.Avatar.AvatarController.SourceName);
        _vertical.Layout(_settings.VerticalLayout, Native.obs_get_output_source(0) is var prog && prog != IntPtr.Zero ? Release(prog) : IntPtr.Zero, avatar);
    }

    // obs_get_output_source は参照付きで返すので、借用として使うため即座に 1 つ返す（トランジションは TransitionManager が保持している）
    static IntPtr Release(IntPtr src) { Native.obs_source_release(src); return src; }

    void OnVerticalPreview(object sender, RoutedEventArgs e)
    {
        if (_verticalWindow != null) { _verticalWindow.Activate(); return; }
        if (!_vertical.Active) return;
        var host = new PreviewHost { CanvasWidth = VerticalCanvas.Width, CanvasHeight = VerticalCanvas.Height };
        host.RenderSource = _vertical.SceneSource;
        _verticalWindow = new Window
        {
            Title = "縦型プレビュー — VCast Studio", Width = 330, Height = 620, Owner = this, Background = Theme.Bg,
            Content = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Margin = new Thickness(8), Child = host },
        };
        _verticalWindow.Closed += (_, _) => { host.Dispose(); _verticalWindow = null; };
        _verticalWindow.Show();
    }
    internal TransitionManager Transitions => _transitions;

    // --- シーン切り替え・スタジオモード ---
    void OnSceneChosen()
    {
        if (_scenes.Current == null) return;
        if (_settings.StudioMode && ProOn) Preview.RenderSource = _scenes.Current.Source; // プレビューだけ変わる
        else { Preview.RenderSource = IntPtr.Zero; _transitions.Take(_scenes.Current.Source); }
        UpdateStudioLabels();
    }

    void InitTransitionUi()
    {
        TransitionCombo.Items.Clear();
        foreach (var t in TransitionManager.Types()) TransitionCombo.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Id });
        TransitionCombo.SelectedItem = TransitionCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _transitions.CurrentId);
        TransitionMs.Text = _transitions.DurationMs.ToString();
        ApplyStudio();
    }

    void OnTransitionType(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionCombo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        _transitions.SetType(id);
        _settings.TransitionId = id; _settings.Save();
    }

    void OnTransitionMs(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(TransitionMs.Text, out var ms) && ms >= 50 && ms <= 10000) { _transitions.DurationMs = ms; _settings.TransitionMs = ms; _settings.Save(); }
    }

    void OnStudioMode(object sender, RoutedEventArgs e)
    {
        _settings.StudioMode = !_settings.StudioMode;
        _settings.Save();
        ApplyStudio();
    }

    internal void ApplyStudio()
    {
        var studio = _settings.StudioMode && ProOn;
        StudioCtlCol.Width = studio ? GridLength.Auto : new GridLength(0);
        ProgramCol.Width = studio ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        StudioControls.Visibility = ProgramPane.Visibility = PreviewLabel.Visibility = studio ? Visibility.Visible : Visibility.Collapsed;
        StudioButton.Content = studio ? "🎬 スタジオモードを終わる" : "🎬 スタジオモード";
        if (studio && _scenes.Current != null) Preview.RenderSource = _scenes.Current.Source;
        if (!studio)
        {
            Preview.RenderSource = IntPtr.Zero;
            // 抜けるときは編集中のシーンを本番へ
            if (_scenes.Current != null && _transitions.ProgramScene != _scenes.Current.Source) _transitions.Take(_scenes.Current.Source);
        }
        UpdateStudioLabels();
    }

    void UpdateStudioLabels()
    {
        var program = _scenes.Scenes.FirstOrDefault(s => s.Source == _transitions.ProgramScene);
        PreviewLabel.Text = _scenes.Current == program ? "プレビュー（本番と同じシーン）" : $"プレビュー：{_scenes.Current?.Name}（次に出す画面を編集）";
        foreach (var item in SceneList.Items.OfType<SceneVm>()) { } // 一覧の表示は ListBox の選択で十分
    }

    void OnCut(object sender, RoutedEventArgs e) { if (_scenes.Current != null) _transitions.Take(_scenes.Current.Source, animate: false); }
    void OnAuto(object sender, RoutedEventArgs e) { if (_scenes.Current != null) _transitions.Take(_scenes.Current.Source, animate: true); }

    bool _tbarResetting;
    void OnTBar(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_tbarResetting || _scenes.Current == null) return;
        _transitions.TBar(_scenes.Current.Source, e.NewValue);
        if (e.NewValue >= 0.999) ResetTBar();
    }

    void OnTBarReleased(object sender, MouseButtonEventArgs e) { if (TBar.Value >= 0.999) ResetTBar(); }

    void ResetTBar()
    {
        _tbarResetting = true;
        Dispatcher.BeginInvoke(() => { TBar.Value = 0; _tbarResetting = false; });
    }
    internal Soundboard SoundboardCore => _soundboard;
    internal PlaylistManager Playlists => _playlists;

    void OnSoundboard(object sender, RoutedEventArgs e)
    {
        if (_soundboardWindow != null) { _soundboardWindow.Activate(); return; }
        _soundboardWindow = new SoundboardWindow(_soundboard) { Owner = this };
        _soundboardWindow.Closed += (_, _) => _soundboardWindow = null;
        _soundboardWindow.Show();
    }

    // 全シーンのプレイリストを見張る（1 本終わったら次へ）
    void SyncPlaylists() =>
        _playlists.Sync(_scenes.Scenes.SelectMany(s => SceneManager.EnumItems(s.Scene)).Select(Native.obs_sceneitem_get_source).Distinct().ToList());

    // 喋ると BGM が下がる: デスクトップ音声と、いまのシーンの動画・音声（プレイリスト含む）に掛ける
    internal IEnumerable<IntPtr> DuckTargets()
    {
        if (_mixer.Desktop != IntPtr.Zero) yield return _mixer.Desktop;
        foreach (var it in _scenes.Items)
            if (Native.Str(Native.obs_source_get_id(it.Source)) == "ffmpeg_source") yield return it.Source;
    }

    internal string MicName => Native.Str(Native.obs_source_get_name(_mixer.Mic)) ?? "マイク";
    internal SceneManager Scenes => _scenes;

    // 保存データ内のアバター描画ページの URL を今回の起動のもの（トークン入り）に差し替える
    string? FixAvatarUrl(string url) =>
        url.StartsWith("http://localhost:") && url.Contains("/avatar.html") ? _avatar.RenderUrl : null;

    void OnImportScenes(object sender, RoutedEventArgs e)
    {
        var obsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "basic", "scenes");
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "シーンコレクション (*.json)|*.json", InitialDirectory = Directory.Exists(obsDir) ? obsDir : null, RestoreDirectory = true };
        if (d.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "いまのシーンをすべて置き換えて読み込みます。よろしいですか？\n（マイクとデスクトップ音声の設定はそのままです）", "VCast Studio", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        ImportScenes(d.FileName);
    }

    // 読み込み後の本番: 保存されていた本番シーン、無ければ編集中のシーン
    void TakeProgramAfterLoad(string? programName)
    {
        var program = _scenes.Scenes.FirstOrDefault(s => s.Name == programName) ?? _scenes.Current;
        if (program != null) _transitions.Take(program.Source, animate: false);
    }

    internal bool ImportScenes(string path)
    {
        // 先に中身を確かめる（壊れたファイルで今のシーンを消さない）
        if (!SceneStore.LooksValid(path))
        {
            MessageBox.Show(this, "このファイルはシーンコレクションとして読み込めません。いまのシーンはそのままです。", "VCast Studio");
            return false;
        }
        // 取り込むシーンにブラウザソースがあれば、先にブラウザ部品を読み込む
        try { if (File.ReadAllText(path).Contains("\"browser_source\"")) ObsEngine.EnsureBrowser(); } catch { }
        _editor.Select(IntPtr.Zero);
        _scenes.Clear();
        var loaded = SceneStore.Load(path, _scenes, FixAvatarUrl);
        if (loaded?.DesktopData is { } dd && dd != IntPtr.Zero) Native.obs_data_release(dd);
        if (loaded?.MicData is { } md && md != IntPtr.Zero) Native.obs_data_release(md);
        if (_scenes.Scenes.Count == 0) _scenes.SetCurrent(_scenes.AddScene("シーン"));
        TakeProgramAfterLoad(loaded?.ProgramScene);
        _mixer.Rebuild();
        Status.Text = loaded == null ? "読み込めませんでした" : $"{Path.GetFileName(path)} を読み込みました（{_scenes.Scenes.Count} シーン）";
        return loaded != null;
    }
    internal bool SimpleModeOn => _settings.SimpleMode;

    // --- かんたん / プロ ---
    void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _settings.SimpleMode = !_settings.SimpleMode;
        _settings.Save();
        ApplyMode();
    }

    // プロモード = 詳細モード + 設定の「プロモードにする」
    internal bool ProOn => !_settings.SimpleMode && _settings.ProMode;

    internal void ApplyMode()
    {
        var simple = _settings.SimpleMode;
        if (simple && _simple == null)
        {
            _simple = new SimplePanel(_avatar, _mixer, _scenes, _settings, new SimplePanel.Actions(
                OpenAvatar: OpenAvatarWindow,
                PickModel: PickVrm,
                AddAvatar: () => AddAvatar(),
                AddSourceType: id => { var t = SceneManager.InputTypes().FirstOrDefault(x => x.Id == id); if (t != null) AddSource(t); },
                AddFiles: PickAndAddFiles,
                OpenSettings: () => OnSettings(this, new RoutedEventArgs()),
                RegisterMeter: m => { if (!_meters.Contains(m)) _meters.Add(m); },
                UnregisterMeter: m => _meters.Remove(m),
                DuckTargets: () => DuckTargets().ToList(),
                MicName: () => MicName));
            SimpleHost.Content = _simple;
        }
        SimpleHost.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        // かんたんモードの上部バーは、配信・録画・設定・アバター・使い方だけ（細かい機能はプロモードで）
        var pro = simple ? Visibility.Collapsed : Visibility.Visible;
        ReplayButton.Visibility = VcamButton.Visibility = SoundboardButton.Visibility = HotkeysButton.Visibility = pro;
        QuickAdd.Visibility = pro; // 空のときの中央の追加ボタン（右のパネルと二重になるので、かんたんモードでは出さない）
        EmptyTitle.Text = simple ? "まだ何も映っていません" : "このシーンはまだ空です";
        EmptySub.Text = simple ? "右の「アバター」や「映すもの」から追加しましょう" : "配信に映したいものを追加しましょう";
        if (IsInitialized && StudioButton != null) ApplyStudio();
        SourcePanel.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        ScenePanel.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        MixerPanel.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;
        SceneCol.Width = new GridLength(simple ? 0 : 210);
        SideCol.Width = new GridLength(simple ? 360 : 290);
        MixerRow.Height = new GridLength(simple ? 0 : 170);
        ModeButton.Content = simple ? "🔧 詳細モードへ" : "✨ かんたんモードへ";
        var proVis = ProOn ? Visibility.Visible : Visibility.Collapsed;
        ProfileButton.Visibility = proVis; TransitionBox.Visibility = proVis; MacroButton.Visibility = proVis; LayoutButton.Visibility = proVis;
        if (!ProOn && _settings.StudioMode) { _settings.StudioMode = false; ApplyStudio(); }
        _simple?.Refresh();
    }

    void PickAndAddFiles()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, RestoreDirectory = true,
            Filter = "画像・動画・音声|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.mkv;*.mov;*.webm;*.flv;*.avi;*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.html|すべてのファイル|*.*",
        };
        if (d.ShowDialog(this) == true) AddFiles(d.FileNames, null);
    }
    internal OutputManager Outputs => _outputs;
    internal AppSettings Settings => _settings;

    // --- 配信・録画 ---
    void OnSettings(object sender, RoutedEventArgs e)
    {
        var copy = _settings.Clone();
        var busy = _outputs.Streaming || _outputs.Recording || _outputs.ReplayActive;
        var simpleBefore = _settings.SimpleMode; var proBefore = _settings.ProMode;
        if (new SettingsWindow(copy, busy) { Owner = this }.ShowDialog() == true)
        {
            var keepChat = (_settings.ChatChannel, _settings.ChatAutoConnect, _settings.ChatTopmost, _settings.ChatRead, _settings.ChatSpeaker, _settings.ChatSpeed, _settings.ChatVolume, _settings.ChatReadName, _settings.ChatDict);
            _settings.CopyFrom(copy);
            // コメントビューの設定は別の窓で変えるので、設定画面の古い写しで上書きしない
            (_settings.ChatChannel, _settings.ChatAutoConnect, _settings.ChatTopmost, _settings.ChatRead, _settings.ChatSpeaker, _settings.ChatSpeed, _settings.ChatVolume, _settings.ChatReadName, _settings.ChatDict) = keepChat;
            _settings.Save(); ApplyVertical(); ApplyRemote();
            if (_settings.SimpleMode != simpleBefore || _settings.ProMode != proBefore) ApplyMode();
        }
    }

    void OnStream(object sender, RoutedEventArgs e)
    {
        if (_outputs.Streaming)
        {
            if (MessageBox.Show(this, "配信を終了しますか？", "VCast Studio", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) _outputs.StopStream();
            return;
        }
        if (_settings.Destinations.All(d => !d.Enabled || string.IsNullOrWhiteSpace(d.Key)))
        {
            MessageBox.Show(this, "まず配信先とストリームキーを設定してください。\n\nYouTube の場合: YouTube Studio →「ライブ配信を開始」→ ストリームキーをコピーして、次の画面に貼り付けます。\nTwitch の場合: クリエイターダッシュボード → 設定 → 配信 → プライマリストリームキー", "VCast Studio");
            OnSettings(sender, e);
            return;
        }
        if (_settings.PreflightCheck)
        {
            var avatarInScene = _scenes.Items.Any(i => i.Name == VCast.Avatar.AvatarController.SourceName);
            var pf = new PreflightWindow(new PreflightWindow.Context(_settings, _mixer, _scenes, avatarInScene, _avatar.ModelStatus, _settings.SimpleMode)) { Owner = this };
            pf.ShowDialog();
            if (!pf.Proceed) return;
        }
        if (!_outputs.StartStream(out var err)) MessageBox.Show(this, "配信を開始できませんでした。\n" + err, "VCast Studio");
        else if (err.Length > 0) Status.Text = "一部の配信先で失敗: " + err;
        UpdateOutputButtons();
    }

    // ---- プロモード: マクロ / レイアウト集 ----
    VCast.Ui.MacroWindow? _macroWin;

    void OnMacros(object sender, RoutedEventArgs e)
    {
        if (_macroWin is { IsVisible: true }) { _macroWin.Hide(); return; }
        // 開くたびに作り直す（シーンを増やした・名前を変えた直後でも、選択肢が最新になる）
        _macroWin?.CloseForReal();
        _macroWin = new VCast.Ui.MacroWindow(_settings, () => _scenes.Scenes.Select(s => s.Name).ToList(), RunMacro, () => _settings.Save()) { Owner = this };
        _macroWin.Show(); _macroWin.Activate();
    }

    internal void RunMacro(int i)
    {
        if (i < 0 || i >= _settings.Macros.Count) return;
        var m = _settings.Macros[i];
        var did = new List<string>();
        if (m.Scene.Length > 0 && _scenes.Scenes.FirstOrDefault(s => s.Name == m.Scene) is { } target)
        {
            if (_settings.StudioMode && ProOn) { _scenes.SetCurrent(target); _transitions.Take(target.Source); } else _scenes.SetCurrent(target);
            did.Add("シーン");
        }
        if (m.Expression.Length > 0) { _avatar.SetExpression(m.Expression); did.Add("表情"); }
        if (m.Mic.Length > 0 && _mixer.Mic != IntPtr.Zero) { SetMute(_mixer.Mic, m.Mic == "mute"); did.Add("マイク"); }
        if (m.Pad > 0) { _soundboard.Play(m.Pad - 1); did.Add("効果音"); }
        if (m.Record == "start" && !_outputs.Recording) { _outputs.StartRecord(out _); did.Add("録画開始"); }
        else if (m.Record == "stop" && _outputs.Recording) { _outputs.StopRecord(); did.Add("録画停止"); }
        UpdateOutputButtons();
        Status.Text = $"マクロ「{m.Name}」: {(did.Count > 0 ? string.Join("・", did) : "実行する内容がありません")}";
    }

    void OnLayouts(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LayoutButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (id, label) in new[] { ("corner", "ゲーム全画面 ＋ アバターを右下に小さく"), ("big", "ゲーム全画面 ＋ アバターを右に大きく"), ("split", "左右 2 分割（ゲームが左・アバターが右）"), ("avatar", "アバターだけ（ほかは隠す）") })
        {
            var mi = new MenuItem { Header = label };
            var lid = id; mi.Click += (_, _) => ApplyLayout(lid);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // 「ゲーム（一番大きい映像）」と「アバター」を、キャンバスに合わせて置き直す。ほかのソースには触らない
    internal void ApplyLayout(string id)
    {
        var avatar = _scenes.Items.FirstOrDefault(i => i.Name == VCast.Avatar.AvatarController.SourceName);
        var main = _scenes.Items.Where(i => i != avatar && Native.obs_source_get_width(i.Source) > 0)
                               .OrderByDescending(i => (long)Native.obs_source_get_width(i.Source) * Native.obs_source_get_height(i.Source)).FirstOrDefault();
        if (main == null && avatar == null) { Status.Text = "このシーンには、並べるもの（ゲーム画面やアバター）がありません"; return; }
        float W = ObsEngine.BaseWidth, H = ObsEngine.BaseHeight;
        void Fit(SceneItemVm it, float x, float y, float w, float h, bool alignBottomRight = false)
        {
            float sw = Native.obs_source_get_width(it.Source), sh = Native.obs_source_get_height(it.Source);
            if (sw <= 0 || sh <= 0) return;
            var k = Math.Min(w / sw, h / sh);
            Native.obs_sceneitem_set_rot(it.Item, 0); Native.obs_sceneitem_set_bounds_type(it.Item, 0);
            var sc = new Native.vec2 { x = k, y = k }; Native.obs_sceneitem_set_scale(it.Item, ref sc);
            var px = x + (w - sw * k) / (alignBottomRight ? 1 : 2); var py = y + (h - sh * k) / (alignBottomRight ? 1 : 2);
            var p = new Native.vec2 { x = px, y = py }; Native.obs_sceneitem_set_pos(it.Item, ref p);
            it.Visible = true;
        }
        switch (id)
        {
            case "corner": if (main != null) Fit(main, 0, 0, W, H); if (avatar != null) Fit(avatar, W * 0.70f, H * 0.38f, W * 0.30f, H * 0.62f, true); break;
            case "big": if (main != null) Fit(main, 0, 0, W, H); if (avatar != null) Fit(avatar, W * 0.55f, 0, W * 0.45f, H, true); break;
            case "split": if (main != null) Fit(main, 0, 0, W * 0.5f, H); if (avatar != null) Fit(avatar, W * 0.5f, 0, W * 0.5f, H); break;
            case "avatar": if (main != null) main.Visible = false; if (avatar != null) Fit(avatar, 0, 0, W, H); break;
        }
        if (avatar != null) Native.obs_sceneitem_set_order(avatar.Item, 2); // アバターを最前面に（OBS_ORDER_MOVE_TOP）
        Status.Text = "配置を整えました（元に戻すときは Ctrl+Z）";
    }

    // ---- 緊急: 映像を隠して音を消す（放送事故対策。もう一度押すと元に戻る） ----
    IntPtr _panicCover;
    List<(AudioStrip strip, bool wasMuted)>? _panicMutes;
    public bool PanicOn => _panicCover != IntPtr.Zero;

    internal void TogglePanic()
    {
        if (!PanicOn)
        {
            var s = Native.obs_data_create();
            Native.obs_data_set_int(s, "color", 0xFF111118);
            Native.obs_data_set_int(s, "width", (long)ObsEngine.BaseWidth);
            Native.obs_data_set_int(s, "height", (long)ObsEngine.BaseHeight);
            _panicCover = Native.obs_source_create_private("color_source_v3", "vcast_panic_cover", s);
            Native.obs_data_release(s);
            if (_panicCover == IntPtr.Zero) { Status.Text = "隠す画面を作れませんでした"; return; }
            _panicMutes = _mixer.Strips.Select(x => (x, x.Muted)).ToList();
            foreach (var (st, _) in _panicMutes) st.Muted = true;
            _voice.StopAll();
            Native.obs_set_output_source(0, _panicCover); // 配信・録画・仮想カメラ・プレビューのすべてが隠れる
            Status.Text = "🙈 映像を隠して音を消しています（もう一度押すと戻ります）";
        }
        else
        {
            foreach (var (st, was) in _panicMutes ?? new()) st.Muted = was;
            _panicMutes = null;
            _transitions.ReattachOutput(); // 本番のシーンへ戻す（シーンの切り替え状態はそのまま）
            Native.obs_source_release(_panicCover); _panicCover = IntPtr.Zero;
            Status.Text = "映像と音を元に戻しました";
        }
        PanicButton.Content = PanicOn ? "🙈 戻す" : "🙈 隠す";
        PanicButton.Background = PanicOn ? Theme.Live : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#26262E")!;
    }

    // ---- コメントビュー（Twitch チャット + VOICEVOX 読み上げ） ----
    readonly TwitchChat _chat = new();
    readonly VoiceVoxReader _voice = new();
    VCast.Ui.CommentWindow? _commentWin;

    VCast.Ui.CommentWindow Comments()
    {
        if (_commentWin == null)
        {
            _commentWin = new VCast.Ui.CommentWindow(_settings, _chat, _voice, () => _settings.Save()) { Owner = this };
            _commentWin.IsVisibleChanged += (_, _) => CommentButton.BorderBrush = _commentWin.IsVisible ? Theme.Accent : Theme.Line;
        }
        return _commentWin;
    }

    void OnPanic(object sender, RoutedEventArgs e) => TogglePanic();

    void OnToggleComments(object sender, RoutedEventArgs e)
    {
        var w = Comments();
        if (w.IsVisible) w.Hide(); else { w.Show(); w.Activate(); }
    }

    // 起動時: 前回のチャンネルへ自動で繋ぐ（コメント窓は開かず、読み上げだけ動く）
    void StartChatIfWanted()
    {
        if (!_settings.ChatAutoConnect || TwitchChat.NormalizeChannel(_settings.ChatChannel).Length == 0) return;
        Comments().ApplyReader();
        _chat.Start(_settings.ChatChannel);
    }

    void OnRecord(object sender, RoutedEventArgs e)
    {
        // 停止の直後にもう一度押してしまっても、新しい録画が始まらないようにする（停止中は押せない・停止から少しの間は無視）
        if (_outputs.RecordStopping)
        {
            // 5 秒たっても保存が終わらないときは、もう一度押すと強制的に止める
            if ((DateTime.Now - _recordStopAt).TotalSeconds > 5) { _outputs.ForceStopRecord(); Status.Text = "録画を強制的に止めました"; }
            return;
        }
        if ((DateTime.Now - _recordToggleAt).TotalMilliseconds < 1500) return;
        _recordToggleAt = DateTime.Now;
        if (_outputs.Recording) { _recordStopAt = DateTime.Now; _outputs.StopRecord(); UpdateOutputButtons(); return; }
        if (!_outputs.StartRecord(out var err)) MessageBox.Show(this, "録画を開始できませんでした。\n" + err, "VCast Studio");
        UpdateOutputButtons();
    }

    DateTime _recordToggleAt = DateTime.MinValue, _recordStopAt = DateTime.MinValue;

    void OnOpenRecordFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.RecordFolder);
            // 直前の録画があればそのファイルを選んだ状態で開く
            var last = _outputs.LastRecordingPath;
            if (last != null && File.Exists(last)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{last}\"");
            else System.Diagnostics.Process.Start("explorer.exe", $"\"{_settings.RecordFolder}\"");
        }
        catch (Exception ex) { Status.Text = "フォルダを開けませんでした: " + ex.Message; }
    }

    void OnReplay(object sender, RoutedEventArgs e)
    {
        if (!_outputs.ReplayActive)
        {
            if (!_outputs.StartReplay(out var err)) MessageBox.Show(this, "リプレイを開始できませんでした。\n" + err, "VCast Studio");
            else Status.Text = $"リプレイ待機中: もう一度押すと直前 {_settings.ReplaySeconds} 秒を保存します";
        }
        else if (!_outputs.SaveReplay()) Status.Text = "リプレイを保存できませんでした";
        UpdateOutputButtons();
    }

    void OnReplayStop(object sender, MouseButtonEventArgs e) { _outputs.StopReplay(); e.Handled = true; }

    void OnVirtualCam(object sender, RoutedEventArgs e)
    {
        if (_outputs.VirtualCamActive) { _outputs.StopVirtualCam(); return; }
        if (!_outputs.StartVirtualCam(out var err)) MessageBox.Show(this, "仮想カメラを開始できませんでした。\n" + err, "VCast Studio");
        UpdateOutputButtons();
    }

    void OnOutputEvent(OutputEvent ev)
    {
        ObsEngine.Log($"[output] {ev.Kind} {ev.Name}: {ev.Message}");
        Status.Text = $"{ev.Name}: {ev.Message}";
        // 異常終了は見落とさないようダイアログでも知らせる（自己テスト中は出さない）
        if (ev.IsError && !ObsEngine.TestMode)
            MessageBox.Show(this, $"{ev.Name}: {ev.Message}", "VCast Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateOutputButtons();
    }

    void UpdateOutputButtons()
    {
        StreamButton.Content = _outputs.Streaming ? "■ 配信終了" : "● 配信開始";
        StreamButton.Background = _outputs.Streaming ? Theme.Live : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#26262E")!;
        RecordButton.Content = _outputs.RecordStopping ? "保存中…" : _outputs.Recording ? "■ 録画停止" : "● 録画";
        RecordButton.Background = _outputs.Recording ? Theme.Record : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#26262E")!;
        ReplayButton.Content = _outputs.ReplayActive ? "↺ 直前を保存" : "↺ リプレイ";
        ReplayButton.BorderBrush = _outputs.ReplayActive ? Theme.Accent : Theme.Line;
        VcamButton.Content = _outputs.VirtualCamActive ? "📷 仮想カメラ停止" : "📷 仮想カメラ";
        VcamButton.BorderBrush = _outputs.VirtualCamActive ? Theme.Accent : Theme.Line;
    }

    // 1 秒ごと: 配信状態と負荷の表示
    void OnSecond()
    {
        var s = _perf.Sample();
        Perf.Text = $"CPU {s.CpuPercent:0.0}%  ・ メモリ {s.MemoryMb:0} MB  ・ {s.Fps:0} fps  ・ 描画落ち {s.RenderLagPercent:0.#}%  ・ 圧縮落ち {s.EncodeSkipPercent:0.#}%";
        Perf.Foreground = s.RenderLagPercent > 2 || s.EncodeSkipPercent > 2 ? Theme.Live : Theme.Sub;
        var parts = new List<string>();
        if (_outputs.Streaming && _outputs.StreamStartedAt is { } st)
        {
            var (kbps, dropped, total, cong) = _outputs.StreamStats();
            var dropPct = total > 0 ? dropped * 100.0 / total : 0;
            parts.Add($"🔴 LIVE {DateTime.Now - st:hh\\:mm\\:ss}  {kbps:0} kbps  送信落ち {dropPct:0.#}%" + (cong > 0.5 ? "  ⚠ 回線が混雑" : "") + (_settings.StreamDelaySec > 0 ? $"  ⏱ {_settings.StreamDelaySec}秒遅延" : ""));
        }
        if (_outputs.Recording && _outputs.RecordStartedAt is { } rt) parts.Add($"⏺ 録画 {DateTime.Now - rt:hh\\:mm\\:ss}");
        if (_outputs.ReplayActive) parts.Add("↺ リプレイ待機中");
        if (_outputs.VirtualCamActive) parts.Add("📷 仮想カメラ");
        if (parts.Count == 0 && !_settings.Destinations.Any(d => d.Enabled && !string.IsNullOrEmpty(d.KeyProtected)))
            parts.Add("⚠ 配信先が未設定（「⚙ 設定」でストリームキーを入れてください）");
        LiveStatus.Text = string.Join("   ", parts);
        LiveStatus.Foreground = _outputs.Streaming ? Theme.Live : parts.Any(p => p.StartsWith("⚠")) ? Theme.Record : Theme.Sub;
    }

    void OnClosingConfirm(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || Environment.GetCommandLineArgs().Contains("--selftest") || Environment.GetCommandLineArgs().Contains("--soak")) return;
        if (!Environment.GetCommandLineArgs().Contains("--soak") && (_outputs.Streaming || _outputs.Recording))
        {
            var what = _outputs.Streaming ? "配信" : "録画";
            if (MessageBox.Show(this, $"{what}中です。終了すると{what}も止まります。終了しますか？", "VCast Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
        }
        _closingConfirmed = true;
        if (_avatar.Settings.WalkMode) _avatar.SetWalk(false, _scenes); // 枠を元の位置・大きさへ戻してから保存する
    }
    internal VCast.Avatar.AvatarController Avatar => _avatar;

    // --- アバター ---
    void OnAvatar(object sender, RoutedEventArgs e) => OpenAvatarWindow();

    void OpenAvatarWindow()
    {
        if (_avatarWindow != null) { _avatarWindow.Activate(); return; }
        _avatarWindow = new AvatarWindow(_avatar, () => AddAvatar(), () => _scenes.Items.Any(i => i.Name == VCast.Avatar.AvatarController.SourceName), () => _avatar.ApplyResolution(_scenes)) { Owner = this };
        _avatarWindow.Closed += (_, _) => _avatarWindow = null;
        _avatarWindow.Show();
    }

    internal SceneItemVm? AddAvatar()
    {
        var vm = _avatar.AddToScene(_scenes);
        if (vm != null) _editor.Select(vm.Item);
        if (_avatar.Settings.ModelPath == null)
        {
            Status.Text = "アバターを置きました。「🧍 アバター」で VRM ファイルを選んでください";
            OpenAvatarWindow();
        }
        return vm;
    }

    // --- 音声ミキサー ---
    void OnMeterLoaded(object sender, RoutedEventArgs e) { if (sender is LevelMeter m && !_meters.Contains(m)) _meters.Add(m); }
    void OnMeterUnloaded(object sender, RoutedEventArgs e) { if (sender is LevelMeter m) _meters.Remove(m); }

    void OnMonitoring(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioStrip s) return;
        s.Monitoring = (s.Monitoring + 1) % 3;
    }

    void OnStripMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AudioStrip s) return;
        var menu = new ContextMenu { PlacementTarget = fe };
        var filters = new MenuItem { Header = "フィルタ（ノイズ除去・コンプなど）…" };
        filters.Click += (_, _) => new FiltersWindow(s.Source) { Owner = this }.ShowDialog();
        menu.Items.Add(filters);
        var clean = new MenuItem { Header = "🎙 声をきれいにする（おすすめ4点セット）" };
        clean.Click += (_, _) =>
        {
            var n = Filters.ApplyCleanMicPreset(s.Source);
            Status.Text = n > 0 ? $"「{s.Name}」にノイズ除去・ゲート・コンプ・リミッターを追加しました" : "すでに全部入っています";
        };
        menu.Items.Add(clean);
        if (s.Source != _mixer.Mic)
        {
            var duck = new MenuItem { Header = "喋ったら下げる（自動ダッキング）", IsCheckable = true, IsChecked = Filters.HasDucking(s.Source) };
            duck.Click += (_, _) => Filters.SetDucking(s.Source, MicName, duck.IsChecked);
            menu.Items.Add(duck);
        }
        var props = new MenuItem { Header = s.IsGlobal ? "デバイスの選択…" : "ソースの設定…" };
        props.Click += (_, _) => { new PropertiesWindow(s.Source) { Owner = this }.ShowDialog(); s.Refresh(); };
        menu.Items.Add(props);
        menu.IsOpen = true;
    }

    void OnSourceFilters(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } vm) { Status.Text = "フィルタを付けるソースを選んでください"; return; }
        new FiltersWindow(vm.Source) { Owner = this }.ShowDialog();
    }

    // --- 空のシーンの案内 ---
    void UpdateEmptyState()
    {
        var empty = _scenes.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void BuildQuickAdd()
    {
        void Tile(string icon, string label, string tip, Action onClick)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = icon, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            var b = new Button
            {
                Content = sp, Width = 112, Height = 92, Margin = new Thickness(6), ToolTip = tip,
                Background = Theme.Input, Foreground = Theme.Fg, BorderBrush = Theme.Line,
            };
            b.Click += (_, _) => onClick();
            QuickAdd.Children.Add(b);
        }
        var types = SceneManager.InputTypes();
        void Type(string icon, string id, string label)
        {
            var t = types.FirstOrDefault(x => x.Id == id);
            if (t != null) Tile(icon, label, SourceCatalog.Describe(id), () => AddSource(t));
        }
        Tile("🧍", "3Dアバター", SourceCatalog.Describe(SourceCatalog.AvatarId), () => AddAvatar());
        Type("🎮", "game_capture", "ゲーム画面");
        Type("🪟", "window_capture", "ウィンドウ");
        Type("🖥", "monitor_capture", "画面全体");
        Type("📷", "dshow_input", "カメラ");
        Tile("🖼", "画像・動画", "画像・動画・音声ファイルを選んで追加", () =>
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true, RestoreDirectory = true,
                Filter = "画像・動画・音声|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.mkv;*.mov;*.webm;*.flv;*.avi;*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.html|すべてのファイル|*.*",
            };
            if (d.ShowDialog(this) == true) AddFiles(d.FileNames, null);
        });
        Tile("＋", "そのほか", "すべての種類から選ぶ", () => ShowAddMenu(QuickAdd));
    }

    // --- ファイルのドラッグ＆ドロップ ---
    void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnEmptyDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files, null);
    }

    void AddFiles(string[] files, PreviewPointer? at)
    {
        var skipped = new List<string>();
        SceneItemVm? last = null;
        var offset = 0;
        foreach (var f in files)
        {
            var vm = SourceCatalog.CanOpen(f) ? SourceCatalog.AddFile(_scenes, f) : null;
            if (vm == null) { skipped.Add(Path.GetFileName(f)); continue; }
            // 複数ファイルは少しずつずらして重ならないように
            PlaceAt(vm.Item, (at?.X ?? ObsEngine.BaseWidth / 2.0) + offset, (at?.Y ?? ObsEngine.BaseHeight / 2.0) + offset);
            offset += 40;
            last = vm;
        }
        if (last != null) _editor.Select(last.Item);
        if (skipped.Count > 0)
            MessageBox.Show(this, "次のファイルは追加できませんでした（対応していない形式）:\n" + string.Join("\n", skipped), "VCast Studio");
    }

    // ソースの大きさは読み込み後に決まるので、決まるまで最大3秒待ってから中心を合わせる
    static void PlaceAt(IntPtr item, double x, double y)
    {
        Native.obs_sceneitem_addref(item);
        var tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool TryPlace()
        {
            var src = Native.obs_sceneitem_get_source(item);
            if (Native.obs_source_get_width(src) == 0 && ++tries <= 30) return false;
            SceneManager.CenterAt(item, x, y);
            Native.obs_sceneitem_release(item);
            return true;
        }
        if (TryPlace()) return;
        timer.Tick += (_, _) => { if (TryPlace()) timer.Stop(); };
        timer.Start();
    }

    // --- 開いているアプリ（タスクバーの代わり） ---
    WindowInfo? _appDown;
    Point _appDownPos;
    bool _appDragging;
    DragGhost? _ghost;
    FrameworkElement? _appTile;

    void OnRefreshApps(object sender, RoutedEventArgs e) => RefreshApps();

    // 右クリック → そのアプリの音だけを取り込む（出力先が既定でないゲームや、デスクトップ音声で拾えないとき用）
    void OnWalkToggle(object sender, RoutedEventArgs e) => _avatar.SetWalk(!_avatar.Settings.WalkMode, _scenes);

    void RefreshWalkButton()
    {
        var st = _avatar.Settings;
        WalkButton.Visibility = st.WalkEnabled ? Visibility.Visible : Visibility.Collapsed;
        WalkButton.Content = st.WalkMode ? "🚶 WASD ON" : "🚶 WASD OFF";
        WalkButton.Background = st.WalkMode ? Theme.Accent : Theme.Input;
    }

    void OnAddAppAudio(object sender, RoutedEventArgs e)
    {
        if (((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is not FrameworkElement { DataContext: WindowInfo w }) return;
        var vm = SourceCatalog.AddAppAudio(_scenes, w);
        if (vm == null) { MessageBox.Show(this, "アプリの音を追加できませんでした。", "VCast Studio"); return; }
        _editor.Select(vm.Item);
        Status.Text = $"「{w.Exe}」の音を追加しました。ミキサーのメーターが動けば取れています";
    }

    void RefreshApps()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        AppStrip.ItemsSource = WindowList.Enumerate(hwnd);
    }

    void OnAppDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WindowInfo w) return;
        _appDown = w;
        _appDownPos = e.GetPosition(this);
        _appDragging = false;
        _appTile = (FrameworkElement)sender;
        SetTilePressed(_appTile, true); // 押した瞬間に少し沈む
        _appTile.CaptureMouse();
        e.Handled = true;
    }

    void OnAppMove(object sender, MouseEventArgs e)
    {
        if (_appDown == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _appDownPos;
        if (!_appDragging && Math.Abs(d.X) + Math.Abs(d.Y) > 6)
        {
            // 持ち上げ: 元のカードは半透明にして、カーソルにカードを付ける
            _appDragging = true;
            Mouse.OverrideCursor = Cursors.Hand;
            if (_appTile != null) { SetTilePressed(_appTile, false); _appTile.Opacity = 0.35; }
            _ghost = new DragGhost(_appDown.Icon, _appDown.Title);
            Status.Text = $"「{_appDown.Title}」をプレビューの上で離すと、その位置に追加します";
        }
        if (_appDragging && _ghost != null)
        {
            GetCursorPos(out var cp);
            var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _ghost.MoveTo(cp.X, cp.Y, dpi);
            if (!_ghost.IsVisible) _ghost.Show();
            var over = Preview.ScreenToCanvas(cp.X, cp.Y) != null || (EmptyState.IsVisible && IsInside(EmptyState, e));
            _ghost.SetOver(over);
            PreviewFrame.BorderBrush = over ? Theme.Accent : (System.Windows.Media.Brush)FindResource("Line");
            PreviewFrame.BorderThickness = new Thickness(over ? 2 : 1);
        }
    }

    void OnAppUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        _ghost?.Close();
        _ghost = null;
        if (_appTile != null) { SetTilePressed(_appTile, false); _appTile.Opacity = 1; _appTile = null; }
        PreviewFrame.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");
        PreviewFrame.BorderThickness = new Thickness(1);
        var w = _appDown;
        var dragging = _appDragging;
        _appDown = null;
        _appDragging = false;
        Status.Text = DefaultStatus;
        if (w == null) return;

        PreviewPointer? at = null;
        if (dragging)
        {
            var screen = PointToScreen(e.GetPosition(this));
            at = Preview.ScreenToCanvas((int)screen.X, (int)screen.Y);
            var overEmpty = EmptyState.IsVisible && IsInside(EmptyState, e);
            if (at == null && !overEmpty) return; // プレビューの外で離したら何もしない
        }
        var vm = SourceCatalog.AddWindow(_scenes, w);
        if (vm == null) { MessageBox.Show(this, "ウィンドウを追加できませんでした。", "VCast Studio"); return; }
        PlaceAt(vm.Item, at?.X ?? ObsEngine.BaseWidth / 2.0, at?.Y ?? ObsEngine.BaseHeight / 2.0);
        _editor.Select(vm.Item);
    }

    static void SetTilePressed(FrameworkElement tile, bool pressed)
    {
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        tile.RenderTransform = pressed ? new System.Windows.Media.ScaleTransform(0.94, 0.94) : System.Windows.Media.Transform.Identity;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32")] static extern bool GetCursorPos(out POINT p);

    static bool IsInside(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        // リストにフォーカスがあるときの矢印キーはリスト移動に使う
        var inList = Keyboard.FocusedElement is ListBox or ListBoxItem;
        if (inList && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10f : 1f;
        switch (e.Key)
        {
            case Key.Delete: RemoveSelected(); break;
            case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): UndoLast(); break;
            case Key.F2: RenameSelected(); break;
            case Key.Left: _editor.Nudge(-step, 0); break;
            case Key.Right: _editor.Nudge(step, 0); break;
            case Key.Up: _editor.Nudge(0, -step); break;
            case Key.Down: _editor.Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }
}
