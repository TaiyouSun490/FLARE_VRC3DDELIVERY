# FLARE 宣言型ギミック MVP

## 何を配信するか

GLB内のGeometry / Material / Hierarchyと、`nodes[].extras.vrc_gimmick`のEvent→Actionデータを配信する。任意のC#・Udonプログラム・Shader・Component型名をロードして実行する仕組みではない。

VRChatの[World Component許可リスト](https://creators.vrchat.com/worlds/whitelisted-world-components/)はビルドに含められるComponentの範囲であり、全メソッドのUdon公開や、外部からのコード実行を許可するものではない。FLAREはさらに固定された操作・対象・上限に絞る。

既存RAC2/VAT/パーティクルの経路は変更せず、新しい静的GLB用モジュールとして追加する。

## 試す

1. Unity 2022.3.22f1 / VRChat Worlds SDK / UdonSharpがあるプロジェクトにリポジトリのAssetsをコピーする。
2. `Assets/FLAREGimmicks/FLARE-GLB-Gimmick-Player.prefab`をシーンの床の高さに配置する。
3. Play Mode / VRChatでパネルの **LOAD DEMO** を押す。URL入力→**LOAD URL**で自分の公開GLBへ切り替えられる。
4. 読み込み完了後、左側の立方体ボタンをInteractする。別ノードのドアが0.5秒で90度回転し、インジケーターの移動・表示切替と確認音も実行する。
5. **CLEAR**で破棄する。再ロードすると初期Transformと表示状態へ戻る。

デモURLはこの機能ブランチのGitHub rawファイルを指す。URLを利用できない場合はAllow Untrusted URLsとHTTPエラーを確認する。ブランチを削除する場合はDemoUrlを新しい配信先へ変更する。必要なら`Tools > FLARE > Build Gimmick Prefab and Demo`で生成物を再作成できる。

## 自分のギミックを書き出す

1. 一つの親GameObject以下にモデルを配置する。ドアは回転させたい蝶番位置を親ノードの原点にする。
2. 動作を持たせるノードへ`FlareGimmickDefinition`を付ける。`GimmickId`は一意で永続的なID、`On`は`interact`または任意のローカルイベント名。
3. Actionsに操作を追加し、Targetへ同じ親以下のTransformを指定する。Interactするボタンには有効な非TriggerのBoxColliderを付ける。
4. 親を選び、`Tools > FLARE > Export Selected Gimmick GLB...`で保存する。親の位置・回転は書き出し原点として正規化され、親自身のlocalScaleと子の位置・回転・スケール・階層を保持する。選択した親より上のTransformは含めない。
5. GLBを公開HTTPSへ置き、PrefabのURL入力から読み込む。音声はInterpreterのAudioIds / AudioClipsへワールド作者が事前登録する。

通常のGLBを使う場合、PNG/JPEGがあるファイルは`Prepare Existing GLB Textures...`で準備してから配信する。この処理は画像をGLBのBIN内のRGBA32へ追加し、既存extrasを維持する。Geometryの未対応機能まで変換するツールではない。

同一のMaterial/Texture参照は重複排除する。準備済みの同一RGBA領域は再利用するため、変更せずに再準備しても増量しない。テクスチャのtiling/offsetはUVへ焼き込んでからエクスポートする。

## メタデータ

```json
{
  "gimmickId": "button",
  "collider": { "type": "box", "size": [1, 1, 1], "center": [0, 0, 0] },
  "on": "interact",
  "actions": [
    { "type": "emitEvent", "target": "door", "event": "open" }
  ]
}
```

ドア側は`gimmickId: "door"`, `on: "open"`とし、actionsへ`{"type":"rotate","axis":"y","value":90,"duration":0.5,"easing":"easeInOut"}`を指定する。複数のイベントは`bindings: [{"on":"...","actions":[...]}]`で記述できる。Target省略は同じノード。指定したTargetが存在しなければその操作だけを無視する。名前ではなくIDを使用する。

対応操作:

- `rotate`: 自分のローカル軸x/y/z、相対角度-360～360度。負方向・360度の一周も方向を維持する。
- `move`: 親座標系のlocalPositionへの相対差分`value: [x,y,z]`。累積移動もローカル位置上限を検査する。
- `toggleActive`, `setActive`: ノードと子孫の表示/有効状態。setActiveは真偽値または0/1。
- `playAudio`: ワールド側の登録済み`clip` IDのみ。`value`は0～1の音量係数。最大音量をさらに適用。
- `stopAudio`
- `emitEvent`: このロード内のノードにだけ宣言イベントを送る。UdonのSendCustomEventには変換しない。

Tweenは`duration: 0..30`秒、`easing: linear/easeIn/easeOut/easeInOut`。同じノード・同じ種類のTweenを再指定すると現在の補間位置から置換する。無制限にTweenを積み上げない。非アクティブになった対象の補間もホスト側で継続する。

GLBのTRS・GeometryはglTF座標からZ反転とwinding補正でUnityへ変換する。一方、独自extras内のmove/rotate/colliderは**変換後のUnityローカル座標**で定義する。`initialActive`省略はtrue。

## 4レイヤー

- `FlareGlbDownloader`: VRCUrl / VRCUrlInputField、VRCStringDownloader.ResultBytes、開始・取得成功/失敗・解析成功/失敗の表示。キャンセル済みの応答を捨て、同時ダウンロードを開始しない。
- `FlareGlbSceneLoader`: サイズ/階層/アクセサの検証、画像とノードを段階的に復元。全体の成功後だけ有効化し、失敗時は部分生成物も破棄。
- `FlareRuntimeNode`: 事前搭載のMeshFilter / MeshRenderer / BoxCollider / AudioSource / UdonBehaviour。ランタイムAddComponentなし。
- `FlareGimmickInterpreter`: ID解決、許可操作、イベントキュー、固定数のTween。ホスト側のAudio allowlistと負荷ポリシーは外部データで上書き不可。

メッシュの数値復元は `MeshElementsPerFrame`（既定128、16〜512）で分割する。値を小さくするとフレーム負荷を抑え、完了までのフレーム数は増える。静止ノードは毎フレーム更新しない。イベントとAudioClipは読み込み時に解決するため、Audio allowlist変更後は再ロードする。

破綻修正・計算量・回帰試験の詳細は [アルゴリズム点検記録](FLARE-gimmick-algorithm-audit-2026-09-18.md) を参照。

## 上限と対象外

既定は16 nodes（設定上限32）、階層16段、16,000 vertices合計（最大40,000）、2,048 vertices/node（最大4,096）、12,288 indices/node、8 material定義（最大16）、4 textures、512px、10MB/GLB、256KiB/JSON、64 actions（最大128）。1 dispatchは最大32 events（設定上限64）かつ128実行操作まで、毎秒16 dispatchまで。イベントの循環は次フレームへ持ち越さない。

GLBは1つの埋め込みbuffer、TRS、indexed TRIANGLES、1 primitive/node、FLOAT POSITION/NORMAL/UV0、整数indices、OpaqueのbaseColorとbaseColorTextureに限定。画像は`extras.flare_rgba_textures`のwidth/height/bufferViewをtexture index順に参照し、UdonでPNG/JPEGデコーダーは実行しない。任意Shaderは読み込まず、ワールド内MaterialTemplateを使う。

未対応: matrix node、skin/morph、glTF animation、複数primitive/node、外部buffer/image URI、法線マップ等のPBR詳細、Rigidbody/Pickup/Trigger/Particleの宣言制御、タイマー、ネットワーク同期。VRChatの許可リストにある全Componentの自動復元ではない。

MVPはLocal-only。生成GameObjectを同期オブジェクトとは扱わない。将来は永続的gimmickId→論理状態を既存の同期ホストで管理する。現状では複数クライアントの同じ状態を保証しない。

## 検証

`AvatarCatalog.Remote.FlareGimmickRegression.RunBatch`がデモをコンパイル済みUdon VMへ渡し、実Interactイベント・階層・Tween・表示・音声参照・再読込・不正データ・循環上限・テクスチャを検証する。結果は`Library/FlareGimmickRegression.result`。外部ネットワークとヘッドセットの操作検証とは別に記録する。

### 2026-09-18 実行結果

アルゴリズム修正後の再試験は **67 assertions PASS / Network=True**（Editor書き出し＋実Udon VM）。位置上限判定、静止時のノードTick 0回、上限サイズメッシュの128要素分割、途中キャンセル、スケール・材質の維持まで追加検証した。詳細・残る制限は [点検記録](FLARE-gimmick-algorithm-audit-2026-09-18.md) を参照。以下の36項目は初回MVP時の記録。

- UdonSharpコンパイル成功。
- `RunNetworkBatch`: **36 assertions PASS / Network=True**。保存済みPrefabのLOAD DEMOボタンを発火→GitHub rawから実ダウンロード→解析→実Udon Interactイベント→ドア90度・移動・非表示・許可音声まで確認。CLEARボタンの保存済み接続も確認。
- ゼロ移動・即時逆回転、Boolean setActive、Clear/再読込、循環階層・重複ID・範囲外アクセサ・過大画像・過剰Actionの拒否、未知Action/外部IDの無視、循環イベントの停止を確認。
- 実描画の前後画像もローカルで確認。画像は`Library/Flare-gimmick-before.png`と`after.png`に出力する（ClientSimの案内UIも写る）。
- テストはUnity Editor / ClientSim内のコンパイル済みUdon VM。VRChatクライアント/ヘッドセット操作、別の新規Unityプロジェクトへの導入、負荷の最悪値は未検証。ボタンは自動試験で保存済みUnityEventを発火しており、実ユーザーのポインター/VR操作の確認ではない。
- 再生成時にはUdon/OdinのPrefab保存に伴う`ArgumentNullException (unityObject)`ログが残る。生成Prefabの実行テストは上記のとおりPASSだが、ログが完全にクリーンな正式リリースと扱わない。

`Samples/Runtime-Test.unity`は自動試験用の床・スポーン設定であり、通常の導入にはPlayer Prefabを既存ワールドへ配置する。
