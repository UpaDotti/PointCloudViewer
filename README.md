# PointCloudViewer

PointCloudViewerは、LAS/TPC/OPC由来の大量点群をUnityで表示するための検証用Viewerです。
LOD選択、Frustum Culling、Streaming、GraphicsBuffer、Indirect Drawを組み合わせ、数百万〜1億点規模の表示を扱います。
現在の主な成果は分割描画方式です。旧画像スライス方式では、同一ベンチマーク経路でDirect描画の平均31.38msに対し、Split count 4で平均18.23msまで短縮しました（2026-07計測）。
2026-10に、カメラ移動時のちらつきを解消するため点キャッシュ方式へ作り替えました。2026-10-24のエディタ上の計測では、Direct描画の平均25.8msに対し、Split count 4で平均23.0msでした。
スクリーンショット、SplitCount比較、ちらつき計測、URP / Render Graph上の制約は [分割描画検証](docs/split-render-validation.md) に記録しています。

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

- `RenderPassEvent.AfterRenderingTransparents` を使い、通常のシーン描画後に分割描画点群を合成します。
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
