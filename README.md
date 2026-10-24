<p align="center"><img src="docs/icon.png" width="128" alt="VCast Studio"></p>

# VCast Studio（仮称）

3Dアバター配信に特化した、軽量な Windows 向け配信ソフトです。VRM モデルを読み込んで、そのまま YouTube / Twitch などへ配信できます。

- **かんたんモード**: ①アバター ②マイク ③配信先 ④映すもの を上から順に設定して「配信開始」
- **プロモード**: シーン編集・スタジオモード・音声ミキサー・サウンドボード・ホットキー・プロファイル
- **アバター**: VRM 表示、Web カメラの表情トラッキング、キーボード/マウスに合わせた動き、WASD で歩かせるモード
- **外部操作**: obs-websocket v5 互換のリモコン（Stream Deck など。パスワード必須）

## 動作環境

Windows 10 / 11（64bit）。映像の圧縮に GPU（NVIDIA / AMD / Intel）を使います。

## 使い方

配布 zip を展開して `VCastStudio.exe` を起動するだけです（インストール不要）。操作の説明は [dist-files/README.txt](dist-files/README.txt) を見てください。

## ソースから動かす

.NET 8 SDK が必要です。

```powershell
.\start.bat                          # Release ビルドして起動
powershell -File tools\selftest.ps1  # 自動テスト
powershell -File tools\package.ps1 -Version 0.1.0 -Draft   # 配布 zip
```

設計・経緯・既知の未確認事項は [SPEC.md](SPEC.md) にまとめています。

## 構成

| 要素 | 内容 |
|---|---|
| エンジン | libobs（OBS のコア）を .NET 8 から P/Invoke で利用 |
| UI | WPF |
| アバター描画 | three.js + three-vrm（ブラウザソース）／実験的に D3D11 ネイティブ描画 |

## ライセンス

GPL-2.0-or-later（OBS Studio のエンジンを使うため。本文は [LICENSE](LICENSE)）。使用ライブラリは [dist-files/THIRD_PARTY_NOTICES.txt](dist-files/THIRD_PARTY_NOTICES.txt) を参照してください。「OBS」は OBS Project の商標で、本ソフトは公式のものではありません。

コード署名はしていません。Windows に「PC を保護しました」と出たら「詳細情報」→「実行」で起動できます。
