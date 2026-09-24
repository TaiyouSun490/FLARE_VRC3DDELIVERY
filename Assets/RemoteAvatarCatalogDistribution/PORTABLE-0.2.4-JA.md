# FLARE Core 0.2.4 ポータブル配布版

購入者向けの手順: [日本語](USER-GUIDE-JA.md) / [English](USER-GUIDE-EN.md)。以下は元のポータブル配布版の構成記録です。言語対応の変更はunitypackageの再生成後に配布へ反映されます。

宣言型GLBギミック追加前のmain、fd06c839e6056a7e9c4ed65415a615aefb101fa8を元にしたUnityPackageです。
モデル・VAT・Particleを扱う既存RAC2の機能は維持しています。新しいFlareGimmick系の実行機能は含みません。

## 導入

1. VCCでUnity 2022.3.22f1 / Built-inのVRChat Worldsプロジェクトを用意してください。
2. Worlds SDK（UdonSharp同梱）とlilToonを先にインストールしてください。パッケージ作成・導入検証にはWorlds SDK 3.10.4とlilToon 2.3.4を使用します。第三者SDK本体は同梱していません。
3. Assets > Import Package > Custom PackageからFLARE-Core-0.2.4.unitypackageを選び、全項目をImportします。
4. Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad.prefabをシーンの床位置へ配置します。
5. 自分で公開したHTTPSの.rac2 URLをパネルに貼り付け、LOAD RAC2を押します。必要に応じてVRChat側のAllow Untrusted URLsを有効にしてください。

書き出しはTools > FLARE > RAC2 Creator...から行えます。詳しくは同じフォルダのREADME-JA.mdを参照してください。

## バリエーション

- RAC2-ImagePad.prefab：基本のモデル表示。
- RAC2-ImagePad-Pedestal.prefab：モデル表示と商品情報・アバター試着を統合。
- RAC2-Product-Pedestal.prefab：商品情報・アバター試着のみ。

運営モールへの依存を持たせないため、配布PrefabのSample RAC2 URLは空にし、そのサンプルボタンを非表示にしています。通常のURL入力・読み込み機能は変更していません。GLBの既存サンプルボタンはKhronosの公開Boxサンプルを参照します。

別プロジェクトでのコンパイルに必要なAvatarCatalog.Remote.Runtime.UdonSharpAssembly.assetもRuntimeフォルダへ同梱しています。以前は開発プロジェクト内の別フォルダにあった設定で、リモートmainから欠落していたものです。Reconstructionプレビュー版ではRAC2の出現演出を追加しています。設定・制限はRECONSTRUCTION-JA.mdを参照してください。

Assets/NightSlotMallの名称は参照互換性のために残っていますが、中身は必要なマテリアルとUdonプログラムです。モールのシーン・バックエンド・ユーザーのアバター・CDN契約は不要です。任意の公開HTTPS配信先を利用できます。

## 注意

既に同じFLAREのGUIDを持つアセットがあるプロジェクトへImportすると、それらのファイルが置き換わります。まずプロジェクトのバックアップ、または別の新規プロジェクトで試してください。新しいギミック版が入ったプロジェクトへのダウングレード手段ではありません。

PC向けです。VRChat実機の動作・FPS、Quest対応を保証するものではありません。SDKとlilToon導入済みの別プロジェクトでコンパイルとPrefab参照を検証します。結果は配布フォルダの検証記録に記載します。
