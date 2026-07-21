# 1フレーム原因分析

## 計測条件

- `BenchmarkSession (capture=60fps)`
- Split描画、`SplitCount=4`
- サンプル数: 570フレーム（描画統計は571回）
- 入力: `sample.opc`
- 実行環境: オンボードGPU（ユーザー実行）

## 実測値（Console出力）

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

## 原因分析

CPU側の主要マーカー合計は平均0.85ms（0.28 + 0.41 + 0.16）で、平均Frame 17.59msの大半ではない。したがって、この計測ではLOD可視判定、ストリーミング同期、描画コマンド準備が主なCPUボトルネックではない。

`GpuUpload` は平均0.20ms、最大0.96msで、ロードが発生するフレームに限定したスパイク要因である。Frame最大36.62msとの直接の因果は、このConsole統計だけでは断定できない。

描画はSplitCount=4で、総描画点数約467万点のうち1フレーム約117万点を更新している。CPU markerが小さいため、残るフレーム時間はGPUの点描画・スライス合成、またはUnity/画面表示側の処理を疑うべきである。

## 限界

この記録はCPU `ProfilerRecorder` とベンチマーク統計に基づく。GPU専用時間、RenderDoc/Unity GPU Usageのパス別内訳は未取得であり、GPU内の支配パスまでは特定していない。

## Profilerキャプチャによる補足

GPU Usageを有効にしたUnity Profilerキャプチャを、Editorスクリプト `Assets/Editor/ProfilerDataExporter.cs` で全スレッドCSVへ出力して確認した。

| Render Threadサンプル | 平均 (ms) | 最大 (ms) | 解釈 |
| --- | ---: | ---: | --- |
| `OpcSplitDraw` | 1.402 | 2.821 | Render Thread上で最大の点群描画処理 |
| `UniversalRenderPipeline.RenderSingleCameraInternal: Main Camera` | 1.649 | 3.178 | カメラ描画全体。大半を`OpcSplitDraw`が占める |
| `OpcSplitComposite` | 0.011 / call | 0.368 / call | 4回の合成は支配的ではない |
| `GPUProfiler.EndQueries` | 2.823 | 9.242 | GPU timestampを回収するProfiler計測コスト。描画GPU時間そのものではない |

Main Threadの `TimeUpdate.WaitForLastPresentationAndUpdateTime` は最大4.264msだった。従って、フレーム時間の変動には点群描画だけでなく表示同期またはGPU完了待ちが寄与する。

この分析では、Split描画がRender Thread内の主要な描画処理であること、ただし平均Frame 17.59msを単独では説明しないことを確認した。GPU Usageを有効にすることで計測自体の問い合わせコストも発生するため、`GPUProfiler.EndQueries`を実描画時間としては扱わない。
