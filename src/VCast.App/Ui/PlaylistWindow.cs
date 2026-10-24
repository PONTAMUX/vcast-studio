using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VCast.Core;
using VCast.Obs;

namespace VCast.Ui;

// プレイリストの編集と再生操作（非モーダル）
public sealed class PlaylistWindow : Window
{
    readonly PlaylistManager _pm;
    readonly IntPtr _source;
    readonly ListBox _list = new() { Background = Theme.Panel, Foreground = Theme.Fg, BorderBrush = Theme.Line, MinHeight = 160 };
    readonly CheckBox _loop = new() { Content = "最後まで行ったら最初から", Foreground = Theme.Fg, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _shuffle = new() { Content = "シャッフル", Foreground = Theme.Fg };
    readonly TextBlock _now = Theme.Label("", sub: true);
    readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 4, 0, 8) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    List<string> _files;

    public PlaylistWindow(PlaylistManager pm, IntPtr source)
    {
        _pm = pm;
        _source = Native.obs_source_get_ref(source);
        _files = PlaylistManager.Files(source);
        var (loop, shuffle, _) = PlaylistManager.Options(source);
        _loop.IsChecked = loop; _shuffle.IsChecked = shuffle;
        Title = $"プレイリスト — {Native.Str(Native.obs_source_get_name(source))}";
        Width = 560; Height = 520; MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        Button Ctl(string t, string tip, Action a) { var b = Theme.Button(t); b.ToolTip = tip; b.Margin = new Thickness(0, 0, 6, 0); b.MinWidth = 44; b.Click += (_, _) => a(); controls.Children.Add(b); return b; }
        Ctl("⏮", "前へ", () => _pm.Next(_source, -1));
        Ctl("▶ / ❚❚", "再生 / 一時停止", () =>
        {
            var st = Native.obs_source_media_get_state(_source);
            if (st == Native.OBS_MEDIA_STATE_PLAYING) Native.obs_source_media_play_pause(_source, true);
            else if (st == Native.OBS_MEDIA_STATE_PAUSED) Native.obs_source_media_play_pause(_source, false);
            else Native.obs_source_media_restart(_source);
        });
        Ctl("⏭", "次へ", () => _pm.Next(_source, 1));
        Ctl("■", "停止", () => Native.obs_source_media_stop(_source));

        var add = Theme.Button("＋ ファイルを追加"); add.Margin = new Thickness(0, 0, 6, 0);
        add.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Multiselect = true, RestoreDirectory = true, Filter = "動画・音声|*.mp4;*.mkv;*.mov;*.webm;*.flv;*.avi;*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.opus|すべてのファイル|*.*" };
            if (d.ShowDialog(this) != true) return;
            var wasEmpty = _files.Count == 0;
            _files.AddRange(d.FileNames);
            Save(loadFile: wasEmpty);
        };
        var remove = Theme.Button("削除"); remove.Margin = new Thickness(0, 0, 6, 0);
        remove.Click += (_, _) =>
        {
            var i = _list.SelectedIndex;
            if (i < 0 || i >= _files.Count) return;
            var (_, _, idx) = PlaylistManager.Options(_source);
            var playingRemoved = i == idx;
            _files.RemoveAt(i);
            if (_files.Count == 0) { Native.obs_source_media_stop(_source); SaveIndex(0, clearFile: true); return; }
            SaveIndex(i < idx ? idx - 1 : Math.Min(idx, _files.Count - 1), loadFile: playingRemoved);
        };
        var up = Theme.Button("↑"); up.MinWidth = 32; up.Margin = new Thickness(0, 0, 6, 0); up.Click += (_, _) => Move(-1);
        var down = Theme.Button("↓"); down.MinWidth = 32; down.Click += (_, _) => Move(1);
        var edit = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        edit.Children.Add(add); edit.Children.Add(remove); edit.Children.Add(up); edit.Children.Add(down);

        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _loop.Click += (_, _) => Save(false); _shuffle.Click += (_, _) => Save(false);
        opts.Children.Add(_loop); opts.Children.Add(_shuffle);

        // ダブルクリックでその曲から再生
        _list.MouseDoubleClick += (_, _) =>
        {
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _files.Count) return;
            var (l, s, _) = PlaylistManager.Options(_source);
            _pm.Write(_source, _files, l, s, _list.SelectedIndex, loadFile: true);
            Native.obs_source_media_restart(_source);
        };

        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel(); head.Children.Add(controls); head.Children.Add(_now); head.Children.Add(_progress);
        DockPanel.SetDock(head, Dock.Top);
        var foot = new StackPanel(); foot.Children.Add(edit); foot.Children.Add(opts);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(head); root.Children.Add(foot); root.Children.Add(_list);
        Content = root;

        Fill();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Native.obs_source_release(_source); };
    }

    void Move(int d)
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _files.Count || i + d < 0 || i + d >= _files.Count) return;
        var (_, _, idx) = PlaylistManager.Options(_source);
        (_files[i], _files[i + d]) = (_files[i + d], _files[i]);
        // 再生中の曲が動いたら、再生位置もついていく
        var newIdx = idx == i ? i + d : idx == i + d ? i : idx;
        SaveIndex(newIdx, loadFile: false);
        _list.SelectedIndex = i + d;
    }

    void SaveIndex(int index, bool loadFile = false, bool clearFile = false)
    {
        _pm.Write(_source, _files, _loop.IsChecked == true, _shuffle.IsChecked == true, index, loadFile);
        if (clearFile)
        {
            var s = Native.obs_data_create();
            Native.obs_data_set_string(s, "local_file", "");
            Native.obs_source_update(_source, s);
            Native.obs_data_release(s);
        }
        Fill();
    }

    void Save(bool loadFile)
    {
        var (_, _, index) = PlaylistManager.Options(_source);
        _pm.Write(_source, _files, _loop.IsChecked == true, _shuffle.IsChecked == true, index, loadFile);
        Fill();
    }

    void Fill()
    {
        var sel = _list.SelectedIndex;
        var (_, _, index) = PlaylistManager.Options(_source);
        _list.Items.Clear();
        for (var i = 0; i < _files.Count; i++) _list.Items.Add($"{(i == index ? "▶ " : "　 ")}{i + 1}. {Path.GetFileName(_files[i])}");
        if (sel < _list.Items.Count) _list.SelectedIndex = sel;
        _list.IsEnabled = _files.Count > 0;
        if (_files.Count == 0) _list.Items.Add("（「＋ ファイルを追加」で動画や音声を入れてください）");
    }

    int _lastIndex = -1;
    void Tick()
    {
        var (_, _, index) = PlaylistManager.Options(_source);
        if (index != _lastIndex) { _lastIndex = index; Fill(); }
        var st = Native.obs_source_media_get_state(_source);
        long t = Native.obs_source_media_get_time(_source), d = Native.obs_source_media_get_duration(_source);
        var name = _files.Count > 0 && index < _files.Count ? Path.GetFileName(_files[index]) : "";
        var label = st switch { Native.OBS_MEDIA_STATE_PLAYING => "再生中", Native.OBS_MEDIA_STATE_PAUSED => "一時停止", Native.OBS_MEDIA_STATE_ENDED => "終了", Native.OBS_MEDIA_STATE_ERROR => "再生できません", _ => "停止" };
        _now.Text = name.Length == 0 ? "" : $"{label}: {name}  {Fmt(t)} / {Fmt(d)}";
        _progress.Maximum = Math.Max(1, d); _progress.Value = Math.Clamp(t, 0, Math.Max(1, d));
    }

    static string Fmt(long ms) => ms <= 0 ? "--:--" : TimeSpan.FromMilliseconds(ms).ToString(ms >= 3600_000 ? @"h\:mm\:ss" : @"m\:ss");
}
