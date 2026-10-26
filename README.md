# iPhone / iPad データ転送

Windows PC と iPhone / iPad の間で、既存アプリの共有書類やカメラロールの写真・動画をUSB経由でコピーするアプリです。端末側に新しいアプリをインストールせずに使えます。

## できること

- 端末にすでに入っている「ファイル共有」対応アプリの書類と、PCのフォルダーの間でファイルやフォルダーを双方向にコピー
- 複数項目の選択と、上書き前の確認
- iPhone / iPad のDCIMにある写真・動画をPCへコピー
- 写真・動画の元データは端末に残す

## ファイル転送の制限

iOSは、Windowsアプリから端末全体や他のアプリの内部データを自由に開くことを許可していません。このアプリで任意ファイルを受け渡すには、端末にすでに入っているアプリがUSBファイル共有に対応している必要があります。対応アプリがない場合、新しいアプリを入れない条件では、任意ファイルの受け渡し先を用意できません。写真・動画の取り込みは別タブから利用できます。

## 起動方法

1. `AppleDeviceTransfer.exe` と同じフォルダーにあるファイルを含め、配布フォルダー全体をPCへ展開します。
2. `AppleDeviceTransfer.exe` を起動します。
3. 端末をUSB接続し、ロックを解除します。接続・取り外しは自動で検出します。端末に「このコンピュータを信頼」が表示されたら「信頼」を選びます。

このアプリはiTunesやApple Devicesの画面を起動しません。通信にはWindows上のApple Mobile Device Support（Apple Mobile Device Service）が必要です。サービスが停止または無効の場合は、Windowsの「サービス」（`services.msc`）で `Apple Mobile Device Service` を確認してください。無効の場合は、管理者権限でスタートアップの種類を「手動」にしてから起動します。

## 使い方

### アプリ書類との双方向コピー

1. 「アプリのファイルを双方向転送」タブで端末と共有対応アプリを選びます。
2. 左側でPCのフォルダー、右側でアプリの書類内の場所を開きます。
3. コピーする項目を選び、中央のボタンを押します。

上書きは確認してから行います。コピー元の項目を削除する操作はありません。ファイルは、そのアプリの書類領域に保存されます。端末の「ファイル」アプリで使う場合は、転送先アプリがその書類を表示・読み込みできる必要があります。

### 写真・動画の取り込み

1. 「写真・動画の取り込み」タブでPC側の保存先を選び、「端末を検索」を押します。
2. 取り込む項目を選び、「選択項目を取り込む」を押します。

保存先には、実行ごとの `AppleImport_日時` フォルダーを作成し、端末内のフォルダー構成を保ってコピーします。iCloud写真で「ストレージを最適化」にしている場合や、端末側でオリジナルが取得できない場合は、一覧や取り込み結果に影響することがあります。

## 必要な環境

- Windows 10 / 11 x64
- Apple Mobile Device Support（Apple Mobile Device Service）がインストールされ、起動できること
- iPhone / iPadを認識できるUSBケーブル
- .NETランタイムの追加インストールは不要です（配布版は自己完結型です）

本アプリはUSB接続した端末とPCの間で処理し、ファイルをクラウドへ送信しません。

## アイコンのクレジット

アプリのアイコンには、muhamad afiffudin による「backup」アイコンを使用しています。[Creative Commons Attribution 3.0 (CC BY 3.0)](https://creativecommons.org/licenses/by/3.0/) の条件に従って利用しています。[Noun Project のアイコンページ](https://thenounproject.com/icon/backup-6970243/)。

## ビルド

開発環境に.NET 10 SDKとRust MSVCツールチェーンを用意し、プロジェクトのルートで実行します。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish\win-x64
```

配布版には `AppleDeviceTransfer.exe` と `apple-device-bridge.exe` の両方が必要です。`THIRD-PARTY-NOTICES.md` はRust側依存パッケージの一覧とライセンス情報です。
