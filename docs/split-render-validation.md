# 分割描画検証

記録日: 2026-07-04（2026-10-24 点キャッシュ方式へ更新）

## 概要

PointCloudViewerでは、以前のStage方式ではなく分割描画方式を使っています。
現在の実装は点キャッシュ方式です。毎フレーム点群の1/Nを1画素の点として整列し、1画素1点に絞った点一覧（ワールド座標・色・ワールド半径）へ詰め直します。点一覧はN本保持し、全N本を毎フレーム現在のカメラで円として描き直します。

2026-07の検証は旧画像スライス方式（1フレームに1スライスを永続スライス色・深度バッファへ描画し、有効な全スライスを再投影と深度テストで合成）で行いました。カメラ移動時のちらつきを解消するため、2026-10に点キャッシュ方式へ作り替えています。経緯と計測は [点キャッシュ方式への変更とちらつき計測](#点キャッシュ方式への変更とちらつき計測) を参照してください。

2026-07-04のユーザー目視確認では、旧方式の分割描画の出力は静止画として正しく描画されていると確認しました。2026-10-24のユーザー目視確認では、点キャッシュ方式でカメラ移動時のチカチカがなくなったことを確認しました。

## 現在の実装

- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - `OpcSplitRenderFeature`: URPから分割描画パスを有効化します。
  - `OpcSplitRenderPass`: 整列・詰め直し・表示の3パスを積みます。1フレームに1本の点一覧を作り直し、全N本を表示します。
  - `OpcSplitRenderBridge`: `OpcLoader` が準備した描画データを受け取ります。
- `Assets/PointCloudViewer/Shader/PointCloud_SplitFill.shader`
  - スライスオフセットとストライドを使い、現在のスライスに属する点だけを1画素の点として描きます。深度テストで1画素1点（最も手前）に絞ります。
- `Assets/PointCloudViewer/Shader/PointCloudCacheCompact.compute`
  - 点のある画素だけを点一覧へ詰め、点数を間接描画の引数へ書きます。
- `Assets/PointCloudViewer/Shader/PointCloud_CacheSplat.shader`
  - 点一覧の各点を、Direct描画と同じ点サイズ式で円として描き、深度を書き込みます。
- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - ベンチマーク比較用に `Direct` / `Split` 描画モードを切り替えます。

2026-07の検証時は `PointCloud_CircleSplit.shader`（スライス描画）と `PointCloudSplitComposite.shader`（再投影合成）を使っていました。どちらも2026-10に削除しています。

## 検証条件（2026-07、旧画像スライス方式）

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
- 描画先として、`UniversalResourceData.activeColorTexture` と `activeDepthTexture` を現在のカメラ色・深度ターゲットとして使います。
- 整列用の作業テクスチャ（ワールド座標+半径、色、深度）は、そのフレームで使い切るためRender Graphの一時テクスチャです。
- 点一覧と間接描画の引数は永続的な外部 `GraphicsBuffer` です。フレームをまたいで内容を保持する必要があるため、毎フレーム `renderGraph.ImportBuffer` でRender Graphへ取り込みます。
- 整列と表示は `AddRasterRenderPass`、詰め直しは `AddComputePass` で登録します。

現在の制約:

- 結果は `AfterRenderingTransparents` に依存します。注入タイミングを変えると、シーンジオメトリ、Transparent、点群深度の関係が変わる可能性があります。
- Render Graph内部の一時リソースはフレーム間で内容を保持できないため、点一覧は手動管理しています。点一覧1本の容量は画面の画素数です（1080pで1本約40MB）。
- 表示パスは1つですが、有効な点一覧ごとに間接描画を発行するため、Draw call数と表示する点の数は `SplitCount` とともに増えます。
- 実装はGameカメラ経路でactive color/depth textureが有効であることを前提にしています。

移行作業が必要になる条件:

- URPがここで使っているframe dataアクセス、特に `UniversalResourceData.activeColorTexture` または `activeDepthTexture` を変更・削除した場合。
- 手動管理のクロスフレーム点一覧ではなく、サポートされたcamera historyベースの設計が必要になった場合。
- `SplitCount` を増やし、表示パスのオーバーヘッドが実測上のボトルネックになった場合。
- Scene view、camera stacking、XR、複数カメラ、メインカメラ以外の描画を、点一覧を壊さずに支援する必要が出た場合。

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
## 点キャッシュ方式への変更とちらつき計測

記録日: 2026-10-24

### 旧画像スライス方式のちらつきの原因

- 円同士の奥行きがほぼ同じ場合、カメラが少し回転するだけで前後関係が入れ替わります。
- スライスを描いた時点で他の円に隠れていた部分はスライス画像に残らないため、後から手前に来ると円が三日月状に欠けて点滅します。
- スライス画像を保存する方式そのものの限界で、再投影の計算精度を上げても解消しません。

### 計測方法

- エディタのPlayモードでカメラを決まった経路で毎フレーム動かし、Direct描画の画面を正解として保存した上で、同じ経路のSplit描画と画素単位で比較しました。
- 指標は、正解と色が異なる画素のうち、カメラの回転を打ち消した位置で前後フレームでは食い違っていない画素の割合（補正瞬き）です。景色ごと動くだけの欠けを数えないための補正です。
- 条件: 回転0.3度/フレーム、40フレーム、1920x1080、`SplitCount = 4`。Direct同士の比較では補正瞬き0.000%でした。

| 方式 | 表示する点の数 | 補正瞬き | 補正瞬き（LOD固定） |
| --- | ---: | ---: | ---: |
| 旧画像スライス方式 | – | 0.297% | 0.292% |
| 点キャッシュ方式 `SplitCount = 1` | 約65万 | 0.149% | 0.140% |
| 点キャッシュ方式 `SplitCount = 4` | 約132万 | 0.150% | 0.036% |

Direct描画は同じ条件で約400万点を描画しています。

### 解釈

- 点キャッシュ方式は点ごとにワールド座標を持つため、隠れていた点が後から手前に来ても正しく描けます。補正瞬きは旧方式の約半分になりました。
- 1画素1点の間引きは、カメラが動くと残る点が入れ替わるため瞬きの原因になります。`SplitCount = 4` では点一覧の作り直しが1フレームに1/4だけになり、LOD固定時の瞬きは約4分の1になりました。
- 残りの大部分はLODの変化です。各ノードの点数と点サイズは毎フレーム変わりますが、点一覧には最大Nフレーム前の状態が残ります。
- 目視では、点キャッシュ方式 `SplitCount = 4` でカメラ移動時のチカチカがなくなったことを確認しました。

### 処理時間

記録日: 2026-10-24

計測条件: Unityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080、`SplitCount = 4`。各モード2回計測。

| モード | 平均Frame ms | p95 | 平均Draw points | 平均Fill points/frame |
| --- | ---: | ---: | ---: | ---: |
| Direct | 25.8 / 25.7 | 30.6 / 29.1 | 4,290,099 | – |
| Split（点キャッシュ） | 23.0 / 22.9 | 26.8 / 26.9 | 4,290,099 | 1,072,525 |

メモ:

- 作り替え直後のSplitは平均42.8msで、Directより遅くなっていました。パスを1つずつ止めた計測では、整列が約3ms、詰め直しが約0.6ms、表示が約33msでした。
- 表示が遅い原因は描く順番でした。点一覧を画面の行順に詰めると重なり合う円が連続して描かれ、同じ画素への書き込み待ちが増えます。表示する円の面積の合計はDirectより少ないことを確認しています。
- 詰め直しで画素を「通し番号 × 係数 mod 画素数」のばらけた順に走査するよう変更し、Splitは平均23.0msになりました。
- 静止時にDirectと食い違う画素は、変更後も0.06%（1280x720のキャプチャ）でした。

## スクリーンショット証拠（2026-07、旧画像スライス方式）

画像は公開リポジトリに含めていません。元のキャプチャは `Recordings/`（git管理外）に残しています。

### 点群のみの静止画

元画像: `Recordings/Image Sequence_003_0000.jpg`

結果:

- 分割描画の出力は街の点群全体を覆っており、目立つタイル抜けや黒い欠損は見えません。
- この静止画では、大きな深度順序の破綻は見えません。
- 静止画としての視覚的な正しさを支える証拠ですが、時間方向のちらつきや解像度変更後の破綻は検証していません。

### 背景ジオメトリとの深度確認

元画像: `Recordings/Image Sequence_004_0000.jpg`

結果:

- 背景ジオメトリが点群より手前にある箇所では、点群を正しく隠しています。
- ジオメトリ周辺でも点群は表示されており、目立つタイル抜けや黒い欠損は見えません。
- この静止画では、テストジオメトリ周辺に大きな深度順序の破綻は見えません。
- 分割描画された点群スライスとシーンジオメトリの静止画上の深度関係を支える証拠です。
## ベンチマーク結果（2026-07、旧画像スライス方式）

この節の値はすべて旧画像スライス方式で計測したものです。点キャッシュ方式の値は [処理時間](#処理時間) を参照してください。計測環境と描画点数が異なるため、直接は比較できません。

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

## 解釈（旧画像スライス方式のベンチマーク）

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

- 点キャッシュ方式の処理時間は、エディタ上の1経路・`SplitCount = 4` だけの計測です。上のベンチマークとSplitCount比較は旧画像スライス方式の値です。
- 静止画の点群出力と背景ジオメトリとの深度関係のスクリーンショット証拠は、旧画像スライス方式で取得したものです。
- 点キャッシュ方式は1画素1点に間引くため、静止時でもDirect描画と約0.2%の画素が異なります。
- Direct描画、整列・詰め直し・表示パスごとのGPU専用時間はまだ分離して測定していません。
- `Frame` には点群描画以外のフレーム処理も含まれます。
- 結果は1つのカメラ経路、1つのデータセット、1つの解像度、測定済みの分割数に対するものです。
- `SplitCount = 2`、`3`、`4` は比較済みですが、`SplitCount = 8` は未測定です。

## 次の確認

- 点キャッシュ方式で、SplitCount比較とビルド版での計測を取り直す。
- カメラ移動後と解像度変更後の追加スクリーンショットを保存する。
- `SplitCount = 8` を追加測定し、表示コストが支配的になり始めるか確認する。
- フレーム時間以上の説明が必要になったら、GPU Profiler証拠を追加する。
