# PointCloudViewer

PointCloudViewerは、LAS/TPC/OPC由来の大量点群をUnityで表示するための検証用Viewerです。
LOD選択、Frustum Culling、Streaming、GraphicsBuffer、Indirect Drawを組み合わせ、数百万〜1億点規模の表示を扱います。
現在の主な成果は分割描画方式です。旧画像スライス方式では、同一ベンチマーク経路でDirect描画の平均31.38msに対し、Split count 4で平均18.23msまで短縮しました（2026-07計測）。
2026-10に、カメラ移動時のちらつきを解消するため点キャッシュ方式へ作り替えました。2026-10-24のエディタ上の計測では、Direct描画の平均25.8msに対し、Split count 4で平均23.0msでした。
スクリーンショット、SplitCount比較、ちらつき計測、URP / Render Graph上の制約は [分割描画検証](docs/split-render-validation.md) に記録しています。
2026-11に、時間分割を使わずに速くする試作として集め直し方式（Gather）を追加しました。エディタ上の計測では、Direct描画の平均24.6〜26.7msに対し、Gatherは平均17.9〜18.7msでした（[集め直し方式](#集め直し方式gather試作)）。

## 現在の焦点

現在の描画検証では、以前のStage方式ではなく分割描画方式を使っています。
毎フレーム点群の1/Nだけを1画素の点として整列し、1画素1点に絞った点一覧（ワールド座標・色・ワールド半径）へ詰め直します。
点一覧はN本保持し、全N本を毎フレーム現在のカメラで円として描き直します。

現在のベンチマークと正しさのメモは [分割描画検証](docs/split-render-validation.md) を参照してください。

## 分割描画の概要

主なファイル:

- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - OPCデータの読み込み、表示ノード選択、点データのストリーミング、描画コマンド準備、`Direct` / `Split` 描画モードの切り替えを担当します。
- `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs`
  - 描画コマンドを準備し、ベンチマークログ用に準備済み点数を公開します。
- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - 整列・詰め直し・表示の3パスを積むURP Renderer Featureです。1フレームに1本の点一覧を作り直し、全N本を表示します。
- `Assets/PointCloudViewer/Shader/PointCloud_SplitFill.shader`
  - 現在のスライスに属する点を1画素の点として描き、ワールド座標・ワールド半径・色を出力します。
- `Assets/PointCloudViewer/Shader/PointCloudCacheCompact.compute`
  - 点のある画素だけを点一覧へ詰め、点数を間接描画の引数へ書きます。
- `Assets/PointCloudViewer/Shader/PointCloud_CacheSplat.shader`
  - 点一覧の各点を、Direct描画と同じ点サイズ式で円として描きます。

## URP / Render Graphメモ

対象環境: Unity 6000.3.9f1、URP 17.3.0。

分割描画経路は、URPの `ScriptableRendererFeature` と `ScriptableRenderPass` として実装しています。パスは `AddRenderPasses` から注入し、実際の描画処理は `RecordRenderGraph` でRender GraphのRaster Passとして登録します。

現在の依存:

- `RenderPassEvent.BeforeRenderingTransparents` を使い、不透明と空の描画後・半透明の前に分割描画点群を合成します（集め直し方式も同じ）。半透明は点群の深度で前後判定されます。`_CameraDepthTexture`（不透明の直後にコピー）と `_CameraOpaqueTexture`（空の直後にコピー）には点群は入りません。
- `RecordRenderGraph` で `UniversalResourceData.activeColorTexture` と `activeDepthTexture` を読み、現在のカメラの色・深度ターゲットへ点群の円を書き込みます。
- 整列用の作業テクスチャは、そのフレームで使い切るためRender Graphの一時テクスチャにしています。
- 点一覧と間接描画の引数は外部 `GraphicsBuffer` として保持し、毎フレーム `ImportBuffer` でRender Graphへ取り込みます。点一覧1本の容量は画面の画素数です。
- 詰め直しは `AddComputePass`、整列と表示は `AddRasterRenderPass` で登録します。表示パスは1つで、その中で有効な点一覧ごとに間接描画を発行するため、Draw call数は `SplitCount` に比例します。

移行メモ:

- 現在の実装は、旧Compatibility modeの `Execute` 経路ではなく、すでにRender Graph経路を使っています。
- 大きな移行が必要になるのは、URPのactive color/depthのframe data契約が変わる場合、フレームをまたぐ点一覧をURP camera historyへ寄せる場合、または表示コスト削減のために点一覧の統合が必要になった場合です。
- 今は正しさのスクリーンショットとベンチマーク証拠を優先します。この段階で描画経路を大きく作り替えると、現在の測定結果との比較が難しくなります。

2026-07-08に確認したUnity公式資料:

- Unity Manual: Render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph.html
- Unity Manual: Write a render pass using the render graph system in URP: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-write-render-pass.html
- Unity Manual: Optimize a render graph: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-optimize.html
## 集め直し方式（Gather、試作）

内蔵GPUでは点群描画が頂点処理で詰まっており、Direct描画は1点を4頂点・三角形2枚のQuadで描いています。集め直し方式は、点を1頂点の1画素点で描き、円の形は画面上で作り直します。

1. 全点を1画素の点として作業テクスチャへ描き、1画素に最も手前の1点だけを残します（色・画面半径・深度）。
2. computeシェーダで、各画素の周り半径4画素の点を調べ、自分を覆う円のうち最も手前の点の色と深度を作業テクスチャへ書きます。8x8画素の区画と周り4画素を共有メモリへ1回だけ読み込み、テクスチャ読み込みを減らしています。
3. 全画面パスで、その結果をカメラの色と深度へ書き写します。円に覆われない画素は書きません。
4. ノード中心での画面半径が4画素を超えるノード（周回中の点の約10%）は、これまで通りQuadで描きます。

主なファイル:

- `Assets/PointCloudViewer/Script/Opc/GatherRender/OpcGatherRenderFeature.cs`: 点描き → 集め直し → 書き写しのパスを積むURP Renderer Featureです。`_useComputeResolve` をオフにすると、集め直しを全画面のフラグメントシェーダで行う版に切り替わります。
- `Assets/PointCloudViewer/Shader/PointCloud_GatherFill.shader`: 1画素の点を描き、色と画面半径を詰めた値・深度を出力します。
- `Assets/PointCloudViewer/Shader/PointCloudGatherResolve.compute`: 共有メモリを使った集め直しです。
- `Assets/PointCloudViewer/Shader/PointCloud_GatherComposite.shader`: 集め直しの結果をカメラへ書き写します。
- `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs`: 円が大きいノードのコマンドを先頭へ集め、その範囲だけQuadで描きます。

計測日: 2026-11-01、Unityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080、内蔵GPU（Intel UHD、D3D11）。モードを交互に各2回計測しました。

| モード | 平均フレーム時間 | メモ |
| --- | ---: | --- |
| Direct | 24.6 / 26.7 ms | Quadで円を描く |
| Gather（大きい円はQuad） | 18.7 / 17.9 ms | Quadで描く点は平均約44万点（全体の約10%） |
| Gather（全点を集め直し、半径4で切る） | 16.9 / 17.4 ms | 2026-11-01の別の回。Directは27.6 / 26.6 ms |
| Gather（フラグメント版の集め直し） | 22.4 / 22.6 ms | 同上 |
| DirectPixelPoints（比較用、全点1画素） | 11.4〜11.6 ms | 円を作らないため手前の面が透け、見た目は別物 |

見た目: 静止画（1280x720のキャプチャ）でDirectと色が8段階より大きく違う画素は9.0%で、円の境目でどちらの円が手前になるかの違いです。明るさの偏りや画素のずれはなく、目視ではほぼ同じです。

制限:

- エディタ上の1経路・1視点だけの計測です。ビルドでの計測と、カメラ移動時の見た目の確認はまだです。
- パスごとのGPU時間は取れていません。集め直しの重さは、半径を変えた時のフレーム時間の差から見積もっています（フラグメント版で半径4が約9ms）。
- 円が大きいノードの判定はノード中心の奥行きで行うため、同じノード内でカメラに近い点の円は半径4画素で切れることがあります。
- 集め直しの半径（4画素）は、`OpcLoader.GatherRadiusPixels` と2つのシェーダの `GATHER_RADIUS` で揃える必要があります。

## ベンチマーク（点キャッシュ方式）

Direct描画と分割描画の比較。計測日: 2026-10-24、Unityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080。

| モード | 平均フレーム時間 | p95 | 概算FPS | 平均点数 | メモ |
| --- | ---: | ---: | ---: | ---: | --- |
| Split, count 4 | 23.0 ms | 26.8 ms | 43.5 FPS | 4,290,099 | 1フレームあたり約1,072,525点を整列し、4本の点一覧を表示 |
| Direct | 25.8 ms | 30.6 ms | 38.8 FPS | 4,290,099 | 単一の直接Indirect Draw経路 |

2回ずつ計測し、Directは25.8 / 25.7ms、Splitは23.0 / 22.9msでした。このベンチマーク経路では、分割描画はDirect描画より平均フレーム時間で約1.12倍高速です。
下の旧方式の値とは計測環境と描画点数が異なるため、直接は比較できません。

## ベンチマーク（旧画像スライス方式）

以下は2026-07に旧画像スライス方式（スライスを色・深度バッファとして保持し、再投影して合成）で計測した値です。

Direct描画と分割描画の比較。計測日: 2026-07-04

| モード | 平均フレーム時間 | 概算FPS | 平均点数 | メモ |
| --- | ---: | ---: | ---: | --- |
| Split, count 4 | 18.23 ms | 54.9 FPS | 5,215,645 | 1フレームあたり約1,303,911点を描画し、4スライスを合成 |
| Direct | 31.38 ms | 31.9 FPS | 5,207,191 | 単一の直接Indirect Draw経路 |

このベンチマーク経路では、分割描画はDirect描画より平均フレーム時間で約1.72倍高速でした。

SplitCount比較。計測日: 2026-07-08

| SplitCount | 平均フレーム時間 | 概算FPS | 平均点数 | 1フレームあたりのスライス点数 | 合成Draw call |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 19.72 ms | 50.7 FPS | 4,652,939 | 2,326,470 | 2 |
| 3 | 17.26 ms | 57.9 FPS | 4,672,523 | 1,557,508 | 3 |
| 4 | 16.85 ms | 59.3 FPS | 4,672,523 | 1,168,131 | 4 |

このベンチマーク経路では、測定した範囲の中で `SplitCount = 4` が最も60FPSに近い結果でした。ただし `SplitCount` を増やすほど合成Draw callも増えるため、より大きい分割数は別途測定が必要です。

## 現在の制限

- 点キャッシュ方式の処理時間は、エディタ上の1経路・`SplitCount = 4` だけの計測です。SplitCount比較は旧画像スライス方式の値です。
- 静止画出力と背景ジオメトリとの深度関係のスクリーンショット証拠は、旧画像スライス方式で取得したものです（[分割描画検証](docs/split-render-validation.md)）。
- 点一覧は1画素1点に間引くため、静止時でもDirect描画と約0.2%の画素が異なります。
- LODの変化は点一覧の作り直しまで最大Nフレーム遅れて反映されます。目視ではちらつきとして見えないことを確認済みです。
- Direct描画、整列・詰め直し・表示パスごとのGPU専用時間はまだ記録していません。
- 旧方式のベンチマークは1つのデータセット、1つのカメラ経路での結果です。`SplitCount = 2`、`3`、`4` は測定済みですが、`SplitCount = 8` は未測定です。
