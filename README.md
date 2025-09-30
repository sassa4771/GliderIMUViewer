# GliderIMUViewer

[![Unity Version](https://img.shields.io/badge/Unity-6000.0.48f1-blue.svg)](https://unity.com/)
[![URP](https://img.shields.io/badge/URP-17.0.4-green.svg)](https://unity.com/srp/universal-render-pipeline)

グライダーの慣性計測装置（IMU）データを3D空間で可視化し、フライトシミュレーションを行うためのUnityアプリケーションです。

## 概要

GliderIMUViewerは、グライダーから取得したIMUセンサーデータ（加速度、角速度、姿勢角など）をCSVファイルから読み込み、3D空間でグライダーの動きを再現・可視化するツールです。タイムライン再生機能とグラフ表示により、フライトデータの詳細な分析が可能です。

## 主な機能

### データ可視化機能
- **CSVファイル読み込み**: クロスプラットフォーム対応のファイルダイアログでIMUデータを読み込み
- **3D姿勢表示**: リアルタイムでグライダーの姿勢（ロール、ピッチ、ヨー）を3D空間に表示
- **グラフ表示**: ロール、ピッチ、ヨー角の時系列グラフをリアルタイム描画
- **タイムライン再生**: 再生、一時停止、シーク、ループ機能を備えた直感的な再生コントロール

### フライトシミュレーション機能
- **物理ベースの空力モデル**: 揚力、抗力、失速を考慮した本格的なグライダーシミュレーション
- **キネマティクス再生**: IMUデータに基づいたグライダーの運動軌跡の再現
- **初速推定**: 加速度データから発射時の初速を自動推定

### 対応プラットフォーム
- Windows
- macOS
- Linux
- WebGL（ブラウザ対応）

## 使用技術

### Unity環境
- **Unity**: 6000.0.48f1 (Unity 6)
- **レンダリング**: Universal Render Pipeline (URP) 17.0.4
- **入力システム**: Unity Input System 1.14.0
- **UI**: TextMesh Pro

### 主要パッケージ
```json
{
  "com.unity.render-pipelines.universal": "17.0.4",
  "com.unity.inputsystem": "1.14.0",
  "com.unity.timeline": "1.8.7",
  "com.unity.ugui": "2.0.0"
}
```

### サードパーティアセット
- **UnityStandaloneFileBrowser**: クロスプラットフォームファイルダイアログ
- **Simple Line Graph** (MKM Tools): グラフ可視化
- **KTNK**: 3Dモデルとアニメーション
- **Ciathyza Gridbox Prototype Materials**: プロトタイプマテリアル

## セットアップ手順

### 必要要件
- Unity Hub
- Unity 6000.0.48f1 以降
- Git

### インストール

1. **リポジトリのクローン**
   ```bash
   git clone https://github.com/sassa4771/GliderIMUViewer.git
   cd GliderIMUViewer
   ```

2. **Unity Hubでプロジェクトを開く**
   - Unity Hubを起動
   - 「Open」→「Add project from disk」を選択
   - クローンしたディレクトリを選択

3. **依存関係の自動解決**
   - Unityがプロジェクトを開くと、必要なパッケージが自動的にダウンロードされます
   - 初回起動時は数分かかる場合があります

## 使用方法

### シーンの説明

プロジェクトには2つのメインシーンがあります：

#### 1. KinematicPreview（キネマティクスプレビュー）
`Assets/Scenes/KinematicPreview.unity`

IMUデータの基本的な可視化と再生を行うシーンです。

**主な機能:**
- CSVファイルの読み込み
- 姿勢角（ロール、ピッチ、ヨー）のグラフ表示
- タイムライン再生コントロール
- 3D空間での姿勢表示

#### 2. FlightSimulation（フライトシミュレーション）
`Assets/Scenes/FlightSimulation.unity`

物理ベースのフライトシミュレーションを行うシーンです。

**主な機能:**
- IMUデータに基づく飛行軌跡の再現
- 空力シミュレーション（揚力、抗力、失速モデル）
- キネマティクスベースの運動制御
- リアルタイム物理演算

### CSVファイルの読み込み

1. Unity Editorでシーンを開く（KinematicPreview または FlightSimulation）
2. 再生ボタンを押してアプリケーションを起動
3. 「CSVを開く」ボタンをクリック
4. ファイルダイアログでIMUデータのCSVファイルを選択
5. データが自動的に読み込まれ、可視化が開始されます

### 再生コントロール

- **再生/一時停止**: 画面下部の再生ボタンまたはスペースキー
- **シーク**: タイムラインスライダーをドラッグ
- **巻き戻し**: 巻き戻しボタン
- **ループ**: ループトグルで有効/無効を切り替え

### CSVデータ形式

IMUデータは以下の形式のCSVファイルで提供する必要があります：

#### 必須カラム
- `t_ms`: タイムスタンプ（ミリ秒）
- `roll`, `pitch`, `yaw`: 姿勢角（ラジアンまたは度）

#### オプションカラム
- `ax`, `ay`, `az`: 加速度（m/s² または G単位）
- `gx`, `gy`, `gz`: 角速度（deg/s）
- `dt_ms`: デルタタイム（ミリ秒）
- その他のカスタムカラム（PID制御値、サーボ値など）

#### サンプルデータ
```csv
src_seq,t_ms,dt_ms,ax,ay,az,gx,gy,gz,roll,pitch,yaw,pid_roll,pid_pitch,sv1,sv3
1,10437.000,0.000000,0.062000,0.019000,0.978000,2.800000,-2.590000,-1.400000,2.359000,0.000000,1.776000,4.689000,0.259000,97.000000,90.000000
2,10450.000,526.005005,0.066000,0.020000,0.990000,0.490000,-2.660000,0.070000,2.585000,0.000000,1.762000,5.166000,0.266000,97.000000,90.000000
```

サンプルCSVファイルは `Assets/StreamingAssets/out_20250912-152517.csv` にあります。

### カスタマイズ

#### PoseCsvStore設定
`PoseCsvStore`コンポーネントで以下の設定を調整できます：
- 角度単位（ラジアン/度）の自動検出
- 軸マッピング（X, Y, Z軸の割り当て）
- 加速度単位（m/s² または G）
- カラム名のマッピング

#### GliderAero設定（フライトシミュレーション）
`GliderAero`コンポーネントで空力パラメータを調整できます：
- 翼面積、アスペクト比
- 揚力係数、抗力係数
- 失速迎角、ピッチ安定性
- 大気密度

## プロジェクト構造

```
GliderIMUViewer/
├── Assets/
│   ├── Scenes/                    # メインシーン
│   │   ├── KinematicPreview.unity
│   │   └── FlightSimulation.unity
│   ├── Scripts/                   # C#スクリプト
│   │   ├── CsvOpenDialogButton.cs # CSVファイル選択UI
│   │   ├── FlightSimulation/      # フライトシミュレーション
│   │   │   ├── GliderAero.cs      # 空力モデル
│   │   │   └── GliderKinematicByPlayback.cs
│   │   └── KinematicPreview/      # データ可視化
│   │       ├── PoseCsvStore.cs    # CSVデータ管理
│   │       ├── PosePlaybackController.cs # 再生制御
│   │       ├── PoseApplier.cs     # 姿勢適用
│   │       └── PoseGraphView.cs   # グラフ描画
│   ├── Prefabs/                   # プレハブ
│   │   └── glider.fbx             # グライダー3Dモデル
│   ├── StreamingAssets/           # サンプルデータ
│   │   └── out_20250912-152517.csv
│   ├── StandaloneFileBrowser/     # ファイルダイアログライブラリ
│   └── Thirdparty/                # サードパーティアセット
├── Packages/                      # Unityパッケージ設定
└── ProjectSettings/               # プロジェクト設定
```

## ビルド方法

### スタンドアロンビルド（Windows/Mac/Linux）

1. `File` → `Build Settings` を開く
2. ターゲットプラットフォームを選択
3. `Build` または `Build And Run` をクリック

### WebGLビルド

1. `File` → `Build Settings` を開く
2. プラットフォームを `WebGL` に切り替え
3. `Build` をクリック
4. ビルドされたファイルをWebサーバーにデプロイ

**注意**: WebGL版では、ブラウザのセキュリティ制約によりローカルファイルの読み込みに制限があります。

## トラブルシューティング

### CSVファイルが読み込めない
- CSVファイルのエンコーディングがUTF-8であることを確認してください
- 必須カラム（`t_ms`, `roll`, `pitch`, `yaw`）が存在することを確認してください
- デリミタがカンマ（`,`）であることを確認してください

### グライダーが正しく表示されない
- PoseCsvStoreの角度単位設定を確認してください（ラジアン/度）
- 軸マッピング設定が正しいか確認してください

### フライトシミュレーションが不安定
- GliderAeroコンポーネントのパラメータを調整してください
- 特に`aeroSpeedClamp`、`maxForcePerStep`、`maxTorquePerStep`の値を確認してください

## ライセンス

プロジェクトのライセンスについては、リポジトリの管理者にお問い合わせください。

## 貢献

バグ報告や機能リクエストは、GitHubのIssuesページでお願いします。

## 作成者

**Sasatake Yuta** ([@sassa4771](https://github.com/sassa4771))

## 関連リンク

- [Unity公式サイト](https://unity.com/)
- [Universal Render Pipeline](https://unity.com/srp/universal-render-pipeline)
- [Unity Input System](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.14/manual/index.html)

---

**注意**: このプロジェクトは開発中です。機能や仕様は予告なく変更される可能性があります。
