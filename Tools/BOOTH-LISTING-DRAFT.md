# BOOTH掲載文の下書き

## 日本語

FLARE — 外部3D展示物ローダー for VRChat Worlds（PC）

公開HTTPS URLから3D展示物を読み込むワールド向けギミックです。複数メッシュ・マテリアル、VATアニメーション、対応パーティクルをRAC2ファイルにまとめて表示できます。

- 統合Creatorで静止モデル・アニメーション・パーティクルを書き出し
- ImagePad、商品ペデスタル統合版、ペデスタル単体版
- ブロック状の出現演出、持ち運び設定、自動読み込み負荷調整
- 日本語／英語のCreator・ImagePad表示、言語別の使い方

必要環境: Unity 2022.3.22f1 / VRChat Worlds SDK 3.10.4 / lilToon 2.3.4 / Built-in / PC。SDK・シェーダーは別途導入が必要です。配信用HTTPSホストも利用者側で用意してください。

アバター本体、衣装、サンプルモデル、CDN契約は含みません。RAC2はアバターアップロードや任意スクリプト実行を行う形式ではありません。PhysBoneは書き出し時の動きをVATへ記録し、読み込み後のライブ物理演算は行いません。lilToonの全設定を再現するものではありません。

詳細はUSER-GUIDE-JA.mdをご確認ください。販売条件・ライセンス・価格・問い合わせ先は出品者が別途設定してください。

既知の問題: シリウスの確認データで読込後に虹彩・瞳孔が見えない問題が残っています。すべてのモデルの外観再現を保証するものではありません。購入前に同梱のKNOWN-ISSUES.mdに記載した制限をご確認ください。

## English

FLARE — Remote 3D Exhibit Loader for VRChat Worlds (PC)

Load 3D exhibits from public HTTPS URLs. Package multiple meshes, materials, baked VAT animation and supported particles in one RAC2 file.

Includes an integrated Creator workflow, ImagePad, integrated and standalone product pedestals, a block-style reveal effect, pickup settings, adaptive loading, JP/EN presentation and separate user guides.

Requirements: Unity 2022.3.22f1, VRChat Worlds SDK 3.10.4, lilToon 2.3.4, Built-in pipeline, PC. Install SDKs and shaders separately and provide your own HTTPS hosting.

Avatars, clothing, model examples and hosting subscriptions are not included. RAC2 does not upload avatars or execute arbitrary scripts. PhysBone motion is baked into VAT, not simulated live after loading. Full lilToon material fidelity is not supported.

See USER-GUIDE-EN.md. The seller must supply pricing, license terms and a support contact separately.

Known issue: irises/pupils are not visible after loading a Sirius test exhibit. Identical rendering of every model is not guaranteed. Review KNOWN-ISSUES.md before purchase.

## 販売前チェック（掲載文には混ぜず、出品者が確認）

- [x] 未解決のシリウスの目の表示問題を掲載文・同梱KNOWN-ISSUESに明記（修正済みではない。BOOTH公開時にも掲載すること）
- [x] 今回の変更を含む0.2.5 unitypackageを再生成
- [x] 新規プロジェクトでインポート・C#コンパイル・Udonコンパイル（初回SDKログの留意点はTools/Release-0.2.5.md）
- [ ] 実際のVRChatでJP/ENの文字表示・ボタンの収まり・言語設定を確認
- [ ] 静止 / VAT / PhysBone焼き込み / パーティクル / 再試行 / クリアを確認
- [ ] 3種類のPrefab、出現エフェクト、商品表示・試着を確認
- [x] 個人アバター・開発シーン・SDK本体・運営RAC2 URLを設定したPrefabの混入を検査
- [ ] ライセンス・価格・問い合わせ先・更新方針を決める
