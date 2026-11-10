# PointCloudViewer

PointCloudViewerは、LAS由来の大量点群（数百万〜1億点規模）をUnityで表示するための検証用Viewerです。
Octreeの画面占有面積でLODを選び、見える範囲の点だけを段階的にGPUへ常駐させて、Indirect Drawで描画します。
内蔵GPUで詰まっている頂点処理を減らす方式を比べた結果、Split（点キャッシュ）をメインに採用し、Directを基準として残しています（詰まっている根拠は[フレーム時間の原因分析](docs/profiler-frame-analysis.md)、採用の理由は[集め直し方式](docs/gather-prototype.md#採用の判断)）。

## 描画モード

`OpcLoader` の Render Mode で切り替えます。

| モード | 位置づけ | 円（点の見た目）の作り方 | 詳細 |
| --- | --- | --- | --- |
| Direct | 基準 | 1点を4頂点のQuadで描く | [仕組み](docs/point-cloud-processing-flow.md) |
| Split | 採用（メイン） | 1/Nずつ作り直す点一覧N本を、円として描く | [分割描画検証](docs/split-render-validation.md) |

## 最新の計測

### ビルド

2026-11-02、Windowsプレイヤー（通常ビルド、D3D11、Intel UHD）、`Tools/Run-Benchmark.ps1`（`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080の窓）、`Assets/sample.opc`、コミット 35d5c52。当時あった4モードを順に4回測った平均フレーム時間の範囲です。計測中の画面で4モードとも点群が写っていることを確認しています。ここには今あるモードだけを載せます。削除済みモードの値は[集め直し方式](docs/gather-prototype.md)にあります。

| モード | 平均フレーム時間 | Directとの比 | 詳細 |
| --- | ---: | ---: | --- |
| Direct | 24.2〜24.3 ms | 1.00 | [仕組み](docs/point-cloud-processing-flow.md) |
| Split（点キャッシュ、count 4） | 20.6〜20.8 ms | 0.86 | [分割描画検証](docs/split-render-validation.md#処理時間) |

### エディタ

すべてUnityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080での値です。
回ごとにDirectの値も揺れるため、同じ回のDirectと比べます。

| 計測日 | モード | 平均フレーム時間 | 同じ回のDirect | 詳細 |
| --- | --- | ---: | ---: | --- |
| 2026-10-24 | Split（点キャッシュ、count 4） | 23.0 / 22.9 ms | 25.8 / 25.7 ms | [分割描画検証](docs/split-render-validation.md#処理時間) |

## 文書

| 知りたいこと | 文書 |
| --- | --- |
| 次にやること・人の判断待ち | [次にやること](docs/status.md) |
| 仕組み（処理の流れ・クラス・データの形・設計判断） | [点群の変換から描画まで](docs/point-cloud-processing-flow.md) |
| 分割描画の実装・計測・URP / Render Graphの制約 | [分割描画検証](docs/split-render-validation.md) |
| 集め直し方式の計測と不採用の理由（コードは削除済み） | [集め直し方式](docs/gather-prototype.md) |
| 1億点超データの出所と再現条件 | [1億点超データの出所](docs/hundred-million-point-reproduction.md) |
| 何が重いか（フレーム時間の内訳、頂点処理で詰まっている根拠） | [フレーム時間の原因分析](docs/profiler-frame-analysis.md) |
| AIへの作業指示 | [CLAUDE.md](CLAUDE.md) |

## 動かし方

- Unity 6000.3.9f1、URP 17.3.0（Render Graph有効）。
- `Assets/Scenes/TcpViewer.unity` を開いてPlayします。点群ファイルは `OpcLoader` の File Path（既定は `Assets/sample.opc`）です。
- エディタで計測するときは、シーンの `Benchmark` オブジェクトを有効にしてPlayします。終わるとConsoleに `=== BenchmarkSession` から始まる結果が出ます。

## 現在の制限

- 計測は1データ・1カメラ経路・1台のGPU（Intel UHD）だけです。
- 2026-11-01より前のビルドの計測値は無効です（シェーダのタグ違いで点群が描かれていませんでした）。
