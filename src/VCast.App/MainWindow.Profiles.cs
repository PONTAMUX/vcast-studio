using System.Windows;
using System.Windows.Controls;
using VCast.Core;
using VCast.Obs;
using VCast.Ui;

namespace VCast;

// プロファイル（番組ごとの設定＋シーン）の切り替え・作成・複製・名前変更・削除
public partial class MainWindow
{
    internal void UpdateProfileButton() => ProfileButton.Content = $"📁 {Profiles.Current} ▾";

    void OnProfiles(object sender, RoutedEventArgs e)
    {
        var busy = _outputs.Streaming || _outputs.Recording;
        var menu = new ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var name in Profiles.List())
        {
            var n = name;
            var mi = new MenuItem { Header = n, IsCheckable = true, IsChecked = n == Profiles.Current, IsEnabled = !busy || n == Profiles.Current };
            mi.Click += (_, _) => { if (n != Profiles.Current) SwitchProfile(n); else mi.IsChecked = true; };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        void Add(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Add("新しいプロファイルを作る…（初期設定・空のシーン）", () => NewProfile(copy: false), !busy);
        Add("いまのプロファイルを複製…（設定もシーンもそのまま）", () => NewProfile(copy: true), !busy);
        Add("名前を変える…", RenameProfile);
        Add("削除…", DeleteProfile, Profiles.List().Count > 1);
        if (busy) { menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "配信・録画中は切り替えられません", IsEnabled = false }); }
        menu.IsOpen = true;
    }

    // いまの設定とシーンを保存（プロファイルを離れる前）
    internal bool ForceProfileSave; // 自己テスト用（通常のテストではユーザーのデータ扱いのファイルを書かない）
    bool _profileSwitching;         // 切り替え中は自動保存を止める（途中の状態を別のプロファイルへ保存しないため）
    bool SaveCurrentProfile()
    {
        if (ObsEngine.TestMode && !ForceProfileSave) return true;
        if (_profileSwitching) return true;
        return _settings.Save() & SceneStore.Save(SceneStore.DefaultPath, _scenes, _mixer, _transitions.ProgramScene, _vertical.SceneSource);
    }

    // 切り替え先のシーンを読み込む。常設音声もそのプロファイルのものに入れ替える。読めなかったら false
    bool LoadProfileScenes(string path)
    {
        var exists = File.Exists(path) || File.Exists(path + ".bak");
        try { if (exists && File.ReadAllText(File.Exists(path) ? path : path + ".bak").Contains("\"browser_source\"")) ObsEngine.EnsureBrowser(); } catch { }
        _editor.Select(IntPtr.Zero);
        _scenes.Clear();
        SceneStore.Loaded? loaded = null;
        if (exists) loaded = SceneStore.Load(path, _scenes, FixAvatarUrl, (d, m) => _mixer.ReplaceGlobals(d, m));
        if (loaded == null) _mixer.ReplaceGlobals(IntPtr.Zero, IntPtr.Zero); // 新しいプロファイルは既定の音声設定から
        if (loaded?.DesktopData is { } dd && dd != IntPtr.Zero) Native.obs_data_release(dd);
        if (loaded?.MicData is { } md && md != IntPtr.Zero) Native.obs_data_release(md);
        if (_scenes.Scenes.Count == 0) _scenes.SetCurrent(_scenes.AddScene("メイン"));
        TakeProgramAfterLoad(loaded?.ProgramScene);
        _mixer.Rebuild();
        return loaded != null || !exists;
    }

    internal void SwitchProfile(string name)
    {
        if (name == Profiles.Current || !Profiles.Exists(name)) return;
        if (_outputs.Streaming || _outputs.Recording) { MessageBox.Show(this, "配信・録画中はプロファイルを切り替えられません。", "VCast Studio"); return; }
        // リプレイは出力設定（保存先・長さ・エンコーダ）を持つので、いったん止めてから切り替える
        var replayWas = _outputs.ReplayActive;
        if (replayWas) { _outputs.StopReplay(); for (var i = 0; i < 60 && _outputs.ReplayActive; i++) System.Threading.Thread.Sleep(50); }
        // 1) いまのものを保存できなければ切り替えない（データを失わないため）
        if (!SaveCurrentProfile())
        {
            MessageBox.Show(this, "いまの設定とシーンを保存できなかったので、切り替えを取りやめました。ディスクの空きを確認してください。", "VCast Studio");
            if (replayWas) _outputs.StartReplay(out _);
            return;
        }
        // 2) 切り替え先が壊れていないか先に確認（読めないファイルで、いまのシーンを消さない）
        var target = Profiles.ScenesPathOf(name);
        if ((File.Exists(target) || File.Exists(target + ".bak")) && !SceneStore.LooksValid(target))
        {
            MessageBox.Show(this, $"プロファイル「{name}」のシーンを読み込めません。切り替えを取りやめました。", "VCast Studio");
            if (replayWas) _outputs.StartReplay(out _);
            return;
        }
        var previous = Profiles.Current;
        if (!Profiles.SetCurrent(name)) { MessageBox.Show(this, "切り替えを記録できませんでした。", "VCast Studio"); if (replayWas) _outputs.StartReplay(out _); return; }
        _profileSwitching = true;
        try
        {
            _settings.CopyFrom(AppSettings.Load());
            _undo.Clear();
            if (!LoadProfileScenes(Profiles.ScenesPath)) Status.Text = "シーンを一部読み込めませんでした";
        }
        finally { _profileSwitching = false; }
        ApplyProfileSettings();
        Status.Text = $"プロファイル「{name}」に切り替えました";
        ObsEngine.Log($"[profiles] switched to {name}");
    }

    // 設定が入れ替わったあとに、画面と動作を新しい設定に合わせる
    void ApplyProfileSettings()
    {
        _transitions.DurationMs = _settings.TransitionMs;
        _transitions.SetType(_settings.TransitionId);
        InitTransitionUi();
        ApplyMode();
        ApplyStudio();
        ApplyVertical();
        ApplyRemote();
        UpdateOutputButtons();
        UpdateProfileButton();
        _simple?.Refresh();
        if (_settings.ReplayEnabled && !_outputs.ReplayActive) _outputs.StartReplay(out _);
    }

    void NewProfile(bool copy)
    {
        var suggested = copy ? $"{Profiles.Current} のコピー" : "新しい番組";
        var name = InputDialog.Ask(this, copy ? "プロファイルを複製" : "プロファイルを作る", "プロファイル名（番組名・出演者名など）", suggested);
        if (name == null) return;
        name = name.Trim();
        if (Profiles.ValidateName(name) is { } err) { MessageBox.Show(this, err, "VCast Studio"); return; }
        SaveCurrentProfile(); // 複製元に最新の状態を反映してからコピー
        Profiles.Create(name, copy ? Profiles.Current : null);
        SwitchProfile(name);
    }

    void RenameProfile()
    {
        var name = InputDialog.Ask(this, "プロファイルの名前を変える", "新しい名前", Profiles.Current);
        if (name == null || name.Trim() == Profiles.Current) return;
        name = name.Trim();
        if (Profiles.ValidateName(name) is { } err) { MessageBox.Show(this, err, "VCast Studio"); return; }
        SaveCurrentProfile();
        try { Profiles.Rename(Profiles.Current, name); }
        catch (Exception ex) { MessageBox.Show(this, "名前を変えられませんでした: " + ex.Message, "VCast Studio"); return; }
        UpdateProfileButton();
    }

    void DeleteProfile()
    {
        var others = Profiles.List().Where(n => n != Profiles.Current).ToList();
        if (others.Count == 0) return;
        var target = others.Count == 1 ? others[0] : InputDialog.Ask(this, "プロファイルを削除", $"削除するプロファイル名を入力（{string.Join(" / ", others)}）", others[0]);
        if (target == null || !others.Contains(target.Trim())) return;
        target = target.Trim();
        if (MessageBox.Show(this, $"プロファイル「{target}」を削除します。設定とシーンは元に戻せません。よろしいですか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Profiles.Delete(target); Status.Text = $"プロファイル「{target}」を削除しました"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "VCast Studio"); }
    }
}
