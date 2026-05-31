# YMM4 Browser Music Controller

YMM4上からブラウザ/Spotifyなどの再生を操作できるUIパネルプラグインです。

## 主な機能

- 再生/一時停止
- 前へ/次へ
- 停止
- シーク（再生時間に合わせた滑らかな進行）
- セッション選択（Firefox優先を含む）
- アートワーク表示（サムネ中央クロップ）
- 音声連動波形（低音強調、ソフトクリップ）

## 対応環境

- Windows 10+
- YukkuriMovieMaker 4
- .NET 10 SDK（開発時）

## インストール（利用者向け）

1. Release の `BrowserMusicController.ymme` を取得
2. YMM4にインポート
3. YMM4再起動

## 開発者向けセットアップ

1. [Directory.Build.props](Directory.Build.props) の `YMM4DirPath` を自分のYMM4フォルダに合わせる
2. ビルド

```powershell
dotnet build BrowserMusicController/BrowserMusicController.csproj -c Release
```

## ymme 更新手順

以下で、最新DLLとメタファイルを使って `BrowserMusicController.ymme` を再生成できます。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Update-Ymme.ps1
```

## GitHub公開前チェック

- `dotnet build` が通る
- `BrowserMusicController.ymme` の更新日時が最新
- [BrowserMusicController/plugin.json](BrowserMusicController/plugin.json) の version を更新

## GitHub Topics（YMM4向け）

- 共通: `ymm4-plugin`

このリポジトリはUI操作系プラグインのため、まずは共通トピック `ymm4-plugin` を設定するのが推奨です。

## ライセンス

MIT
