# FLARE — VRC 3D Delivery

Federated Library for Avatar Retrieval & Embodiment

公開HTTPS上のRAC2ファイルから、VRChatワールド内に3Dモデル・VATアニメーション・パーティクルを読み込むUnityギミックです。

## 現在の版

Unityコア 0.2.4。検証中の開発版であり、正式リリース認定ではありません。

- 複数メッシュ・複数マテリアル、VAT、パーティクルを1ファイルに格納
- 分割読み込みとローカルFPSに応じた負荷調整
- 同一テクスチャの共有、旧RAC2 v2/v3の読み込み
- ImagePad単体、ペデスタル統合、ペデスタル単体のPrefab

## 導入

1. Unity 2022.3.22f1 / Built-in Render PipelineのVRChat Worldsプロジェクトを用意してください。
2. VRChat Worlds SDK（UdonSharp同梱）、lilToonをVCC/VPM等から先に導入してください。
3. このリポジトリの`Assets`の内容を、`.meta`を維持してプロジェクトの`Assets`へコピーしてください。
4. コンパイル完了後、`Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad.prefab`をシーンに配置してください。

詳しい操作は[利用ガイド](Assets/RemoteAvatarCatalogDistribution/README-JA.md)、[依存関係](Assets/RemoteAvatarCatalogDistribution/DEPENDENCIES-JA.md)、[Creator / RAC2](Assets/com.avatarcatalog.remote/README-RAC2-JA.md)を参照してください。新規プロジェクトへの導入手順は未実機検証です。

## ソース構成

- `Assets/com.avatarcatalog.remote`: Creator、ランタイム、Shader、開発用テスト
- `Assets/RemoteAvatarCatalogDistribution`: 配布Prefabとドキュメント
- `Assets/NightSlotMall`: Prefabが参照するマテリアル・Udonプログラムのみ（互換性のため元のパスを維持）
- `Assets/SerializedUdonPrograms`: Prefabに対応する生成済みUdonプログラム
- `Docs`: アルゴリズム点検・修正・形式仕様

モールのシーン、運営バックエンド、CDNデータ、テスト用アバター、サードパーティSDK本体、Unityキャッシュは含みません。これはUnityプロジェクト全体やUPM Gitインストール用パッケージではありません。

## 検証と制約

元の開発プロジェクトで回帰テスト185項目と、複合v3 / Sakura v2 / Maria v3のUdon VM試験に合格しています。実ファイルを使うテストはこのリポジトリに含まれないローカルfixture・シーンを必要とします。

Prefab保存時のUdon/Odin例外ログが残っています。別プロジェクトへの新規導入・VRChatクライアント上の動作・最大フレーム時間は未検証です。単体のGPU転送やMesh API呼び出しは不可分で、完全な無停止を保証しません。

VAT座標修正とテクスチャ共有の効果には再書き出しが必要です。`Share identical textures`を有効にした生成物にはRuntime 0.2.4以降を使用してください。

[修正・検証記録](Docs/RAC2-algorithm-fixes-2026-09-18.md) / [形式仕様](Docs/RAC2-0.2.4-format.md)
