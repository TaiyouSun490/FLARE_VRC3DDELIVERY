# FLARE — RAC2 ImagePad

初めて使う方は [日本語の使い方](USER-GUIDE-JA.md) / [English user guide](USER-GUIDE-EN.md) を参照してください。本書は技術概要・変更履歴です。

## 0.2.4 の変更と互換性

- VAT は全メッシュの復元後に同一時刻で開始し、Renderer と親 Transform のアニメーションも展示座標系に焼き込みます。
- 頂点・属性・インデックスの解析、Mesh 反映、VAT 画像、マテリアルを段階的に復元します。Inspector の Mesh Elements Per Frame と Decompression Bytes Per Frame で最大処理量を調整できます。
- 同一の画像はファイル内と実行時で共有します。Creator の Share identical textures は既定で ON です。生成物の読込には 0.2.4 以降が必要です。古いワールド向けには OFF にしてください。
- ON で圧縮保存したファイルには標準 Adler-32 を使用します。旧ファイルの独自チェックサムも引き続き読み込めます。
- 粒子は経過時間から軌道を計算し、放出ゼロと Billboard の回転設定を反映します。
- ペデスタル単体版も複合 RAC2 の商品情報を読み込み、圧縮時は商品情報だけを展開します。

Texture2D の GPU 転送や Unity の単体 Mesh API は分割できないため、完全に無停止になる保証ではありません。VRChat 実機の負荷はワールド構成・端末ごとに確認してください。

公開 HTTPS 上の `.rac2` を VRChat World 内で読み込む、完成版 ImagePad とペデスタルの配布セットです。RAC2 v3 は、複数メッシュ、複数マテリアル、VAT、複数 ParticleSystem、持ち運び設定、商品情報を 1 ファイルで扱います。

## 同梱 Prefab

- `Prefabs/RAC2-ImagePad.prefab` — ImagePad 単体版
- `Prefabs/RAC2-ImagePad-Pedestal.prefab` — ImagePad と商品表示・Avatar 試着ペデスタルの統合版
- `Prefabs/RAC2-Product-Pedestal.prefab` — 商品 RAC2 を直接読み込むペデスタル単体版

通常は `RAC2-ImagePad.prefab` を使ってください。商品情報と Avatar 試着を同じ展示に出す場合は統合版、ImagePad と別の場所へ商品ペデスタルだけ置く場合は単体版を使います。

## 導入

1. Unity 2022.3.22f1 の VRChat Worlds プロジェクトへ、VRChat Worlds SDK と lilToon を先に導入します。
2. unitypackage を Import します。
3. 使用する Prefab を Scene へ配置します。
4. PC 向けWorldとしてビルドします。信頼済み以外のURLを使う利用者はVRChat側の `Allow Untrusted URLs` を有効にします。
5. 実行時に、認証不要の公開 HTTPS `.rac2` URL を入力して `LOAD RAC2` を押します。

## RAC2 を作る

入口は `Tools > FLARE > RAC2 Creator...` に統一しています。Hierarchy の右クリックからも
`FLARE > Create RAC2 from this object...` を利用できます。
`FLARE > Developer` は開発・検証・旧形式向けです。通常の作成には使いません。
メニュー名のみの整理なので、既存の Prefab・RAC2 ファイル・パッケージ識別子は変更していません。

1. 展示物を 1 個のルート GameObject 以下へまとめて選択します。
2. `Tools > FLARE > RAC2 Creator...` を開きます。
3. Scene ビューの「保存後の配置」を確認します。Root の位置や Pivot は関係なく、保存時に展示物を自動で床置き・中央寄せします。
4. 赤い場合だけ `大きすぎる要素を選択` を使い、表示された横幅・高さ・奥行の超過分だけモデル／VATを小さくします。ParticleSystem はこの判定の対象外です。
5. 必要なら Animation Clip、Particle、商品情報、Portable を設定します。
6. `Create RAC2...` を押します。ParticleSystem は床・中央・サイズ判定に含まれず、ブース外へ出た粒子は再生時に自動で隠れます。

Clip があれば全 SkinnedMeshRenderer を VAT 化し、子 ParticleSystem も同じファイルへ入ります。旧 VAT 専用／Particle 専用アップローダーやテストランナーを操作する必要はありません。

## 事前最適化の推奨

RAC2 は複数メッシュをフレームごとに順次復元し、すべて完成してから一斉表示します。ただし、Unity の Mesh 反映処理はメッシュ単位で実行されるため、単一の巨大メッシュを復元する瞬間の負荷は完全には分割できません。

書き出し前に Mesh Baker 等を使用し、見た目やVAT頂点順を壊さない範囲で次を行うことを推奨します。

- 過剰に細分化されたメッシュの結合
- 同一設定マテリアルの統合
- 不要な頂点・三角形・サブメッシュの削減
- 不要なテクスチャと法線マップの削除
- VAT化したメッシュでは、ベイク後に頂点順が変わらないことを確認
## 自動負荷調整

ImagePad はロード開始前のローカル FPS を基準に、RAC2 の展開・検証量を毎フレーム連続調整します。FPS が落ちたときは素早く処理量を下げ、回復時はゆっくり増やします。

- `ADAPTIVE LOAD: ON`（既定・推奨）: 実測 FPS に追従して自動調整
- `ADAPTIVE LOAD: OFF`: Prefab の Inspector に保存された固定デバッグ予算を使用

切り替えは次回の RAC2 読み込みから反映され、プレイヤー間では同期されません。固定の SMOOTH / BALANCED / FAST 値は一般利用者向け UI には表示せず、開発者向け Inspector 設定として残します。

新しく生成する VAT 付き RAC2 v3 は、重複していた基準頂点位置を省略し、絶対座標 VAT の 0 フレーム目から復元します。三角形、UV、法線、接線、サブメッシュ、マテリアル情報は保持されます。旧 RAC2 v2 / v3 も引き続き読み込めます。

## 対応範囲

- 最大 16 renderer node
- 最大 64 material slot
- 最大 4 particle emitter、各 32 particle
- 合計250,000頂点 / 1,500,000インデックス（500,000三角形）、保存・展開後それぞれ128 MiB
- VAT 2～240 frames、1～60 FPS
- lilToon Opaque / Cutout、Main Texture、Normal Map
- Collider / Portable / VRC Pickup
- 商品名、作者名、商品 URL、Avatar Blueprint ID、試着可否
- 旧 RAC2 v2 の読込互換

ロード、再試行、クリアのたびに、展示物は Prefab の初期ローカル位置・回転へ戻ります。

## 配信条件

認証・Cookie 不要、redirect なし、直接 `200 OK` で raw RAC2 bytes を返す公開 HTTPS URL が必要です。推奨 Content-Type は `application/octet-stream` です。

詳しい制限と制作手順は `Assets/com.avatarcatalog.remote/README-RAC2-JA.md` を参照してください。
