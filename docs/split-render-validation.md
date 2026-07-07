# 分割描画検証

記録日: 2026-07-04

## 概要

PointCloudViewerでは、以前のStage方式ではなく分割描画方式を使っています。
現在の実装は、1フレームに1スライスを永続スライス色・深度バッファへ描画し、有効な全スライスを再投影と深度テストで合成します。

2026-07-04のユーザー目視確認では、分割描画の出力は正しく描画されていると確認しました。

## 検証対象の実装

- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - `OpcSplitRenderFeature`: URPから分割描画パスを有効化します。
  - `OpcSplitRenderPass`: 1フレームに1スライスを描画し、有効なスライスを合成します。
  - `OpcSplitRenderBridge`: `OpcLoader` が準備した描画データを受け取ります。
- `Assets/PointCloudViewer/Shader/PointCloud_CircleSplit.shader`
  - スライスオフセットとストライドを使い、現在のスライスに属する点だけを描画します。
- `Assets/PointCloudViewer/Shader/PointCloudSplitComposite.shader`
  - 各スライスを、その撮影時VP行列から現在カメラVP行列へ再投影します。
  - 深度を書き込み、最前面のスライスや背景との前後関係を深度テストで評価できるようにします。
- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - ベンチマーク比較用に `Direct` / `Split` 描画モードを切り替えます。

## 検証条件

| 項目 | 結果 | 証拠 |
| --- | --- | --- |
| Splitモード選択 | 確認済み | `OpcLoader.Mode.Split avg = 1.00` |
| 比較用Directモード選択 | 確認済み | Direct実行で `OpcLoader.Mode.Direct avg = 1.00` |
| 同一カメラ経路 | ベンチマーク設定で確認 | 両方の実行で `CameraOrbitMover` と `BenchmarkSession` を使用 |
| 選択ノード数の比較可能性 | 確認済み | Split平均 799.98、Direct平均 799.98 |
| 描画コマンド数の比較可能性 | 確認済み | Split平均 669.53、Direct平均 671.17 |
| 描画点数の比較可能性 | 確認済み | Split平均 5,215,645、Direct平均 5,207,191 |
| 視覚的な正しさ | ユーザー確認済み | 正しく描画されているとユーザーが確認 |

## URP / Render Graph制約

記録日: 2026-07-08

対象バージョン:

- Unity Editor: 6000.3.9f1
- URP package: `com.unity.render-pipelines.universal` 17.3.0

現在の実装経路:

- `OpcSplitRenderFeature` は `ScriptableRendererFeature` を継承しています。
- `AddRenderPasses` はメインGameカメラだけを対象に、Rendererへパスを注入します。
- パスは `RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)` を使うため、URP Render Graph経路に依存しています。
- 合成先として、`UniversalResourceData.activeColorTexture` と `activeDepthTexture` を現在のカメラ色・深度ターゲットとして使います。
- スライス色・深度バッファは永続的な外部 `RTHandle` です。フレームをまたいで内容を保持する必要があるため、毎フレーム `renderGraph.ImportTexture` でRender Graphへ取り込みます。
- スライス描画とスライス合成は `AddRasterRenderPass` で登録します。

現在の制約:

- 結果は `AfterRenderingTransparents` に依存します。注入タイミングを変えると、シーンジオメトリ、Transparent、点群深度の関係が変わる可能性があります。
- Render Graph内部の一時テクスチャはスライス内容をフレーム間で保持できないため、分割バッファは手動管理しています。
- 現在は有効な各スライスが1つの合成Raster Passになるため、合成コストは `SplitCount` とともに増えます。
- 実装はGameカメラ経路でactive color/depth textureが有効であることを前提にしています。

移行作業が必要になる条件:

- URPがここで使っているframe dataアクセス、特に `UniversalResourceData.activeColorTexture` または `activeDepthTexture` を変更・削除した場合。
- 手動管理のクロスフレーム `RTHandle` スライスではなく、サポートされたcamera historyベースの設計が必要になった場合。
- `SplitCount` を増やし、合成パスのオーバーヘッドが実測上のボトルネックになった場合。
- Scene view、camera stacking、XR、複数カメラ、メインカメラ以外の描画を、スライス履歴を壊さずに支援する必要が出た場合。

今すぐ大きく移行しない理由:

- 現在の実装は `RecordRenderGraph` を通じて、すでにRender Graph API経路に乗っています。
- 現在の証拠化作業は、正しさのスクリーンショット、SplitCount比較、既知制限の整理が中心です。
- 追加検証の前に描画経路をリファクタリングすると、現在のベンチマークとスクリーンショット証拠との比較が難しくなります。

2026-07-08に確認したUnity公式資料:

- Unity Manual: Render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph.html
- Unity Manual: Introduction to the render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-introduction.html
- Unity Manual: Write a render pass using the render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-write-render-pass.html
- Unity Manual: Frame data in the render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-frame-data.html
- Unity Manual: Optimize a render graph: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-optimize.html
## スクリーンショット証拠

ドキュメント用画像は横幅1280pxにリサイズしています。元のキャプチャは `Recordings/` に残しています。

### 点群のみの静止画

![分割描画された街の点群](images/split-render-city-still-1280.jpg)

元画像: `Recordings/Image Sequence_003_0000.jpg`

結果:

- 分割描画の出力は街の点群全体を覆っており、目立つタイル抜けや黒い欠損は見えません。
- この静止画では、大きな深度順序の破綻は見えません。
- 静止画としての視覚的な正しさを支える証拠ですが、時間方向のちらつきや解像度変更後の破綻は検証していません。

### 背景ジオメトリとの深度確認

![テストジオメトリを含む分割描画点群](images/split-render-depth-geometry-1280.jpg)

元画像: `Recordings/Image Sequence_004_0000.jpg`

結果:

- 背景ジオメトリが点群より手前にある箇所では、点群を正しく隠しています。
- ジオメトリ周辺でも点群は表示されており、目立つタイル抜けや黒い欠損は見えません。
- この静止画では、テストジオメトリ周辺に大きな深度順序の破綻は見えません。
- 分割描画された点群スライスとシーンジオメトリの静止画上の深度関係を支える証拠です。
## ベンチマーク結果

### 分割描画

| 指標 | 平均 | p50 | p95 | 最大 |
| --- | ---: | ---: | ---: | ---: |
| Frame ms | 18.23 | 18.13 | 20.67 | 27.72 |
| Draw commands | 669.53 | 709.00 | 776.00 | 789.00 |
| Draw points | 5,215,645 | 5,229,510 | 5,531,473 | 5,673,077 |
| Slice points per frame | 1,303,911 | 1,307,369 | 1,382,625 | 1,418,164 |
| Split count | 4.00 | 4.00 | 4.00 | 4.00 |
| Composite draw calls | 4.00 | 4.00 | 4.00 | 4.00 |
| Slice draw calls | 669.53 | 709.00 | 776.00 | 789.00 |
| GetVisibleNodes ms | 0.29 | 0.28 | 0.37 | 0.48 |
| SyncNodeStreaming ms | 0.42 | 0.39 | 0.67 | 2.00 |
| DrawPointCloud CPU marker ms | 0.13 | 0.12 | 0.17 | 0.23 |

平均フレーム時間から計算した概算FPS: 54.9 FPS。

### Direct描画

| 指標 | 平均 | p50 | p95 | 最大 |
| --- | ---: | ---: | ---: | ---: |
| Frame ms | 31.38 | 31.42 | 34.29 | 79.32 |
| Draw commands | 671.17 | 709.00 | 774.00 | 789.00 |
| Draw points | 5,207,191 | 5,217,677 | 5,530,117 | 5,659,720 |
| Direct draw calls | 1.00 | 1.00 | 1.00 | 1.00 |
| GetVisibleNodes ms | 0.26 | 0.26 | 0.33 | 0.90 |
| SyncNodeStreaming ms | 0.31 | 0.29 | 0.50 | 1.22 |
| DrawPointCloud CPU marker ms | 0.11 | 0.11 | 0.15 | 0.23 |

平均フレーム時間から計算した概算FPS: 31.9 FPS。

### SplitCount比較

記録日: 2026-07-08

計測条件:

- 描画モード: Split
- ベンチマーク実行: `CameraOrbitMover` + `BenchmarkSession`
- Capture設定: `BenchmarkSession (capture=60fps)`
- 比較値: `SplitCount = 2`、`3`、`4`
- 同一経路の意図: 3回の実行で同じベンチマーク経路と同じシーン設定を使用

| SplitCount | 平均Frame ms | 概算FPS | 平均Draw points | 平均Slice points/frame | 平均Composite draw calls | 平均Draw commands |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 19.72 | 50.7 | 4,652,939 | 2,326,470 | 2.00 | 680.46 |
| 3 | 17.26 | 57.9 | 4,672,523 | 1,557,508 | 3.00 | 683.88 |
| 4 | 16.85 | 59.3 | 4,672,523 | 1,168,131 | 4.00 | 683.88 |

メモ:

- `SplitCount = 2` は1フレームあたり約233万点のスライス点を描画し、平均19.72msでした。
- `SplitCount = 3` はスライス点数が1フレームあたり約156万点まで減り、平均17.26msでした。
- `SplitCount = 4` はスライス点数が1フレームあたり約117万点まで減り、平均16.85msでした。
- この経路では、測定した値の中で `SplitCount = 4` が最も60FPSに近い結果でした。
- 合成Draw callは `SplitCount` に比例して増えるため、`8` などの高い値は別途確認が必要です。

## 解釈

選択ノード数、描画コマンド数、総描画点数がほぼ同じなので、2つの実行結果は比較可能です。
この条件では、分割描画は平均フレーム時間を31.38msから18.23msへ短縮しました。
平均フレーム時間では約1.72倍高速です。

CPUマーカー `OpcLoader.DrawPointCloud` は描画処理全体のGPUコストを含みません。
GPU timestampやProfiler captureを追加するまでは、性能比較の主指標として `Frame` を使います。

`SplitCount = 4` では、分割描画経路は1フレームあたり約4分の1の点を描画します。

- 準備された総点数: 約522万点
- 1フレームあたりのスライス点数: 約130万点

このテストでは、4回のフルスクリーン合成コストより、点描画コスト削減の効果が大きいです。

## 既知の制限

- 静止画の点群出力と背景ジオメトリとの深度関係については、スクリーンショット証拠をコミット済みです。
- Direct描画、スライス描画、合成パスごとのGPU専用時間はまだ分離して測定していません。
- `Frame` には点群描画以外のフレーム処理も含まれます。
- 結果は1つのカメラ経路、1つのデータセット、1つの解像度、測定済みの分割数に対するものです。
- `SplitCount = 2`、`3`、`4` は比較済みですが、`SplitCount = 8` は未測定です。

## 次の確認

- カメラ移動後と解像度変更後の追加スクリーンショットを保存する。
- `SplitCount = 8` を追加測定し、合成コストが支配的になり始めるか確認する。
- フレーム時間以上の説明が必要になったら、GPU Profiler証拠を追加する。
