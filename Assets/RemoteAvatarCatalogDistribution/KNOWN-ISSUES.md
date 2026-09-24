# FLARE Core 0.2.5 — Known issues / 既知の問題

## 日本語

- シリウスの確認データで、読込後に虹彩・瞳孔が見えない事象を確認しています。原因・修正は未確定です。このパッケージで解決済みとはしていません。
- lilToonの全設定・全てのモデルの外観を完全再現する形式ではありません。
- PhysBoneはSDK 3.10.4を使って動きをVATへ焼き込む機能です。読込後のライブ物理・プレイヤーの掴み操作は再現しません。
- 大きなメッシュやテクスチャの読み込みでは、一時的な処理落ちが発生する場合があります。
- PC / Built-in向けです。Quest / Androidは未検証です。
- JP/ENのGUIレイアウト、日本語フォント、VRChat実機での表示・操作は、今回の自動検証には含まれません。

販売時はこの制限を購入前に確認できるようにしてください。使用手順はUSER-GUIDE-JA.mdを参照してください。

## English

- A Sirius test exhibit has missing visible irises/pupils after loading. The cause and fix are unconfirmed; this package does not claim to resolve it.
- Full lilToon material fidelity and identical appearance for every model are not guaranteed.
- PhysBone motion is baked into VAT using SDK 3.10.4. Live physics and player grabbing after loading are not reproduced.
- Large meshes and textures may cause loading hitches.
- Intended for PC / Built-in. Quest / Android are not verified.
- JP/EN visual layout, Japanese glyph rendering and VRChat interaction were not covered by the automated checks for this delivery.

Disclose these limitations before purchase. See USER-GUIDE-EN.md for instructions.
