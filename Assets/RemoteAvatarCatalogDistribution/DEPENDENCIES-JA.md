# 依存関係

- Unity 2022.3.22f1
- VRChat Worlds SDK 3.7.0 以上
- UdonSharp（VRChat Worlds SDK 同梱）
- lilToon
- Built-in Render Pipeline
- PC 向け VRChat World

VRChat Worlds SDK と lilToon は、unitypackage を Import する前に VCC / VPM から導入してください。

## ネットワーク

実行時の RAC2 読込には、World 側の `Allow Untrusted URLs` と、認証不要の公開 HTTPS エンドポイントが必要です。エンドポイントは redirect や HTML wrapper を挟まず、raw RAC2 bytes を直接返してください。