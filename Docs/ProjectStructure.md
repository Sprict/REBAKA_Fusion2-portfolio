# REBAKA_Fusion2 プロジェクト構造ドキュメント

## 1. フォルダ構成

### 主要ディレクトリ

```
Assets/
  ├── Art/                 - モデル、テクスチャ、マテリアルなどのアート関連ファイル
  │   ├── Materials/       - マテリアル
  │   ├── Models/          - 3Dモデル
  │   └── Textures/        - テクスチャ
  │
  ├── Audio/               - 音楽、効果音などのオーディオファイル
  │   ├── Music/           - BGM
  │   └── Sound/           - 効果音
  │
  ├── Code/                - スクリプトファイル
  │   ├── Scripts/         - C#スクリプト
  │   │   ├── Network/     - ネットワーク関連のスクリプト
  │   │   ├── Player/      - プレイヤー関連のスクリプト
  │   │   └── Utils/       - ユーティリティスクリプト
  │   └── Shaders/         - シェーダーファイル
  │
  ├── Docs/                - ドキュメント
  │   └── Snippets/        - コードスニペット
  │
  ├── Level/               - レベル関連のファイル
  │   ├── Prefabs/         - レベル用プレハブ
  │   ├── Scenes/          - シーンファイル
  │   └── UI/              - UI関連のファイル
  │
  ├── Photon/              - Photon Fusion関連のファイル
  │   ├── Fusion/          - Fusionコア
  │   ├── FusionDemos/     - デモシーン
  │   └── PhotonLibs/      - Photonライブラリ
  │
  ├── Prefabs/             - プレハブ
  │   └── Player/          - プレイヤー関連のプレハブ
  │
  └── Settings/            - プロジェクト設定
```

### _Deprecated

`_Deprecated`フォルダには、以前使用されていたが現在は使用されていないアセットが格納されています。新規開発は、このフォルダ以外のアセットを参照してください。

## 2. 主要クラスの役割と関係性

### ネットワーク関連

#### GameLauncher
`Assets/Code/Scripts/Network/GameLauncher.cs`

Photon Fusionを使用したネットワークゲームの起動と管理を担当します。主な機能は：
- ゲームセッションの作成/参加
- プレイヤーのスポーン処理
- 入力の処理と送信
- ネットワークコールバックの処理

#### NetworkInputData
`Assets/Code/Scripts/Network/NetworkInputData.cs`

ネットワーク上で共有される入力データの構造体です。主なフィールドは：
- `direction`: 移動方向ベクトル
- `BodyDir`: 視点方向
- `buttons`: アクションボタン状態

### プレイヤー関連

#### RagdollInput
`Assets/Code/Scripts/Player/RagdollInput.cs`

プレイヤーの入力処理を担当します。`NetworkInputData`からゲームアクション（移動、ジャンプなど）への変換を行います。

#### RagdollController
`Assets/Code/Scripts/Player/RagdollController.cs`

プレイヤーキャラクターの全体的な制御を担当します。

#### RagdollPhysics
`Assets/Code/Scripts/Player/RagdollPhysics.cs`

物理演算に基づくキャラクターの動きを処理します。

#### RagdollState
`Assets/Code/Scripts/Player/RagdollState.cs`

キャラクターの状態（立っている、ジャンプ中など）を管理します。

### カメラ関連

#### MyCameraController
`Assets/Code/Scripts/MyCameraController.cs`

プレイヤーカメラの制御を担当します。

## 3. Photon Fusion設定

### 基本設定

Photon Fusionは、リアルタイムマルチプレイヤーゲーム用のネットワークエンジンです。本プロジェクトでは以下の設定で使用されています：

- **ネットワークモード**: `GameMode.AutoHostOrClient`（自動的にホストになるか、既存のセッションに参加）
- **プレイヤー上限**: 4人
- **同期方式**: 状態ベースとイベントベースの混合

### ネットワーク同期

以下の要素がネットワーク上で同期されます：
- プレイヤーの位置と回転
- プレイヤーの入力と状態
- ゲーム内の相互作用オブジェクト

### 実装上の注意点

- `NetworkObject` コンポーネントを持つオブジェクトのみがネットワーク上で同期されます
- 入力は `NetworkInputData` 構造体を通じて送信されます
- 状態変更が頻繁なオブジェクトには `NetworkTransform` を使用してください 