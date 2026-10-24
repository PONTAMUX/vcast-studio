# VCast Studio（仮称）SPEC

3Dアバター配信に特化した、軽量な配信ソフト。VTuber個人〜企業まで対象。
かんたんモード（1画面で配信開始）と、プロモード（vMix風のオペレーター画面）の2つを持つ。

## いまの状態（要約。再開時はまずここ）
- **ロードマップ M0〜M11 は完了**（M11 ネイティブアバター描画は「実験」として統合済み・設定でオン/オフ。詳細は末尾の M11 節）。自動テスト（tools\\selftest.ps1）約 90 項目が全部通る。連続運転テスト（tools\\soak.ps1、ローカル RTMP へ本物の配信）は 25〜30 分で PASS（送信落ち 0・音映像ズレ 0.05 秒以内・圧縮落ち平均 0%）
- **実機での確認は未実施**（Webカメラ・実マイク・実際の YouTube/Twitch 配信・VSeeFace の VMC・別の PC/GPU・Windows 10）。詳細は「実機で未確認」の節
- 配布: tools\\package.ps1（試作は -Draft）。公開前に dist-files\\SOURCE.txt へソース公開先を書く（GPL）。コード署名なし

### よく使う操作
| やりたいこと | コマンド |
|---|---|
| 起動 | start.bat（Release ビルドして起動） |
| 全項目の自動テスト | powershell -File tools\\selftest.ps1（サブディスプレイ・低優先度・音なし。テスト用の設定は %APPDATA%\\VCastStudio-selftest） |
| 画面の撮影確認 | tools\\snap.ps1 -Snap <avatar\|rest\|typing\|face-turn\|face-mouth\|simple\|studio\|preflight> |
| 連続運転（本物の RTMP をこの PC 内へ） | tools\\soak.ps1 -Minutes 30（結果は ログの [soak]） |
| 配布 zip | powershell -File tools\\package.ps1 -Version 0.1.0 [-Draft] |
| 最小ランタイム | python tools\\make_runtime.py |
- PowerShell スクリプトは **UTF-8 BOM 付き**で保存（BOM なしだと日本語が化ける）。テスト中は他のビルドを同じ bin に出さない（ロックされる。別の場所へは -p:OutDir=）
- 相談役: astra（Codex CLI）と Fable。記録は logs\\astra_review_*.md / fable_review_*.md

### 次にやること（優先順）
1. 実機確認（ユーザー）→ 出た不具合の修正
2. SOURCE.txt の記入 → package.ps1 で配布 zip
3. 数時間の連続運転・別 GPU/Windows 10 での確認
4. ネイティブ描画の実機確認（複数 GPU・VRM0 の見た目・MToon の細部）と、問題なければ既定にするか判断
5. コード署名（費用がかかるため保留）、インストーラ（今は展開して使う形）

## 確定した方針（2026-09-29）
- **エンジン = libobs（OBSのコア）を .NET 8 から P/Invoke で使う。GPL-2.0 を受け入れる**
  - B案（自前エンジン、GPLなし）も検討したが、開発期間が約2倍（4〜6か月）になるため見送り。ユーザーの判断で「おとなしくGPL」
  - 販売する場合はソース公開が必要。B2Bの売り物は導入・カスタム・保守。「OBS」の商標は製品名に使わない（「libobs採用」と表記）
- **OBSのバイナリ一式を同梱する**（`runtime/obs/`、OBS 32.1.2 からコピー）。ユーザーがインストールしたOBSには依存しない（構造体レイアウトがバージョンごとに変わるため）
- **UI = WPF**（HwndHost + obs_display でプレビューを埋め込む）。WebView2 / Avalonia は不採用
- **プラグインは許可リスト方式**（`ObsEngine.AllowedModules`）。Qtの画面部品を作るプラグインは QApplication が無いため落ちる
  - 許可: image-source, obs-ffmpeg, obs-filters, obs-nvenc, obs-qsv11, obs-outputs, obs-text, obs-transitions, obs-webrtc, obs-x264, rtmp-services, text-freetype2, win-capture, win-dshow, win-wasapi, nv-filters, obs-browser
  - 不可: frontend-tools, obs-websocket（M10で互換サーバーを自作して代替）, *-output-ui, decklink-captions, obs-vst（エディタ画面でQt必須）
- **3Dアバター**: v1 = obs-browser(CEF) + three-vrm + MediaPipe。トラッカーと描画はローカル WebSocket で分離しておく。v2（M11）= C# + D3D11 のネイティブ描画 → 共有テクスチャ → C#で登録した `vrm_source`（軽量化の本命）
- 相談役: astra（Codex CLI）と Fable。記録は `logs/`

## 構成
```
F:\vcast-studio\
  start.bat                 ビルドして起動
  SPEC.md                   この文書
  docs/PROPOSAL_v0.md       最初の企画たたき台（機能案の一覧）
  logs/                     astra / Fable のレビュー記録
  runtime/obs/              同梱OBSエンジン（gitには入れない。OBS 32.1.2 の Program Files からコピー）
  spikes/obs-pinvoke/       最初の実現性検証（コンソール）
  src/VCast.App/            本体（WPF, net8.0-windows, x64）
    Obs/Native.cs           libobs P/Invoke 宣言（32.1.x の構造体レイアウト）
    Obs/ObsEngine.cs        起動/終了・許可リストでモジュール読込・ログ/クラッシュ記録
    Obs/PreviewHost.cs      WPFへのプレビュー埋め込み（描画はグラフィックススレッド）
    Obs/Native.Scene.cs     シーン/アイテム/プロパティ/オーバーレイ描画の宣言
    Core/SceneManager.cs    シーン一覧・現在シーン・ソース追加削除・並べ替え
    Core/SceneEditor.cs     プレビュー上の選択・移動・拡大縮小・吸着・選択枠描画
    Core/SelfTest.cs        --selftest（全ソース種別の生成+設定画面、編集操作、シーン操作を自動確認）
    Ui/PropertiesWindow.cs  obs_properties → WPF 設定画面の自動生成（即時反映・キャンセルで復元）
    Ui/Theme.cs, InputDialog.cs
    MainWindow.xaml(.cs)    左=シーン / 中央=プレビュー / 右=ソース
```
- 設定・ログ: `%APPDATA%\VCastStudio\`（logs\*.log、crash-*.txt、plugin_config\）

## libobs を扱うときの注意（ハマった点）
- DLL検索: `SetDefaultDllDirectories` + `AddDllDirectory(bin\64bit, obs-plugins\64bit)`。data/module パスは**絶対パス**で登録する（cwd依存にしない）
- `obs-nvenc-test.exe` などの検出用exeと `obs-ffmpeg-mux.exe`（録画用）は**自分のexeと同じフォルダ**に必要 → 起動時に自動コピー
- ログは `base_set_log_handler`。書式展開は msvcrt の `_vsnprintf`（**ucrtbase は vsnprintf をエクスポートしていない**）。コールバック内の例外は必ず握りつぶす（ネイティブへ投げるとプロセスが落ちる）
- `obs_apply_private_data({"BrowserHWAccel":true})` はモジュール読込前
- 初期化順: startup → data/module path → private data → reset_audio → reset_video → モジュール → post_load
- **終了順が重要**: プレビュー(display)破棄 → シーン/ソースを `obs_source_remove` + release（OBS 31+ はキャンバスが参照を持つので remove しないと消えない）→ `obs_wait_for_destroy_queue` → `obs_shutdown`。守らないと終了時にアクセス違反
- 描画コールバックはグラフィックススレッド。UIスレッドを待たない
- プレビューの子HWNDは "static" クラスなので **SS_NOTIFY + WM_NCHITTEST→HTCLIENT** にしないとマウスが親に素通りする
- オーバーレイは画面ピクセル座標の ortho に切り替え、gs_matrix_identity してから SOLID エフェクトで四角形を描く
- 動作確認は `VCastStudio.exe --selftest`（ログに [selftest] OK/FAIL/DONE）。`VCAST_SNAP=<種別ID>` でその設定画面を2.5秒開く。自己テスト中はウィンドウがフォーカスを奪わない
- RTWQ: OBS本体の main() と同じく obs_startup 前に RtwqStartup、obs_shutdown 後に RtwqShutdown（無いと win-wasapi が "RTWQ setup failed" で MMCSS 優先度を失う）
- 設定画面は**編集用の複製 settings** を使う。OBS_PROPERTIES_DEFER_UPDATE のソースは OK 時のみ反映、それ以外は即時反映・キャンセルで reset_settings。"update_properties" シグナルで項目を再取得。閉じた後の遅延処理は _closed で無効化
- 選択中アイテムの addref/release は **SceneEditor._lock の外**で行う（release が graphics mutex を取りうるためデッドロック回避）
- 既知の許容: UIスレッドの transform 変更と描画スレッドの box_transform 読み取りは同期していない（OBS本体と同じ。最悪1フレーム枠がずれるだけ）

## マイルストーン
| | 内容 | 状態 |
|---|---|---|
| M0 | コンソールで libobs 起動・モジュール列挙 | ✅ 2026-09-29 |
| M1 | WPF + プレビュー表示、正常終了 | ✅ 2026-09-29（仮シーン。画面キャプチャはモニター未指定で黒） |
| M2 | 汎用プロパティ編集UI（obs_properties → WPF 自動生成）、シーン/ソースの追加・削除、プレビュー上の移動/拡大 | ✅ 2026-09-29（selftest 全項目OK） |
| M3 | 音声ミキサー（メーター・ミュート・モニタリング・ノイズ抑制/コンプ/ゲート/リミッター） | ✅ 2026-09-29（selftest: メーター-12dBFS検出・フェーダー・ミュート・フィルタ） |
| M4 | 配信/録画（NVENC/x264、RTMP、hybrid mp4）+ かんたんモード。状態表示（コマ落ち・切断・再接続） | 🟡 配信・録画・リプレイ・仮想カメラ・配信前チェック・負荷表示 ✅ 2026-09-29（かんたんモード画面は未） |
| M5 | スタジオモード（プレビュー/本番、カット/フェード、Tバー） | ✅ 2026-09-29（selftest: 通常追従/スタジオ保持/カット/Tバー/オート） |
| M6 | 3Dアバター v1（CEF + three-vrm + MediaPipe、表情ホットキー、VMC受信） | ✅ 2026-09-29 前倒し（selftest: VRM表示・VMC受信OK。実カメラ・VSeeFace実機は未確認） |
| M7 | メディアプレイリスト、サウンドボード、自動ダッキング | ✅ 2026-09-29（selftest: 再生/終了検知/表情連動/曲送り/ダッキング） |
| M8 | 保存（OBSシーンコレクションJSON互換）、プロファイル、ホットキー保存、自動保存 | 🟡 保存/読込/自動保存/OBS取込 ✅（プロファイル切替・ホットキー変更は未） |
| M9 | 同時配信、縦型キャンバス、リプレイバッファ、仮想カメラ、遅延配信 | ✅ 2026-09-29（縦型: 1080x1920 録画・黒でないこと確認） |
| M10 | obs-websocket 互換サーバー（Stream Deck等） | ✅ 2026-09-29（selftest: 認証・シーン一覧/切替・ミュート・vendor表情・パスワード違い拒否） |
| M11 | 3Dアバター v2（ネイティブ描画 vrm_source） | |

v2以降に回すもの: 字幕(音声認識)、ボイスチェンジャー、アイテム投げ、コメント読み上げ、タリー、NDI、配信前チェックリスト

## 追加方法と説明（2026-09-29 完了）
- 追加メニューはカテゴリ（映像/音声/素材・文字/その他）＋一行説明（Core/SourceCatalog.cs）
- シーンが空なら Preview を隠して案内パネル（よく使う追加ボタン）。HwndHost の上には重ねられないため入れ替え方式
- 「開いているアプリ」バー（Core/WindowList.cs、タスクバーと同条件で列挙、5秒ごと更新）: クリック→中央に追加、ドラッグ→離した位置に追加。**タスクバー本体からのドラッグは Windows の仕様で不可**なのでこれが代替
  - つまんでいる感: 押すと沈む → 持ち上げると元カード半透明＋カーソルにカード(Ui/DragGhost.cs、影・傾き・膨らむアニメ、クリック透過) → プレビュー上で枠が紫に光り「離して追加」
  - 最小化中のウィンドウは WGC で映らないので半透明表示＋注意書き
  - ウィンドウキャプチャは method=2 (WGC) を明示（自動だと Chrome/Terminal 等が黒い）
- エクスプローラーからのファイルD&D: プレビュー子HWNDは WS_EX_ACCEPTFILES + WM_DROPFILES、空状態パネルは WPF Drop。画像→image_source、動画/音声→ffmpeg_source、HTML→browser_source。大きさが決まるまで待ってからドロップ位置に中心合わせ
- 「？ 使い方」(Ui/HelpWindow.cs)
- **libobs の UI タスク**: obs_set_ui_task_handler を設定しないと、モジュールの UI タスク依頼が宙に浮いて終了時にハングする。通常は Dispatcher、終了処理中はその場で実行
- tools/selftest.ps1 で自己テスト（タイムアウト時は強制終了して TIMEOUT）

## VTuber向け方針（2026-09-29 調査・ユーザー合意）
調べた不満: OBSは設定が難しい／トラッカー＋OBSの掛け持ちが重く不安定（透過キャプチャが固まる・消える）／Zoom へ出すのに回り道／同時配信・縦型・アプリ別音声がプラグイン頼み。
→ 軸は「アバターソフトと OBS の二刀流をやめられる」。実装する: アバター内蔵、カメラなしでも動く、表情ホットキー、キーボード/マウスと手の連動、VMC受信、仮想カメラ内蔵、同時配信・縦型を標準、配信準備OKチェック、軽さの見える化。

## 内蔵3Dアバター（M6 v1）
- Avatar/AvatarServer.cs: http://localhost:18480〜（空きポート）。静的配信（Avatar/www、パス外は403）＋ /model.vrm ＋ WebSocket ハブ（role=render/tracker）。高頻度データは詰まったら捨て、モデル/表情/設定は reliable
- 描画: OBS の browser_source（1080x1080、60fps、透明背景）で avatar.html を開く。three.js 0.186 + @pixiv/three-vrm 3.5.5（importmap、vendor 同梱）。VRM0 は rotateVRM0。画角は外接箱から自動（胸から上／全身）
- 動き: 顔トラッキング > VMC > 待機（呼吸・ゆらぎ）。口はカメラ＞VMC＞マイク音量（-50〜-20dBFS→aa）。自動まばたき。表情ホットキーは happy/angry/sad/relaxed/surprised を上書き。パーフェクトシンク（ARKit名の表情）があれば生値も適用
- トラッカー: Avatar/TrackerWindow.cs = 画面外の WebView2（背景スロットリング無効化フラグ）、localhost のカメラ権限だけ自動許可。tracker.js = MediaPipe Face Landmarker(float16, GPU) 30Hz、表情52種＋変換行列から yaw/pitch/roll（鏡像にするため yaw・roll 反転 ※実カメラで符号要確認）
- 入力連動: GetAsyncKeyState で「左手側/右手側のキーが押されたか」だけ（キーの種類は記録・送信しない）、マウスは位置と動いたか
- 表情ホットキー: RegisterHotKey Ctrl+Alt+1〜6（他アプリ使用中も有効）
- VMC: Avatar/VmcReceiver.cs（UDP 39539、OSC bundle 対応、Blend/Val・Apply・Bone/Pos）。ボーンは Unity→three で x,z 反転（※VSeeFace 実機で要確認）
- 設定: %APPDATA%\VCastStudiovatar.json。UI: Ui/AvatarWindow.cs（非モーダル）、上部「🧍 アバター」、追加メニュー/空シーン案内に「3Dアバター」
- 同梱ライセンス: three.js MIT / three-vrm MIT / MediaPipe tasks-vision Apache-2.0（face_landmarker.task は Google 配布モデル）
- tools/snap.ps1 -Snap avatar で表示確認のスクショ

## 配信・録画（M4）
- Core/OutputManager.cs: 映像/音声エンコーダを配信・録画・リプレイで共有（負荷1回分）。エンコーダ自動選択 NVENC(obs_nvenc_h264_tex)→QSV→AMF→x264
  - 配信: 有効な配信先ごとに rtmp_custom + rtmp_output（=同時配信）。YouTube=rtmps://a.rtmps.youtube.com:443/live2、Twitch=rtmp://live.twitch.tv/app。自動再接続 初回3秒から間隔を伸ばしつつ最大20回
  - 録画: mp4_output（ハイブリッドMP4）/ ffmpeg_muxer(mkv)。保存先 既定=ビデオ\VCast
  - リプレイ: replay_buffer（proc "save"、保存先は "saved" シグナル後に proc "get_last_replay"。**"saved" の calldata には output が入っていない**）
  - 仮想カメラ: virtualcam_output + obs_output_set_media。OBS の仮想カメラドライバ（CLSID A3FCE0F5-…）が登録済みの PC で動く（本家OBS同梱ドライバを共用）
  - シグナル start/stop/reconnect/reconnect_success は UI スレッドへ。stop code → 日本語の原因メッセージ
- Core/AppSettings.cs: %APPDATA%\VCastStudio\settings.json。ストリームキーは DPAPI(CurrentUser) で暗号化。画質プリセット 高8000/標準6000/軽量3500 kbps、fps 60/30（再起動で反映）。設定画面は Clone を編集→保存時 CopyFrom
- Ui/PreflightWindow.cs: 配信前チェック。3秒実測（マイク最大・PC音の音割れ・描画落ち）＋キー/URL/エンコーダ/空シーン/アバター。キー未入力などは開始不可
- Core/PerfMonitor.cs: 本体＋同梱 obs-browser-page の CPU/メモリ、fps、描画落ち(lagged)、圧縮落ち(skipped)。ステータスバー右
- **ハマった点**: VIDEO_FORMAT_NV12 は **2**（3=YVYU にしていたため GPU 変換が効かず、録画中にプロセスが数秒止まり停止時に VCRUNTIME でクラッシュ）
- **終了時**: ObsEngine.BeginTeardown() を出力解放の前に呼ぶ（仮想カメラ解放中の UI タスク待ちでデッドロックした）
- 自己テストはサブディスプレイ・優先度 BelowNormal で起動（ゲーム中のユーザーの邪魔をしない）。tools/rectest.ps1 = 録画だけの切り分け
- 最小化中はプレビュー描画停止（obs_display_set_enabled）。アプリ一覧のアイコン/プロセス名はキャッシュ

## 最終レビュー対応（2026-09-29: astra 06 / Fable 04）
- RemoteServer: パスワード必須（空なら自動生成、起動しない）、接続数上限（全体16・IP別6・認証前も数える）、HTTP ヘッダ 5 秒・Identify 10 秒の期限、Upgrade の検証（Upgrade/Connection/Version 13/Key 16 バイト）、Origin があるのは localhost だけ、Binary 拒否、Batch 50 件・毎秒 100 リクエスト上限、送信は期限つき＋詰まったら切断、全 TCP を Dispose で閉じる。v5 の状態遷移（再 Identify 4008 / 未認証 Reidentify 4007）。ws:// は平文なので LAN は信頼できる場所だけ
- プロファイル切り替え: いまの保存に失敗したら中止 → 切り替え先を先に検証（壊れていれば中止）→ ポインタ書き込み成功後に確定 → 切り替え中は自動保存を止める → 常設音声（マイク/デスクトップ）もプロファイルごとに復元（ReplaceGlobals）→ リプレイは止めてから切り替え。設定の保存は tmp→置換
- ブラウザ部品の要否判定は実際に読むプロファイルのシーン（.bak 含む）。obs_post_load_modules の再実行は、許可モジュールのうち post_load を持つのが obs-browser だけ（DLL のエクスポートで確認）なので二重初期化は起きない。**許可モジュールを増やすときは要再確認**
- 「録画中」は縦型録画も含む。HEVC 選択と明示エンコーダの整合、縦型の profile（HEVC=main）
- ホットキー: 設定画面を開いている間は既存を止める、重複は（修飾キー, VK）で判定、他アプリ使用中で登録できないものを画面に表示、「すべて止める」、配信停止ホットキーは 3 秒以内に 2 回押し
- 初心者向け（Fable）: かんたんモードのカード順（1アバター/2マイク/3配信先/4映すもの）、「VRM ファイルを選ぶ」1 回で配信画面まで、はじめの 3 ステップ（✓ 表示・済んだら消える）、上部バーはかんたんモードでは絞る、確認ダイアログの既定を「いいえ」、録画ボタンは琥珀色（配信の赤と区別）、ストリームキー/外部操作パスワードは伏せ字＋表示切替、配信先未設定は上部に警告、既定シーン名「メイン」、用語統一（配信設定/声をきれいにする/映し方）
- 配布: GPL 本文と AUTHORS を runtime\obs\licenses\ に同梱（無ければ make_runtime.py が失敗）。SOURCE.txt に入手先が未記入だと package.ps1 は失敗（試作は -Draft）

## 30分連続運転の結果（2026-09-29、ローカル RTMP、Seed-san・録画・リプレイ・サウンド・シーン切替・ミュート込み）
- 配信: 送信落ち 0 / 108,372 フレーム、平均 6.1Mbps、混雑度 0、受信ファイル 1,331MB、映像 1806.53s・音声 1806.50s（差 0.036 秒）
- 圧縮落ち/描画落ちの最大 3.3% は、同時に私がビルド等を走らせていた時刻と一致（テスト用 VCast は BelowNormal 優先度）。判定は平均と p95 に変更
- メモリ: 1,204 → 1,277MB（+67MB、階段状。最後の 5 分は横ばい）。**本体/ブラウザ部品の内訳と 1 時間あたりの増加見積もりは、静かな状態での 25 分運転で再測定（soak ログの "memory by part"）**
- 未実施: 数時間の連続、実際の YouTube/Twitch、別 PC/GPU

## プロファイル・長時間テスト（2026-09-29）
- Core/Profiles.cs + MainWindow.Profiles.cs: 番組ごとの「設定＋シーン」。%APPDATA%\VCastStudio\profiles\<名前>\{settings.json,scenes.json}。初回起動で従来のファイルを「標準」として取り込む（元は残す）。上部「📁 名前 ▾」から 切り替え/新規/複製/名前変更/削除。配信・録画中は切り替え不可。切り替え時は現プロファイルを保存 → 設定を入れ替え → シーンを読み込み（無ければ空のシーン）→ モード/スタジオ/縦型/外部操作/切り替え効果を再適用。自己テストは隔離・復元・名前検証・削除を確認
- tools/soak.ps1 -Minutes N（VCast --soak N）: **この PC 内の RTMP 受信口（ffmpeg -listen 1、127.0.0.1:19351）へ本物の配信**をしながら、録画・リプレイ・アバター(Seed-san)・サウンド・シーン切替・ミュートを回し、10 秒ごとにメモリ/CPU/fps/描画落ち/圧縮落ち/配信ビットレート/送信落ち/混雑度を記録。終了時に受信ファイルを ffprobe で解析し、映像と音声の長さの差（音ズレ）・受信時間・メモリ増加を判定（PASS/FAIL）。外部サービスには一切つながない。2分: 落ち 0/7249、メモリ増加 0MB、音映像差 0.034 秒
- 顔追跡の自己テスト: TestMode では WebView2 に偽カメラ（--use-fake-device-for-media-stream）を渡して、本物のカメラを使わずにトラッカー起動・カメラ一覧・モデル読込を確認（VCAST_REAL_CAMERA=1 で本物）。AvatarController.InjectFace で人工の顔データを描画ページへ送り、頭の向き・口の見た目を撮影確認

## ホットキー設定（2026-09-29）
- Core/Hotkeys.cs: HotkeyManager（RegisterHotKey、割り当ては hotkeys.json）。修飾キー（Ctrl/Alt/Shift/Win）必須（単独キーはふだんの入力を奪うので不可）。重複と「他のアプリが使用中」は設定時に拒否して理由を出す
- MainWindow.Hotkeys.cs: アクション一覧 = 表情(既定 Ctrl+Alt+1〜6)・正面リセット・サウンド 1〜10(既定 Ctrl+Shift+1〜0)・全停止・配信/録画の開始停止(既定なし=誤爆防止)・リプレイ保存(Ctrl+Alt+S)・マイクミュート(Ctrl+Alt+M)・次/前のシーン・スタジオのオート
- Ui/HotkeysWindow.cs: ボタンを押してキーを押す方式（Esc でやめる、Delete で外す）。上部「⌨ ホットキー」
- 自己テスト: 解析/整形、重複拒否、SendInput（INPUT は x64 で 40 バイト）で実際にキーを送って動作確認

## 配布（2026-09-29）
- tools/make_runtime.py: 元の OBS（既定 C:\Program Files\obs-studio）から、読み込むプラグインの DLL の**インポート表を再帰的にたどって**必要な DLL だけを取り出す（Qt は obs-browser の分だけ残る）。pdb・obs64.exe・不要な言語は除外、data の locale は ja/en のみ。409MB → 354MB（大半は CEF）。runtime/obs-min に出力
- tools/package.ps1 [-Version 0.1.0] [-SkipRuntime]: dotnet publish（win-x64・self-contained・SatelliteResourceLanguages=ja）→ runtime/obs を同梱 → dist-files/（README・SOURCE・THIRD_PARTY_NOTICES）を追加 → zip。519MB（zip 221MB）。**配布フォルダからの --selftest も全項目OK**
- **PowerShell スクリプトは UTF-8 BOM 付きで保存する**（BOM なしだと Windows PowerShell 5.1 が日本語を CP932 と読み、文字列の引用符が壊れる）
- VCAST_RUNTIME=<dir> で開発時にランタイムを差し替え可能
- **配布前にやること**: dist-files/SOURCE.txt にソース公開先を書く（GPL）。未署名 exe は SmartScreen が止める（README に対処を記載）。WebView2 ランタイムは Win11 標準、Win10 は別途必要な場合あり

## 画質（2026-09-29）
- 圧縮方式: 設定「映像の圧縮方式」H.264 / HEVC（NVENC・AMF・QSV の HEVC があるときのみ選べる。profile は main）。YouTube 向け、Twitch は H.264
- 録画だけ高画質: 別エンコーダ（NVENC=CQP / x264=CRF、既定 18）。配信とは独立、録画は互換性の高い H.264、GPU 負荷が少し増える
- アバター描画: 「高精細で描く」で 810x1080 → 1620x2160（ブラウザソースの解像度を 2 倍にし、拡大率を 1/2 掛けて画面上の大きさは変えない。ApplyResolution）
- プレビューの縮小は area.effect（前述）

## ブラウザ部品の後読み込み（軽量化）
- obs-browser（CEF、常時数プロセス・約240MB）は、保存済みシーンに browser_source が無ければ起動時に読まない。ブラウザソース/アバターを追加する瞬間に ObsEngine.EnsureBrowser()（obs_find_modules2 で対象だけ open/init → obs_post_load_modules）。OBS 取込前にも呼ぶ。一度読むと終了まで残る。追加メニューには未読込でも「ブラウザ」を出す。VCAST_BROWSER=1 で常に読む

## 外部操作（M10）
- Core/RemoteServer.cs: obs-websocket v5 互換（サブプロトコル obswebsocket.json、Hello/Identify/Identified/Request/RequestBatch/Event、SHA256 のチャレンジ認証）。TcpListener で HTTP Upgrade を自前処理（管理者権限なしで LAN にも出せる）。既定はオフ・127.0.0.1:4455、LAN は別スイッチ。1MB 超のメッセージは切断
- MainWindow.Remote.cs: 対応リクエスト = GetVersion / GetStats / シーン（一覧・本番・プレビュー・スタジオモード・遷移）/ 配信・録画・リプレイ・仮想カメラ（Get/Start/Stop/Toggle、Save）/ 入力（一覧・ミュート・音量）/ シーンアイテム（一覧・ID・表示）/ CallVendorRequest（vendorName "vcast": SetExpression・PlaySound・StopAllSounds・CalibrateAvatar）。未対応は code 204
  - 外部からの StartStream は配信前チェックの画面を出さない
  - イベント: CurrentProgramSceneChanged / CurrentPreviewSceneChanged / SceneListChanged / Stream・Record・ReplayBuffer・Virtualcam の StateChanged / ReplayBufferSaved / InputMuteStateChanged / StudioModeStateChanged
  - 操作は Dispatcher.Invoke（10秒タイムアウト）で UI スレッド実行
- 設定画面「外部から操作」: オンでパスワード自動生成（コピー・作り直し）、ポート変更（OBS の WebSocket と同時なら 4455 以外に）

## 元に戻す・右クリック（2026-09-29）
- Core/UndoStack.cs（最大50件、やり直しは無し）。SceneEditor.TransformCommitted(item, 変更前) で積む
- 削除の取り消し: obs_save_source（uuid は消す）→ obs_load_source → 位置・表示・ロック・順番を復元。**取り除いたソースは破棄されるまで名前を持ち続けるので、削除時に __removed__<guid> へ改名して名前を空ける**（しないと復元が「名前 2」になる）

## 縦型・遅延配信（M9）
- Core/VerticalCanvas.cs: obs_canvas_create("縦型", 1080x1920, ACTIVATE|SCENE_REF) + obs_canvas_scene_create + set_channel(0)。レイアウトはプリセット（stack=上に本番・下にアバター / avatar / crop）。本番はトランジションソースをそのまま入れる（シーン切り替えに追従）
  - enum obs_canvas_flags: MAIN=1, ACTIVATE=2, MIX_AUDIO=4, SCENE_REF=8, EPHEMERAL=16（ACTIVATE が無いと中のソースが動かない）
  - 有効なときだけキャンバスを作る（GPU負荷）。縦型シーンは保存データから除外（起動ごとに作り直す）
- 縦型用の映像エンコーダ（同じ設定・video=canvas）、音声エンコーダは共有。縦型の配信先（1件）と縦型録画（別ファイル「… 縦型.mp4」）
- 上部「📱 縦型」でプレビュー窓（PreviewHost.CanvasWidth/Height）
- 遅延配信: obs_output_set_delay(sec, PRESERVE)。設定で なし/10/30/60 秒。LIVE 表示に「⏱ N秒遅延」

## スタジオモード（M5）
- Core/Transitions.cs: 出力チャンネル 0 は常にトランジション（cut/fade/swipe/slide/fade_to_color/wipe、stinger は v1 では出さない）。通常モードはシーン選択で Take、スタジオモードはプレビューだけ変わりカット/オート/Tバー（MANUAL + set_manual_time、1.0 で確定）
- PreviewHost.RenderSource: 指定シーンを obs_source_video_render で描く（スタジオモードのプレビュー）。本番は ProgramView（main texture）
- シーン欄の下に切り替え効果＋長さ、「🎬 スタジオモード」。かんたんモード中はスタジオモード無効
- SceneManager は出力チャンネル 0 を触らない（SceneRemoving で TransitionManager.Forget）

## かんたんモード・保存・サウンドボード（2026-09-29）
- Ui/SimplePanel.cs: かんたんモードの右パネル（1 アバター / 2 マイク: デバイス・メーター・音量・声をきれいに・自分の声を聞く・喋るとBGMを下げる / 3 映すもの: 場面ボタン・大きい追加ボタン・映っているもの / 4 配信先）。上部「🔧 プロモードへ ⇄ ✨ かんたんモードへ」。新規ユーザーの既定はかんたん
  - WPF 標準の ToggleButton はオン時に薄い水色で読めないので、Button の背景色で状態を表す
- Core/SceneStore.cs: 保存は OBS のシーンコレクションと同じ形式（対応範囲: sources+groups / scene_order / current_scene / current_program_scene / DesktopAudioDevice1・AuxAudioDevice1。transitions・追加の常設音声・canvases は未対応。VCast のプレイリストは OBS で開いても自動で曲送りされない）（sources / scene_order / current_scene、常設音声は DesktopAudioDevice1 / AuxAudioDevice1）。%APPDATA%\VCastStudio\scenes\default.json、1分ごと＋終了時、obs_data_save_json_safe（tmp→bak）。プロモードのシーン欄「読込」で OBS の %APPDATA%\obs-studioasic\scenes\*.json を取り込める
  - アバターの browser_source の URL は起動ごとにトークンが変わるので、読み込み前に差し替える
  - 自己テストはユーザーの保存データを読まない・書かない
- Core/Soundboard.cs + Ui/SoundboardWindow.cs: 10 パッド、Ctrl+Shift+1〜0。パッドごとに非公開 ffmpeg_source、鳴らすときだけ出力チャンネル 10〜19 に挿す（待機中はデコードしない）。モニター＋配信。鳴らすとアバター表情、鳴り終わり(media_ended)で戻す。soundboard.json
- Core/Playlist.cs + Ui/PlaylistWindow.cs: ffmpeg_source の設定に vcast_playlist / vcast_loop / vcast_shuffle / vcast_index を持たせる（保存に自動で乗る）。media_ended で次へ。追加メニュー「プレイリスト」
- 自動ダッキング: compressor_filter（ratio 8, threshold -30, attack 20, release 400, sidechain_source=マイク）を「自動ダッキング（喋ると下がる）」という名前で付け外し
- ログのマスク: ストリームキー（12文字以上）は ObsEngine.Log で **** に置換

## 音声ミキサー（M3）
- Core/AudioMixer.cs: 常設音声 ch1=デスクトップ音声 / ch3=マイク（device_id=default）。行=常設＋現シーンの AUDIO フラグ付きソース。並びが同じなら作り直さない
- AudioStrip: obs_fader(CUBIC)＋obs_volmeter(LOG, SAMPLE_PEAK)。メーター値は音声スレッドが volatile float に書くだけ、UIは DispatcherTimer(Render, 33ms) 1本で全 LevelMeter.Tick()
- Ui/LevelMeter.cs: OnRender のみ。-60〜0dB、緑/黄(-20)/赤(-9)、ピーク減衰 20dB/1.7s、ホールド線1.5s
- 🎧 モニタリングは 0→1→2 の循環ボタン。既定はオフ（obs_set_audio_monitoring_device("既定","default")）
- Core/Filters.cs + Ui/FiltersWindow.cs: ソースの種類に合うフィルタだけ提示（音声/映像）、説明付き、オンオフ・順序・設定（PropertiesWindow を流用）。「マイクをきれいにする」= rnnoise → gate → compressor → limiter
- 破棄順: volmeter/fader の callback 解除 → detach → destroy → ソース release。常設音声は scenes より前に remove

## UI改善バックログ（Fable review 02 より。M4のかんたんモード作成時にまとめて対応）
- 空キャンバス中央に「ソースを追加」大ボタン、ファイルD&Dで画像/動画ソース自動生成、追加メニューのカテゴリ化+検索
- プロパティ: 2カラム、グループ折りたたみ、プロモードは右パネルの非モーダルインスペクタ、FRAME_RATE 対応（映像キャプチャデバイスで必要）
- 済: 8ハンドル（辺は一方向だけ伸縮）、右クリックメニュー（プレビュー/ソース一覧）、元に戻す Ctrl+Z（移動・大きさ・削除・並べ替え、削除確認は廃止）、ロック。未: GridSplitter、アイコンツールバー
- ステータスバーは状態専用（FPS/CPU/コマ落ち/配信時間）、上部バーにモード切替・配信/録画・設定
- ソース行に音声/ロック/フィルタのアイコン。かんたんモードは呼称を「文字/画像/カメラ/ゲーム画面/アバター」に変換
- 済: ソース名は自動採番（F2で変更）、追加直後の設定画面キャンセルで追加取り消し、空キャンバスの案内、D&D、カテゴリ化

## 実機で未確認（ユーザー確認待ち・2026-09-29）
- Webカメラの顔トラッキング: 頭の左右の向き、片目ウインクで同じ側が閉じるか（MediaPipe の eyeBlink L/R の基準）
- 実マイクでの口パク・メーター、サウンドボードが自分の耳に聞こえるか
- 実際の YouTube / Twitch 配信、仮想カメラを Zoom 等で受ける
- VSeeFace からの VMC（ボーンの座標変換）
- プレビューの見え方（エリア縮小に変えた後）
- テスト用モデル: Downloads\Seed-san.vrm（VRM1 人型・VRM Public License 1.0）、Downloads	anuki_1.1.vrm（VRM0）

## 未決事項
- 製品名（VCast Studio は仮）
- 未署名exeは SmartScreen で止められる（企業向けで問題になる。ゼロ予算方針との兼ね合い）
- 「軽量」の合格基準（例: アバター込み1080p60配信で CPU __%、メモリ __MB 以下）を M4 までに数値で決める

## M11 試作の進捗（spikes\native-avatar）
- 2026-09-29: VrmModel.cs（VRM0/1 読み込み: ノード・スキン・素材(MToon)・モーフ・表情・人体ボーン）が Seed-san.vrm で動作。読み込み 0.6 秒。次は D3D11 オフスクリーン描画（スキニング→PNG 出力→three-vrm と見比べ）。
- 2026-09-29: **Renderer.cs（D3D11 オフスクリーン）が動作**。Seed-san.vrm をスキニング+モーフ+MToon 風 2 段影+輪郭線+半透明で描画し PNG 出力（spikes\native-avatar\out\、gitignore）。表情(happy/aa/blink)も反映。1 フレーム約 9.5ms（CPU スキニング。GPU スキニング化で大幅に減らせる見込み）。
  - 残り: ①three-vrm 表示との見比べ ②GPU スキニング ③VMC/顔追跡の姿勢・視線・揺れ物(springBone) ④libobs のカスタムソース化（共有テクスチャ）⑤ブラウザ方式へのフォールバック切替
  - 注意: HLSL の行列は row_major 指定が必要（System.Numerics は行優先）。クリーンアップ: build-alt / dist / テスト用 APPDATA / Temp の録画は削除済み（いつでも再生成可）
- 2026-09-29: **GPU スキニング化**（StructuredBuffer のボーン行列、モーフは重み変化時のみ CPU 再生成）。1 フレーム 9.5ms → 0.86ms、出力は CPU 版と画素差 0。VRM0(tanuki) も browser 方式と同じ色・輪郭で描画確認。残り: 顔追跡/VMC の姿勢・視線接続 → springBone → libobs ソース化 → フォールバック切替
- 2026-09-29（訂正）: 先の「GPU スキニングは CPU 版と画素差 0」は**古い画像同士の比較で無効**だった。実際は構造化バッファの行列が転置で読まれ、静止姿勢で単位行列に近い関節（頭・腰）だけ正しく見えていた。シェーダーで transpose して修正、作り直した画像で CPU 版との差 42 画素まで一致。教訓: 比較用の画像は毎回消してから作る
- 2026-09-29: **Motion.cs（avatar.js の動き計算の C# 移植）が動作**: 頭/首/背骨の追従、視線(lookAt 表情)、口・まばたき・表情、マイク口パク、自動まばたき、キーボード/マウス連動の腕、VMC ボーン、正規化ボーン→元ボーンの変換（Seed-san のように静止姿勢の骨の向きが揃っていないモデルでも正しく動く）。動き+姿勢+描画で約 0.55ms/フレーム。ブラウザ方式の見た目（腕を下ろす/タイピング）と一致。
  - 未実装: springBone（Seed-san のロボアームは springBone で垂れる想定。いまは静止姿勢のまま右へ伸びる）、VRM0 の視線・揺れ物、Motion の顔入力の実接続（AvatarController → Motion）、libobs ソース化、ブラウザ方式へのフォールバック
- 2026-09-29: **springBone（VRM1/VRM0）とノード制約（VRMC_node_constraint の rotation/roll）を実装**。Seed-san のロボアームは制約で本体の腕の回転を写す方式だった（揺れ物ではなかった）。髪・ワイヤーは springBone。順序は Motion → ApplyConstraints → SpringBone.Update → Renderer.Pose。試作の描画は browser 方式と同じ見た目（腕・タイピング・ロボアーム）。
  - 次: 実アプリへ統合（VrmModel/Renderer/Motion/SpringBone を VCast.App へ移し、libobs のカスタムソース + 共有テクスチャで映す。入力は AvatarController の顔/VMC/マイク/キー入力をそのまま渡す。設定で「ネイティブ描画（実験）」を切替、失敗時はブラウザ方式へ）

## M11 ネイティブアバター描画（実験）— 実アプリへ統合済み（2026-09-29）
- 構成: `src\VCast.App\Avatar\Native\`（名前空間 VCast.AvatarNative。VCast.Avatar.Native だと Obs の Native クラスと衝突するので注意）
  - VrmModel（VRM0/1 読み込み・表情・MToon・springBone・ノード制約）/ Renderer（D3D11・GPU スキニング・共有テクスチャ）/ Motion（avatar.js の動き計算の移植）/ SpringBone / NativeAvatar（60fps 専用スレッド・入力 JSON を受ける）/ NativeAvatarSource（libobs カスタムソース `vcast_native_avatar`。obs_register_source_s に obs_source_info の先頭部分だけ渡す）
  - 入力はブラウザ方式が描画ページへ送るのと同じ JSON を `AvatarServer.Tap` で受け取る（顔・VMC・マイク・キー入力・表情・設定・キャリブレーション）。モデルは Settings.ModelPath を直接読む
  - 切替: アバター設定「ネイティブ描画（実験）」。`AvatarController.ApplyResolution` がソースを差し替える（位置・拡大率は引き継ぎ）。オフに戻せばブラウザ方式
- 自己テスト（selftest に追加、fails=0）: ソース追加 / VRM 読込 / 描画ループ 55fps・0.43ms/フレーム / 共有ハンドル / 不透明画素 52% / 顔入力で画が変わる / 停止。`tools\snap.ps1 -Snap native` で撮影（VCAST_VRM=Seed-san.vrm）
- 既知の制限・未確認: VRM0 は試作で描画のみ確認（アプリ経由は未）、視線ボーン型(lookAt=bone)、MToon の細部（マットキャップ・リムライト・UV スクロール等は未実装）、複数 GPU（libobs と別アダプターだと共有できない）、実カメラ/VMC の実機。長時間運転の soak はネイティブ側で未実施
- 2026-09-29 ソーク（ネイティブ描画、8 分）: PASS。全体メモリ 約 850〜880MB（ブラウザ方式は約 1240MB → **約 390MB 減**）、圧縮落ち平均 0.01%、A/V ズレ 0.025 秒、増加 +25MB/8 分（ブラウザ方式と同程度）。selftest 全項目 fails=0（VRM0 の tanuki でもネイティブ通過）
- 注意（ハマり所）: `ObsEngine.Register` の挿入位置は EnsureBrowser ではなく StartCore。bash から `-p:OutDir=F:\...` を渡すとバックスラッシュが消えて src 配下にゴミフォルダができる → ビルド出力先の指定は PowerShell から
- 2026-09-29 配布版（tools\package.ps1 -Draft、520MB/zip 221MB）でも selftest のネイティブ項目が全部通ることを確認。D3D デバイスは libobs と同じ adapter 0 で作成、共有テクスチャを開けない場合はログに出す
- 2026-09-29 ネイティブ描画 30 分ソーク: PASS。メモリ 850→875MB（+25MB、以降ほぼ横ばい。約 +55MB/h の換算）、圧縮落ち平均 0.10%（最大 5.8%）、A/V ズレ 0.053 秒。数時間の連続運転と実機確認は未実施
- 2026-10-01 録画まわりの使いにくさを修正（ユーザー報告「録画ファイルどこ／設定どこ／止められない」）。実ログで、停止の直後 0.4 秒で新しい録画が始まっていた（停止後の連打で再開始）。対応: ①停止中は「保存中…」表示で押せない・停止直後 1.5 秒は無視・5 秒たっても終わらなければ再押しで強制停止 ②「📁」ボタンで録画フォルダ（直前の録画を選択）を開く ③「⚙ 配信設定」→「⚙ 設定」に改名（録画・リプレイ・縦型・外部操作も入っているため）、設定画面タイトルも変更 ④ヘルプに「録画のしかた」を追加。録画の初期保存先は C:\Users\user\Videos\VCast

## コメントビュー・読み上げ・緊急ボタンほか（2026-10-04）
- コメントビュー: `Core/TwitchChat.cs`（匿名・読み取り専用の IRC over WebSocket、エモート除去、自動再接続）/ `Core/VoiceVox.cs`（127.0.0.1:50021 の VOICEVOX へ audio_query→synthesis、順番に再生、溜まったら古いものを捨てる、bot・「!」命令・URL・絵文字の除去、辞書、長文省略）/ `Ui/CommentWindow.cs`（閉じても隠れるだけで接続・読み上げは続く。メイン画面の「💬 コメント」で表示/非表示）。設定は AppSettings.Chat*、ホットキー「読み上げ オン/オフ」「止める」
- 読み上げ音声は SoundPlayer でこの PC の既定の出力へ。配信に載せるにはデスクトップ音声が配信に入っていること。VOICEVOX 未起動なら窓に案内を出す（落ちない）
- 配信遅延に 2 秒・5 秒を追加（Twitch 自体の低遅延とは別の「放送事故対策」の遅延）
- 録画の圧縮方式を配信と分離: AppSettings.RecordCodec（既定 hevc、h264、same）。HEVC 非対応 GPU は自動で H.264。配信は H.264 のまま（Twitch 向け）。ffprobe で録画が HEVC(Main) になることを確認
- 「配信と同時に録画」(RecordWithStream)、設定に「プロ向けに変更する」チェック（SimpleMode と連動、保存すると即切替）
- 緊急ボタン「🙈 隠す」/ Ctrl+Alt+P: 出力チャンネル 0 を非公開の暗い画面に差し替え＋全音声ミュート＋読み上げ停止。戻すと ReattachOutput() で本番へ復帰しミュートも元通り。※最初の実装は復帰で出力を戻し忘れ、縦型録画の黒画面/プレイリスト失敗で気づいた（selftest が拾った）
- selftest 追加: チャット解析・チャンネル名・読み上げ整形・模擬 VOICEVOX・オフライン時・緊急ボタン。fails=0
- 未着手（ユーザー要望）: vMix 的機能 — ①ワンクリックのレイアウト集 ②マクロ（複数操作を 1 ボタン）③NDI/SRT 入出力 ④名前テロップのテンプレート ⑤予約配信・録画の自動分割。GLB ステージ背景 → FBX。実機での Twitch テスト配信はユーザーが実施予定（キーは再発行すること）

## 3 段階のモード（2026-10-04、ユーザー指示で修正）
- **かんたんモード** ⇔ **詳細モード**（上のバーのボタン。以前の「プロモード」画面の名前を変更）。設定の「プロモードにする」（AppSettings.ProMode）をオンにすると、詳細モードに **プロモード**の機能が足される。`MainWindow.ProOn = !SimpleMode && ProMode`
- プロモードで出るもの: スタジオモード（プレビュー/本番・Tバー）と切り替え効果、プロファイル、**マクロ**（`Ui/MacroWindow.cs`、6 スロット、シーン移動＋表情＋マイクのミュート/解除＋効果音＋録画の開始/停止、ホットキー「マクロ N」）、**レイアウト集**（一番大きい映像＝ゲームと、アバターをキャンバスに整える 4 種）。かんたん/詳細ではボタンごと隠れる。プロモードをオフにすると StudioMode も自動で切る
- 最初に作った「プロ向けに変更」（SimpleMode を切り替えるチェック）は意図と違ったので削除
- selftest 追加: モードごとの表示、マクロ、レイアウト。fails=0（※1 回だけ、録画開始 0.2 秒後にアプリが正常終了して途中で止まる現象を観測、再現せず。気になったら再調査）
- 今後（プロモード向け候補）: NDI/SRT 入出力、名前テロップのテンプレート、予約配信・録画の自動分割、複数カメラの切替

## 歩かせるモード（2026-10-12）
- 設定 `AvatarSettings.WalkMode`。アバター設定の「WASD キーでアバターを歩かせる」、またはホットキー `avatar.walk`（既定 Ctrl+Alt+W）でオン/オフ。
- ブラウザ描画のみ（ネイティブ描画は未対応）。オンの間だけ W/A/S/D の押下（それ以外のキーは見ない）を 30Hz で描画ページへ送る（`move`）。他のアプリを操作中も反応する。
- オンにすると、アバターのソース枠を画面いっぱいに広げ（元の位置・大きさは覚えておき、オフで戻す）、アバターを画面の約 45% に縮めて足元を下寄りにする。A/D=左右、W/S=奥/手前（奥ほど小さくなる）。進む向きへ体を向け、足・膝・腕の振りと体の上下を付ける（歩きは手続き生成）。
- 画面外へ出ない範囲に制限（奥行きごとの見える幅 − 体の半幅）。
- 自己テスト: `FakeMoveX/Z` で歩く姿を撮影（`tools\snap.ps1 -Snap walk-right|walk-away`）。
- 未確認: 脚のあるモデルでの歩きの見た目（確認に使った tanuki は脚が見えない）、実キーでの操作、背景（ステージ）との組み合わせ。

### 歩かせるモード 追加（2026-10-12）
- マウス: 左ボタンを押している間、カーソル位置（プライマリ画面基準）へ歩く。キー入力があればキー優先。設定 `WalkMouse`。
- Shift 押下で走る（速度約2倍・足振り大きめ）。`move` メッセージの `run`。
- 「WASD 歩行を使う」(`WalkEnabled`) をアバター設定でオンにすると、右パネル（かんたん=アバターカード / 詳細=ソース下）に「🚶 WASD ON/OFF」ボタンが出る。ボタン/ホットキー(Ctrl+Alt+W)は `AvatarController.SetWalk` を共通で使う（ホットキーでオンにすると WalkEnabled も立つ）。
- 未検証: 実キー/実マウス入力、脚のあるモデルでの歩行アニメ。
