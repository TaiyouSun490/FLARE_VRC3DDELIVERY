# FLARE — VRC 3D Delivery

Federated Library for Avatar Retrieval & Embodiment

公開HTTPS上のRAC2ファイルから、VRChatワールド内に3Dモデル・VATアニメーション・パーティクルを読み込むUnityギミックです。

## 現在の版

Unityコア 0.2.5。JP/ENの利用ガイドと既知の問題を同梱した配布版です。VRChat実機での表示・操作の最終確認は未完了です。

- 複数メッシュ・複数マテリアル、VAT、パーティクルを1ファイルに格納
- 分割読み込みとローカルFPSに応じた負荷調整
- 同一テクスチャの共有、旧RAC2 v2/v3の読み込み
- ImagePad単体、ペデスタル統合、ペデスタル単体のPrefab

## 導入

1. Unity 2022.3.22f1 / Built-in Render PipelineのVRChat Worldsプロジェクトを用意してください。
2. VRChat Worlds SDK（UdonSharp同梱）、lilToonをVCC/VPM等から先に導入してください。
3. このリポジトリの`Assets`の内容を、`.meta`を維持してプロジェクトの`Assets`へコピーしてください。
4. コンパイル完了後、`Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad.prefab`をシーンに配置してください。

詳しい操作は[日本語ガイド](Assets/RemoteAvatarCatalogDistribution/USER-GUIDE-JA.md) / [English guide](Assets/RemoteAvatarCatalogDistribution/USER-GUIDE-EN.md)、[依存関係](Assets/RemoteAvatarCatalogDistribution/DEPENDENCIES-JA.md)、[既知の問題](Assets/RemoteAvatarCatalogDistribution/KNOWN-ISSUES.md)を参照してください。

## ソース構成

- `Assets/com.avatarcatalog.remote`: Creator、ランタイム、Shader、開発用テスト
- `Assets/RemoteAvatarCatalogDistribution`: 配布Prefabとドキュメント
- `Assets/NightSlotMall`: Prefabが参照するマテリアル・Udonプログラムのみ（互換性のため元のパスを維持）
- `Assets/SerializedUdonPrograms`: Prefabに対応する生成済みUdonプログラム
- `Docs`: アルゴリズム点検・修正・形式仕様

モールのシーン、運営バックエンド、CDNデータ、テスト用アバター、サードパーティSDK本体、Unityキャッシュは含みません。これはUnityプロジェクト全体やUPM Gitインストール用パッケージではありません。

## 検証と制約

元の開発プロジェクトで回帰テスト185項目と、複合v3 / Sakura v2 / Maria v3のUdon VM試験に合格しています。実ファイルを使うテストはこのリポジトリに含まれないローカルfixture・シーンを必要とします。

Prefab保存時のUdon/Odin例外ログが残っています。VRChatクライアント上の動作・最大フレーム時間は未検証です。シリウスの虹彩・瞳孔が表示されない問題は未解決です。単体のGPU転送やMesh API呼び出しは不可分で、完全な無停止を保証しません。今回の配布検証範囲は[梱包・検証記録](Tools/Release-0.2.5.md)に記載します。

VAT座標修正とテクスチャ共有の効果には再書き出しが必要です。`Share identical textures`を有効にした生成物にはRuntime 0.2.4以降を使用してください。

[修正・検証記録](Docs/RAC2-algorithm-fixes-2026-09-18.md) / [形式仕様](Docs/RAC2-0.2.4-format.md)
