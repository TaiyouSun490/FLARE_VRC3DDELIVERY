# RAC2 Creator / Complete ImagePad

RAC2 は、VRChat World 上の ImagePad が公開 HTTPS URL から読み込む 3D 展示形式です。
統合版（RAC2 v3）では、複数メッシュ、複数マテリアル、VAT アニメーション、複数 ParticleSystem、持ち運び設定、商品情報を 1 個の `.rac2` にまとめられます。

## 最短手順

1. 展示したい GameObject 一式を、1 個のルート GameObject の子にまとめます。
2. そのルートを選択します。
3. `Tools > FLARE > RAC2 Creator...` を開きます。
4. Scene ビューの「保存後の配置」を確認します。Root の位置や Pivot は関係なく、保存時に展示物を自動で床置き・中央寄せします。
5. アニメーションが必要なら `Animation Clip` を指定します。指定しない場合、SkinnedMeshRenderer は現在のポーズで保存されます。
   髪・衣装などの揺れも記録する場合は `PhysBone を含める` を ON にします（SDK 3.10.4 対応）。
   Clip 未指定では VAT / PhysBone の揺れは入りません。作成完了画面の `VAT nodes` が 0 の場合は静止版です。
6. 必要なら商品情報、Collider、`Portable / VRC Pickup` を設定します。
   読み込み速度を優先する場合は `Smaller file (slower load)` を OFF にします。
7. `Create RAC2...` を押して保存します。
8. `.rac2` を認証不要の公開 HTTPS URL に配置し、ImagePad へ URL を入力して `LOAD RAC2` を押します。

GameObject の右クリックメニュー `FLARE > Create RAC2 from this object...` からも開始できます。

## Modular Avatar の衣装

MA Merge Armature などの衣装設定がある場合、Creator は書き出し用コピーに NDMF の
Generic platform 処理を実行し、骨格を結合してから VAT を焼き込みます。
元のアバター・Clip は変更しません。別メッシュ・別マテリアルのまま利用できます。
指定 Clip も NDMF に渡すため、骨格結合に伴うアニメーションの参照パス変更が反映されます。

- MA 衣装の**作成環境**には Modular Avatar と NDMF が必要です。
  骨格結合と衣装追従を CLI 検証した組合せは MA 1.18.7 / NDMF 1.14.8、Unity 2022.3.22f1 です。
  公式 VPM リポジトリ: https://vpm.nadena.dev/vpm.json
- ワールド用 SDK をアバター用 SDK に置き換える必要はありません。
- 通常のモデル、および作成済み RAC2 の**読み込み先**には MA / NDMF は不要です。
- MA が未導入で Missing Script がある場合、書き出しを停止して不足を案内します。
  NDMF が構築エラーを報告した場合も RAC2 は保存しません。
- この処理は展示用メッシュの構築です。アバターのメニュー・FX・PhysBone の動作全体を
  RAC2 で再現するものではありません。
- 衣装が追従しない状態で既に作成した RAC2 は、修正後に再作成・再配置が必要です。

## Creator が自動でまとめるもの

- ルート以下の MeshRenderer / MeshFilter
- ルート以下の SkinnedMeshRenderer
- 各 Mesh の全 submesh と対応する material slot
- 指定した Animation Clip を使った全 SkinnedMeshRenderer の VAT
- ルート以下の ParticleSystem（最大 4 emitter）
- Collider / Portable 設定
- 商品名、作者名、商品 URL、Avatar Blueprint ID、試着可否


## 保存容量と読み込み速度

大きな VAT では `Smaller file (slower load)` を OFF にすることを推奨します。

ON にすると LZ4 で通信量を減らせますが、Udon VM での展開・検証が必要になります。
通信時間と復元時間の合計はファイル内容と端末・回線によって変わります。
VAT と Particle を別々にアップロードする必要はありません。両方が子にあれば、同じ `.rac2` に入ります。

0.2.4 から `Share identical textures`（既定 ON）で、同一画像をファイル内と実行時で共有します。
圧縮時のチェックサムも標準 Adler-32 に切り替わります。この設定で出力する場合はワールドの Runtime も 0.2.4 以降へ更新してください。
旧 Runtime 向けに書き出す場合は OFF にします。既存の RAC2 v2/v3 は新 Runtime で引き続き読み込めます。

## 複数メッシュ・複数マテリアル

- 最大 16 renderer node
- 1 Mesh あたり最大 16 submesh
- 全 node 合計で最大 64 material slot
- 全 node 合計で最大 40,000 vertices / 120,000 indices

通常マテリアルは Base Color と Main Texture を保存します。lilToon は Opaque / Cutout の対応パラメータ、Main Texture、Normal Map も保存します。未対応の shader 固有機能は書き出しません。

## VAT

Animation Clip を指定すると、全 SkinnedMeshRenderer を同じタイムラインでベイクします。各 renderer は独立した Mesh、material 配列、VAT texture を持ちます。

- 1～60 FPS
- 2～240 frames
- Position VAT は RGBAHalf
- Normal VAT は任意
- VAT texture は最大幅 2048、高さ 4096
- Animator Controller 全体と、読み込み後のリアルタイム物理は対象外

### PhysBone の揺れを含める

`Animation Clip` を指定して `PhysBone を含める` を ON にします（既定 OFF）。
MA の衣装構築後、SDK 本体の PhysBone solver を書き出し用コピーに対して進め、
髪・衣装などが揺れた頂点位置と法線を通常の VAT として保存します。
Play Mode への切り替えや、ユーザーのシーン全体の物理更新は行いません。

- 作成側は **VRChat Base SDK 3.10.4** 対応です。SDK には公開のオフライン step API がないため、
  他バージョンでは安全のため停止します。OFF にすれば従来の VAT を作成できます。
- `開始姿勢の安定化 (秒)`（既定 1 秒、0～10 秒）は Clip の先頭姿勢を保持して揺れを落ち着かせます。
  出力フレーム数・Clip の開始時刻には加算しません。
- `ループ末尾の揺れ補間 (秒)`（既定 0.15 秒、0 で無効）は、ループ末尾の物理による差分だけを
  最初のフレームの差分に近づけます。最大で Clip 長の半分に制限します。
  Clip 自体の先頭と末尾の姿勢や Root Motion が異なる場合、その段差までは修正しません。
- 物理は 1/60 秒以下の刻みで時間順に計算するため、通常の VAT より書き出しに時間がかかります。
  同じ頂点数・VAT フレーム数・法線設定なら、VAT の非圧縮データ量は増えません。
- 有効な PhysBone がない場合は通常の VAT です。Clip がない場合は現状ポーズの書き出しです。
- ルート内の PhysBone Collider を使います。骨・Collider のルート外参照は停止して案内します。
  ワールドの通常の Collider、他人の手、掴み・ポーズ、Contact/FX の操作は再現しません。
- Clip による PhysBone/PhysBone Collider 設定や GameObject の有効切り替えは未対応です。
  該当カーブがある場合は黙って無視せず停止します。
- 読み込み先は従来の VAT 再生で動きます。PhysBone コンポーネントをダウンロード・復元する機能ではありません。
  既存 RAC2 は作り直してください。

## Particle

最大 4 emitter、各 emitter 最大 32 particles です。各 emitter は独立した particle mesh と texture を持つため、表示 Mesh と共有されません。

対応範囲は Local simulation、Point / Sphere / Cone / Box、Billboard / Mesh、速度、重力、回転、色フェード、Texture Sheet Animation、Alpha / Additive です。Noise、Collision、Trails、Sub Emitters、Lights は未対応です。

ParticleSystem は床位置・中央・ブース寸法の判定から完全に除外します。モデル／VAT がある展示ではその実形状だけを基準にし、Particle-only では最初の Emitter 原点をゼロ寸法の配置アンカーにします。再生中にブース外へ出た粒子だけを自動で非表示にします。

## 持ち運びとロード位置

`Portable / VRC Pickup` は RAC2 内のメタデータとして保存されます。Portable を選ぶと Collider も有効になります。

ImagePad は `LOAD RAC2`、`RETRY`、`CLEAR`、内部の再読込を開始するたびに Pickup を解除し、速度と角速度を消去して、Prefab に保存された初期ローカル位置・回転へ戻します。その後、新しい RAC2 の Portable 設定を適用します。

## 配布 Prefab

- `RAC2-ImagePad.prefab`: ImagePad 単体の完成版
- `RAC2-ImagePad-Pedestal.prefab`: ImagePad と商品表示・Avatar 試着ペデスタルの統合版
- `RAC2-Product-Pedestal.prefab`: 商品情報を持つ RAC2 を単独で読み込むペデスタル版

ImagePad は 16 renderer slot と 4 emitter pool（各 32 particle slot）をあらかじめ持つため、実行中に任意の GameObject 構成を生成しません。

## 上限と安全条件

- 展開後・保存時とも最大 64 MB
- 標準 booth: 3 × 3 × 2.7 m
- 通常 texture: 最大 1024 × 1024 / 4 MB
- RAC2 v3: 16 renderer、64 material、4 emitter
- 旧 RAC2 v2 も引き続き読込可能

Creator は静的 Mesh と全 VAT frame の実形状だけで全体寸法を検査します。ParticleSystem、Root の位置、Pivot は判定に使いません。書き出し時にモデル／VATを基準として X/Z 中央寄せと床置きを自動適用し、Particle は相対位置を保ったまま一緒に移動します。

Creator を開いている間は、Scene ビューへ保存後の配置ガイドが自動表示されます。

- 緑のワイヤー箱: 保存後の標準ブース（幅 3 m × 奥行 3 m × 高さ 2.7 m）
- 青い面: 保存後の床
- 黄色い箱: 展示物の全体寸法が上限内
- 赤い箱: 横幅・高さ・奥行のいずれかが上限超過

赤くなった場合は Creator の `大きすぎる要素を選択` を押します。エラーには「高さ 4.27 m / 上限 2.70 m（1.57 m縮小が必要）」のように、対象寸法と必要な縮小量を表示します。ParticleSystem は赤判定と違反要素一覧に含めません。

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

## 配信条件

- 認証・Cookie 不要の公開 HTTPS URL
- redirect なしで直接 `200 OK`
- HTML / JSON wrapper ではなく raw RAC2 bytes
- 推奨 `Content-Type: application/octet-stream`
- VRChat World 側で `Allow Untrusted URLs` を有効化
- 更新時は versioned URL または適切な CDN cache 制御を使用
