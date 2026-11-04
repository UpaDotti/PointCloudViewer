# フレーム時間の原因分析

内蔵GPU（Intel UHD、D3D11）で、フレーム時間が何に使われているかを実測した記録。結論は次の3つ。

- CPU側（LOD選択・Streaming・描画準備）は合計1ms未満で、主因ではない。
- 主因はGPUの点群描画で、頂点処理で詰まっている。塗る画素の量（フィルレート）は主因ではない。
- 点群以外に約6msの固定費がある。最大はSSAOで、Directでは約2.4ms。

頂点処理を減らす描画方式（DirectPixelPoints、Gather）を比べたのは、この結論による。どちらも不採用で、コードは削除した（[採用の判断](gather-prototype.md#採用の判断)）。

## 頂点処理で詰まっている根拠

記録日: 2026-06-07

条件: ノートPC内蔵GPU、エディタ、Direct描画（`PointCloud_Circle.shader`、1点を4頂点のQuadで描く。今のDirectと同じシェーダ）。

- メインスレッドの待ち（`WaitForSignal`）の大半はGPU待ちで、CPU側の処理は合計1ms未満。
- Point Budget 200万点でFrame約29ms（GPUの `RenderLoop.Draw` が21ms）。100万点に下げると大きく改善し、フレーム時間は点数にほぼ比例した。
- 点の最大画面サイズ（maxPixelSize）を下げても変わらない。塗る画素の量は主因ではない。
- シェーダの `discard` を外しても変わらない。Early-Z（深度テストを色の計算より前に行う最適化）が効かないことも主因ではない。
- D3D11では `RenderPrimitivesIndexedIndirect` が1回のマルチドローにならず、ノードごとの個別描画になる（Profilerに `Graphics.DrawProcedural` が435個並ぶ）。

## パスごとのGPU時間

記録日: 2026-10-24（Split表示の内訳は2026-10-25）

条件: Intel UHD、D3D11、エディタ、1920x1080、`CameraOrbitMover` の周回、`SplitCount = 4`。`ProfilerRecorder` + `GpuRecorder` で取得。

| モード | Frame | 点群のGPU時間 | その他（URP・エディタ） |
| --- | ---: | --- | ---: |
| Direct | 約25 ms | 17.2 ms（`DrawOpaqueObjects` に含まれる） | 約6 ms |
| Split（点キャッシュ） | 約22 ms | 表示 9.6 + 整列 2.4 + 詰め直し 2.3 ms | 約6 ms |

- Splitの表示で描く点は、点一覧4本で約159万点（Directの約430万点の37%）。
- 1点あたりの時間は、Splitの表示が6.0ns、Directが4.0ns。表示する点を4割弱に減らしても、1点あたりが1.5倍重いため、点群のGPU時間は14.3msまでしか減らない。

Splitの表示が1点あたり重い原因を、静止カメラ・点一覧132万点（表示7.56ms）で切り分けた目安:

| 内訳 | 時間 | 確かめ方 |
| --- | ---: | --- |
| 頂点処理の下限 | 約4.7 ms | 1点3.5nsで、Directと同程度。インスタンス＋Quadで描く方式の限界 |
| 画面上の局所性の損失 | 約1.4 ms | 円を0.5画素にして、点一覧の並び順だけを変えた差 |
| 画素を塗る分 | 約1.6 ms | 1画素1点で手前の点だけ残るため、残る円が大きめに偏る |

点一覧の1点を20 byteから32 byteにしても0.7msしか増えず、メモリ帯域は主因ではない。

Gatherの各パスのGPU時間は取れていない（自前の `ProfilingSampler` でも0のまま）。集め直しの重さは、半径を変えたときのフレーム時間の差から見積もっている（[集め直し方式](gather-prototype.md)）。

## 1画素の点で頂点を減らした場合

記録日: 2026-10-25

条件: 上と同じ環境、Point Budget 400万点、pointSizeScale 0.5、各2回。

| モード | Frame | 点群のGPU時間（`DrawOpaqueObjects`） |
| --- | ---: | ---: |
| Direct（1点4頂点） | 25.1 / 25.2 ms | 17.2 ms |
| DirectPixelPoints（1点1頂点） | 11.6 / 11.5 ms | 約4.3 ms |

- 頂点数を1/4にすると、点群のGPU時間もほぼ1/4になった。頂点処理が主因であることと合う。
- ただし見た目は別物になる。円の隙間から奥の点が見え、手前の面が透ける（静止画で約40%の画素が変わる）。
- 点の画面半径を、周回中の点数で重み付けして数えると、2〜3画素が約76〜78%、1画素未満は約5%だった。静止した初期視点では3〜4画素が98%。
- このため、小さい点だけを1画素の点にする案は約5%の点にしか効かない。1画素の点にするなら、円の隙間を画面上で埋める処理が要る。これがGatherの出発点である。

## 点群以外の固定費

記録日: 2026-10-25（Gatherは2026-11-01）

DirectでURPの機能を順に切ったときのFrame（累積、各2回）:

| 設定 | Frame |
| --- | ---: |
| 基準 | 26.2 ms |
| SSAOを切る | 23.9 ms |
| ＋Opaque Texture・Depth Textureを切る | 23.5 ms |
| ＋HDRを切る | 23.3 ms |
| ＋影を切る | 23.3 ms |

- 合計で約2.9ms減り、最大はSSAO（約2.4ms）。「その他約6ms」の残り約3msはエディタとURPの基礎分で、分けていない。
- Gatherでは、SSAOありが16.24 / 16.24 ms、なしが14.77 / 14.78 ms で、SSAOは約1.5ms（同じ回のDirectはSSAOありで25.53 ms）。
- SSAOは `Assets/Settings/PC_Renderer.asset` で有効。点はUnlitでSSAOの影響を受けず、今のシーンにはメッシュも無いため、今は見た目を変えずに時間だけかかっている。
- 2026-11-02に人が「SSAOは残す」と判断した。点群と普通のオブジェクトを一緒に置く前提で作るため。SSAOはレンダラー全体の機能で、切るとメッシュの陰影も消える。

## 試して続けなかった方式: computeでの点描き

頂点処理を通らずに点を描くため、computeシェーダで点を直接画面へ書き込む方式を試した。コードはリポジトリに残っていない。

- 2026-06-07、1画素の点: 200万点で16.84ms（当時のエディタの下限、約16.7msに張り付いた値）、800万点で23.05ms。同じ日のQuad版は200万点で29.45ms。
- 同じ日、円の形にすると塗る画素数×2パスに比例して重くなり、400万点・最大半径8画素で44ms。
- 2026-10-22、円（最大半径8画素、2パス）、約429万点: Direct 25.5ms に対して 68.2ms（2.7倍遅い）。

1画素なら速いが、円を自前で塗ると遅い。Gatherが1画素の点を描いてから円を集め直すのは、この結果による。

## 2026-07の記録（旧画像スライス方式）

この節は2026-07時点の旧画像スライス方式で取得したもの。`OpcSplitDraw` と `OpcSplitComposite` は旧方式のパス名で、今の点キャッシュ方式（`OpcSplitFill`、`OpcSplitCompact`、`OpcSplitSplat`）には無い。CPU側が主因ではないという結論は、上の結果と変わらない。

### 計測条件

- `BenchmarkSession (capture=60fps)`
- Split描画、`SplitCount=4`
- サンプル数: 570フレーム（描画統計は571回）
- 入力: `sample.opc`
- 実行環境: オンボードGPU（ユーザー実行）

### 実測値（Console出力）

| 指標 | 平均 | p95 | 最大 |
| --- | ---: | ---: | ---: |
| Frame (ms) | 17.59 | 21.13 | 36.62 |
| GetVisibleNodes (ms) | 0.28 | 0.36 | 0.46 |
| SyncNodeStreaming (ms) | 0.41 | 0.67 | 1.35 |
| DrawPointCloud (CPU marker, ms) | 0.16 | 0.23 | 0.86 |
| GpuUpload (ms) | 0.20 | 0.41 | 0.96 |
| 選択ノード数 | 799.98 | 897 | 951 |
| 描画点数 | 4,672,523 | 5,067,395 | 5,253,619 |
| 1フレームのSlice点数 | 1,168,131 | 1,267,112 | 1,313,103 |

### 原因分析

CPU側の主要マーカー合計は平均0.85ms（0.28 + 0.41 + 0.16）で、平均Frame 17.59msの大半ではない。したがって、この計測ではLOD可視判定、ストリーミング同期、描画コマンド準備が主なCPUボトルネックではない。

`GpuUpload` は平均0.20ms、最大0.96msで、ロードが発生するフレームに限定したスパイク要因である。Frame最大36.62msとの直接の因果は、このConsole統計だけでは断定できない。

描画はSplitCount=4で、総描画点数約467万点のうち1フレーム約117万点を更新している。CPU markerが小さいため、残るフレーム時間はGPUの点描画・スライス合成、またはUnity/画面表示側の処理を疑うべきである。

### 限界

この記録はCPU `ProfilerRecorder` とベンチマーク統計に基づく。GPU専用時間、RenderDoc/Unity GPU Usageのパス別内訳は未取得であり、GPU内の支配パスまでは特定していない。

### Profilerキャプチャによる補足

GPU Usageを有効にしたUnity Profilerキャプチャを、Editorスクリプト `Assets/Editor/ProfilerDataExporter.cs` で全スレッドCSVへ出力して確認した。

| Render Threadサンプル | 平均 (ms) | 最大 (ms) | 解釈 |
| --- | ---: | ---: | --- |
| `OpcSplitDraw` | 1.402 | 2.821 | Render Thread上で最大の点群描画処理 |
| `UniversalRenderPipeline.RenderSingleCameraInternal: Main Camera` | 1.649 | 3.178 | カメラ描画全体。大半を`OpcSplitDraw`が占める |
| `OpcSplitComposite` | 0.011 / call | 0.368 / call | 4回の合成は支配的ではない |
| `GPUProfiler.EndQueries` | 2.823 | 9.242 | GPU timestampを回収するProfiler計測コスト。描画GPU時間そのものではない |

Main Threadの `TimeUpdate.WaitForLastPresentationAndUpdateTime` は最大4.264msだった。従って、フレーム時間の変動には点群描画だけでなく表示同期またはGPU完了待ちが寄与する。

この分析では、Split描画がRender Thread内の主要な描画処理であること、ただし平均Frame 17.59msを単独では説明しないことを確認した。GPU Usageを有効にすることで計測自体の問い合わせコストも発生するため、`GPUProfiler.EndQueries`を実描画時間としては扱わない。
