# 依存関係

- Unity 2022.3.22f1
- VRChat Worlds SDK 3.10.4（現在の検証構成。PhysBone焼き込みはこの版に依存）
- UdonSharp（VRChat Worlds SDK 同梱）
- lilToon 2.3.4（現在の検証構成）
- Built-in Render Pipeline
- PC 向け VRChat World

VRChat Worlds SDK と lilToon は、unitypackage を Import する前に VCC / VPM から導入してください。

## ネットワーク

実行時のRAC2読み込みには、認証不要の公開HTTPSエンドポイントが必要です。信頼済み以外のドメインでは利用者側のVRChat設定で `Allow Untrusted URLs` を有効にします。リダイレクトやHTML閲覧ページを挟まず、RAC2ファイル本体を直接返してください。

MA衣装を書き出す場合のみModular Avatar / NDMFが必要です。検証構成はMA 1.18.7 / NDMF 1.14.8です。詳細はUSER-GUIDE-JA.mdを参照してください。
