using System.Windows;
using VCast.Avatar;
using VCast.Core;
using VCast.Obs;

namespace VCast;

// 全体ホットキーの登録（割り当ては HotkeyManager が hotkeys.json で管理）
public partial class MainWindow
{
    readonly HotkeyManager _hotkeys = new();
    DateTime _stopArmedUntil = DateTime.MinValue;
    internal HotkeyManager Hotkeys => _hotkeys;

    void InitHotkeys()
    {
        // 表情（既定は Ctrl+Alt+1〜6）
        for (var i = 0; i < AvatarController.Expressions.Length; i++)
        {
            var (id, label) = AvatarController.Expressions[i];
            _hotkeys.Add(new HotkeyAction("expr." + id, "アバターの表情", label, $"Ctrl+Alt+{i + 1}", () => _avatar.SetExpression(id)));
        }
        _hotkeys.Add(new HotkeyAction("avatar.walk", "アバターの表情", "歩かせるモード（WASD）オン / オフ", "Ctrl+Alt+W", () =>
        {
            _avatar.SetWalk(!_avatar.Settings.WalkMode, _scenes);
            Status.Text = _avatar.Settings.WalkMode ? "🚶 歩かせるモード: オン（W/A/S/D で動かせます）" : "歩かせるモード: オフ";
        }));
        _hotkeys.Add(new HotkeyAction("avatar.calibrate", "アバターの表情", "正面リセット（顔の向きを覚え直す）", "", () => _avatar.Calibrate()));

        // サウンドボード（既定は Ctrl+Shift+1〜0）
        for (var i = 0; i < Soundboard.PadCount; i++)
        {
            var n = i;
            _hotkeys.Add(new HotkeyAction("sound." + (i + 1), "サウンドボード", $"パッド {i + 1}", $"Ctrl+Shift+{(i + 1) % 10}", () => _soundboard.Play(n)));
        }
        _hotkeys.Add(new HotkeyAction("sound.stop", "サウンドボード", "全部止める", "", () => _soundboard.StopAll()));

        // 配信・録画（誤爆で配信が始まらないよう、既定は割り当てなし）
        _hotkeys.Add(new HotkeyAction("stream.toggle", "配信・録画", "配信の開始 / 終了", "", () =>
        {
            if (_outputs.Streaming)
            {
                // 誤爆で配信が終わらないよう、3 秒以内にもう一度押したときだけ終了する
                if (DateTime.Now < _stopArmedUntil) { _stopArmedUntil = DateTime.MinValue; _outputs.StopStream(); Status.Text = "ホットキーで配信を終了しました"; }
                else { _stopArmedUntil = DateTime.Now.AddSeconds(3); Status.Text = "⚠ 配信を終了するには、3 秒以内にもう一度同じキーを押してください"; }
            }
            else if (_outputs.StartStream(out var err)) Status.Text = "ホットキーで配信を開始しました";
            else Status.Text = "配信を開始できません: " + err;
            UpdateOutputButtons();
        }));
        _hotkeys.Add(new HotkeyAction("record.toggle", "配信・録画", "録画の開始 / 停止", "", () =>
        {
            if (_outputs.Recording) _outputs.StopRecord();
            else if (!_outputs.StartRecord(out var err)) Status.Text = "録画を開始できません: " + err;
            UpdateOutputButtons();
        }));
        _hotkeys.Add(new HotkeyAction("replay.save", "配信・録画", "リプレイを保存（直前の映像）", "Ctrl+Alt+S", () =>
        {
            if (!_outputs.ReplayActive) { Status.Text = "リプレイが始まっていません（「↺ リプレイ」を押しておいてください）"; return; }
            _outputs.SaveReplay();
        }));

        for (var i = 0; i < VCast.Ui.MacroWindow.HotkeyCount; i++)
        {
            var n = i;
            _hotkeys.Add(new HotkeyAction("macro." + (i + 1), "マクロ（プロモード）", $"マクロ {i + 1} を実行", "", () => { if (ProOn) RunMacro(n); else Status.Text = "マクロはプロモードの機能です（設定でプロモードにしてください）"; }));
        }
        _hotkeys.Add(new HotkeyAction("panic.toggle", "配信・録画", "緊急: 映像を隠して音を消す / 戻す", "Ctrl+Alt+P", () => TogglePanic()));
        _hotkeys.Add(new HotkeyAction("chat.read", "コメント", "コメントの読み上げ オン / オフ", "", () =>
        {
            _settings.ChatRead = !_settings.ChatRead; _settings.Save();
            if (!_settings.ChatRead) _voice.StopAll();
            Status.Text = _settings.ChatRead ? "コメントの読み上げ: オン" : "コメントの読み上げ: オフ";
        }));
        _hotkeys.Add(new HotkeyAction("chat.stop", "コメント", "いまの読み上げを止める", "", () => _voice.StopAll()));

        // 音声
        _hotkeys.Add(new HotkeyAction("mic.mute", "音声", "マイクのミュート / 解除", "Ctrl+Alt+M", () =>
        {
            var mic = _mixer.Mic;
            if (mic == IntPtr.Zero) return;
            var mute = !Native.obs_source_muted(mic);
            SetMute(mic, mute);
            Status.Text = mute ? "🔇 マイクをミュートしました" : "🎙 マイクのミュートを解除しました";
        }));

        // シーン（プロモード: 次 / 前）
        _hotkeys.Add(new HotkeyAction("scene.next", "シーン", "次のシーンへ", "", () => StepScene(1)));
        _hotkeys.Add(new HotkeyAction("scene.prev", "シーン", "前のシーンへ", "", () => StepScene(-1)));
        _hotkeys.Add(new HotkeyAction("studio.take", "シーン", "スタジオモード: プレビューを本番へ（オート）", "", () => OnAuto(this, new RoutedEventArgs())));

        SourceInitialized += (_, _) => _hotkeys.Attach(this);
    }

    void StepScene(int d)
    {
        if (_scenes.Scenes.Count < 2 || _scenes.Current == null) return;
        var i = (_scenes.Scenes.IndexOf(_scenes.Current) + d + _scenes.Scenes.Count) % _scenes.Scenes.Count;
        var target = _scenes.Scenes[i];
        // スタジオモードのときは本番を直接切り替える
        if (_settings.StudioMode && ProOn) { _scenes.SetCurrent(target); _transitions.Take(target.Source); }
        else _scenes.SetCurrent(target);
        Status.Text = $"シーン: {target.Name}";
    }

    void OnHotkeys(object sender, RoutedEventArgs e)
    {
        var w = new Ui.HotkeysWindow(_hotkeys) { Owner = this };
        w.ShowDialog();
        // 表示中のラベル（表情ボタン・パッド）を更新
        _avatarWindow?.Close(); _soundboardWindow?.Close();
    }
}
