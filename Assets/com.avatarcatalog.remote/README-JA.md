# RAC1 配布・URL読み込みガイド

このガイドは、Unityで書き出したRAC1ファイルをインターネット上へ配置し、VRChatワールドの`RemoteAvatarCatalogLoader`または3D ImagePadから復元する方法を説明します。

RAC1はアバターやショップモデルを、ボーン・Animator・スクリプトを含まない**静的な3Dプレビュー**へ変換したバイナリ形式です。公開前に、geometry・textureの変換、公開ホスティング、第三者によるダウンロードについて権利者の許諾を得てください。

> [!IMPORTANT]
> 公開URLへ置いたRAC1は、VRChat利用者以外も保存できます。非公開モデルや、再配布許諾のないデータをアップロードしないでください。

## 「RAC1の直リンク」とは

このプロジェクトでいう直リンクは、URLの末尾が`.rac1`かどうかではなく、次の動作をするURLです。

```text
GET https://host.example/some/path
  -> 200 OK
  -> response bodyがRAC1ファイルそのもの
```

次の2つは、どちらも条件を満たせば直リンクです。

```text
https://owner.github.io/catalog/models/sample.rac1
https://catalog.example/api/slots/booth-001/rac1
```

2番目はURL上ではAPI routeですが、JSONを返さずRAC1のraw bytesを直接返すため、loaderから見れば直リンクです。拡張子ではなく、body先頭のASCII `RAC1` magicとバイナリ構造をloaderが検証します。

次のURLは使用できません。

- ログイン画面やダウンロードボタンを表示するHTMLページ
- Cookie、Bearer token、独自HTTP headerを要求するprivate URL
- RAC1をBase64やJSONへ包んで返すAPI
- SPAの`index.html`へフォールバックする存在しないパス
- 短縮URL、認証redirect、期限切れになる一時共有URL

## 全体の流れ

```text
UnityでRAC1を書き出す
  -> validatorで検証する
  -> 公開HTTPSホストへ配置する
  -> URLを確認する
  -> VRChatの3D ImagePadへURLを入力する
  -> VRCStringDownloader.ResultBytesをRAC1 parserが復元する
```

サーバーがMesh情報をJSONで返す方式ではありません。`VRCStringDownloader`という名前ですが、このloaderはUTF-8 textの`Result`ではなく、raw binaryの`ResultBytes`を使用します。

## 1. RAC1を書き出す

### ブース一式

1. Unityで`GameObject > Avatar Catalog > Create RAC1 Booth Authoring Root`を実行します。
2. ポーズを確定したアバターを`Avatar Root`へ割り当てます。
3. 店舗装飾を`Shop Visual Root`へ割り当てます。
4. Inspectorで`Validate`を押します。
5. エラーがなければ`Capture / Refresh`を押します。
6. `Capture + Export RAC1...`で`.rac1`を書き出します。

`SkinnedMeshRenderer`は書き出し時の姿勢で静的MeshへBakeされます。Animator、PhysBone、Collider、Rigidbody、Light、Audio、UdonなどはRAC1に含まれません。

### 単一Mesh

単一の`MeshFilter`または`SkinnedMeshRenderer`だけを書き出す場合は、Hierarchyで対象を選び、`Tools > FLARE > Developer > Legacy Exporters > RAC1 Exporter`を使用します。

### ローカル検証

リポジトリ付属validatorを実行します。

```powershell
python tools/rac1_validate.py .\sample.rac1
```

JSON形式の結果が必要な場合:

```powershell
python tools/rac1_validate.py --json .\sample.rac1
```

配信前のSHA-256も記録しておくと、アップロード後の破損を確認できます。

```powershell
Get-FileHash .\sample.rac1 -Algorithm SHA256
```

## 2. RAC1をHTTPSで公開する

### このプロジェクトの配信契約

配信URLは次を満たしてください。

| 項目 | 要件 |
|---|---|
| Protocol | 公開`https://` |
| Method | 認証不要の`GET` |
| Status | `200 OK` |
| Body | RAC1のraw bytesのみ |
| Magic | 先頭4 bytesがASCII `RAC1` |
| Content-Type | `application/octet-stream`推奨 |
| Content-Length | 正しいbyte数を推奨 |
| Redirect | 使用しない |
| Authentication | Cookie、login、Authorization header不要 |
| Not found | HTMLへfallbackせず`404` |

VRChat公式仕様が`Content-Type`、CORS、redirectの全挙動を保証しているわけではありません。本プロジェクトでは、環境差を減らすため「匿名GET・200・redirectなし・raw binary」を安全側の配信契約とします。

### 方法A: GitHub Pages

小規模な検証にはGitHub Pagesが簡単です。

```text
公開repository
└─ docs
   ├─ .nojekyll
   └─ models
      └─ sample.rac1
```

GitHubの`Settings > Pages`で、`main` branchの`/docs`を公開します。URLは次の形になります。

```text
https://OWNER.github.io/REPOSITORY/models/sample.rac1
```

`*.github.io`はVRChat String Loadingの標準Trusted URLに含まれます。GitHub Pagesへ独自ドメインを設定した場合、その独自ドメイン自体は`*.github.io`ではないため、別途Trusted URLの扱いを確認してください。

### 方法B: Object Storage / CDN

Cloudflare R2、S3互換storage、一般的なCDNでも配信できます。

1. `.rac1`をobjectとしてアップロードします。
2. objectを匿名でGETできるpublic URLにします。
3. `Content-Type`を`application/octet-stream`にします。
4. loginや期限付きredirectを挟まず、最終objectを直接返します。
5. custom domainがVRChatのTrusted URLでなければ、利用者へ`Allow Untrusted URLs`が必要なことを案内します。

常設カタログでは、期限付き署名URLよりも、権利確認済みpreviewだけを置く安定したpublic URLを推奨します。

### 方法C: Web API route

静的file hostでなくても、routeがraw bytesを直接streamすれば使用できます。

```text
GET /api/catalog/slots/booth-001/rac1
Content-Type: application/octet-stream
Body: <RAC1 bytes>
```

現在の`chatgpt.site`版はこの方式です。サイト全体のログインgateを有効にするとUdonはCookieを送れず`401`になるため、RAC1配信routeまで匿名アクセス可能でなければなりません。

`chatgpt.site`はVRChat String Loadingの標準Trusted URLではありません。プロトタイプでは各利用者がVRChatの`Settings > Security > Allow Untrusted URLs`をONにします。本番で操作不要にしたい場合は、VRChatの最新Trusted URL一覧に含まれる配信先を選んでください。

## 3. 公開URLを検証する

ブラウザーで開けるだけでは不十分です。HTML表示や認証redirectを見落とさないよう、実際にbytesを保存して検証します。

```powershell
$racUrl = 'https://example.com/models/sample.rac1'
curl.exe --fail --silent --show-error `
  --dump-header .\rac1-headers.txt `
  --output .\downloaded.rac1 `
  $racUrl

python tools/rac1_validate.py .\downloaded.rac1
Get-FileHash .\downloaded.rac1 -Algorithm SHA256
```

次を確認します。

- 最初の応答が`200 OK`
- 保存したfileをvalidatorがRAC1として受理する
- 元fileとダウンロード後fileのbyte数・SHA-256が一致する
- response bodyがHTML、JSON、ログイン画面ではない
- URLが短時間で失効しない

`HEAD`に対応しないhostもあるため、最終確認は必ず`GET`で行います。

## 4. 3D ImagePadから読み込む

3D ImagePad prefabを含むワールドでは、利用者が実行時にURLを入力できます。

1. VRChatで3D ImagePadのURL欄を選択します。
2. 公開HTTPS URLを貼り付けます。
3. `LOAD URL`を押します。
4. `READY`とvertex/index数が表示されるまで待ちます。

ボタンの意味:

| Button | 動作 |
|---|---|
| `LOAD URL` | 入力中のURLを読み込む |
| `SAMPLE` | world作者が登録した検証用URLを読み込む |
| `RETRY` | 最後に要求したURLを再試行する |
| `CLEAR` | 現在の表示を消す |

現在のImagePadは次の仕様です。

- URLは空でない小文字の`https://`から始まる必要があります。
- file名の拡張子は判定せず、取得後のRAC1 magicと構造を検証します。
- loading中の二重要求は拒否します。
- `CLEAR`は進行中のHTTP request自体をcancelできません。結果が返った時点で破棄してから次を受け付けます。
- URL、選択、表示Meshは各visitorのローカル状態で、自動同期しません。

### 固定URL loaderとの違い

`RemoteAvatarCatalogLoader`単体は、world作者がUnity Inspectorの`Catalog Urls`へ事前登録した`VRCUrl`を読み込みます。ダウンロードしたJSON中の文字列から、Udonが任意の`VRCUrl`を生成することはできません。

3D ImagePadは、利用者が`VRCUrlInputField`へ直接入力した値を取得し、専用loaderへ渡すwrapperです。そのため、worldを再buildせずに利用者自身のURLを試せます。

## VRChatとRAC1の上限

VRChat String Loadingの公式上限:

- 1 downloadにつき最大100 MB
- string downloadは5秒に1件
- queueは最大1000件
- 上限を超えた要求はqueueされ、順序は保証されない

ただし、Standard Booth profileを有効にした本プロジェクトの3D ImagePadはさらに厳しく検証します。

| Resource | Standard-3x3-v1 limit |
|---|---:|
| RAC1 file | 10,000,000 bytes |
| Vertices | 40,000 |
| Triangle indices | 120,000 |
| Texture | RGBA32 1024 x 1024、1枚 |
| Booth volume | 3.0 m x 2.7 m x 3.0 m |

このMVPはPC版VRChat向けです。Quest / Androidでの復元は対象外です。

## 更新とcache

同じURLの内容を差し替える場合は、古いRAC1がCDN cacheから返らないようにします。

- 検証中・固定slot: `Cache-Control: no-store, max-age=0`を推奨
- immutable配布: file名へversionまたはSHA-256を含め、内容を上書きしない
- CDNを使う場合: 更新時にpurgeして、originと公開URLのhashを再確認

固定Inspector slotでURL自体を変更するとworldの再buildが必要です。同じ固定URLのbodyだけを安全に更新すれば、worldを再uploadせず表示内容を差し替えられます。ImagePadでは利用者が新しいURLを入力できます。

## Troubleshooting

| 症状 / Status | 主な原因 | 確認すること |
|---|---|---|
| `401` / `403` | private host、login gate | 匿名GETにする。Cookie・認証headerを要求しない |
| `404` | URLまたはobject keyが違う | browser pageではなく実object pathを確認 |
| `RAC1 magic is missing` | HTML、JSON、エラーpageを取得 | `curl`でbodyを保存しvalidatorを実行 |
| URLを取得できない | Untrusted URL、TLS、network | VRChatの`Allow Untrusted URLs`とdomainを確認 |
| file cap超過 | Standard profileで10 MB超 | exporterの容量を減らす |
| vertex/index cap超過 | geometryが重い | preview用Meshを簡略化する |
| booth bounds error | 3 x 3 x 2.7 m外 | BoothRootの位置、scale、floor原点を確認 |
| texture error | 1024超過、不正byteLength | 1024 RGBA32 atlasとして再export |
| 更新しても古い表示 | CDN/browser cache | cache purge、no-store、公開URLのhash確認 |

ImagePadはloader errorの末尾に`Allow Untrusted URLs`の案内を付けますが、原因が必ずUntrusted URLとは限りません。最初に表示された形式・HTTP・上限エラーを先に確認してください。

## 配布前チェックリスト

- [ ] geometry・textureの変換とpublic hostingについて権利者の許諾がある
- [ ] `rac1_validate.py`が成功する
- [ ] fileがStandard profileの上限内に収まる
- [ ] URLが匿名HTTPS GETで`200`とraw RAC1を返す
- [ ] 元fileと公開URLから取得したfileのSHA-256が一致する
- [ ] Trusted URL、または`Allow Untrusted URLs`の案内を用意した
- [ ] PC版VRChatのBuild & Testで表示を確認した
- [ ] 掲載終了時にobjectとcacheを削除できる運用がある

## 公式資料

- [VRChat: String Loading](https://creators.vrchat.com/worlds/udon/string-loading/)
- [VRChat: External URLs](https://creators.vrchat.com/worlds/udon/external-urls/)
- [GitHub Pages](https://docs.github.com/pages/getting-started-with-github-pages/creating-a-github-pages-site)
