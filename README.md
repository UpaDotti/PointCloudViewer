# PointCloudViewer

大量点群の描画、ストリーミング、LOD選択、分割描画を検証するためのUnity製Point Cloud Viewerです。

## 現在の焦点

現在の描画検証では、以前のStage方式ではなく分割描画方式を使っています。
点群の1スライスだけを毎フレーム永続スライスバッファへ描画し、保存済みの有効スライスを再投影と深度テストで合成します。

現在のベンチマークと正しさのメモは [分割描画検証](docs/split-render-validation.md) を参照してください。

## 分割描画の概要

主なファイル:

- `Assets/PointCloudViewer/Script/Opc/OpcLoader.cs`
  - OPCデータの読み込み、表示ノード選択、点データのストリーミング、描画コマンド準備、`Direct` / `Split` 描画モードの切り替えを担当します。
- `Assets/PointCloudViewer/Script/Opc/MultiDrawRenderer.cs`
  - 描画コマンドを準備し、ベンチマークログ用に準備済み点数を公開します。
- `Assets/PointCloudViewer/Script/Opc/SplitRender/OpcSplitRenderFeature.cs`
  - 1フレームに1スライスを描画し、有効なスライスを合成するURP Renderer Featureです。
- `Assets/PointCloudViewer/Shader/PointCloud_CircleSplit.shader`
  - 現在のスライスだけを描画する点群シェーダです。
- `Assets/PointCloudViewer/Shader/PointCloudSplitComposite.shader`
  - スライスの色・深度を現在のカメラビューへ再投影して合成するシェーダです。

## 最新ベンチマーク

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

- 静止画出力と背景ジオメトリとの深度関係については、[分割描画検証](docs/split-render-validation.md) にスクリーンショット証拠があります。
- Direct描画、スライス描画、合成パスごとのGPU専用時間はまだ記録していません。
- ベンチマークは1つのデータセット、1つのカメラ経路での結果です。`SplitCount = 2`、`3`、`4` は測定済みですが、`SplitCount = 8` は未測定です。
