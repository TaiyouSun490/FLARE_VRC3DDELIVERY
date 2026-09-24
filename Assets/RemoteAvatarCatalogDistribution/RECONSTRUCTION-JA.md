# 幾何学的な復元エフェクト（PC向けプレビュー）

付属のRAC2-ImagePad / RAC2-ImagePad-Pedestal Prefabで、RAC2読み込み完了後に下から上へブロック状に出現します。全メッシュが共通の高さ・開始時刻を使い、青白いHDR発光を伴います。VATにも対応するlilToon拡張シェーダーを使用しています。lilToon 2.3.4を先に導入してください。

Rac2RuntimeLoaderのReconstruction設定:

- Enabled: 演出の有効・無効。
- Duration: 秒数（初期値2.25秒。旧1.8秒に対して速度0.8倍、0で即時表示）。
- Block Size: ブロックの大きさ（展示ルート座標、初期値0.08m）。
- Scatter: 高さ方向のランダムなずれ。
- Edge Width / Edge Color: 発光帯の幅・HDR色。
- Inflation: 発光帯の法線方向の膨らみ（初期値0.018m、0で無効）。

既存シーンのPrefabインスタンスでMaterialのOverrideがある場合は、新しい付属Prefabを配置するか、Materials/Reconstruction-Static-Opaque・Static-Cutout・VAT-Opaque・VAT-Cutoutを対応する4つのTemplate欄へ設定してください。従来テンプレートは削除していません。

外部からRestartReconstructionイベントを送ると出現演出を再生できます。通常のロードでは毎回自動で開始します。マテリアルはロード時に複製したものだけを変更します。毎フレームのUdon更新は追加していません。パーティクルの出現・寿命、コライダー、ブース判定、RAC2ファイル形式には変更を加えません。

## 制限

マスクはUV画像ではなく展示座標から計算する3Dブロックパターンです。縁の膨らみはジオメトリシェーダーによるポリゴン生成ではなく既存頂点の法線方向変位です。粗いメッシュでは膨らみも粗くなります。膨らみで切断面を塞ぐ処理はありません。必要に応じてInflationを0にしてください。

HDR発光はありますが、周囲へ光がにじむBloomはワールド側のポストプロセス設定に依存します。Quest/Android・透過ブレンド・GLBローダーには今回の演出を追加していません。VRChat実機での左右眼・影・負荷と実際のRAC2/VATでの見た目はリリース前に確認してください。

## Inspectorの回帰検証

2026-09-21: 標準Material InspectorがlilToonの未翻訳ラベルをlilVec3BDrawerへ渡し、配列範囲外例外になる問題を修正。両シェーダーにlilToon継承のReconstructionInspectorを明示し、SDK本体は変更しない。不透明・カットアウトのMaterialEditor.OnInspectorGUIを実際にLayout/Repaintして成功を確認。開発用メニュー Tools > FLARE > Check Reconstruction Inspector から再検証可能（検証ハーネスは配布対象外）。
