# PointCloudViewer

PointCloudViewerは、LAS由来の大量点群（数百万〜1億点規模）をUnityで表示するための検証用Viewerです。
Octreeの画面占有面積でLODを選び、見える範囲の点だけを段階的にGPUへ常駐させて、Indirect Drawで描画します。
今は、内蔵GPUで詰まっている頂点処理を減らすための描画方式を比べています（詰まっている根拠は[フレーム時間の原因分析](docs/profiler-frame-analysis.md)）。

## 描画モード

`OpcLoader` の Render Mode で切り替えます。

| モード | 位置づけ | 円（点の見た目）の作り方 | 詳細 |
| --- | --- | --- | --- |
| Direct | 基準 | 1点を4頂点のQuadで描く | [仕組み](docs/point-cloud-processing-flow.md) |
| Split | 検証中 | 1/Nずつ作り直す点一覧N本を、円として描く | [分割描画検証](docs/split-render-validation.md) |
| DirectPixelPoints | 比較用 | 円を作らない（1画素点のみ。手前の面が透ける） | [集め直し方式](docs/gather-prototype.md) |
| Gather | 試作 | 1画素点を描いた後、画面上で周りの円を集め直す | [集め直し方式](docs/gather-prototype.md) |

## 最新の計測

すべてUnityエディタ上、`CameraOrbitMover` + `BenchmarkSession (capture=60fps)`、570フレーム、1920x1080での値です。
回ごとにDirectの値も揺れるため、同じ回のDirectと比べます。

| 計測日 | モード | 平均フレーム時間 | 同じ回のDirect | 詳細 |
| --- | --- | ---: | ---: | --- |
| 2026-11-01 | Gather（大きい円はQuad） | 18.7 / 17.9 ms | 24.6 / 26.7 ms | [集め直し方式](docs/gather-prototype.md) |
| 2026-10-25 | DirectPixelPoints | 11.6 / 11.5 ms | 25.1 / 25.2 ms | [フレーム時間の原因分析](docs/profiler-frame-analysis.md#1画素の点で頂点を減らした場合) |
| 2026-10-24 | Split（点キャッシュ、count 4） | 23.0 / 22.9 ms | 25.8 / 25.7 ms | [分割描画検証](docs/split-render-validation.md#処理時間) |

## 文書

| 知りたいこと | 文書 |
| --- | --- |
| 次にやること・人の判断待ち | [次にやること](docs/status.md) |
| 仕組み（処理の流れ・クラス・データの形・設計判断） | [点群の変換から描画まで](docs/point-cloud-processing-flow.md) |
| 分割描画の実装・計測・URP / Render Graphの制約 | [分割描画検証](docs/split-render-validation.md) |
| 集め直し方式の実装・計測 | [集め直し方式](docs/gather-prototype.md) |
| 1億点超データの出所と再現条件 | [1億点超データの出所](docs/hundred-million-point-reproduction.md) |
| 何が重いか（フレーム時間の内訳、頂点処理で詰まっている根拠） | [フレーム時間の原因分析](docs/profiler-frame-analysis.md) |
| AIへの作業指示 | [CLAUDE.md](CLAUDE.md) |

## 動かし方

- Unity 6000.3.9f1、URP 17.3.0（Render Graph有効）。
- `Assets/Scenes/TcpViewer.unity` を開いてPlayします。点群ファイルは `OpcLoader` の File Path（既定は `Assets/sample.opc`）です。
- エディタで計測するときは、シーンの `Benchmark` オブジェクトを有効にしてPlayします。終わるとConsoleに `=== BenchmarkSession` から始まる結果が出ます。

## 現在の制限

- 計測はエディタ上の1データ・1カメラ経路だけです。
- ビルドしたプレイヤーでは、全モードで点群が描かれません（2026-11-01に確認、原因は未調査）。そのため、ビルドでの計測値はありません。
