# RAC2 0.2.4 アルゴリズム修正

## 実装

- VAT 全ノードを表示直前の同一時刻で開始。復元待ち時間が再生位相に混入しない。
- VAT を固定した展示座標系へベイク。Renderer/親のアニメーションを位置と法線へ反映し、基準メッシュの接線・反転 winding も補正。粒子収集前に AnimationMode を終了し、最終サンプル姿勢を emitter の初期位置へ混入させない。
- v3 メッシュの属性・インデックスを再開可能な段階に分割。Mesh 反映、VAT 転送、各マテリアルの準備も段階化。大きなフレーム停止も適応負荷制御へ反映。
- LZ4 の長さ拡張、literal、重複 match をバイト予算で中断・再開。重複コピーは既存 prefix の倍増コピーで処理し、分割点で繰返し位相を保持。
- NODE テクスチャの内容と用途を照合し、同一画像を後方参照化。読み込み側は一度だけ生成・破棄し、アップロード後の CPU 読み取りコピーを解放。
- 新規圧縮ファイルは標準 Adler-32。旧ファイルは旧 signed-overflow 計算のまま読む。選択を container flag で明示。
- 粒子は出生時刻から軌道を計算。EmissionRate=0 は生成ゼロ。Billboard に回転を反映。1フレームに複数発生しても出生順で古い粒子を取り替える。プロパティ存在確認を初期化時に集約。
- ペデスタル単体も v3 に対応。商品表示だけならモデル全体を解凍せず PROD のみを展開・検証。
- ImagePad の読み込み中表示を段階の進行へ追従。
- 配布対象へ Creator が必要とする metadata utility と normal encode shader を追加。Udon コンパイルに失敗した状態でのパッキングを拒否。

## 互換性と操作

既存 RAC2 v2/v3 は読み込み可能。VAT ベイク修正・ファイル内テクスチャ共有の効果は Creator からの再書き出しが必要。
`Share identical textures` は既定 ON。生成先ワールドにも Runtime 0.2.4 以降が必要。旧 Runtime 向けには OFF にする。
詳細なデータ契約は [0.2.4 format](RAC2-0.2.4-format.md) を参照。

## 検証方法

`AvatarCatalog.Remote.Rac2AlgorithmRegression.RunBatch` は次を順番に行う。

1. UdonSharp コンパイル。失敗を明示的に検出。
2. Unity C# 回帰テスト185アサーション。LZ4の分割位置・重複コピー・壊れた長さ、旧/標準 checksum、0放出、15/30/90 FPSの軌道、Billboard回転、飽和プール、Renderer移動のVAT化、共有画像、複数VAT開始時刻、Clear/再読込、商品情報を確認。
3. 実際のコンパイル済み Udon VM で、新規複合v3、既存Sakura v2、提供されたMaria v3を順に読み込み、VAT/粒子/renderer/ロード時リセットを確認。

結果: `Library/Rac2AlgorithmRegression.result` と `Library/Rac2CompositeUdonPlayModeTest.result`。
最終実行は185アサーション PASS、上記3ファイルの Udon VM 試験 PASS。Maria の最終ロード記録は44.9秒（その前の実行は30.0秒）。バックグラウンド負荷を固定した比較ベンチマークではない。
通常C#の編集モード試験では、RuntimeのDestroyを呼ぶ箇所でUnityの編集モード警告が出る。実際のPlay Mode検証とは区別する。

## 配布物

`Builds/RemoteAvatarCatalog-Complete-RAC2-0.2.4/RemoteAvatarCatalog-Complete-RAC2-0.2.4.unitypackage` を再生成した。BuilderのPrefab参照・Udon配列検証はPASS、Unityは終了コード0。アーカイブ内60 assets、3種類のPrefabと必要な追加スクリプト/Shaderを確認。テストランナーは同梱しない。
Prefab保存時にUdon/Odinの`ArgumentNullException (unityObject)`ログが出ているため、「警告・例外ログが一切ない配布検証」や「別プロジェクトでの新規導入済み」とは扱わない。RuntimeのVM試験PASSと、パッケージの生成・構造検証PASSを区別する。

## 残る制約

- 個々の Texture2D GPU転送、Mesh API 呼び出しは不可分。全停止の解消を保証するものではない。
- 複数ローダー間の共通作業予算は未実装。v2の旧メッシュ解析は既存経路。
- 粒子は引き続き最大32枚/emitterのrenderer方式。GPU一括シミュレーションへは変更していない。
- 実ネットワーク経由のダウンロード、VRChatクライアント/ヘッドセット上の見え方、最大フレーム時間の分布、クリーンな別プロジェクトへのインストールは今回の自動検証対象外。
- Editor内ロード秒数はFPS改善率ではなく、過去の別条件の結果と直接比較しない。
- ローカル `Backend/night-slot-site/lib/rac2.ts` は従来の単純な非圧縮v2専用検証器であり、今回のUnityコア修正の対象外。v3登録対応と本番コードとの差分は別途確認が必要。この記録はカタログ登録までの完了を意味しない。
